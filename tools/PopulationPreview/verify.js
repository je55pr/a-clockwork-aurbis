'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const data=JSON.parse(fs.readFileSync(process.argv[2]||path.resolve(__dirname,'../../.research/auridon-population.json'),'utf8'));
assert.equal(data.schemaVersion,1);
assert.equal(data.population,data.people.length);
const town=data.people.filter(p=>p.town);
for(const person of data.people){
 assert.ok(person.details && person.details.wikiTitle && person.details.sourcePage,
   "missing linked UESP page provenance for "+person.id);
 assert.ok(person.details.summaryVariants?.length>=1,
   "missing original infobox data for "+person.id);
 assert.equal(typeof person.details.articleWikitext,"string",
   "missing full source article for "+person.id);
 assert.ok(person.details.sourceFields && typeof person.details.sourceFields==="object",
   "missing source field dictionary for "+person.id);
}
assert.equal(new Set(data.people.map(p=>p.id)).size,data.people.length,'no duplicate NPC profile IDs');
assert.equal(town.length,data.coverage.vulkhelTownNpcCount);
assert.equal(data.population,893,'expected island-wide source query coverage');
assert.equal(town.length,254,'expected town source coverage');
const city=town.filter(p=>p.town.kind==='estimated_city_district');
assert.ok(city.length>=100,'need the broad Vulkhel Guard district group');
assert.equal(new Set(city.map(p=>p.town.x+','+p.town.y)).size,city.length,
  'city residents must not share one default anchor');
const explicit=town.filter(p=>p.town.kind==='uesp_map_marker_alias');
assert.ok(explicit.length>=8,'must resolve explicit Vulkhel Docks source links to Vulkhel Guard Docks marker');
for(const p of explicit){
 assert.equal(p.town.place,'Vulkhel Guard Docks');
 assert.equal(p.town.markerId,6119,'Vulkhel Guard Docks original UESP map marker');
}
const inferred=town.filter(p=>p.town.kind==='inferred_profession');
assert.ok(inferred.length>=4,'dock-related occupations need provisional dock anchors');
for(const p of inferred)assert.match(p.town.place,/Vulkhel Guard Docks/);
for(const name of ['Minthelir','Calenethel','Belane','Rodaeril','Abelaya','Qusan','Lanullin','Ohirros']){
 const p=town.find(p=>p.name===name);
 assert.ok(p&&p.town.kind==='uesp_map_marker_alias',name+' needs an explicit docks link');
}
for(const name of ['Dock Rascal','Sugar-Claws','Vulkhel Wharfrat','Sailor (Auridon)']){
 const p=town.find(p=>p.name===name);
 assert.ok(p&&p.town.kind==='inferred_profession',name+' should follow dock occupation fallback');
}
for(const name of ['Silsailen Defender']){
 const p=town.find(p=>p.name===name);
 assert.ok(p&&p.town.kind!=='inferred_profession',name+' must not match sailor substring');
}
console.log(JSON.stringify({status:'passed',auridon:data.population,vulkhel:town.length,
  uniqueGeneralTownPoints:city.length,explicitDockAnchors:explicit.length,
  professionInferredDockAnchors:inferred.length,
  otherNamedAnchors:town.length-city.length-explicit.length-inferred.length},null,2));
