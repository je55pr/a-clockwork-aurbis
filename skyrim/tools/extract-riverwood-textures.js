const fs=require('fs'),path=require('path'),{execFileSync}=require('child_process');
const {open}=require('./bsa'),{getShapes}=require('./nif-to-glb');
const root=path.resolve(__dirname,'../generated');
const index=JSON.parse(fs.readFileSync(path.join(root,'model-index.json'),'utf8'));
const modelNames=Object.keys(index.models);
const meshSources=[open('Skyrim - Meshes0.bsa'),open('Skyrim - Meshes1.bsa')];
const textureSources=Array.from({length:9},(_,i)=>open('Skyrim - Textures'+i+'.bsa'));
const found=new Map();
for(const model of modelNames){
 let mesh=null;for(const s of meshSources){mesh=s.get(model);if(mesh)break}
 if(!mesh)continue;
 const shapes=getShapes(mesh).shapes;
 for(const shape of shapes){
  if(!shape.texture)continue;
  const key=shape.texture.toLowerCase().replace(/\//g,'\\');
  if(!found.has(key))found.set(key,0);
  found.set(key,found.get(key)+index.models[model].instances);
 }
}
console.log('source diffuse textures referenced:',found.size);
let count=0,missing=0,failed=0;
const sorted=[...found.entries()].sort((a,b)=>b[1]-a[1]);
for(const [key,uses] of sorted){
 if(count>=120)break;
 const texname=key.replace(/^textures\\/,'').replace(/\.dds$/i,'.png');
 const output=path.resolve(root,'textures',texname);
 if(fs.existsSync(output)){count++;continue}
 let payload;
 for(const source of textureSources){try{payload=source.get(key);if(payload)break}catch(e){console.log('ERR-BSA',key,e.message)}}
 if(!payload){missing++;console.log('MISSING',key);continue}
 const raw=path.resolve(root,'texture-work',key);
 fs.mkdirSync(path.dirname(raw),{recursive:true});
 fs.writeFileSync(raw,payload);
 fs.mkdirSync(path.dirname(output),{recursive:true});
 try{
  execFileSync('ffmpeg',['-hide_banner','-loglevel','error','-y','-i',raw,'-vf','scale=1024:1024:force_original_aspect_ratio=decrease','-frames:v','1',output],{stdio:'pipe',timeout:30000});
  count++;
  if(count%10===0)console.log('CONVERTED',count,'textures');
 }catch(e){failed++;console.log('ERR-CONVERT',key,e.stderr?.toString()?.slice(-180)||e.message);}
 fs.rmSync(raw,{force:true});
}
for(const s of meshSources.concat(textureSources))s.close();
console.log('SUMMARY',JSON.stringify({referenced:found.size,converted:count,missing,failed}));
