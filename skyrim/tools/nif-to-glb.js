const fs=require('fs'),path=require('path');
const {open}=require('./bsa');
const root=path.resolve(__dirname,'../generated');
function parseNif(b){
 const line=b.indexOf(10),count=b.readUInt32LE(line+10);
 let found=null;
 for(let start=line+18;start<line+240;start++){
  if(start+12>b.length)break;
  const n=b.readUInt16LE(start);if(n<=0||n>110)continue;
  let p=start+2,types=[];
  try{
   for(let i=0;i<n;i++){
    const len=b.readUInt32LE(p);p+=4;if(len<3||len>80||p+len>b.length)throw Error('len');
    const t=b.toString('latin1',p,p+len);p+=len;if(!/^[A-Za-z]\w+$/.test(t))throw Error('type');types.push(t);
   }
   const ids=[];for(let i=0;i<count;i++){const id=b.readUInt16LE(p);p+=2;if(id>=types.length)throw Error('id');ids.push(id)}
   const sizes=[];for(let i=0;i<count;i++){const sz=b.readUInt32LE(p);p+=4;if(sz>50_000_000)throw Error('sz');sizes.push(sz)}
   const nstr=b.readUInt32LE(p);p+=4, maxlen=b.readUInt32LE(p);p+=4;
   if(nstr>100000||maxlen>100000)throw Error('nstr');
   const names=[];for(let i=0;i<nstr;i++){const len=b.readUInt32LE(p);p+=4;if(len>maxlen||len>100000)throw Error('slen');names.push(b.toString('utf8',p,p+len));p+=len}
   const ng=b.readUInt32LE(p);p+=4;if(ng>10000)throw Error('ng');p+=ng*4;
   const total=sizes.reduce((a,b)=>a+b,0);
   if(total > b.length-p || b.length-p-total>2048)throw Error('checksum');
   const blocks=[];for(let i=0;i<count;i++){blocks.push({type:types[ids[i]],data:b.subarray(p,p+sizes[i]),name:names});p+=sizes[i]}
   found=blocks;break;
  }catch(e){}
 }
 if(!found)throw Error('NIF header block table not found');
 return found;
}
function halfToFloat(h){
 const sign=(h&0x8000)?-1:1,exp=(h>>10)&31,m=h&1023;
 if(exp===0)return sign*Math.pow(2,-14)*(m/1024);
 if(exp===31)return m?NaN:sign*Infinity;
 return sign*Math.pow(2,exp-15)*(1+m/1024);
}


