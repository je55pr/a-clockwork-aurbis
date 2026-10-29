const fs=require('fs'),path=require('path');
const data=JSON.parse(fs.readFileSync(path.resolve(__dirname,'../generated/riverwood.json'),'utf8'));
const cells=new Map(data.terrain.map(t=>[t.x+','+t.y,t]));
let xEdges=0,yEdges=0,maxError=0;
for(const cell of data.terrain){
 const east=cells.get((cell.x+1)+','+cell.y);
 if(east)for(let y=0;y<33;y++){
  const diff=Math.abs(cell.heights[y*33+32]-east.heights[y*33]);
  maxError=Math.max(maxError,diff);xEdges++;
 }
 const north=cells.get(cell.x+','+(cell.y+1));
 if(north)for(let x=0;x<33;x++){
  const diff=Math.abs(cell.heights[32*33+x]-north.heights[x]);
  maxError=Math.max(maxError,diff);yEdges++;
 }
}
if(data.terrain.length!==25||xEdges!==660||yEdges!==660||maxError!==0)throw Error('LAND seam mismatch: '+JSON.stringify({tiles:data.terrain.length,xEdges,yEdges,maxError}));
if(data.people.length<14||data.objects.length<1600||data.actors.length<10)throw Error('Unexpected Riverwood source counts');
console.log('Verified',data.terrain.length,'LAND cells,',xEdges,'east/west and',yEdges,'north/south vertex seam pairs; maximum discontinuity',maxError);
console.log('Original source REFR',data.objects.length,'ACHR outdoors',data.actors.length,'interior residents',data.people.length);
