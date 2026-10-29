const fs=require('fs'),path=require('path'),zlib=require('zlib');
const gameRoot=process.env.SKYRIM_DATA||'C:\\Program Files (x86)\\Steam\\steamapps\\common\\SkyrimVR\\Data';
const plugins=['Skyrim.esm']; const around={x:20700,y:-46000,radius:10500};
const result={source:'SkyrimVR/Skyrim.esm',center:{x:around.x,y:around.y},scale:0.01,terrain:[],objects:[],actors:[],people:[]};
const counters={},names=new Map(),refs=[],lands=[],cells=[],worlds=[],records=[],groups=[];
function parse(file){
 const buffer=fs.readFileSync(path.join(gameRoot,file));
 function scan(start,end,ancestry){
  let off=start;
  while(off+24<=end){
   const kind=buffer.toString('ascii',off,off+4),length=buffer.readUInt32LE(off+4);
   if(kind==='GRUP'){
    if(length<24||off+length>end)throw Error('Bad group '+off);
    const type=buffer.readInt32LE(off+12),raw=buffer.readUInt32LE(off+8);
    const label=type===0?buffer.toString('ascii',off+8,off+12):raw;
    scan(off+24,off+length,ancestry.concat({type,label}));
    off+=length;
   }else{
    if(off+24+length>end)throw Error('Bad record '+kind+' '+off);
    const keep=['TES4','NPC_','ACHR','CELL','WRLD','LAND','REFR','STAT','MSTT','TREE','FLOR','ACTI','FURN','CONT','DOOR','LIGH','FURN','PACK','FACT','LVLN','LVLI','RACE','OTFT','ARMO','ARMA','WRLD','LTEX','TXST'].includes(kind);
    if(keep){
     records.push({kind,id:buffer.readUInt32LE(off+12),flags:buffer.readUInt32LE(off+8),start:off+24,len:length,parents:ancestry,buffer});
    }
    off+=24+length;
   }
  }
  if(off!==end)throw Error('Group remainder '+off+'..'+end);
 }
 scan(0,buffer.length,[]);
}
function fields(r){
 if(r.fields)return r.fields;
 let b=r.buffer.subarray(r.start,r.start+r.len);
 if(r.flags&0x40000){
  const expected=b.readUInt32LE(0);b=zlib.inflateSync(b.subarray(4));
  if(expected!==b.length)throw Error('Bad inflate '+r.kind);
 }
 const res=new Map();let at=0,extended=0;
 while(at+6<=b.length){
  const tag=b.toString('ascii',at,at+4),normal=b.readUInt16LE(at+4);at+=6;
  if(tag==='XXXX'){extended=b.readUInt32LE(at);at+=normal;continue;}
  const length=extended||normal;extended=0;
  if(at+length>b.length)break;
  if(!res.has(tag))res.set(tag,[]);
  res.get(tag).push(b.subarray(at,at+length));at+=length;
 }
 r.fields=res;return res;
}
const field=(r,k)=>fields(r).get(k)?.[0]||null;
const field4=(r,k)=>{const b=field(r,k);return b&&b.length>=4?b.readUInt32LE(0):0};
const edid=(r)=>field(r,'EDID')?.toString('utf8').replace(/\0.*$/,'')||'';
const fnum=(b,i)=>b?.length>=i+4?b.readFloatLE(i):0;
function info(r){return {kind:r.kind,id:r.id,editor:edid(r),model:edid(r),nif:field(r,'MODL')?.toString('utf8').replace(/\0.*$/,'')||null};}
parse('Skyrim.esm');
const index=new Map(records.filter(r=>!['ACHR','REFR','LAND'].includes(r.kind)).map(r=>[r.id,r]));
for(const r of records.filter(r=>r.kind==='CELL')){
 const p=r.parents;
 const x=field(r,'XCLC');
 if(x?.length>=8)cells.push({id:r.id,name:edid(r),x:x.readInt32LE(0),y:x.readInt32LE(4),world:p.findLast(k=>k.type===1)?.label||null,record:r});
}
console.log('CELLS',cells.length,'EXTERIOR',cells.filter(x=>x.world===0x3c).length);
console.log('RIVERWOOD NAMED EXTERIOR',cells.filter(c=>(c.name||'').toLowerCase().includes('riverwood')).map(c=>({name:c.name,coords:[c.x,c.y],id:c.id.toString(16)})));
const cx=Math.floor(around.x/4096),cy=Math.floor(around.y/4096);
const minx=cx-2,maxx=cx+2,miny=cy-2,maxy=cy+2;
console.log('PATCH',cx,cy,minx,maxx,miny,maxy);
const chosenCells=cells.filter(c=>c.world===0x3c&&c.x>=minx&&c.x<=maxx&&c.y>=miny&&c.y<=maxy);
console.log('PATCH CELL COUNT',chosenCells.length,'named',chosenCells.filter(c=>c.name).map(c=>c.name));
const cset=new Map(chosenCells.map(c=>[c.id,c]));
const used=new Map();
const landscapeTextures={};
function textureDefinition(formId){
 if(!formId)return null;
 const key=formId.toString(16).padStart(8,'0');
 if(Object.prototype.hasOwnProperty.call(landscapeTextures,key))return key;
 const ltex=index.get(formId);
 if(!ltex||ltex.kind!=='LTEX')return null;
 const textureSet=index.get(field4(ltex,'TNAM'));
 if(!textureSet||textureSet.kind!=='TXST')return null;
 const file=field(textureSet,'TX00')?.toString('utf8').replace(/\0.*$/,'').replace(/\\/g,'/');
 if(!file)return null;
 const normal=field(textureSet,'TX01')?.toString('utf8').replace(/\0.*$/,'').replace(/\\/g,'/');
 landscapeTextures[key]={name:edid(ltex),textureSet:field4(ltex,'TNAM').toString(16).padStart(8,'0'),diffuse:file,normal:normal||null};
 return key;
}
function terrainLayers(f){
 const btxt=f.get('BTXT')||[],atxt=f.get('ATXT')||[],vtxt=f.get('VTXT')||[];
 if(atxt.length!==vtxt.length)throw Error('LAND alpha layer and weights counts mismatch');
 const parseLayer=b=>({
  texture:b.length>=8?textureDefinition(b.readUInt32LE(0)):null,
  quadrant:b.length>=8?b.readUInt8(4):null,
  layer:b.length>=8?b.readUInt16LE(6):null
 });
 const base=btxt.map(parseLayer);
 const alpha=atxt.map((layer,i)=>{
  const p=parseLayer(layer),v=vtxt[i],weights=[];
  if(v.length%8)throw Error('Invalid VTXT record byte length '+v.length);
  for(let at=0;at<v.length;at+=8){
   const index=v.readUInt16LE(at),opacity=v.readFloatLE(at+4);
   if(index>=289||!Number.isFinite(opacity))throw Error('Invalid VTXT vertex index/opacity '+index+':'+opacity);
   weights.push([index,+Math.max(0,Math.min(1,opacity)).toFixed(6)]);
  }
  return {...p,weights};
 });
 return {base,alpha};
}