function legacyTreeShape(blocks, block, index){
 const d=block.data;
 if(d.length<96)throw Error('Short NiTriShape record');
 const geomId=d.readInt32LE(72),shaderId=d.readInt32LE(89);
 const data=blocks[geomId];
 if(!data||data.type!=='NiTriShapeData')throw Error('Missing NiTriShapeData at '+geomId);
 const buf=data.data,n=buf.readUInt32LE(4);
 if(n<=0||n>65535||buf.readUInt8(8)!==1)throw Error('Invalid legacy vertex count');
 let at=9;
 const positions=[],normals=[],tangents=[],bitangents=[],uv=[],colors=[],indices=[];
 const convert=(x,y,z)=>[x*0.01,z*0.01,-y*0.01];
 const vec3=(p)=>[buf.readFloatLE(p),buf.readFloatLE(p+4),buf.readFloatLE(p+8)];
 for(let v=0;v<n;v++,at+=12){const [x,y,z]=vec3(at);positions.push(...convert(x,y,z))}
 if(buf.readUInt8(at)!==1||buf.readUInt32LE(at+1)!==16||buf.readUInt8(at+5)!==0||buf.readUInt8(at+6)!==1)
  throw Error('Unsupported legacy normal flags '+buf.subarray(at,at+7).toString('hex'));
 at+=7;
 // NiTriShapeData stores full precision normals/tangents/bitangents.
 for(const target of [normals,tangents,bitangents]){
  for(let v=0;v<n;v++,at+=12){const [x,y,z]=vec3(at);target.push(x,z,-y)}
 }
 at+=16; // bounding sphere in Skyrim coordinates
 if(buf.readUInt8(at++)!==1)throw Error('Unsupported legacy tree vertex colors');
 for(let v=0;v<n;v++){for(let k=0;k<4;k++,at+=4)colors.push(buf.readFloatLE(at))}
 for(let v=0;v<n;v++){uv.push(buf.readFloatLE(at),buf.readFloatLE(at+4));at+=8}
 // Skyrim 20.2.0.7 legacy geometry data: consistency flags + shape triangles.
 if(buf.readUInt16LE(at)!==0x4000||buf.readUInt32LE(at+2)!==0xffffffff)
  throw Error('Unknown legacy tree UV/consistency flag at '+at+':'+buf.subarray(at,at+6).toString('hex'));
 at+=6;
 const tris=buf.readUInt16LE(at),points=buf.readUInt32LE(at+2),hasTriangles=buf.readUInt8(at+6);
 at+=7;
 if(tris<1||points!==tris*3||hasTriangles!==1||at+tris*6+2!==buf.length)
  throw Error('Invalid legacy triangle data '+n+' '+tris+' at '+at+' of '+buf.length);
 for(let i=0;i<tris*3;i++){
  const v=buf.readUInt16LE(at+i*2);
  if(v>=n)throw Error('Legacy triangle index '+v+' >= '+n);
  indices.push(v);
 }
 if(buf.readUInt16LE(at+tris*6)!==0)throw Error('Legacy match groups unsupported');
 const shader=blocks[shaderId];
 let maps=null;
 if(shader?.type==='BSLightingShaderProperty' && shader.data.length>=44){
  const texId=shader.data.readInt32LE(40),set=blocks[texId];
  if(set?.type==='BSShaderTextureSet'){
   let p=4;const paths=[];
   for(let i=0;i<Math.min(12,set.data.readUInt32LE(0));i++){
    if(p+4>set.data.length)break;
    const len=set.data.readUInt32LE(p);p+=4;
    if(p+len>set.data.length)break;
    paths.push(set.data.toString('latin1',p,p+len).replace(/^skyrimhd[\\/]build[\\/]pc[\\/]data[\\/]/i,''));
    p+=len;
   }
   maps={diffuse:paths[0]||null,normal:paths[1]||null};
  }
 }
 return {
  name:block.name[d.readInt32LE(0)]||'NiTriShape_'+index,positions,uv,colors,
  sourceNormals:normals,sourceTangents:tangents,sourceBitangents:bitangents,
  texture:maps?.diffuse||null,normalTexture:maps?.normal||null,
  alphaTest:maps?.diffuse?true:false,indices,vertices:n,tris,block:index
 };
}


