'use strict';
const fs = require('node:fs');
const path = require('node:path');
const {DatabaseSync} = require('node:sqlite');
const {SCHEMA_VERSION, rawOrNull, plain, chooseConsensus, nullableInteger,
  factionName, resolvePage, urlFor, locationsFrom, hashId} = require('./profile');
const defaultDb = path.resolve(__dirname, '../../.research/eso-data.db');
const defaultOut = path.resolve(__dirname, '../../.research/character-profiles');
function usage() {
  console.log('Usage: node tools/ProfileBuilder/build.js [--db PATH] [--out DIR] [--count 10000]');
  console.log('Produces all-npcs.jsonl, initial-npcs.jsonl, and manifest.json. Source database is read-only.');
}
function options(argv) {
  let db = defaultDb, out = defaultOut, count = 10000;
  for(let i=0;i<argv.length;i++){
    const a=argv[i];
    if(a==='--help'||a==='-h'){usage();return null}
    if(a==='--db'){if(!argv[i+1])throw Error('--db needs a path');db=path.resolve(argv[++i])}
    else if(a==='--out'){if(!argv[i+1])throw Error('--out needs a directory');out=path.resolve(argv[++i])}
    else if(a==='--count'){
      count=Number(argv[++i]);if(!Number.isSafeInteger(count)||count<1)throw Error('--count must be a positive integer');
    }else throw Error('Unknown argument '+a);
  }
  return {db,out,count};
}
const groupBy=(rows,key)=>{
  const map=new Map();
  for(const row of rows){const k=row[key];if(!map.has(k))map.set(k,[]);map.get(k).push(row);}
  return map;
};
function sourcePages(db) {
  return new Map(db.prepare('SELECT page_id AS pageId,title FROM wiki_pages').all().map(r=>[r.title.toLowerCase(),r]));
}
function placePages(db) {
  return new Map(db.prepare('SELECT page_id AS pageId,title FROM world_places').all().map(r=>[r.title.toLowerCase(),r]));
}
function lorePages(db) {
  return new Map(db.prepare('SELECT page_id AS pageId,title,redirect_title AS redirectTitle FROM uesp_dump_pages WHERE namespace_id=130').all().map(r=>[r.title.toLowerCase(),r]));
}
function scalar(variants, field) {return chooseConsensus(variants,field).value;}
function booleanFlag(variants, field) {
  const values=new Set(variants.map(v=>rawOrNull(v.fields?.[field])).filter(Boolean));
  if(values.size===0)return null;
  if(values.size===1&&values.has('1'))return true;
  if(values.size===1&&values.has('0'))return false;
  return null;
}
function refFrom(name, pages, prefix) {
  const page=resolvePage(name,pages,prefix);
  return {name, pageId:page?.pageId??null, pageTitle:page?.title??null,
    wikiUrl:page?urlFor(page.title):null, redirectTitle:page?.redirectTitle??null, resolution:page?'exact_title':'unresolved'};
}
function profileFor(n, variants, quests, pages, places, lore, creatureIds) {
  const id='npc:uesp-online:'+n.page_id;
  const race=scalar(variants,'race'),gender=scalar(variants,'gender');
  const title=scalar(variants,'title'),gameClass=scalar(variants,'class');
  const faction=chooseConsensus(variants,'faction',factionName).value;
  const reaction=scalar(variants,'reaction'),service=scalar(variants,'sells');
  const health=chooseConsensus(variants,'health',nullableInteger).value;
  const healthRaw=chooseConsensus(variants,'health',rawOrNull).value;
  const level=chooseConsensus(variants,'level',nullableInteger).value;
  const generic=booleanFlag(variants,'generic');
  const creature=booleanFlag(variants,'creature')===true||creatureIds.has(n.page_id);
  const locations=locationsFrom(variants,places);
  const loreLabel=scalar(variants,'lorepage');
  const loreRef=loreLabel?refFrom(loreLabel,lore,'Lore:'):null;
  const raceReferences=race?{eso:resolvePage(race,pages,'Online:')?refFrom(race,pages,'Online:'):null,
    lore:resolvePage(race,lore,'Lore:')?refFrom(race,lore,'Lore:'):null}:null;
  const questRefs=[...new Set(quests.map(q=>q.quest_label).filter(Boolean))].map(q=>refFrom(q,pages,'Online:'));
  const conflicts={};
  for(const f of ['race','gender','class','title','faction','reaction','health','level','sells','lorepage']){
    const normalized=f==='faction'?factionName:f==='health'||f==='level'?rawOrNull:plain;
    const x=chooseConsensus(variants,f,normalized);
    if(x.conflict)conflicts[f]=x.conflict;
  }
  const primary=variants.length===1&&!locations.ambiguous?locations.primary:null;
  const eligibility=[];
  if(generic===true)eligibility.push('generic_npc_template');
  if(creature)eligibility.push('creature_profile');
  if(variants.length!==1)eligibility.push('multiple_source_appearances');
  if(!race)eligibility.push('unknown_race');
  if(!gender)eligibility.push('unknown_gender');
  if(!primary)eligibility.push('no_single_resolved_location');
  return {
    schemaVersion:SCHEMA_VERSION,kind:'npc',id,
    identity:{name:n.name??null,race,gender,title,gameNpcId:scalar(variants,'id'),generic,creature},
    society:{faction,serviceRole:service,occupation:null,socialStatus:null},
    combat:{gameClass,reaction,health,healthRaw,level,difficulty:scalar(variants,'difficulty')},
    locations:{primary, candidates:locations.candidates, ambiguous:locations.ambiguous, multiAppearance:variants.length>1},
    references:{quests:questRefs,lore:loreRef,race:raceReferences,imageFilename:scalar(variants,'image')},
    source:{provider:'uesp',game:'eso',pageId:n.page_id,wikiTitle:n.wiki_title,
      revisionId:n.revision_id??null,revisionTimestamp:n.revision_timestamp==null?null:String(n.revision_timestamp),
      wikiUrl:urlFor(n.wiki_title)},
    appearances:variants.map(v=>({summaryNumber:v.number,rawFields:v.fields})),
    conflicts,
    simulation:{age:null,personality:null,attributes:null,inventory:null,schedule:null,home:null,
      relationships:null,wealth:null,currentHealth:null},
    selection:{eligible:eligibility.length===0,reasons:eligibility,hash:hashId(id)}
  };
}
function run(settings) {
  if(!fs.existsSync(settings.db))throw Error('Database not found: '+settings.db);
  const db=new DatabaseSync(settings.db,{readOnly:true});
  const pages=sourcePages(db),places=placePages(db),lore=lorePages(db);
  const creatureIds=new Set(db.prepare("SELECT page_id FROM wiki_page_category_assignments WHERE category_title='Category:Online-Creatures-All'").all().map(r=>r.page_id));
  const summaries=groupBy(db.prepare('SELECT page_id,summary_number,fields_json FROM npc_summaries ORDER BY page_id,summary_number').all(),'page_id');
  const quests=groupBy(db.prepare('SELECT page_id,sequence_number,quest_label FROM npc_quest_links ORDER BY page_id,sequence_number').all(),'page_id');
  fs.mkdirSync(settings.out,{recursive:true});
  const allPath=path.join(settings.out,'all-npcs.jsonl');
  const selectedPath=path.join(settings.out,'initial-npcs.jsonl');
  const allFd=fs.openSync(allPath,'w'),eligible=[];
  const manifest={schemaVersion:SCHEMA_VERSION,kind:'npc',source:'UESP Online NPC pages',
    selection:'SHA256 of stable profile ID; eligible profiles only; no regional preference',
    requestedInitialCount:settings.count,records:0,eligible:0,excluded:0,selected:0,
    sourceSummaryRecords:0,exclusionReasons:{},knownRace:0,knownGender:0,knownPrimaryLocation:0,
    multiAppearancePages:0,loreLinks:0,raceEsoLinks:0,raceLoreLinks:0,questReferences:0,generatedUtc:null};
  try{
    const statement=db.prepare('SELECT n.page_id,n.name,n.wiki_title,w.revision_id,w.revision_timestamp FROM npcs n JOIN wiki_pages w ON w.page_id=n.page_id WHERE n.summary_count>0 ORDER BY n.page_id');
    for(const npc of statement.iterate()){
      const variations=(summaries.get(npc.page_id)||[]).map(v=>({number:v.summary_number,fields:JSON.parse(v.fields_json||'{}')}));
      if(!variations.length)throw Error('Missing source summary for '+npc.page_id);
      const q=quests.get(npc.page_id)||[];
      const p=profileFor(npc,variations,q,pages,places,lore,creatureIds);
      const line=JSON.stringify(p)+'\n';
      fs.writeSync(allFd,line,'utf8');
      manifest.records++;manifest.sourceSummaryRecords+=variations.length;
      if(p.identity.race)manifest.knownRace++;
      if(p.identity.gender)manifest.knownGender++;
      if(p.locations.primary)manifest.knownPrimaryLocation++;
      if(p.locations.multiAppearance)manifest.multiAppearancePages++;
      if(p.references.lore?.pageId)manifest.loreLinks++;
      if(p.references.race?.eso?.pageId)manifest.raceEsoLinks++;
      if(p.references.race?.lore?.pageId)manifest.raceLoreLinks++;
      manifest.questReferences+=p.references.quests.length;
      if(p.selection.eligible){manifest.eligible++;eligible.push({id:p.id,hash:p.selection.hash,line});}
      else {
        manifest.excluded++;
        for(const reason of p.selection.reasons)manifest.exclusionReasons[reason]=(manifest.exclusionReasons[reason]||0)+1;
      }
      if(manifest.records%5000===0)console.log('PROFILES',manifest.records,'eligible',manifest.eligible);
    }
  }finally{fs.closeSync(allFd);db.close();}
  eligible.sort((a,b)=>a.hash.localeCompare(b.hash)||a.id.localeCompare(b.id));
  const selected=eligible.slice(0,settings.count);
  const fd=fs.openSync(selectedPath,'w');
  try{for(const row of selected)fs.writeSync(fd,row.line,'utf8');}finally{fs.closeSync(fd);}
  manifest.selected=selected.length;
  manifest.shortfall=settings.count-selected.length;
  manifest.generatedUtc=new Date().toISOString();
  manifest.output={allProfiles:'all-npcs.jsonl',initialProfiles:'initial-npcs.jsonl'};
  fs.writeFileSync(path.join(settings.out,'manifest.json'),JSON.stringify(manifest,null,2)+'\n');
  console.log('PROFILE EXPORT COMPLETE',JSON.stringify(manifest,null,2));
  if(manifest.shortfall>0)throw Error('Only '+eligible.length+' eligible profiles; requested '+settings.count+'. No records invented.');
}
if(require.main===module){
  try{const cfg=options(process.argv.slice(2));if(cfg)run(cfg);}
  catch(e){console.error(e.stack??e);process.exitCode=1;}
}
module.exports={options,profileFor,run};