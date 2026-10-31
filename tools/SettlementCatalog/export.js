'use strict';
// Export a named settlement from the *global* source database.
// Node is an offline build-time adapter; the C# simulation consumes JSON only.
const fs=require('node:fs'),path=require('node:path');
const {DatabaseSync}=require('node:sqlite');
const args=process.argv.slice(2);
const option=(key,required=false)=>{
 const n=args.indexOf('--'+key);
 if(n<0){if(required)throw Error('Missing --'+key);return null;}
 if(!args[n+1])throw Error('Missing value for --'+key);
 return args[n+1];
};
const dbPath=option('db',true),name=option('settlement'),out=option('out',true),zone=option('zone'),wholeZone=option('whole-zone');
if((name===null)===(wholeZone===null))throw Error('Supply exactly one of --settlement or --whole-zone');
const db=new DatabaseSync(dbPath,{readOnly:true});
const unique=items=>[...new Set(items)];
function role(place){
 const type=(place.place_type||'').toLowerCase(),label=(place.name||'').toLowerCase();
 if(type==='home'||type==='player house')return 'dwelling';
 if(type==='farm'||type==='plantation'||type==='fishery')return 'food_producer';
 if(type==='mine'||type==='grove'||type==='quarry')return 'resource_producer';
 if(type==='inn'||type==='marketplace'||type.includes('tavern'))return 'food_retail';
 if(type.includes('transportation')&&/dock|harbou?r|port|quay/.test(label))return 'import_hub';
 if(type.includes('carpentry')||type.includes('smithy')||type.includes('forge'))return 'workshop';
 if(/armory|outfitter|apothecary|store|stable|bookshop|general store|trader|vendor|jeweler/.test(type))return 'goods_retail';
 if(/bank|guildhall|chapel|barracks|lighthouse|warehouse|gladiator/.test(type))return 'institution';
 return 'landmark';
}
try{
 const all=db.prepare('SELECT page_id,name,place_type,zone,settlement,city,subzone,title FROM world_places').all();

 let included;
 if(wholeZone!==null){
   const regionName=wholeZone.trim();
   const root=all.find(p=>p.name.toLowerCase()===regionName.toLowerCase() &&
     p.place_type==='Zone');
   if(!root)throw Error('Unknown UESP zone '+wholeZone);
   const categoryMembers=new Set(db.prepare(
     'SELECT place_page_id FROM world_zone_memberships WHERE zone_title=?'
   ).all(root.title).map(x=>x.place_page_id));
   included=all.filter(p=>categoryMembers.has(p.page_id) ||
     p.zone?.trim().toLowerCase()===regionName.toLowerCase());
 }else{
   const roots=all.filter(p=>p.name.toLowerCase()===name.toLowerCase() &&
     (zone===null||p.zone?.toLowerCase()===zone.toLowerCase()));
   if(new Set(roots.map(r=>(r.zone||'').toLowerCase())).size>1)
     throw Error('Ambiguous settlement: supply --zone');
   const origin=roots[0];
   if(!origin)throw Error('No source settlement/zone with name '+name);
   const settlementZone=origin.zone?.toLowerCase()??null;
   const names=new Set([name.toLowerCase()]);let changed=true;
   while(changed){
     changed=false;
     for(const p of all){
       if(settlementZone && p.zone?.toLowerCase()!==settlementZone)continue;
       if(names.has(p.settlement?.toLowerCase())||names.has(p.city?.toLowerCase())){
         if(!names.has(p.name.toLowerCase()) && p.place_type==='Transportation'){
           names.add(p.name.toLowerCase());changed=true;
         }
       }
     }
   }
   included=all.filter(p=>{
     if(p.name.toLowerCase()===name.toLowerCase())
       return settlementZone===null||p.zone?.toLowerCase()===settlementZone;
     if(settlementZone && p.zone?.toLowerCase()!==settlementZone)return false;
     return names.has(p.settlement?.toLowerCase())||names.has(p.city?.toLowerCase());
   });
 }
 included.sort((a,b)=>a.page_id-b.page_id);
 if(included.length===0)throw Error('No source places match scope '+(wholeZone??name));
 const allPlacesByName=new Map();
 for(const q of included){const n=q.name.toLowerCase();if(!allPlacesByName.has(n))allPlacesByName.set(n,q);}
 const haveWorlds=db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='map_worlds'").get();
 const haveLocations=db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='map_locations'").get();
 const mainWorld=wholeZone??zone??all.find(q=>q.name.toLowerCase()===name?.toLowerCase())?.zone;
 let markers=[];
 if(haveWorlds&&haveLocations&&mainWorld){
   const w=db.prepare("SELECT world_id FROM map_worlds WHERE LOWER(display_name)=LOWER(?) ORDER BY world_id LIMIT 1").get(mainWorld);
   if(w)markers=db.prepare("SELECT location_id,name,wiki_page,x,y FROM map_locations WHERE world_id=? AND x BETWEEN 0 AND 1000000 AND y BETWEEN 0 AND 1000000").all(w.world_id);
 }
 const norm=v=>String(v||'').toLowerCase().trim().replace(/^online:/,'').replace(/_/g,' ').replace(/\s+/g,' ');
 const byMarker=new Map();
 for(const m of markers)for(const key of [m.wiki_page,m.name]){const n=norm(key);if(n&&!byMarker.has(n))byMarker.set(n,m);}
 function locate(q,visited=new Set()){
   if(visited.has(q.page_id))return null;
   visited.add(q.page_id);
   const exact=byMarker.get(norm(q.title))||byMarker.get(norm(q.name));
   if(exact)return {x:exact.x,y:exact.y,evidence:visited.size===1?'source_map_marker':'estimated_parent_settlement_marker'};
   for(const name of [q.settlement,q.city]){
     const parent=allPlacesByName.get(String(name||'').toLowerCase());
     if(parent){const point=locate(parent,visited);if(point)return {...point,evidence:'estimated_parent_settlement_marker'};}
   }
   return null;
 }
 function district(q,visited=new Set()){
   if(visited.has(q.page_id))return null;
   visited.add(q.page_id);
   if(q.place_type==='City'||q.place_type==='Town')return q.name;
   for(const name of [q.settlement,q.city]){
     const parent=allPlacesByName.get(String(name||'').toLowerCase());
     if(parent){const d=district(parent,visited);if(d)return d;}
   }
   return null;
 }
 const placeIds=new Set(included.map(p=>p.page_id));
 const pageIdList=included.map(p=>p.page_id).join(',');
 const links=db.prepare('SELECT npc_page_id,npc_name,place_page_id,relation,resolution FROM world_npc_place_links WHERE place_page_id IN ('+pageIdList+') ORDER BY npc_page_id,place_page_id,relation').all();
 const profiles=db.prepare('SELECT page_id,profile_id,name,race,eligible,appearance_count,profile_json FROM standard_npc_profiles_v1 WHERE page_id IN ('+
  unique(links.map(l=>l.npc_page_id)).join(',')+')').all();
 const valid=profiles.filter(p=>p.eligible===1 && p.appearance_count===1);
 const knownIds=new Set(valid.map(p=>p.page_id));
 const mapped=links.filter(l=>knownIds.has(l.npc_page_id));
 const byNpc=new Map();
 for(const l of mapped){const a=byNpc.get(l.npc_page_id)||[];a.push(l);byNpc.set(l.npc_page_id,a);}
 const roleByPage=new Map(included.map(p=>[p.page_id,role(p)]));
 const placeLabel=id=>'uesp:place:'+id;
 const resultPlaces=included.map(p=>({
  id:placeLabel(p.page_id),sourcePageId:p.page_id,name:p.name,
  sourceType:p.place_type??null,settlement:p.settlement??null,zone:p.zone??null,
  role:roleByPage.get(p.page_id),
  sourceUrl:'https://en.uesp.net/wiki/'+p.title.replaceAll(' ','_'),
  mapX:locate(p)?.x??null,mapY:locate(p)?.y??null,
  mapEvidence:locate(p)?.evidence??'unmapped',district:district(p)
 }));
 const businessKinds=new Set(['food_retail','goods_retail','workshop','import_hub','institution','food_producer','resource_producer']);
 const businesses=included.filter(p=>businessKinds.has(roleByPage.get(p.page_id))).map(p=>({
  placeId:placeLabel(p.page_id),kind:roleByPage.get(p.page_id),
  ownerNpcId:null, // Evidence does not establish legal ownership.
  operatorNpcIds:unique(mapped.filter(l=>l.place_page_id===p.page_id && l.relation==='npc_store')
     .map(l=>'npc:uesp-online:'+l.npc_page_id)),
  evidence:'UESP place type and npc_store links; finances and inventory are simulation defaults'
 }));
 // Real ESO workplaces remain as documented. Additional agriculture and
 // provision stalls are hypotheses, not an amenity automatically given to
 // every population centre. Availability now follows sparse geographic
 // evidence/proxies; most settlements must obtain food from elsewhere.
 if(wholeZone){
   const sourceSites=[...resultPlaces];
   const distance=(a,b)=>a.mapX===null||b.mapX===null?Infinity:
     Math.hypot(a.mapX-b.mapX,a.mapY-b.mapY);
   const anchorPriority={npc_house:0,npc_store:1,npc_ship:2,npc_loc:3,
     npc_settlement:4,npc_city:5};
   const counts=new Map();
   for(const sourceLinks of byNpc.values()){
     const candidates=sourceLinks.filter(l=>l.relation!=='npc_city' ||
       !sourceLinks.some(q=>q.relation!=='npc_city'&&q.relation!=='npc_settlement'));
     candidates.sort((a,b)=>(anchorPriority[a.relation]??10)-
       (anchorPriority[b.relation]??10)||a.place_page_id-b.place_page_id);
     const anchor=candidates.length ? resultPlaces.find(p=>
       p.sourcePageId===candidates[0].place_page_id) : null;
     if(anchor?.mapX===null||!anchor)continue;
     const key=anchor.mapX+','+anchor.mapY;
     counts.set(key,(counts.get(key)||0)+1);
   }
   const populationNear=p=>[...counts].reduce((sum,[key,n])=>{
     const [x,y]=key.split(',').map(Number);
     return sum+(Math.hypot(x-p.mapX,y-p.mapY)<35000?n:0);
   },0);
   const farmEvidence=p=>{
     const name=p.name.toLowerCase(),type=(p.sourceType||'').toLowerCase();
     if(/mine|ruin|cave|delve|camp|dungeon|lighthouse|tomb|keep|fort|castle|crypt|tunnel/.test(type))
       return -1;
     if(p.role==='food_producer')return 100;
     if(/farm|plantation|orchard|vineyard|field/.test(name))return 75;
     if(/grove|vale|greenwater|meadow|garden|cove/.test(name))return 50;
     if(type==='grove'||type==='town')return 25;
     return -1;
   };
   const farms=sourceSites.filter(p=>p.mapX!==null&&farmEvidence(p)>0)
     .map(p=>({place:p,score:farmEvidence(p)}))
     .sort((a,b)=>b.score-a.score||a.place.id.localeCompare(b.place.id));
   const selected=[];
   for(const {place} of farms){
     if(selected.length>=7)break;
     if(selected.some(p=>distance(p,place)<110000))continue;
     if(sourceSites.some(p=>p.role==='food_producer'&&distance(p,place)<110000))
       continue;
     selected.push(place);
   }
   for(const anchor of selected){
     const suffix=anchor.id.replace(/[^a-z0-9]+/gi,'-');
     const farmId='sim:growing-grounds:'+suffix;
     const district=anchor.district||anchor.name;
     resultPlaces.push({id:farmId,sourcePageId:0,
       name:'Simulated growing grounds near '+anchor.name,
       sourceType:'simulation_inferred_agriculture',settlement:anchor.name,
       zone:wholeZone,role:'food_producer',sourceUrl:'',
       mapX:anchor.mapX,mapY:anchor.mapY,
       mapEvidence:'simulation_at_source_anchor',district});
     businesses.push({placeId:farmId,kind:'food_producer',ownerNpcId:null,
       operatorNpcIds:[],evidence:'SIMULATION HYPOTHESIS based on geographic/name hints, NOT confirmed ESO farmland'});
   }
   // Only a few unserved, populous settlements warrant a hypothetical food
   // market. Ruins and every roadside landmark do not receive one by default.
   const settlements=sourceSites.filter(p=>p.mapX!==null &&
       ['Town','City'].includes(p.sourceType))
     .map(p=>({place:p,pop:populationNear(p)}))
     .filter(x=>x.pop>=12)
     .sort((a,b)=>b.pop-a.pop||a.place.id.localeCompare(b.place.id));
   let addedMarkets=0;
   for(const {place,pop} of settlements){
     if(addedMarkets>=3)break;
     if(businesses.some(b=>b.kind==='food_retail'&&
       resultPlaces.some(p=>p.id===b.placeId&&distance(p,place)<65000)))continue;
     const suffix=place.id.replace(/[^a-z0-9]+/gi,'-');
     const id='sim:food-market:'+suffix;
     resultPlaces.push({id,sourcePageId:0,
       name:'Simulated provisions near '+place.name,
       sourceType:'simulation_inferred_market',settlement:place.name,
       zone:wholeZone,role:'food_retail',sourceUrl:'',
       mapX:place.mapX,mapY:place.mapY,
       mapEvidence:'simulation_at_source_anchor',district:place.district||place.name});
     businesses.push({placeId:id,kind:'food_retail',ownerNpcId:null,
       operatorNpcIds:[],evidence:'SIMULATION HYPOTHESIS for an underserved population centre, NOT confirmed ESO market'});
     addedMarkets++;
   }
 }
 const businessIds=new Set(businesses.map(b=>b.placeId));
 const residents=valid.map(row=>{
  const data=JSON.parse(row.profile_json);
  const sourceLinks=byNpc.get(row.page_id)||[];
  // Known home only if explicitly described as a house, never infer residence
  // from somebody merely standing inside a tavern or shop.
  const houses=[...new Map(sourceLinks.filter(l=>l.relation==='npc_house' &&
    roleByPage.get(l.place_page_id)==='dwelling').map(l=>[l.place_page_id,l])).values()];
  const stores=[...new Map(sourceLinks.filter(l=>l.relation==='npc_store' &&
    businessIds.has(placeLabel(l.place_page_id))).map(l=>[l.place_page_id,l])).values()];
  const profession=data.society?.occupation||data.society?.serviceRole||null;
  const presences=[...new Map(sourceLinks.filter(l=>l.relation==='npc_loc' &&
    businessIds.has(placeLabel(l.place_page_id))).map(l=>[l.place_page_id,l])).values()];
  let workplace=null,evidence='unknown';
  if(stores.length===1){workplace=stores[0].place_page_id;evidence='explicit_npc_store';}
  else if(stores.length>1){evidence='ambiguous_multiple_source_stores';}
  else if(stores.length===0 && profession && presences.length===1){
   workplace=presences[0].place_page_id;evidence='inferred_profession_and_location';
  }
  const anchorCandidates=[...sourceLinks].filter(l=>l.relation!=='npc_city' ||
    !sourceLinks.some(q=>q.relation!=='npc_city'&&q.relation!=='npc_settlement'));
  const order={npc_house:0,npc_store:1,npc_ship:2,npc_loc:3,npc_settlement:4,npc_city:5};
  anchorCandidates.sort((a,b)=>(order[a.relation]??10)-(order[b.relation]??10)||a.place_page_id-b.place_page_id);
  const anchor=anchorCandidates.find(l=>l.place_page_id!==null)??null;
  return {
   id:row.profile_id,sourcePageId:row.page_id,name:data.identity?.name||row.name,
   race:data.identity?.race||null,profession,
   homePlaceId:houses.length===1?placeLabel(houses[0].place_page_id):null,
   homeEvidence:houses.length===1?'explicit_npc_house':'unknown',
   workPlaceId:workplace===null?null:placeLabel(workplace),
   workEvidence:evidence,
   // Preserve location evidence separately from actual employment.
   locationPlaceIds:unique(sourceLinks.filter(l=>l.relation!=='npc_city')
       .map(l=>placeLabel(l.place_page_id))),
   sourceUrl:data.source?.wikiUrl||null,
   anchorPlaceId:anchor?placeLabel(anchor.place_page_id):null,
   anchorEvidence:anchor?'associated_source_place_not_home':'unmapped'
  };
 }).sort((a,b)=>a.sourcePageId-b.sourcePageId);
 const exportData={schemaVersion:1,settlement:wholeZone??name,source:'UESP Online world_places, world_zone_memberships, world_npc_place_links, standard_npc_profiles_v1',
   counts:{places:resultPlaces.length,businesses:businesses.length,
      linkedCharacters:unique(links.map(l=>l.npc_page_id)).length,
      eligibleSingleAppearanceCharacters:residents.length,
      explicitlyEmployed:residents.filter(p=>p.workEvidence==='explicit_npc_store').length,
      inferredEmployed:residents.filter(p=>p.workEvidence==='inferred_profession_and_location').length,
      explicitHomes:residents.filter(p=>p.homeEvidence==='explicit_npc_house').length,
      placesWithExactMapMarkers:resultPlaces.filter(p=>p.mapEvidence==='source_map_marker').length,
      placesWithEstimatedMapMarkers:resultPlaces.filter(p=>p.mapEvidence==='estimated_parent_settlement_marker').length,
      residentsWithMapAnchor:residents.filter(r=>resultPlaces.some(p=>p.id===r.anchorPlaceId&&p.mapX!==null)).length,
      sourcePlaces:resultPlaces.filter(p=>p.sourcePageId>0).length,
      simulatedProductionPlaces:resultPlaces.filter(p=>p.sourcePageId===0).length},
   places:resultPlaces,businesses,residents,scope:wholeZone?'whole_zone':'settlement'};
 fs.mkdirSync(path.dirname(out),{recursive:true});
 fs.writeFileSync(out,JSON.stringify(exportData,null,2)+'\n');
 console.log(JSON.stringify({file:out,settlement:wholeZone??name,scope:wholeZone?'whole_zone':'settlement',counts:exportData.counts,
  places:resultPlaces.filter(p=>p.role!=='landmark').map(p=>({name:p.name,role:p.role})).slice(0,45)},null,2));
}finally{db.close();}
