'use strict';
const fs=require('node:fs'),path=require('node:path'),readline=require('node:readline');
const folder=process.argv[2]?path.resolve(process.argv[2]):path.resolve(__dirname,'../../.research/character-profiles');
const manifest=JSON.parse(fs.readFileSync(path.join(folder,'manifest.json'),'utf8'));
async function* lines(file){
 const reader=readline.createInterface({input:fs.createReadStream(path.join(folder,file),{encoding:'utf8'}),crlfDelay:Infinity});
 for await(const line of reader)if(line)yield JSON.parse(line);
}
async function run(){
 const seen=new Set(),eligible=[],names=new Set(),places=new Set(),races=new Map();
 let count=0;
 for await(const p of lines('all-npcs.jsonl')){
  if(p.schemaVersion!==1||p.kind!=='npc'||!p.id||seen.has(p.id))throw Error('Invalid or duplicated source profile');
  seen.add(p.id);count++;
  if(p.selection?.eligible)eligible.push({id:p.id,hash:p.selection.hash});
 }
 eligible.sort((a,b)=>a.hash.localeCompare(b.hash)||a.id.localeCompare(b.id));
 if(count!==manifest.records||eligible.length!==manifest.eligible)throw Error('Manifest source counts do not match files');
 const expected=eligible.slice(0,manifest.selected).map(x=>x.id);
 let selected=0,raceLore=0,personLore=0;
 for await(const p of lines('initial-npcs.jsonl')){
  if(p.id!==expected[selected])throw Error('Selection differs from deterministic source ranking at line '+(selected+1));
  if(!p.selection?.eligible||!p.locations?.primary?.placeId)throw Error('Unqualified selected record '+p.id);
  if(!p.identity?.name||!p.identity?.race||!p.identity?.gender)throw Error('Missing required identity for selected record');
  names.add(p.identity.name);places.add(p.locations.primary.placeId);
  const race=p.identity.race;races.set(race,(races.get(race)||0)+1);
  if(p.references?.race?.lore?.pageId)raceLore++;
  if(p.references?.lore?.pageId)personLore++;
  selected++;
 }
 if(selected!==manifest.selected)throw Error('Initial selection count mismatch');
 console.log('PROFILE VERIFICATION',JSON.stringify({records:count,eligible:eligible.length,selected,
    uniqueNames:names.size,distinctPrimaryPlaces:places.size,distinctRaces:races.size,
    crossGameRaceLinks:raceLore,personalLoreLinks:personLore,
    topRaces:[...races].sort((a,b)=>b[1]-a[1]).slice(0,12)},null,2));
}
run().catch(e=>{console.error(e.stack);process.exitCode=1});
