'use strict';
// Repeatable whole-zone, headless reference benchmark. Does not install
// dependencies or require Godot/ESO asset archives. Raw data stays ignored.
const fs=require('node:fs'),path=require('node:path');
const {execFileSync}=require('node:child_process');

const args=process.argv.slice(2);
function param(key,fallback=null){
 const i=args.indexOf('--'+key);
 if(i===-1)return fallback;
 if(!args[i+1]||args[i+1].startsWith('--'))throw Error('Missing --'+key+' argument');
 return args[i+1];
}
if(args.includes('--help')){
 console.log('node tools/SettlementCatalog/benchmark.js --db PATH --zone Auridon --days 3,30,90 --out-dir PATH [--seed 42] [--economy production|prototype] [--scenario normal|no-foreign|crop-failure|no-assistance]');
 process.exit(0);
}
const dbPath=param('db'),zone=param('zone');
if(!dbPath||!zone)throw Error('Required: --db path to local reference SQLite and --zone name');
const days=param('days','3,30').split(',').map(Number);
if(!days.length||days.some(n=>!Number.isInteger(n)||n<0||n>365))
 throw Error('Days must be comma-separated integers in 0..365');
const seed=param('seed','42');
if(!Number.isInteger(Number(seed)))throw Error('Seed must be an integer');
const root=path.resolve(__dirname,'../..');
const folder=param('out-dir',path.join(root,'.research','benchmarks'));
const scenario=param('scenario','normal');
if(!['normal','no-foreign','crop-failure','no-assistance'].includes(scenario))throw Error('Unknown benchmark scenario');
const economicMode=param('economy','production');
if(!['prototype','production'].includes(economicMode))throw Error('Expected economy prototype|production');
const filename=zone.toLowerCase().normalize('NFKD').replace(/[^a-z0-9]+/g,'-').replace(/^-|-$/g,'')+(scenario==='normal'?'':'-'+scenario);
fs.mkdirSync(folder,{recursive:true});
const catalogPath=path.join(folder,filename+'-catalog.json');
const exporter=path.join(__dirname,'export.js');
execFileSync(process.execPath,[exporter,'--db',dbPath,'--whole-zone',zone,'--out',catalogPath],{
 cwd:root,stdio:['ignore','pipe','inherit'],encoding:'utf8'});
const catalog=JSON.parse(fs.readFileSync(catalogPath,'utf8'));
const project=path.join(root,'tools','SettlementRunner','SettlementRunner.csproj');
execFileSync('dotnet',['build',project,'--configuration','Release','--nologo'],{
 cwd:root,stdio:['ignore','pipe','inherit'],encoding:'utf8'});
const placeMap=new Map(catalog.places.map(p=>[p.id,p]));
const businessMap=new Map(catalog.businesses.map(b=>[b.placeId,b]));
const perArea=new Map();
function area(name){const n=name||'Wilderness / unresolved area';if(!perArea.has(n))perArea.set(n,{
 name:n,residents:0,businesses:0,knownHomes:0,knownJobs:0,mapAnchoredResidents:0
 });return perArea.get(n);}
