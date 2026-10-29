const fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'../generated');
const landscape=JSON.parse(fs.readFileSync(path.join(root,'landscape','manifest.json'),'utf8'));
const indexed=JSON.parse(fs.readFileSync(path.join(root,'model-index.json'),'utf8'));
const source=JSON.parse(fs.readFileSync(path.join(root,'riverwood.json'),'utf8'));
if(landscape.tiles!==25||landscape.decoded<28||landscape.missing!==0)throw Error('Incomplete LAND diffuse+normal source');
let terrainNormals=0;
for(const tile of source.terrain){
 const p=path.join(root,'landscape','cell_'+tile.x+'_'+tile.y+'_normal.png');
 const b=fs.readFileSync(p);
 if(b.subarray(0,8).toString('hex')!=='89504e470d0a1a0a'||b.readUInt32BE(16)!==1024||b.readUInt32BE(20)!==1024)
   throw Error('Invalid normal PNG '+p);
 if(b.length<20000)throw Error('Suspiciously flat normal PNG '+p);
 terrainNormals++;
}
let shapes=0,normalMaterials=0,missingMaterials=0,referenced=new Set(),modelTypes=0;
for(const item of Object.values(indexed.models)){
 const p=path.resolve(root,item.path.replace('res://generated/',''));
 const b=fs.readFileSync(p);
 if(b.toString('ascii',0,4)!=='glTF')throw Error('Invalid GLB '+p);
 const j=JSON.parse(b.subarray(20,20+b.readUInt32LE(12)).toString('utf8'));
 for(const m of j.meshes){
  const prim=m.primitives[0];
  if(prim.attributes.NORMAL===undefined||prim.attributes.TANGENT===undefined)throw Error('Missing mesh tangent basis '+p+' '+m.name);
  if(j.materials[prim.material].normalTexture && prim.attributes.TEXCOORD_0===undefined)throw Error('Normal mapped surface without UVs '+p+' '+m.name);
 }
 for(const material of j.materials){
  if(material.normalTexture){
   const texture=j.textures[material.normalTexture.index];
   const uri=j.images[texture.source].uri,full=path.resolve(path.dirname(p),uri);
   if(!fs.existsSync(full))throw Error('Missing normal material image '+full);
   referenced.add(full);normalMaterials++;
  } else missingMaterials++;
 }
 modelTypes++;shapes+=j.meshes.length;
}
if(modelTypes<133||normalMaterials<350||terrainNormals!==25)throw Error('Incomplete normal map coverage '+JSON.stringify({modelTypes,normalMaterials,terrainNormals}));
console.log(JSON.stringify({terrainNormals,landscapeSources:landscape.decoded,propModels:modelTypes,propShapes:shapes,propNormalMaterials:normalMaterials,propWithoutNormal:missingMaterials,uniquePropNormalTextures:referenced.size,errors:0},null,2));
