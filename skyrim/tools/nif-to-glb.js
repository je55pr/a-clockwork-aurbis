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
  if(!vertCount||!triCount){skips.empty=(skips.empty||0)+1;continue;}
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
  const basis=meshBasis(shape);
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
