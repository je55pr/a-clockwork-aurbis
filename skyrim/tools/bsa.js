const fs=require('fs'),path=require('path'),zlib=require('zlib');
const root=process.env.SKYRIM_DATA||'C:\\Program Files (x86)\\Steam\\steamapps\\common\\SkyrimVR\\Data';
function lz4Decode(bytes,unpacked){
 const out=Buffer.allocUnsafe(unpacked);let si=0,di=0;
 while(si<bytes.length){
  const token=bytes[si++];
  let lit=token>>4;
  if(lit===15){let n;do{if(si>=bytes.length)throw Error('Truncated literal extension');n=bytes[si++];lit+=n}while(n===255)}
  if(si+lit>bytes.length||di+lit>unpacked)throw Error('LZ4 literal overrun');
  bytes.copy(out,di,si,si+lit);si+=lit;di+=lit;
  if(si>=bytes.length)break;
  if(si+2>bytes.length)throw Error('LZ4 missing backref');
  const distance=bytes.readUInt16LE(si);si+=2;
  if(!distance||distance>di)throw Error('LZ4 invalid backref distance '+distance);
  let length=(token&15)+4;
  if((token&15)===15){let n;do{if(si>=bytes.length)throw Error('Truncated length');n=bytes[si++];length+=n}while(n===255)}
  if(di+length>unpacked)throw Error('LZ4 overrun');
  for(let i=0;i<length;i++)out[di+i]=out[di+i-distance];
  di+=length;
 }
 if(di!==unpacked)throw Error('LZ4 output mismatch '+di+'/'+unpacked);
 return out;
}

function decodeLz4Frame(bytes,expected){
 if(bytes.length<7 ||bytes.readUInt32LE(0)!==0x184d2204)throw Error('Not LZ4 frame');
 const flags=bytes[4];let p=6;
 if(flags&0x08)p+=8;
 if(flags&0x01)p+=4;
 p+=1; // header checksum
 const output=Buffer.allocUnsafe(expected);let di=0;
 while(p+4<=bytes.length){
   const blockHeader=bytes.readUInt32LE(p);p+=4;
   if(blockHeader===0)break;
   const raw=!!(blockHeader&0x80000000),size=blockHeader&0x7fffffff;
   if(!size||p+size>bytes.length)throw Error('LZ4 frame bad block '+size);
   const block=bytes.subarray(p,p+size);p+=size;
   if(raw){if(di+size>expected)throw Error('LZ4 raw block overflow');block.copy(output,di);di+=size;}
   else{
     let si=0;
     while(si<block.length){
       const token=block[si++];
       let lit=token>>4;
       if(lit===15){let n;do{if(si>=block.length)throw Error('LZ4 truncated literal');n=block[si++];lit+=n;}while(n===255)}
       if(si+lit>block.length||di+lit>expected)throw Error('LZ4 literal overflow');
       block.copy(output,di,si,si+lit);di+=lit;si+=lit;
       if(si>=block.length)break;
       if(si+2>block.length)throw Error('LZ4 truncated backref');
       const distance=block.readUInt16LE(si);si+=2;
       if(!distance||distance>di)throw Error('LZ4 invalid offset '+distance);
       let length=(token&15)+4;
       if((token&15)===15){let n;do{if(si>=block.length)throw Error('LZ4 truncated match');n=block[si++];length+=n;}while(n===255)}
       if(di+length>expected)throw Error('LZ4 match overflow');
       for(let i=0;i<length;i++)output[di+i]=output[di+i-distance];
       di+=length;
     }
   }
   if(flags&0x10)p+=4; // block checksum
 }
 if(di!==expected)throw Error('LZ4 frame inflated '+di+' expected '+expected);
 return output;
}

function open(name){
 const fd=fs.openSync(path.join(root,name),'r');
 const h=Buffer.alloc(36);fs.readSync(fd,h,0,36,0);
 if(h.toString('ascii',0,3)!=='BSA'||h.readUInt32LE(4)!==105)throw Error('Only Skyrim VR BSA105 supported');
 const flags=h.readUInt32LE(12),folderCount=h.readUInt32LE(16),fileCount=h.readUInt32LE(20),namesBytes=h.readUInt32LE(28);
 const recs=Buffer.alloc(folderCount*24);fs.readSync(fd,recs,0,recs.length,36);
 let end=0;const entries=[];
 for(let i=0;i<folderCount;i++){
  const pos=i*24,n=recs.readUInt32LE(pos+8),start=Number(recs.readBigUInt64LE(pos+16))-namesBytes;
  const len=Buffer.alloc(1);fs.readSync(fd,len,0,1,start);const size=len[0];
  const content=Buffer.alloc(size+n*16);fs.readSync(fd,content,0,content.length,start+1);
  const folder=content.toString('latin1',0,size).replace(/\0/g,'');
  for(let j=0;j<n;j++){
    const x=size+j*16,hash=content.readBigUInt64LE(x),rawSize=content.readUInt32LE(x+8),offset=content.readUInt32LE(x+12);
    entries.push({folder,rawSize,offset,hash});
  }
  end=Math.max(end,start+1+content.length);
 }
 if(entries.length!==fileCount)throw Error('Record count mismatch '+name);
 const names=Buffer.alloc(namesBytes);fs.readSync(fd,names,0,namesBytes,end);
 let at=0,id=0;const byName=new Map();
 for(let j=0;j<names.length;j++)if(names[j]===0){
  const basename=names.toString('latin1',at,j),e=entries[id++];
  e.basename=basename;e.path=(e.folder+'\\'+basename).toLowerCase().replace(/\//g,'\\');
  byName.set(e.path,e);at=j+1;
 }
 function get(name){
  const key=name.toLowerCase().replace(/\//g,'\\');
  const entry=byName.get(key);
  if(!entry)return null;
  const rawSize=entry.rawSize&0x3fffffff;
  let compressed=((flags&4)!==0)!==((entry.rawSize&0x40000000)!==0);
  let data=Buffer.alloc(rawSize);fs.readSync(fd,data,0,rawSize,entry.offset);
  if((flags&0x100)!==0){
   const len=data[0];
   if(len<1 || len+1>data.length)throw Error('Bad BSA filename');
   data=data.subarray(len+1);
  }
  if(!compressed)return data;
  if(data.length<4)throw Error('Compressed file too small');

  const sz=data.readUInt32LE(0);
  // Skyrim Special Edition BSA 105 uses LZ4, unlike version 104.
  if(data[4]===0x78){const v=zlib.inflateSync(data.subarray(4));if(v.length!==sz)throw Error('bad zlib');return v;}
  return data.readUInt32LE(4)===0x184d2204?decodeLz4Frame(data.subarray(4),sz):lz4Decode(data.subarray(4),sz);
 }
 return {get,entries,close:()=>fs.closeSync(fd)};
}
if(require.main===module){
const a=open('Skyrim - Meshes0.bsa');
for(const name of ['meshes\\architecture\\farmhouse\\inn01.nif','meshes\\architecture\\farmhouse\\smith01.nif','meshes\\architecture\\farmhouse\\farmhouse04.nif','meshes\\actors\\character\\character assets\\skeleton.nif']){
 try{const b=a.get(name); console.log(name,'bytes',b?.length,'prefix',b?.subarray(0,145).toString('latin1').replace(/[^\x20-\x7e]/g,'.'));if(b){const dest=path.resolve(__dirname,'../generated/nif-samples',name);fs.mkdirSync(path.dirname(dest),{recursive:true});fs.writeFileSync(dest,b)}}catch(e){console.error('ERROR',e.message)}
}
a.close();
}
module.exports={open};
