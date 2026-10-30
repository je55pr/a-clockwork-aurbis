'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const p=require('./profile'),{profileFor,options}=require('./build');
const places=new Map([
  ['online:auridon',{title:'Online:Auridon',pageId:101}],
  ['online:vulkhel guard',{title:'Online:Vulkhel Guard',pageId:102}],
  ["online:mages guild (vulkhel guard)",{title:"Online:Mages Guild (Vulkhel Guard)",pageId:103}]
]);
const pages=new Map([...places,['online:the quest',{title:'Online:The Quest',pageId:104}]]);
test('raw values do not become fabricated normalized values',()=>{
  assert.equal(p.plain('{{ESO Health|fa}}'),null);
  assert.equal(p.plain('Varies'),null);
  assert.equal(p.plain('[[ON:Vulkhel Guard|the city]]'),'the city');
  assert.equal(p.nullableInteger('12,345'),12345);
  assert.equal(p.nullableInteger('{{ESO Health|fa}}'),null);
});
test('conflicting source summaries preserve ambiguity',()=>{
  assert.deepEqual(p.chooseConsensus([{fields:{gender:'Female'}},{fields:{gender:'Male'}}],'gender'),
    {value:null,conflict:['Female','Male']});
});
test('more specific source location wins over city without dropping evidence',()=>{
  const result=p.locationsFrom([{number:1,fields:{loc:'[[ON:Mages Guild (Vulkhel Guard)|Mages Guild]]',city:'Vulkhel Guard'}}],places);
  assert.equal(result.primary.placeId,103);
  assert.equal(result.candidates.length,2);
  assert.equal(result.ambiguous,false);
});
test('multiple places at same specificity cannot become a fixed home',()=>{
  const result=p.locationsFrom([{number:1,fields:{loc:'[[ON:Auridon]] or [[ON:Vulkhel Guard]]'}}],places);
  assert.equal(result.primary,null);
  assert.equal(result.ambiguous,true);
});
test('a source-backed NPC has stable ID, nullable simulated facts, and explicit lore link',()=>{
  const record={page_id:77,name:'Eshaba',wiki_title:'Online:Eshaba',revision_id:300,revision_timestamp:'2026-10-25'};
  const variants=[{number:1,fields:{race:'Khajiit',gender:'Female',city:'Vulkhel Guard',
    health:'{{ESO Health|fa}}',reaction:'Friendly',sells:'Merchant',lorepage:'Eshaba'}}];
  const lore=new Map([['lore:eshaba',{pageId:201,title:'Lore:Eshaba'}],
    ['lore:khajiit',{pageId:202,title:'Lore:Khajiit',redirectTitle:null}]]);
  const a=profileFor(record,variants,[{quest_label:'The Quest'}],pages,places,lore,new Set());
  const b=profileFor(record,variants,[{quest_label:'The Quest'}],pages,places,lore,new Set());
  assert.equal(a.id,'npc:uesp-online:77');
  assert.equal(a.selection.hash,b.selection.hash);
  assert.equal(a.locations.primary.placeId,102);
  assert.equal(a.combat.health,null);
  assert.equal(a.combat.healthRaw,'{{ESO Health|fa}}');
  assert.equal(a.references.quests[0].pageId,104);
  assert.equal(a.references.lore.pageId,201);
  assert.equal(a.references.race.eso,null);
  assert.equal(a.references.race.lore.pageId,202);
  assert.equal(a.simulation.inventory,null);
  assert.equal(a.selection.eligible,true);
});
test('generic and multi-appearance records stay in exports but are ineligible',()=>{
  const record={page_id:88,name:'Guard',wiki_title:'Online:Guard',revision_id:1,revision_timestamp:null};
  const variants=[
    {number:1,fields:{race:'Nord',gender:'Male',city:'Auridon',generic:'1'}},
    {number:2,fields:{race:'Nord',gender:'Female',city:'Vulkhel Guard',generic:'1'}}
  ];
  const result=profileFor(record,variants,[],pages,places,new Map(),new Set());
  assert.equal(result.selection.eligible,false);
  assert.equal(result.identity.gender,null);
  assert.equal(result.locations.primary,null);
  assert.deepEqual(result.conflicts.gender,['Male','Female']);
  assert.equal(result.appearances.length,2);
});
test('CLI rejects invalid arguments',()=>{
  assert.throws(()=>options(['--count','0']),/positive integer/);
  assert.throws(()=>options(['--wat']),/Unknown argument/);
});
test('health parser rejects malformed comma groups and unsafe integers',()=>{
 for(const text of ['1,2','5,','10,,5','12,34','001','1,0000','999999999999999999999','1.5','1-3'])
   assert.equal(p.nullableInteger(text),null,'Should reject '+text);
 assert.equal(p.nullableInteger('10,005'),10005);
 assert.equal(p.nullableInteger('1,234,567'),1234567);
 assert.equal(p.nullableInteger('1234567'),1234567);
 assert.equal(p.nullableInteger('0'),0);
});
test('unresolved alternative keeps location ambiguous rather than assigning fixed home',()=>{
 const result=p.locationsFrom([{number:1,fields:{loc:'[[ON:Auridon]] or [[ON:Nowhere]]',city:'Vulkhel Guard'}}],places);
 assert.equal(result.primary,null);
 assert.equal(result.ambiguous,true);
 assert.equal(result.candidates.length,3);
 assert.equal(result.candidates.find(c=>c.name==='Nowhere').resolution,'unresolved');
});
