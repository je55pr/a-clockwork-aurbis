const fs=require('fs'),path=require('path');
const root=path.resolve(__dirname,'../generated');
const data=JSON.parse(fs.readFileSync(path.join(root,'riverwood.json'),'utf8'));
const index=JSON.parse(fs.readFileSync(path.join(root,'model-index.json'),'utf8'));
const modelPaths=[
 'treepineforest01.nif','treepineforest02.nif','treepineforest03.nif',
 'treepineforest04.nif','treepineforest05.nif',
 'treepineforestsnow02.nif','treepineforestsnow03.nif','treepineforestsnow04.nif',
 'treepineforestsnow05.nif','treepineforestsnowl02.nif','treepineforestsnowl03.nif',
 'treepineforestdeadsnowl03.nif','treepineforestdead03.nif','treepineforestdead04.nif',
 'treepineforestsnowl05.nif'
];
let types=0,placements=0,sourceShapes=0,branches=0;
for(const name of modelPaths){
 const model='meshes\\landscape\\trees\\'+name;
 const count=data.objects.filter(x=>x.nif && 'meshes\\'+x.nif.toLowerCase()===model).length;
 if(count<1)throw Error('Expected authored pine tree placements '+name);
 placements+=count;
 const entry=index.models[model];
 if(!entry)throw Error('Missing converted pine model '+model);
 if(entry.instances!==count)throw Error('Source placement count differs for '+name);
 const file=path.resolve(root,entry.path.replace('res://generated/',''));
 const buf=fs.readFileSync(file);
 const json=JSON.parse(buf.subarray(20,20+buf.readUInt32LE(12)).toString());
 if(json.meshes.length!==2)throw Error('Expected trunk and branches in '+name+', got '+json.meshes.length);
 for(const mesh of json.meshes){
  const prim=mesh.primitives[0],material=json.materials[prim.material];
  if(['POSITION','NORMAL','TANGENT','TEXCOORD_0','COLOR_0'].some(a=>prim.attributes[a]===undefined))
   throw Error('Skinned source vertex channels missing '+name+' '+mesh.name);
  if(!material.pbrMetallicRoughness.baseColorTexture||!material.normalTexture)
   throw Error('Original tree diffuse and normal maps missing '+name);
  for(const slot of [material.pbrMetallicRoughness.baseColorTexture,material.normalTexture]){
   const texture=json.textures[slot.index],uri=json.images[texture.source].uri;
   if(!fs.existsSync(path.resolve(path.dirname(file),uri)))throw Error('Missing source texture '+uri+' for '+name);
  }
  if(/branch/i.test(mesh.name.toLowerCase())||/branch/i.test(json.images[json.textures[material.pbrMetallicRoughness.baseColorTexture.index].source].uri)) {
    if(material.alphaMode!=='MASK')throw Error('Missing foliage alpha clip '+name);
    branches++;
  }
  sourceShapes++;
 }
 types++;
}
if(types!==15||placements!==136||branches!==15)
 throw Error('Pine coverage regression '+JSON.stringify({types,placements,branches}));
console.log('Pine source geometry PASS: '+types+' pine model types; '+placements+' authored tree placements; '+sourceShapes+' original skinned meshes; '+branches+' alpha-masked branch materials.');
