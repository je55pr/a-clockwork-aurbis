const fs=require('fs'),path=require('path'),{open}=require('./bsa'),{convert}=require('./nif-to-glb');
const root=path.resolve(__dirname,'../generated');
const data=JSON.parse(fs.readFileSync(path.join(root,'riverwood.json'),'utf8'));
const counts=new Map();
for(const o of data.objects){
 if(!o.nif)continue;
 const nif=('meshes\\'+o.nif).replace(/\//g,'\\').toLowerCase();
 counts.set(nif,(counts.get(nif)||0)+1);
}
const entries=[...counts].sort((a,b)=>{
 const rank=(n)=>/architecture\\farmhouse\\(inn|smith|farmhouse|lumbermill)/i.test(n)?4000:/architecture\\/i.test(n)?2500:/landscape\\trees\\/i.test(n)?1500:/landscape\\rocks\\/i.test(n)?1000:0;
 return rank(b[0])+b[1]-rank(a[0])-a[1];
});
const a=open('Skyrim - Meshes0.bsa'),b=open('Skyrim - Meshes1.bsa');
const asource={get(n){return a.get(n)||b.get(n)}};
const models={},fail=[],stats={attempted:0,converted:0,instances:0,skipped:0,triangleCount:0};
const priority=entries.filter(([n])=>/architecture\\|landscape\\(trees|rocks)\\/i.test(n));
const primary=priority.concat(entries.filter(([n,c])=>c>=6&&!priority.some(x=>x[0]===n)));
console.log('unique source models',entries.length,'priority model candidates',priority.length,'selected',primary.length);
for(const [name,count] of primary){
 if(stats.attempted>=155)break;
 stats.attempted++;
 try{
  const out=convert(asource,name);
  if(out.error){stats.skipped++;fail.push({name,count,...out});continue}
  stats.converted++;stats.instances+=count;stats.triangleCount+=out.tris;
  models[name]={path:'res://generated/models/'+name.replace(/\.nif$/i,'.glb').replace(/\\/g,'/'),instances:count,triangles:out.tris,shapes:out.shapes};
  if(stats.converted%20===0)console.log('CONVERTED',stats.converted,'types',stats.instances,'instances');
 }catch(e){stats.skipped++;fail.push({name,count,error:e.message});console.log('ERROR',name,e.message)}
}
a.close();b.close();
fs.writeFileSync(path.join(root,'model-index.json'),JSON.stringify({models,stats,fail},null,2));
console.log('COMPLETE',JSON.stringify(stats),'top failures',JSON.stringify(fail.slice(0,30)));