function skinnedTreeShape(blocks,block,index){
 const d=block.data;
 if(d.length<120)throw Error('Short skinned BSTriShape');
 const skinId=d.readInt32LE(88),skin=blocks[skinId];
 if(!skin || skin.type!=='NiSkinInstance')throw Error('No skin instance');
 const partitionId=skin.data.readInt32LE(4),partition=blocks[partitionId];
 if(!partition||partition.type!=='NiSkinPartition')throw Error('No skin partition');
 const b=partition.data,parts=b.readUInt32LE(0),bytes=b.readUInt32LE(4),stride=b.readUInt32LE(8),descriptor=b.readBigUInt64LE(12);
 if(parts<1||parts>150||stride<28||stride>64||bytes<=0||bytes%stride!==0||20+bytes>b.length)throw Error('Invalid SSE skin vertex stream');
 const count=bytes/stride,positions=[],uv=[],colors=[],indices=[];
 const uvOffset=Number((descriptor>>8n)&0xfn)*4;
 if(uvOffset!==16)throw Error('Unsupported skinned UV offset '+uvOffset);
 for(let i=0;i<count;i++){
  const off=20+i*stride,xyz=[b.readFloatLE(off),b.readFloatLE(off+4),b.readFloatLE(off+8)];
  if(!xyz.every(Number.isFinite))throw Error('Invalid skinned position');
  positions.push(xyz[0]*0.01,xyz[2]*0.01,-xyz[1]*0.01);
  uv.push(halfToFloat(b.readUInt16LE(off+uvOffset)),halfToFloat(b.readUInt16LE(off+uvOffset+2)));
  // The source's packed RGBA attribute starts immediately before skin weights.
  colors.push(b[off+28]/255,b[off+29]/255,b[off+30]/255,b[off+31]/255);
 }
 let at=20+bytes,triCount=0;
 for(let p=0;p<parts;p++){
  if(at+10>b.length)throw Error('Truncated SSE skin partition');
  const nv=b.readUInt16LE(at),nt=b.readUInt16LE(at+2),nb=b.readUInt16LE(at+4),ns=b.readUInt16LE(at+6),nw=b.readUInt16LE(at+8);
  at+=10;
  if(nv>count||nb>100||ns!==0||nw!==4)throw Error('Unsupported SSE bone partition counts');
  at+=nb*2;
  if(b[at++]!==1)throw Error('Missing SSE vertex map');
  at+=nv*2;
  if(b[at++]!==1)throw Error('Missing SSE vertex weights');
  at+=nv*nw*4;
  if(b[at++]!==1)throw Error('Missing SSE triangles');
  at+=nt*6;
  if(b[at++]!==1)throw Error('Missing SSE bone index list');
  at+=nv*nw;
  if(at+10+nt*6>b.length)throw Error('Truncated SSE global triangle list');
  const partFlag=b.readUInt16LE(at),partDescriptor=b.readBigUInt64LE(at+2);
  if(partDescriptor!==descriptor)throw Error('Mismatched SSE vertex descriptors');
  at+=10;
  for(let j=0;j<nt*3;j++){
   const v=b.readUInt16LE(at+j*2);
   if(v>=count)throw Error('SSE global triangle index '+v+' >= '+count);
   indices.push(v);
  }
  at+=nt*6;triCount+=nt;
 }
 if(at!==b.length || !indices.length)throw Error('SSE skin partition payload mismatch '+at+'/'+b.length);
 const maps=(()=>{
  const shader=blocks[d.readInt32LE(92)];
  if(shader?.type!=='BSLightingShaderProperty')return null;
  const set=blocks[shader.data.readInt32LE(40)];
  if(set?.type!=='BSShaderTextureSet')return null;
  let offset=4;const paths=[];
  for(let i=0;i<Math.min(12,set.data.readUInt32LE(0));i++){
   if(offset+4>set.data.length)break;
   const len=set.data.readUInt32LE(offset);offset+=4;
   if(offset+len>set.data.length)break;
   paths.push(set.data.toString('latin1',offset,offset+len).replace(/^skyrimhd[\\/]build[\\/]pc[\\/]data[\\/]/i,''));offset+=len;
  }
  return paths;
 })();
 return {name:blocks[skinId]?.name[d.readInt32LE(0)]||'SkinnedTree_'+index,
  positions,uv,colors,texture:maps?.[0]||null,normalTexture:maps?.[1]||null,
  alphaTest:!!maps?.[0]&&/branch|leaf|needle/i.test(maps[0]),
  indices,vertices:count,tris:triCount,block:index,skinned:true};
}

