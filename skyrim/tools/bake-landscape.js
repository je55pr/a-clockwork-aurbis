// Bake the exact LAND quadrant base and alpha vertex masks into a visual
// colour atlas per source terrain cell. Uses the original LTEX -> TXST DDS
// images and VTXT weights; vertex colours remain separate in Godot.
const fs=require('fs'),path=require('path'),cp=require('child_process'),{open}=require('./bsa');
const root=path.resolve(__dirname,'../generated');
const source=JSON.parse(fs.readFileSync(path.join(root,'riverwood.json'),'utf8'));
if(!source.landscapeTextures||!source.terrain.every(t=>Array.isArray(t.base)&&Array.isArray(t.alpha)))throw Error('Run extract-riverwood.js to regenerate LAND materials first');
const archiveNames=Array.from({length:9},(_,i)=>'Skyrim - Textures'+i+'.bsa');
const archives=archiveNames.map(open),SIZE=1024,TEXTURE_SIZE=256;
const work=path.join(root,'landscape-work');fs.mkdirSync(work,{recursive:true});
const cache=new Map(),missing=new Set(),stats={sourceTextures:Object.keys(source.landscapeTextures).length,decoded:0,missing:0,tiles:0,overlays:0};
function texture(ref){
 if(!ref)return null;
 const def=source.landscapeTextures[ref];
 if(!def)throw Error('Unresolved LAND texture ref: '+ref);
 const name='textures\\'+def.diffuse.replace(/\//g,'\\').toLowerCase();
 if(cache.has(name))return cache.get(name);
 let bytes;
 for(const a of archives){bytes=a.get(name);if(bytes)break}
 if(!bytes){
  missing.add(name);cache.set(name,null);return null;
 }
 const ddsFile=path.join(work,'source.dds'),rawFile=path.join(work,'source.rgba');
 fs.writeFileSync(ddsFile,bytes);
 const ff=cp.spawnSync('ffmpeg',['-hide_banner','-loglevel','error','-y','-i',ddsFile,'-vf','scale='+TEXTURE_SIZE+':'+TEXTURE_SIZE+':flags=lanczos','-frames:v','1','-f','rawvideo','-pix_fmt','rgba',rawFile],{timeout:30000});
 if(ff.status!==0 ||!fs.existsSync(rawFile))throw Error('DDS decode failed '+name+' '+ff.stderr.toString().slice(0,300));
 const pixels=fs.readFileSync(rawFile);
 if(pixels.length!==TEXTURE_SIZE*TEXTURE_SIZE*4)throw Error('Unexpected DDS pixel count '+name+':'+pixels.length);
 fs.rmSync(ddsFile);fs.rmSync(rawFile);
 cache.set(name,pixels);stats.decoded++;
 return pixels;
}
function mix(a,b,w){return a+(b-a)*w}
function masksFor(cell){
 const quadrants=[[],[],[],[]],bases=Array(4).fill(null);
 for(const layer of cell.base){
  if(layer.quadrant<0||layer.quadrant>3)throw Error('Invalid BTXT quadrant '+layer.quadrant);
  bases[layer.quadrant]=texture(layer.texture);
 }
 for(const layer of cell.alpha){
  if(layer.quadrant<0||layer.quadrant>3)throw Error('Invalid ATXT quadrant '+layer.quadrant);
  const pixels=texture(layer.texture),weights=new Float32Array(17*17);
  for(const [pos,weight] of layer.weights){if(pos<0||pos>=289)throw Error('Invalid VTXT index');weights[pos]=weight}
  quadrants[layer.quadrant].push({order:layer.layer,pixels,weights});
 }
 for(const q of quadrants)q.sort((a,b)=>a.order-b.order);
 return {quadrants,bases};
}
function sampleRgba(texture,u,v){
 const xx=(((u*TEXTURE_SIZE)|0)%TEXTURE_SIZE+TEXTURE_SIZE)%TEXTURE_SIZE;
 const yy=(((v*TEXTURE_SIZE)|0)%TEXTURE_SIZE+TEXTURE_SIZE)%TEXTURE_SIZE;
 return (yy*TEXTURE_SIZE+xx)*4;
}
function bilinear(weights,x,y){
 const ix=Math.min(15,Math.floor(x)),iy=Math.min(15,Math.floor(y));
 const wx=x-ix,wy=y-iy;
 const i=iy*17+ix;
 return mix(mix(weights[i],weights[i+1],wx),mix(weights[i+17],weights[i+18],wx),wy);
}
fs.mkdirSync(path.join(root,'landscape'),{recursive:true});
for(const cell of source.terrain){
 const {quadrants,bases}=masksFor(cell),buf=Buffer.allocUnsafe(SIZE*SIZE*4);
 const wx0=cell.x*4096,wy0=cell.y*4096;
 for(let py=0;py<SIZE;py++){
  const y=(py+0.5)*32/SIZE,qy=y>=16?1:0,localY=y-qy*16;
  // Cross-tile UVs remain continuous, even at negative Skyrim coordinates.
  const v=((wy0+(py+0.5)*4096/SIZE)/512)%1;
  for(let px=0;px<SIZE;px++){
   const x=(px+0.5)*32/SIZE,qx=x>=16?1:0,localX=x-qx*16,q=qx+qy*2;
   const u=((wx0+(px+0.5)*4096/SIZE)/512)%1;
   const b=bases[q],at=(py*SIZE+px)*4;
   if(b){
    const idx=sampleRgba(b,u,v);
    buf[at]=b[idx];buf[at+1]=b[idx+1];buf[at+2]=b[idx+2];buf[at+3]=255;
   }else{
    // No BTXT is a genuine authoring case. Tint remains driven by VCLR.
    buf[at]=195;buf[at+1]=195;buf[at+2]=195;buf[at+3]=255;
   }
   for(const layer of quadrants[q]){
    if(!layer.pixels)continue;
    const weight=bilinear(layer.weights,localX,localY);
    if(weight<0.002)continue;
    const id=sampleRgba(layer.pixels,u,v);
    buf[at]=Math.round(mix(buf[at],layer.pixels[id],weight));
    buf[at+1]=Math.round(mix(buf[at+1],layer.pixels[id+1],weight));
    buf[at+2]=Math.round(mix(buf[at+2],layer.pixels[id+2],weight));
   }
  }
 }
 const outfile=path.join(root,'landscape','cell_'+cell.x+'_'+cell.y+'.png');
 const rawFile=path.join(work,'composite.rgba');fs.writeFileSync(rawFile,buf);
 const ff=cp.spawnSync('ffmpeg',['-hide_banner','-loglevel','error','-y','-f','rawvideo','-pixel_format','rgba','-video_size',SIZE+'x'+SIZE,'-i',rawFile,'-frames:v','1',outfile],{timeout:30000});
 fs.rmSync(rawFile);
 if(ff.status!==0)throw Error('Failed writing '+outfile+': '+ff.stderr.toString().slice(0,300));
 stats.tiles++;stats.overlays+=cell.alpha.length;
 console.log('BAKED LAND',cell.x,cell.y,'layers',cell.alpha.length);
}
archives.forEach(a=>a.close());
fs.rmSync(work,{recursive:true,force:true});
stats.missing=missing.size;
if(missing.size)console.log('MISSING DDS',...missing);
console.log('LANDSCAPE COMPLETE',JSON.stringify(stats));
if(stats.missing>0)throw Error('Some landscape textures could not be resolved');
fs.writeFileSync(path.join(root,'landscape','manifest.json'),JSON.stringify(stats,null,2));