for(const r of records.filter(r=>['LAND','REFR','ACHR'].includes(r.kind))){
 const gp=r.parents;
 const cr=gp.findLast(x=>x.type===6);
 if(!cr)continue;
 const cell=cset.get(cr.label);
 if(!cell)continue;
 const f=fields(r);
 if(r.kind==='LAND'){
  const vh=field(r,'VHGT'),vc=field(r,'VCLR'),vn=field(r,'VNML'),vt=field(r,'VTEX');
  const row={cell:cell.id.toString(16),x:cell.x,y:cell.y,fieldNames:[...f.keys()],heightLen:vh?.length||0,colorLen:vc?.length||0,normalLen:vn?.length||0,textureLen:vt?.length||0};
  console.log('LAND',JSON.stringify(row));
  if(vh?.length>=1093){
   let rowStart=vh.readFloatLE(0)*8,heights=[];
   for(let z=0;z<33;z++){
    let h=rowStart;
    for(let x=0;x<33;x++){
     h+=vh.readInt8(4+z*33+x)*8;
     heights.push(h);
     if(x===0)rowStart=h;
    }
   }
   result.terrain.push({...row,...terrainLayers(f),heights,vertexColors:vc?.length>=3267?vc.subarray(0,3267).toString('base64'):null});
  }
 }else{
  const baseId=field4(r,'NAME'),base=index.get(baseId);
  const p=field(r,'DATA');
  const xyz=p?.length>=24?[fnum(p,0),fnum(p,4),fnum(p,8)]:null;
  if(xyz && Math.hypot(xyz[0]-around.x,xyz[1]-around.y)<=around.radius){
    const b=base?info(base):null;
    const row={ref:r.id.toString(16).padStart(8,'0'),baseId:baseId.toString(16).padStart(8,'0'),kind:b?.kind||'unknown',editor:b?.editor||'',nif:b?.nif||null,
      cell:cell.name||'',cellId:cell.id.toString(16),xyz,rot:p?.length>=24?[fnum(p,12),fnum(p,16),fnum(p,20)]:null,flags:r.flags};
    if(r.kind==='ACHR') result.actors.push(row);else result.objects.push(row);
    used.set(row.baseId,{id:row.baseId,kind:row.kind,editor:row.editor,nif:row.nif});
  }
 }
}
function loadEnglishStrings(){
 const file=path.join(gameRoot,'Strings','Skyrim_English.STRINGS');
 const b=fs.readFileSync(file),count=b.readUInt32LE(0),base=8+count*8,values=new Map();
 for(let i=0;i<count;i++){
  const id=b.readUInt32LE(8+i*8),at=base+b.readUInt32LE(12+i*8),end=b.indexOf(0,at);
  if(end>=at)values.set(id,b.toString('utf8',at,end));
 }
 return values;
}
const englishStrings=loadEnglishStrings();
const residents=[];
for(const ref of records.filter(r=>r.kind==='ACHR')){
 const cellG=ref.parents.findLast(p=>p.type===6);
 if(!cellG)continue;
 const cellRecord=index.get(cellG.label);
 if(!cellRecord||cellRecord.kind!=='CELL')continue;
 const cellName=edid(cellRecord);
 const worldRef=ref.parents.findLast(p=>p.type===1);
 if(worldRef||!/^Riverwood/i.test(cellName))continue;
 const npcId=field4(ref,'NAME'),npc=index.get(npcId);
 if(!npc||npc.kind!=='NPC_')continue;
 const full=field(npc,'FULL');
 const display=full?.length>=4?englishStrings.get(full.readUInt32LE(0)):null;
 const npcFields=fields(npc);
 const schedules=(npcFields.get('PKID')||[]).filter(b=>b.length>=4).map(b=>{
  const id=b.readUInt32LE(0),record=index.get(id);return record?edid(record):id.toString(16);
 });
 const factions=(npcFields.get('SNAM')||[]).filter(b=>b.length>=4).map(b=>{
  const record=index.get(b.readUInt32LE(0));return record?edid(record):b.readUInt32LE(0).toString(16);
 });
 const p=field(ref,'DATA');
 residents.push({
  npc:npcId.toString(16).padStart(8,'0'),
  ref:ref.id.toString(16).padStart(8,'0'),
  editor:edid(npc),name:display||edid(npc),startingInterior:cellName,
  startingPosition:p?.length>=24?[p.readFloatLE(0),p.readFloatLE(4),p.readFloatLE(8)]:null,
  packages:schedules,factions,
  race:field4(npc,'RNAM').toString(16).padStart(8,'0'),
  outfit:field4(npc,'DOFT').toString(16).padStart(8,'0')
 });
}
result.people=residents.sort((a,b)=>a.name.localeCompare(b.name));
console.log('RIVERWOOD INTERIOR RESIDENTS',residents.length,'named',residents.map(p=>p.name+': '+p.startingInterior));

console.log('TERRAIN',result.terrain.length,'OBJECTS',result.objects.length,'ACTORS',result.actors.length,'UNIQUE OBJECT BASES',used.size);
console.log('OBJECT TYPES',Object.entries(result.objects.reduce((acc,x)=>((acc[x.kind]=(acc[x.kind]||0)+1),acc),{})));
console.log('COMMON NIF',Object.entries(result.objects.reduce((acc,x)=>((acc[x.nif]=(acc[x.nif]||0)+1),acc),{})).sort((a,b)=>b[1]-a[1]).slice(0,40));
console.log('ACTORS',result.actors.map(x=>x.editor+' '+x.xyz.join(',')+' '+x.cell).slice(0,100));
const out=path.resolve(__dirname,'../generated/riverwood.json');
fs.mkdirSync(path.dirname(out),{recursive:true});
fs.writeFileSync(out,JSON.stringify({...result,landscapeTextures,models:[...used.values()]},null,0));
console.log('WROTE',out,fs.statSync(out).size);