function getShapes(nif){
 const bs=parseNif(nif),shapes=[],skips={};

 function sourceTextures(d){
  if(d.length<100)return null;
  const shaderRef=d.readInt32LE(92),shader=bs[shaderRef];
  if(!shader||shader.type!=='BSLightingShaderProperty'||shader.data.length<44)return null;
  const texRef=shader.data.readInt32LE(40),texBlock=bs[texRef];
  if(!texBlock||texBlock.type!=='BSShaderTextureSet')return null;
  const bytes=texBlock.data,count=bytes.readUInt32LE(0);
  if(count>30)return null;
  let at=4,paths=[];
  for(let i=0;i<count;i++){
   if(at+4>bytes.length)return null;
   const len=bytes.readUInt32LE(at);at+=4;
   if(at+len>bytes.length)return null;
   paths.push(bytes.toString('latin1',at,at+len).replace(/^skyrimhd[\\/]build[\\/]pc[\\/]data[\\/]/i,''));at+=len;
  }
  return {diffuse:paths[0]||null,normal:paths[1]||null};
 }

 for(const [id,blk] of bs.entries()){
  if(!['BSTriShape','BSMeshLODTriShape'].includes(blk.type))continue;
  const d=blk.data;
  if(d.length<116){skips.short=(skips.short||0)+1;continue}
  const desc=d.readBigUInt64LE(100);
  const stride=Number(desc&0xfn)*4;
  const triCount=d.readUInt16LE(108),vertCount=d.readUInt16LE(110),dataSize=d.readUInt32LE(112);
  if(!vertCount||!triCount){
   try{shapes.push(skinnedTreeShape(bs,blk,id));skips.skinned=(skips.skinned||0)+1;}catch(e){skips.empty=(skips.empty||0)+1;skips.skinnedErrors=(skips.skinnedErrors||[]);if(bs.some(b=>b.type==='BSTreeNode'))skips.skinnedErrors.push('block '+id+': '+e.message);}
   continue;
  }
  if(stride<16||stride>80||vertCount*stride+triCount*6!==dataSize||116+dataSize>d.length){
   console.log('UNSUPPORTED block',id,blk.type,vertCount,triCount,stride,dataSize,d.length);
   skips.layout=(skips.layout||0)+1;continue;
  }
  const pos=[],index=[],uv=[];
  const uvOffset=Number((desc>>8n)&0xfn)*4;
  const hasUv=uvOffset>=16&&uvOffset+4<=stride;
  for(let j=0;j<vertCount;j++){
   const at=116+j*stride;
   const xyz=[d.readFloatLE(at),d.readFloatLE(at+4),d.readFloatLE(at+8)];
   if(!xyz.every(Number.isFinite))throw Error('non-finite pos');
   // native TES space: XY horizontal, Z altitude; Godot: X,Y(horizontal) -> X,-Z; Z(up) -> Y.
   pos.push(xyz[0]*0.01,xyz[2]*0.01,-xyz[1]*0.01);
   if(hasUv)uv.push(halfToFloat(d.readUInt16LE(at+uvOffset)),halfToFloat(d.readUInt16LE(at+uvOffset+2)));
  }
  const at=116+vertCount*stride;
  for(let j=0;j<triCount;j++){
   const i=at+j*6;
   const a=d.readUInt16LE(i),bb=d.readUInt16LE(i+2),c=d.readUInt16LE(i+4);
   if(a>=vertCount||bb>=vertCount||c>=vertCount)throw Error('index out of bounds');
   index.push(a,bb,c);
  }
  // NIF mesh block's own translation/rotation in NiAVObject may be non-identity;
  // preserve native packed geometry for diagnostic reconstruction first.
  const maps=sourceTextures(d);
  shapes.push({name:blk.name[d.readUInt32LE(0)]||'shape_'+id,positions:pos,uv,texture:maps?.diffuse||null,normalTexture:maps?.normal||null,indices:index,vertices:vertCount,tris:triCount,block:id});
 }
 if(!shapes.some(shape=>shape.skinned)) {
 for(const [i,block] of bs.entries())if(block.type==='NiTriShape'){
  try{shapes.push(legacyTreeShape(bs,block,i))}catch(e){skips.legacy=(skips.legacy||[]);skips.legacy.push('block '+i+': '+e.message)}
 }
 }
 return {shapes,skips};
}

