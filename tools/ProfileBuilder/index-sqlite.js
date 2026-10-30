'use strict';
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline');
const {DatabaseSync}=require('node:sqlite');
const base=path.resolve(__dirname,'../../.research');
const dbFile=process.argv[2]?path.resolve(process.argv[2]):path.join(base,'eso-data.db');
const folder=process.argv[3]?path.resolve(process.argv[3]):path.join(base,'character-profiles');
if(process.argv.includes('--help')){
 console.log('Usage: node tools/ProfileBuilder/index-sqlite.js [database] [profile-directory]');
 process.exit(0);
}
const manifest=JSON.parse(fs.readFileSync(path.join(folder,'manifest.json'),'utf8'));
if(manifest.schemaVersion!==1||manifest.kind!=='npc')throw Error('Unsupported profile format');
const ids=new Set();
for(const line of fs.readFileSync(path.join(folder,'initial-npcs.jsonl'),'utf8').split(/\r?\n/)){
 if(!line)continue;const obj=JSON.parse(line);
 if(ids.has(obj.id))throw Error('Duplicate roster ID '+obj.id);
 ids.add(obj.id);
}
if(ids.size!==manifest.selected)throw Error('Roster count disagrees with manifest');
const db=new DatabaseSync(dbFile);
db.exec([
'CREATE TABLE IF NOT EXISTS standard_npc_profiles_v1 (',
'profile_id TEXT, page_id INTEGER, name TEXT, race TEXT, gender TEXT, title TEXT, service_role TEXT,',
'faction TEXT, combat_class TEXT, reaction TEXT, health INTEGER, health_raw TEXT,',
'primary_place_id INTEGER, primary_place_title TEXT, unresolved_home INTEGER,',
'appearance_count INTEGER, lore_person_page_id INTEGER, lore_race_page_id INTEGER,',
'eligible INTEGER, initial_roster INTEGER, selection_reasons_json TEXT, profile_json TEXT)',
].join(' '));
const add=db.prepare('INSERT INTO standard_npc_profiles_v1 VALUES ('+Array(22).fill('?').join(',')+')');
async function run(){
 let count=0,chosen=0;
 db.exec('BEGIN');
 try {
  db.exec('DELETE FROM standard_npc_profiles_v1');
  const stream=readline.createInterface({input:fs.createReadStream(path.join(folder,'all-npcs.jsonl'),{encoding:'utf8'}),crlfDelay:Infinity});
  const seen=new Set();
  for await(const line of stream){
   if(!line)continue;
   const p=JSON.parse(line),selected=ids.has(p.id);
   if(p.schemaVersion!==1||p.kind!=='npc'||!p.id)throw Error('Unsupported profile at line '+(count+1));
   if(seen.has(p.id))throw Error('Duplicate profile '+p.id);
   seen.add(p.id);
   if(selected&&!p.selection?.eligible)throw Error('Ineligible profile selected '+p.id);
   add.run(p.id,p.source?.pageId??null,p.identity?.name??null,p.identity?.race??null,
    p.identity?.gender??null,p.identity?.title??null,p.society?.serviceRole??null,p.society?.faction??null,
    p.combat?.gameClass??null,p.combat?.reaction??null,p.combat?.health??null,p.combat?.healthRaw??null,
    p.locations?.primary?.placeId??null,p.locations?.primary?.pageTitle??null,
    p.locations?.primary?0:1,p.appearances?.length??null,p.references?.lore?.pageId??null,
    p.references?.race?.lore?.pageId??null,p.selection?.eligible?1:0,selected?1:0,
    JSON.stringify(p.selection?.reasons??[]),line);
   count++;if(selected)chosen++;
  }
  if(count!==manifest.records||chosen!==manifest.selected)throw Error('Import counts differ from manifest '+count+'/'+chosen);
  const record=db.prepare('SELECT COUNT(*) n FROM source_collection_coverage WHERE source_key=?');
  const insert=db.prepare('INSERT INTO source_collection_coverage VALUES (?,?,?,?,?,?,?,?)');
  const update=db.prepare('UPDATE source_collection_coverage SET scope=?,expected_count=?,acquired_count=?,collection_status=?,verification_note=?,sample_only=?,updated_at=? WHERE source_key=?');
  const now=new Date().toISOString();
  for(const [key,total,description]of [
    ['standard-npc-profiles-v1',count,'Every ESO NPC page with at least one source summary, source-derived fields, raw appearance variants and no fabricated simulation attributes'],
    ['standard-npc-initial-roster-v1',chosen,'Deterministic hash-selected roster with source-backed location, race and gender; does not imply unique canonical characters beyond UESP page identity']]){
    if(record.get(key).n)update.run(description,total,total,'derived_complete',description,0,now,key);
    else insert.run(key,description,total,total,'derived_complete',description,0,now);
  }
  db.exec('COMMIT');
  db.exec('CREATE UNIQUE INDEX IF NOT EXISTS idx_std_npc_v1_id ON standard_npc_profiles_v1(profile_id)');
  db.exec('CREATE INDEX IF NOT EXISTS idx_std_npc_v1_race ON standard_npc_profiles_v1(race)');
  db.exec('CREATE INDEX IF NOT EXISTS idx_std_npc_v1_place ON standard_npc_profiles_v1(primary_place_id)');
  db.exec('CREATE INDEX IF NOT EXISTS idx_std_npc_v1_name ON standard_npc_profiles_v1(name)');
  db.exec('CREATE INDEX IF NOT EXISTS idx_std_npc_v1_initial ON standard_npc_profiles_v1(initial_roster)');
  db.exec('CREATE VIEW IF NOT EXISTS standard_npc_initial_roster_v1 AS SELECT * FROM standard_npc_profiles_v1 WHERE initial_roster=1');
  console.log(JSON.stringify({indexed:count,initialRoster:chosen,integrity:db.prepare('PRAGMA quick_check').get().quick_check},null,2));
 }catch(e){db.exec('ROLLBACK');throw e}finally{db.close()}
}
run().catch(e=>{console.error(e.stack);process.exitCode=1});
