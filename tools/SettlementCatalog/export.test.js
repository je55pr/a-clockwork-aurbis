'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {DatabaseSync}=require('node:sqlite'),{execFileSync}=require('node:child_process');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'aurbis-place-test-'));
try{
 const dbPath=path.join(root,'test.db'),out=path.join(root,'catalog.json');
 const db=new DatabaseSync(dbPath);
 db.exec('CREATE TABLE world_places(page_id INTEGER,name TEXT,place_type TEXT,zone TEXT,settlement TEXT,city TEXT,subzone TEXT,title TEXT);'+
 'CREATE TABLE world_npc_place_links(npc_page_id INTEGER,npc_name TEXT,place_page_id INTEGER,relation TEXT,resolution TEXT);'+
 'CREATE TABLE standard_npc_profiles_v1(page_id INTEGER,profile_id TEXT,name TEXT,race TEXT,eligible INTEGER,appearance_count INTEGER,profile_json TEXT);');
 const place=db.prepare('INSERT INTO world_places VALUES(?,?,?,?,?,?,?,?)');
 place.run(1,'Exampleton','City','The Example Isles',null,null,null,'Online:Exampleton');
 place.run(2,'Exampleton Docks','Transportation','The Example Isles','Exampleton',null,null,'Online:Exampleton Docks');
 place.run(3,'Ye Olde Tavern','Inn','The Example Isles','Exampleton',null,null,'Online:Ye Olde Tavern');
 place.run(4,'Merchant Cottage','Home','The Example Isles','Exampleton',null,null,'Online:Merchant Cottage');
 place.run(5,'Waterfront Market','Marketplace','The Example Isles','Exampleton Docks',null,null,'Online:Waterfront Market');
 place.run(6,'Unrelated Market','Marketplace','Other Island','Elsewhere',null,null,'Online:Unrelated Market');
 place.run(7,'Exampleton','City','Other Island',null,null,null,'Online:Exampleton (Elsewhere)');
 const link=db.prepare('INSERT INTO world_npc_place_links VALUES(?,?,?,?,?)');
 link.run(100,'Pina',1,'npc_city','exact-title');
 link.run(100,'Pina',3,'npc_store','exact-title');
 link.run(100,'Pina',4,'npc_house','exact-title');
 link.run(101,'Eli',5,'npc_loc','exact-title');
 link.run(102,'Duplicate',3,'npc_store','exact-title');
 const profile=db.prepare('INSERT INTO standard_npc_profiles_v1 VALUES(?,?,?,?,?,?,?)');
 const source=(id,name,role)=>JSON.stringify({id:'npc:uesp-online:'+id,
     identity:{name,race:'Altmer'},
     society:{occupation:null,serviceRole:role},
     source:{wikiUrl:'https://en.uesp.net/wiki/Online:'+name}});
 profile.run(100,'npc:uesp-online:100','Pina','Altmer',1,1,source(100,'Pina','Innkeeper'));
 profile.run(101,'npc:uesp-online:101','Eli','Altmer',1,1,source(101,'Eli','Trader'));
 profile.run(102,'npc:uesp-online:102','Duplicate','Altmer',0,2,source(102,'Duplicate','Chef'));
 db.close();
 execFileSync(process.execPath,[path.join(__dirname,'export.js'),
   '--db',dbPath,'--settlement','Exampleton','--zone','The Example Isles',
   '--out',out],{encoding:'utf8'});
 assert.throws(()=>execFileSync(process.execPath,[path.join(__dirname,'export.js'),
   '--db',dbPath,'--settlement','Exampleton','--out',out],
   {encoding:'utf8',stdio:'pipe'}),/Ambiguous settlement/,
   'same-named source settlements require explicit zone');
 const result=JSON.parse(fs.readFileSync(out,'utf8'));
 assert.equal(result.settlement,'Exampleton');
 assert.equal(result.counts.places,5,'selected settlement expands into dock subarea');
 assert.equal(result.counts.businesses,3,'tavern, dock and market classified');
 assert.equal(result.counts.linkedCharacters,3);
 assert.equal(result.counts.eligibleSingleAppearanceCharacters,2);
 const pina=result.residents.find(x=>x.name==='Pina');
 assert.equal(pina.homePlaceId,'uesp:place:4','explicit home preserved');
 assert.equal(pina.workPlaceId,'uesp:place:3','explicit shop preserved');
 assert.equal(pina.workEvidence,'explicit_npc_store');
 assert.equal(result.businesses.find(x=>x.placeId==='uesp:place:3').ownerNpcId,
   null,'shopkeeper is NOT automatically a known legal owner');
 assert.deepEqual(result.businesses.find(x=>x.placeId==='uesp:place:3').operatorNpcIds,
   ['npc:uesp-online:100']);
 assert.ok(result.places.every(x=>x.sourceUrl.startsWith('https://en.uesp.net/wiki/')));
 assert.equal(result.places.some(x=>x.name==='Unrelated Market'),false);
 assert.equal(result.residents.some(x=>x.name==='Duplicate'),false,
   'multiple-appearance ineligible NPC excluded');
 assert.equal(result.residents.find(x=>x.name==='Eli').homePlaceId,null,
   'a person visiting a market is not automatically a homeowner');
 console.log('PASS settlement catalog: arbitrary zone, real place links, homes, worker evidence, unknown owners, single-appearance exclusions');
}finally{fs.rmSync(root,{recursive:true,force:true});}