function meshBasis(shape){
 // Geometry-derived smoothed normals and per-UV tangents. Tangent handedness
 // is required for glTF normalTexture; do not invent a flat +Y normal on walls.
 const n=shape.vertices,normal=new Float64Array(n*3),tangent=new Float64Array(n*3),bitangent=new Float64Array(n*3);
 const vertices=shape.positions,uv=shape.uv,indices=shape.indices,hasUv=uv.length===n*2;
 for(let i=0;i<indices.length;i+=3){
  const a=indices[i],b=indices[i+1],c=indices[i+2],v=a*3,w=b*3,x=c*3;
  const e1=[vertices[w]-vertices[v],vertices[w+1]-vertices[v+1],vertices[w+2]-vertices[v+2]];
  const e2=[vertices[x]-vertices[v],vertices[x+1]-vertices[v+1],vertices[x+2]-vertices[v+2]];
  const fn=[e1[1]*e2[2]-e1[2]*e2[1],e1[2]*e2[0]-e1[0]*e2[2],e1[0]*e2[1]-e1[1]*e2[0]];
  for(const k of [a,b,c])for(let p=0;p<3;p++)normal[k*3+p]+=fn[p];
  if(!hasUv)continue;
  const u1=uv[b*2]-uv[a*2],v1=uv[b*2+1]-uv[a*2+1];
  const u2=uv[c*2]-uv[a*2],v2=uv[c*2+1]-uv[a*2+1];
  const det=u1*v2-u2*v1;if(Math.abs(det)<1e-12)continue;
  const inv=1/det;
  const t=e1.map((v,j)=>(v*v2-e2[j]*v1)*inv);
  const bt=e1.map((v,j)=>(e2[j]*u1-v*u2)*inv);
  for(const k of [a,b,c])for(let p=0;p<3;p++){tangent[k*3+p]+=t[p];bitangent[k*3+p]+=bt[p];}
 }
 const outN=new Float32Array(n*3),outT=new Float32Array(n*4);
 for(let i=0;i<n;i++){
  let [nx,ny,nz]=normal.slice(i*3,i*3+3);
  let len=Math.hypot(nx,ny,nz);if(len<1e-12){nx=0;ny=1;nz=0;len=1}
  nx/=len;ny/=len;nz/=len;
  outN.set([nx,ny,nz],i*3);
  let [tx,ty,tz]=tangent.slice(i*3,i*3+3);
  const dot=nx*tx+ny*ty+nz*tz;
  tx-=nx*dot;ty-=ny*dot;tz-=nz*dot;
  len=Math.hypot(tx,ty,tz);
  if(len<1e-12){
   const q=Math.abs(ny)<0.9?[0,1,0]:[1,0,0];
   tx=q[1]*nz-q[2]*ny;ty=q[2]*nx-q[0]*nz;tz=q[0]*ny-q[1]*nx;len=Math.hypot(tx,ty,tz);
  }
  tx/=len;ty/=len;tz/=len;
  const b=bitangent.slice(i*3,i*3+3);
  const cx=ny*tz-nz*ty,cy=nz*tx-nx*tz,cz=nx*ty-ny*tx;
  const hand=cx*b[0]+cy*b[1]+cz*b[2]<0?-1:1;
  outT.set([tx,ty,tz,hand],i*4);
 }
 return {normal:outN,tangent:outT};
}

function sourceBasis(shape){
 const n=shape.vertices,normal=new Float32Array(n*3),tangent=new Float32Array(n*4);
 for(let i=0;i<n;i++){
  const p=i*3,q=i*4;
  let [nx,ny,nz]=shape.sourceNormals.slice(p,p+3);
  let len=Math.hypot(nx,ny,nz);
  if(len<1e-10)throw Error('Zero source normal');
  nx/=len;ny/=len;nz/=len;
  normal.set([nx,ny,nz],p);
  let [tx,ty,tz]=shape.sourceTangents.slice(p,p+3);
  const dot=nx*tx+ny*ty+nz*tz;
  tx-=nx*dot;ty-=ny*dot;tz-=nz*dot;
  len=Math.hypot(tx,ty,tz);
  if(len<1e-10)throw Error('Zero source tangent');
  tx/=len;ty/=len;tz/=len;
  const [bx,by,bz]=shape.sourceBitangents.slice(p,p+3);
  const cross=[ny*tz-nz*ty,nz*tx-nx*tz,nx*ty-ny*tx];
  const sign=cross[0]*bx+cross[1]*by+cross[2]*bz<0?-1:1;
  tangent.set([tx,ty,tz,sign],q);
 }
 return {normal,tangent};
}

