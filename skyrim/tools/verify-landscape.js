const fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'../generated');
const data=JSON.parse(fs.readFileSync(path.join(root,'riverwood.json'),'utf8'));
const stats=JSON.parse(fs.readFileSync(path.join(root,'landscape','manifest.json'),'utf8'));
const textures=data.landscapeTextures;
if(data.terrain.length!==25||stats.tiles!==25||stats.missing!==0)throw Error('LAND extraction/PNG count mismatch');
let layers=0,unresolved=0;
for(const cell of data.terrain){
 if(cell.base.length!==4)throw Error('Missing BTXT quadrants '+cell.x+','+cell.y);
 for(const t of cell.base.concat(cell.alpha))if(t.texture&&!textures[t.texture])unresolved++;
 for(const [i,t] of cell.alpha.entries()){
  if(t.layer<0||t.layer>255||t.quadrant>3)throw Error('Invalid alpha layer '+i);
  for(const [index,opacity] of t.weights)if(index<0||index>=289||opacity<0||opacity>1)throw Error('VTXT weight out of bounds');
 }
 layers+=cell.alpha.length;
 const p=path.join(root,'landscape','cell_'+cell.x+'_'+cell.y+'.png');
 const b=fs.readFileSync(p);
 if(b.subarray(0,8).toString('hex')!=='89504e470d0a1a0a'||b.readUInt32BE(16)!==1024||b.readUInt32BE(20)!==1024)throw Error('Invalid 1024px texture '+p);
 if(b.length<10_000)throw Error('Suspicious small source texture '+p);
}
if(unresolved||layers!==stats.overlays||stats.decoded<10)throw Error('Landscape layer provenance mismatch '+JSON.stringify({unresolved,layers,stats}));
console.log('LAND material check PASSED:',data.terrain.length,'source terrain tiles;',layers,'alpha layers;',Object.keys(textures).length,'LTEX definitions;',stats.decoded,'unique source DDS files; 25 valid 1024px atlases.');
