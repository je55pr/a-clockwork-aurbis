'use strict';
// Worldwide source relations are retained in SQLite. This builds a local Auridon preview,
// never an authoritative claim of exact NPC coordinates.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {DatabaseSync}=require('node:sqlite');
const dbPath=process.argv[2]||path.resolve(__dirname,'../../.research/eso-data.db');
const outPath=process.argv[3]||path.resolve(__dirname,'../../.research/auridon-population.json');
const db=new DatabaseSync(dbPath,{readOnly:true});
const query=[
  'SELECT DISTINCT n.page_id npcId,n.name,n.npc_race race,n.npc_gender gender,n.npc_reaction reaction,',
  'n.npc_sells service,n.npc_faction faction,l.place_page_id placeId,l.place_title placeTitle,',
  'p.name placeName,p.city city,p.settlement settlement,p.zone zone',
  'FROM npcs n JOIN world_npc_place_links l ON l.npc_page_id=n.page_id',
  'LEFT JOIN world_places p ON p.page_id=l.place_page_id',
  "LEFT JOIN world_zone_memberships z ON z.place_page_id=l.place_page_id AND z.zone_title='Online:Auridon'",
  "WHERE n.summary_count>0 AND (LOWER(TRIM(p.zone))='auridon' OR LOWER(TRIM(p.name))='auridon' OR z.zone_page_id IS NOT NULL)",
  'ORDER BY n.page_id,l.place_page_id'
].join(' ');
const rows=db.prepare(query).all();
const allMarkers=db.prepare('SELECT world_id,location_id,name,wiki_page,x,y FROM map_locations WHERE world_id IN (124,162)').all()
  .filter(r=>Number.isFinite(r.x)&&Number.isFinite(r.y)&&r.x>=0&&r.x<=1000000&&r.y>=0&&r.y<=1000000);
const norm=s=>String(s||'').toLowerCase().trim().replace(/^online:/,'').replace(/_/g,' ').replace(/\s+/g,' ');
const byWorld=new Map();
for(const world of [124,162]){
 const map=new Map();
 for(const marker of allMarkers.filter(r=>r.world_id===world)){
  for(const label of [marker.wiki_page,marker.name]){
   const key=norm(label);if(!key||map.has(key))continue;
   map.set(key,marker);
  }
 }
 byWorld.set(world,map);
}
const mainMap=byWorld.get(124),townMap=byWorld.get(162);
const byId=new Map();
for(const row of rows){
 let person=byId.get(row.npcId);
 if(!person){
  person={id:'npc:uesp-online:'+row.npcId,pageId:row.npcId,name:row.name,race:row.race,
    gender:row.gender,reaction:row.reaction,service:row.service,faction:row.faction,
    places:[],map:null,town:null};
  byId.set(row.npcId,person);
 }
 if(!person.places.some(p=>p.pageId===row.placeId)){
  person.places.push({pageId:row.placeId,title:row.placeTitle,name:row.placeName,
   city:row.city,settlement:row.settlement,zone:row.zone});
 }
}
const hash=s=>crypto.createHash('sha256').update(s).digest().readUInt32LE(0)/4294967296;
const knownTown=mainMap.get('vulkhel guard');
if(!knownTown)throw Error('Vulkhel Guard is not resolved on Auridon world map');
const guessedTownPoint=(place)=>{
  // Stable approximate district allocation, not a game coordinate or georeference.
  const a=hash('house-x:'+place),b=hash('house-y:'+place);
  return {x:180000+370000*a,y:440000+330000*b,kind:'estimated_district',place};
};
const getMain=(p)=>{
 const candidates=[p.name,p.title,p.city,p.settlement];
 for(const c of candidates){
  const hit=mainMap.get(norm(c));
  if(hit)return {x:hit.x,y:hit.y,kind:'uesp_map_marker',markerId:hit.location_id,place:hit.wiki_page||hit.name};
 }
 return null;
};
const isVulkhel=(p)=>['vulkhel guard','vulkhel docks'].some(x=>[p.city,p.settlement,p.name,p.title].some(y=>norm(y).includes(x))) ||
   [p.city,p.settlement].some(y=>norm(y).includes('vulkhel'));
let mainMarkers=0,mainFallback=0,missing=0,localExact=0,localEstimated=0;
for(const person of byId.values()){
 const ranked=[...person.places].sort((a,b)=>
    (getMain(b)?1:0)-(getMain(a)?1:0) || a.title.localeCompare(b.title));
 let chosen=null;
 for(const p of ranked)if((chosen=getMain(p)))break;
 if(!chosen){
  const pv=ranked.find(isVulkhel);
  if(pv){chosen={x:knownTown.x,y:knownTown.y,kind:'named_settlement',place:'Vulkhel Guard'};mainFallback++;}
 }
 if(chosen){person.map=chosen;if(chosen.kind==='uesp_map_marker')mainMarkers++;}
 else missing++;
 const target=person.places.find(p=>isVulkhel(p));
 if(target){
   person.previewTown='Vulkhel Guard';
   // City-specific wiki locations may have a UESP local-map point.
   const exact=person.places.map(p=>townMap.get(norm(p.name))||townMap.get(norm(p.title))).find(Boolean);
   if(exact){
    person.town={x:exact.x,y:exact.y,kind:'uesp_map_marker',markerId:exact.location_id,place:exact.wiki_page||exact.name};localExact++;
   } else {
    person.town=guessedTownPoint(target.title);localEstimated++;
   }
 }
}
const people=[...byId.values()].sort((a,b)=>a.pageId-b.pageId);
const payload={
 schemaVersion:1,source:'UESP Online NPC summary + place associations + gamemap locations',
 disclaimer:'NPC coordinates are estimated around linked places. Map markers are place positions, never NPC coordinates. 3D terrain alignment is not calibrated.',
 auridonWorldId:124,vulkhelTownWorldId:162,
 population:people.length,people,
 coverage:{exactIslandMapMarkers:mainMarkers,namedSettlementIslandFallback:mainFallback,
   islandUnplaced:missing,vulkhelTownNpcCount:localExact+localEstimated,
   vulkhelTownMarkerAnchors:localExact,vulkhelTownEstimatedAnchors:localEstimated},
 generatedUtc:new Date().toISOString()
};
fs.mkdirSync(path.dirname(outPath),{recursive:true});
fs.writeFileSync(outPath,JSON.stringify(payload));
console.log('AURIDON PLACE-ANCHORED PREVIEW',JSON.stringify({population:people.length,...payload.coverage},null,2));
if(people.length<850)throw Error('Unexpected Auridon population drop: '+people.length);
db.close();
