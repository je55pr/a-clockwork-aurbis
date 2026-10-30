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
// These are settlement overview markers, not exact NPC or walkable locations.
const dockMarker=townMap.get("vulkhel guard docks");
if(!dockMarker)throw Error("Vulkhel Guard Docks marker missing from UESP map");
const outdoors=["Vulkhel Guard Wayshrine","Harborside Market",
  "Vulkhel Guard Docks","Salted Wings Tavern","Mages Guild (Vulkhel Guard)",
  "Fighters Guild (Vulkhel Guard)","Vulkhel Guard Manor & Treasury",
  "Vulkhel Guard Warehouse","Aegis of Auri-El","The Arrowpoint Club",
  "Temple of Auri-El","Mara's Kiss Public House"];
const districtMarkers=outdoors.map(name=>({name,marker:townMap.get(norm(name))})).filter(x=>x.marker);
if(districtMarkers.length<8)throw Error("Missing expected Vulkhel settlement markers");
const seaJob=/\b(?:dock(?:worker|hand|side)?|wharf(?:rat|worker)?|boatswain|navigator|deckhand|sailor|shipwright|fisher(?:man|woman)?|fishmonger|stevedore|harbour\w*|harbor\w*|seafarer|longshore\w*|sailing|sails)\b/i;
const findTownAnchor=p=>{
  for(const key of [p.name,p.title]){
    const normalized=norm(key);
    const alias=normalized==="vulkhel docks"?"vulkhel guard docks":normalized;
    const found=townMap.get(alias);
    if(found)return {marker:found,aliased:normalized!==alias};
  }
  return null;
};
const fromMarker=(marker,kind,place,sourcePlace=null)=>({
  x:marker.x,y:marker.y,kind,markerId:marker.location_id,
  place,sourcePlace
});
const districtPoint=(person)=>{
  const i=Math.floor(hash("district:"+person.id)*districtMarkers.length);
  const {name,marker}=districtMarkers[Math.min(i,districtMarkers.length-1)];
  // Stable 3D-area jitter: ±12 m in world space at current provisional scale.
  const xNoise=(hash("district-x:"+person.id)-0.5)*34000;
  const yNoise=(hash("district-y:"+person.id)-0.5)*34000;
  return {x:marker.x+xNoise,y:marker.y+yNoise,kind:"estimated_city_district",
    place:"Vulkhel Guard (near "+name+")",sourcePlace:"Online:Vulkhel Guard",
    district:name};
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
   person.previewTown="Vulkhel Guard";
   // Explicit location links outrank profession, which outranks the city-wide
   // district estimate. "Vulkhel Docks" aliases the actual map label
   // "Vulkhel Guard Docks", instead of silently falling back to the city.
   const explicitDock=person.places.find(p=>norm(p.name)==="vulkhel docks"||
       norm(p.title)==="vulkhel docks");
   const specific=person.places.map(p=>({place:p,match:findTownAnchor(p)}))
     .filter(x=>x.match && norm(x.place.name)!=="vulkhel guard");
   const seaOccupation=seaJob.test([person.name,person.service].join(" "));
   if(explicitDock)
   {
     person.town=fromMarker(dockMarker,"uesp_map_marker_alias",
       "Vulkhel Guard Docks",explicitDock.title);
     localExact++;
   }
   else if(seaOccupation && specific.length===0)
   {
     person.town=fromMarker(dockMarker,"inferred_profession",
       "Vulkhel Guard Docks (dock-related occupation)",target.title);
     localEstimated++;
   }
   else if(specific.length)
   {
     const {place,match}=specific[0];
     person.town=fromMarker(match.marker,match.aliased
       ?"uesp_map_marker_alias":"uesp_map_marker",
       match.marker.wiki_page||match.marker.name,place.title);
     localExact++;
   }
   else if(seaOccupation)
   {
     person.town=fromMarker(dockMarker,"inferred_profession",
       "Vulkhel Guard Docks (dock-related occupation)",target.title);
     localEstimated++;
   }
   else
   {
     person.town=districtPoint(person);
     localEstimated++;
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