function buildGlb(shapes,sourcePath){
 const views=[],accessors=[],meshes=[],nodes=[],buffers=[],materials=[],images=[],textures=[];
 const textureMap=new Map();
 let binaryLength=0;
 function addBuffer(buf,target){
  const padding=(4-binaryLength%4)%4;
  if(padding){buffers.push(Buffer.alloc(padding));binaryLength+=padding}
  const offset=binaryLength;
  buffers.push(buf);binaryLength+=buf.length;
  const view={buffer:0,byteOffset:offset,byteLength:buf.length};
  if(target)view.target=target;
  views.push(view);
  return views.length-1;
 }
 for(const shape of shapes){
  const basis=shape.sourceNormals?sourceBasis(shape):meshBasis(shape);
  const vb=Buffer.alloc(shape.positions.length*4);
  for(let i=0;i<shape.positions.length;i++)vb.writeFloatLE(shape.positions[i],i*4);
  const ib=Buffer.alloc(shape.indices.length*2);
  for(let i=0;i<shape.indices.length;i++)ib.writeUInt16LE(shape.indices[i],i*2);
  const vView=addBuffer(vb,34962),iView=addBuffer(ib,34963);
  const xs=[],ys=[],zs=[];
  for(let i=0;i<shape.positions.length;i+=3){xs.push(shape.positions[i]);ys.push(shape.positions[i+1]);zs.push(shape.positions[i+2]);}
  const minimum=[Math.min(...xs),Math.min(...ys),Math.min(...zs)];
  const maximum=[Math.max(...xs),Math.max(...ys),Math.max(...zs)];
  const pi=accessors.push({bufferView:vView,componentType:5126,count:shape.vertices,type:'VEC3',min:minimum,max:maximum})-1;
  let uvAccessor;
  if(shape.uv.length===shape.vertices*2){
   const ub=Buffer.alloc(shape.uv.length*4);
   for(let i=0;i<shape.uv.length;i++)ub.writeFloatLE(shape.uv[i],i*4);
   const uvView=addBuffer(ub,34962);
   uvAccessor=accessors.push({bufferView:uvView,componentType:5126,count:shape.vertices,type:'VEC2'})-1;
  }
  const ii=accessors.push({bufferView:iView,componentType:5123,count:shape.indices.length,type:'SCALAR'})-1;
  function floats(values,typ){const buf=Buffer.alloc(values.length*4);for(let i=0;i<values.length;i++)buf.writeFloatLE(values[i],i*4);return accessors.push({bufferView:addBuffer(buf,34962),componentType:5126,count:shape.vertices,type:typ})-1;}
  const normalAccessor=floats(basis.normal,'VEC3'),tangentAccessor=floats(basis.tangent,'VEC4');
  // grayscale neutral diagnostic material, not claimed to be TES original surface
  const matInfo={name:'source-material-'+shape.block,pbrMetallicRoughness:{baseColorFactor:[0.72,0.70,0.63,1],metallicFactor:0,roughnessFactor:0.92},doubleSided:true};
  if(shape.texture && uvAccessor!==undefined){
   const diffuse=shape.texture.replace(/\\/g,'/').replace(/^textures\//i,'').replace(/\.dds$/i,'.png').toLowerCase();
   const texturePath=path.resolve(root,'textures',diffuse);
   if(fs.existsSync(texturePath)){
    if(!textureMap.has(texturePath)){
     const uri=path.relative(path.dirname(sourcePath),texturePath).replace(/\\/g,'/');
     const image=images.push({uri})-1;
     textureMap.set(texturePath,textures.push({source:image})-1);
    }
    matInfo.pbrMetallicRoughness.baseColorFactor=[1,1,1,1];
    matInfo.pbrMetallicRoughness.baseColorTexture={index:textureMap.get(texturePath)};
   }
  }
  if(shape.alphaTest && matInfo.pbrMetallicRoughness.baseColorTexture){
   matInfo.alphaMode='MASK';matInfo.alphaCutoff=0.45;
  }
  const mat=materials.push(matInfo)-1;
  if(shape.normalTexture && uvAccessor!==undefined){
   const file=shape.normalTexture.replace(/\\/g,'/').replace(/^textures\//i,'').replace(/\.dds$/i,'.png').toLowerCase();
   const texturePath=path.resolve(root,'textures',file);
   if(fs.existsSync(texturePath)){
    if(!textureMap.has(texturePath)){
     const uri=path.relative(path.dirname(sourcePath),texturePath).replace(/\\/g,'/');
     const image=images.push({uri})-1;
     textureMap.set(texturePath,textures.push({source:image})-1);
    }
    matInfo.normalTexture={index:textureMap.get(texturePath),scale:1.0};
   }
  }
  const attributes={POSITION:pi,NORMAL:normalAccessor,TANGENT:tangentAccessor};
  if(uvAccessor!==undefined)attributes.TEXCOORD_0=uvAccessor;
  if(shape.colors?.length===shape.vertices*4){
   const colors=Buffer.alloc(shape.colors.length*4);
   for(let i=0;i<shape.colors.length;i++)colors.writeFloatLE(shape.colors[i],i*4);
   attributes.COLOR_0=accessors.push({bufferView:addBuffer(colors,34962),componentType:5126,count:shape.vertices,type:'VEC4'})-1;
  }
  const mi=meshes.push({name:shape.name,primitives:[{attributes,indices:ii,mode:4,material:mat}]})-1;
  nodes.push({mesh:mi,name:shape.name});
 }
 const json={asset:{version:'2.0',generator:'Riverwood local Skyrim NIF geometry proof'},scene:0,scenes:[{nodes:nodes.map((n,i)=>i)}],nodes,meshes,materials,images,textures,accessors,bufferViews:views,buffers:[{byteLength:binaryLength}]};
 let jb=Buffer.from(JSON.stringify(json)),padding=(4-jb.length%4)%4;
 if(padding)jb=Buffer.concat([jb,Buffer.alloc(padding,0x20)]);
 const binary=Buffer.concat(buffers);const binPadding=(4-binary.length%4)%4;
 const total=12+8+jb.length+8+binary.length+binPadding;
 const out=Buffer.alloc(total);
 out.write('glTF',0,'ascii');out.writeUInt32LE(2,4);out.writeUInt32LE(total,8);
 out.writeUInt32LE(jb.length,12);out.write('JSON',16,'ascii');jb.copy(out,20);
 const bAt=20+jb.length;out.writeUInt32LE(binary.length+binPadding,bAt);out.write('BIN\0',bAt+4,'ascii');binary.copy(out,bAt+8);
 return out;
}
function convert(source,name){
 const buf=source.get(name);if(!buf)return {error:'missing in BSA'};
 const parsed=getShapes(buf);if(!parsed.shapes.length)return {error:'no inline supported BSTriShape',skips:parsed.skips};
 const outfile=path.resolve(root,'models',name.replace(/\.nif$/i,'.glb'));
 fs.mkdirSync(path.dirname(outfile),{recursive:true});
 fs.writeFileSync(outfile,buildGlb(parsed.shapes,outfile));
 return {file:outfile,shapes:parsed.shapes.length,tris:parsed.shapes.reduce((s,x)=>s+x.tris,0),skips:parsed.skips};
}
if(require.main===module){
 const source=open('Skyrim - Meshes0.bsa');
 for(const n of ['meshes\\architecture\\farmhouse\\inn01.nif','meshes\\architecture\\farmhouse\\smith01.nif','meshes\\architecture\\farmhouse\\farmhouse04.nif']){
  try{console.log('CONVERT',n,JSON.stringify(convert(source,n)))}catch(e){console.log('ERROR',n,e.stack)}
 }
 source.close();
}
module.exports={parseNif,getShapes,convert};
