const fs=require('fs'),path=require('path');
const scene=JSON.parse(fs.readFileSync(path.join(__dirname,'../generated/riverwood.json'),'utf8'));
const renderer=fs.readFileSync(path.join(__dirname,'../scripts/main.gd'),'utf8');
if(!renderer.includes('item.basis = Basis(Vector3.UP, -rz) * Basis(Vector3(0, 0, -1), -ry) * Basis(Vector3.RIGHT, -rx)'))
 throw Error('Rendering transform is no longer using the source-verified Skyrim rotation signs');const fences=scene.objects.filter(x=>/architecture\\farmhouse\\fencewoven02\.nif$/i.test(x.nif||''));
if(fences.length<30)throw Error('Missing Riverwood woven fence source placements');
const acuteUndirected=(a,b)=>{
 const d=a-b;
 return Math.abs(Math.atan2(Math.sin(d*2),Math.cos(d*2))/2)*180/Math.PI;
};
const pairs=[];
for(let i=0;i<fences.length;i++)for(let j=i+1;j<fences.length;j++){
 const a=fences[i],b=fences[j];
 const dx=b.xyz[0]-a.xyz[0],dy=b.xyz[1]-a.xyz[1];
 const distance=Math.hypot(dx,dy);
 if(distance<108||distance>171)continue;
 const angularDifference=Math.abs(Math.atan2(Math.sin(a.rot[2]-b.rot[2]),Math.cos(a.rot[2]-b.rot[2])));
 if(angularDifference>0.15)continue;
 const bearing=Math.atan2(dy,dx);
 pairs.push({
  ids:[a.ref,b.ref],
  correctedError:acuteUndirected(-a.rot[2],bearing),
  previousError:acuteUndirected(a.rot[2],bearing)
 });
}
const mean=k=>pairs.reduce((s,p)=>s+p[k],0)/pairs.length;
const corrected=mean('correctedError'),previous=mean('previousError');
const worst=Math.max(...pairs.map(x=>x.correctedError));
if(pairs.length<35||corrected>4||previous<35||worst>12)
 throw Error('Skyrim to Godot yaw sign regression '+JSON.stringify({pairs:pairs.length,corrected,previous,worst}));
console.log('Riverwood fence rotation PASS:',pairs.length,'nearby source-derived pairs; corrected mean error',corrected.toFixed(2)+'°; previous error',previous.toFixed(2)+'°; worst corrected pair',worst.toFixed(2)+'°');