for(const b of catalog.businesses)area(placeMap.get(b.placeId)?.district).businesses++;
for(const npc of catalog.residents){
 const place=placeMap.get(npc.anchorPlaceId);
 const group=area(place?.district);
 group.residents++;
 if(npc.homePlaceId!==null)group.knownHomes++;
 if(npc.workPlaceId!==null)group.knownJobs++;
 if(place?.mapX!==null&&place?.mapX!==undefined)group.mapAnchoredResidents++;
}
const areaSummary=[...perArea.values()].sort((a,b)=>b.residents-a.residents||a.name.localeCompare(b.name));
const results=[];
for(const n of days){
 const scenarioFlags=scenario==='no-foreign'?['--no-foreign-trade']:
   scenario==='crop-failure'?['--no-local-production']:
   scenario==='no-assistance'?['--no-assistance']:[];
 const begin=process.hrtime.bigint();
 const text=execFileSync('dotnet',['run','--no-build','-c','Release','--project',project,'--',
   '--catalog',catalogPath,'--days',String(n),'--seed',String(seed),'--economy',economicMode,
   ...scenarioFlags,'--details'],{
   cwd:root,stdio:['ignore','pipe','inherit'],encoding:'utf8',maxBuffer:20*1024*1024});
 const wallMs=Number(process.hrtime.bigint()-begin)/1e6;
 const parsed=JSON.parse(text);
 if(!parsed.GoldConserved||!parsed.FoodLedgerValid||!parsed.MaterialLedgerValid)
   throw Error('Economic invariants failed for '+n+' days');
 const output=path.join(folder,filename+'-'+n+'d.json');
 fs.writeFileSync(output,JSON.stringify(parsed,null,2)+'\n');
 results.push({days:n,events:parsed.Events,simulationMs:parsed.SimulationMs,
   eventsPerSimulationSecond:parsed.SimulationMs>0?
     Math.round(parsed.Events*1000/parsed.SimulationMs):null,
   wallMs:Math.round(wallMs*100)/100,
   trips:parsed.Travel,longJourneys:parsed.LongJourneys,
   travelMinutes:parsed.TravelMinutes,wages:parsed.Wages,
   lowSatiation:parsed.ResidentsWithLowSatiation,
   personHoursBelow20:parsed.PersonHoursBelow20Satiation,
   personHoursAtZero:parsed.PersonHoursAtZeroSatiation,
   zeroSatiation:parsed.ResidentsWithZeroSatiation,
   noFood:parsed.ResidentsWithoutFood,
   noGold:parsed.ResidentsWithoutGold,
   foodProduced:parsed.FoodProducedLocally,foodGathered:parsed.FoodHarvestedForSubsistence,
   materialProduced:parsed.RawMaterialsExtracted,foodExported:parsed.FoodExported,
   goodsExported:parsed.GoodsExported,foodImported:parsed.FoodImported,
   assistanceGold:parsed.AssistanceGoldPaid,assistedResidents:parsed.AssistedResidents,
   simulatedContracts:parsed.SimulatedContracts,
   fiscalGold:parsed.PublicGold,foreignGold:parsed.ExternalGold,
   exportGold:parsed.GoldReceivedFromAbroad,importGold:parsed.GoldPaidAbroad,
   unpaidHours:parsed.UnpaidHours,foodSales:parsed.RetailFoodSales,
   goodsSales:parsed.RetailGoodsSales,craftedGoods:parsed.CraftedGoods,
   goldConserved:parsed.GoldConserved,
   foodConserved:parsed.FoodLedgerValid,
   materialsConserved:parsed.MaterialLedgerValid,
   file:output});
 console.log(n+' day(s): '+parsed.Events.toLocaleString()+' events, '+
   parsed.SimulationMs+' ms simulated, '+parsed.LongJourneys.toLocaleString()+
   ' long trips, ledgers valid');
}
const summary={
 schemaVersion:2,zone,economy:economicMode,scenario,sourceEvidence:catalog.source,
 coverage:catalog.counts,areaSummary,benchmarks:results,
 limitations:[
 'UESP map points are estimates for business/neighbourhood travel, not road distances',
 'Source NPC shop links establish association, not legal ownership',
 'Unlinked homes and work are unknown, not invented source facts',
 'Synthetic agricultural co-ops, food markets, trade demand, welfare and job contracts are assumptions, not UESP canon',
 'Production requires labour and bounded renewable land or mineral reserves; external stocks and gold are finite',
 'Imports and exports explicitly transfer stock and gold to a foreign account, not magic inventory',
 'Needs, prices, generic goods, wages and travel speed remain simulation assumptions',
 'Only eligible single-appearance profiles are simulated, not all source-linked NPCs',
 'Wholesale transfers are currently same-day abstractions without cargo travel',
 'No 3D simulation or Godot scene is loaded'
 ]};
const summaryPath=path.join(folder,filename+'-summary.json');
fs.writeFileSync(summaryPath,JSON.stringify(summary,null,2)+'\n');
console.log(JSON.stringify({zone,coverage:catalog.counts,
 areas:areaSummary.slice(0,15),benchmarks:results.map(x=>({days:x.days,
 events:x.events,simulationMs:x.simulationMs,tripMinutes:x.travelMinutes,
 longTrips:x.longJourneys})),summaryPath},null,2));
