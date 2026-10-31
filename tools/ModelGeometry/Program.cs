using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

if(args.Length>0 && args[0]=="--sample-animation")
    return GrannyClipSamples.Run(args.Skip(1).ToArray());

if(args.Length>0 && args[0]=="--scan-animation-names")
    return AnimationCatalogue.Run(args.Skip(1).ToArray());

if(args.Length>0 && args[0]=="--index-anft")
    return AnftFileTable.Run(args.Skip(1).ToArray());

if(args.Length>0 && args[0]=="--index-zosft")
    return ZosftFileTable.Run(args.Skip(1).ToArray());

if(args.Length>0 && args[0] is "--inspect-animation" or "--inspect-rig")
    return AnimationSurvey.Run(args.Skip(1).ToArray(),args[0]=="--inspect-rig");

if(args.Length<2||args.Contains("--help"))
{
 Console.WriteLine("Usage: ModelGeometry <granny-dll> <hex-resource-id> [output-file] [manifest] [oodle-dll]");
 return 1;
}
string grannyDll=Path.GetFullPath(args[0]);
if(!uint.TryParse(args[1],System.Globalization.NumberStyles.HexNumber,null,out uint assetId))
    throw new ArgumentException("Expected hexadecimal resource ID.");
string output=Path.GetFullPath(args.Length>2?args[2]:$".research/model-preview-{assetId:X8}.esm");
string manifest=args.Length>3?args[3]:"C:/ESO/The Elder Scrolls Online/depot/eso.mnf";
string oodle=args.Length>4?args[4]:".research/ooz.dll";
using var archive=new EsoArchive(manifest);
using var decoder=OodleDecoder.FromEnvironment(oodle)!;
var record=archive.FindId(assetId).FirstOrDefault(r=>archive.GetIdIndexFields(r.Ordinal).Flags==0);
if(record.UncompressedSize<64)throw new InvalidDataException($"Source ID 0x{assetId:X8} missing");
byte[] payload=archive.ReadDecoded(record,decoder);
if(payload.Length<64||payload[0]!=0xE5||payload[1]!=0x9B)
    throw new InvalidDataException("Source resource is not recognized as Granny2.");
using var library=new MeshBridge(grannyDll);
var meshes=library.Parse(payload);
if(meshes.Count==0)throw new InvalidDataException("Granny file contains no usable triangular geometry.");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
string tmp=output+".tmp";
using(var writer=new BinaryWriter(File.Open(tmp,FileMode.Create,FileAccess.Write,FileShare.None)))
{
 writer.Write(new byte[]{(byte)'E',(byte)'S',(byte)'M',(byte)'5'});
 writer.Write(meshes.Count);
 foreach(var mesh in meshes)
 {
  writer.Write(mesh.Name);
  writer.Write(mesh.Vertices.Length/3);
  writer.Write(mesh.Indices.Length);
  writer.Write(mesh.Groups.Length);
  writer.Write(mesh.HasSourceFrame);
  writer.Write(mesh.HasUv2);
  foreach(float value in mesh.Vertices)writer.Write(value);
  foreach(float value in mesh.Uvs)writer.Write(value);
  foreach(float value in mesh.Uv2)writer.Write(value);
  foreach(float value in mesh.Normals)writer.Write(value);
  foreach(float value in mesh.Tangents)writer.Write(value);
  foreach(int value in mesh.Indices)writer.Write(value);
  foreach(var group in mesh.Groups)
  {
   writer.Write(group.Name);
   writer.Write(group.FirstTriangle);
   writer.Write(group.TriangleCount);
   writer.Write(group.Diffuse);
   writer.Write(group.Normal);
   writer.Write(group.Detail);
   writer.Write(group.Diffuse2);
   writer.Write(group.Specular);
   writer.Write(group.FlagsKnown);
   writer.Write(group.DiffuseEnabled);
   writer.Write(group.NormalEnabled);
   writer.Write(group.SpecularEnabled);
   writer.Write(group.DetailEnabled);
   writer.Write(group.TwoSided);
   writer.Write(group.MaterialType);
   writer.Write(group.AlphaMode);
   writer.Write(group.SecondaryGlossiness);
   writer.Write(group.SecondaryFresnel);
   writer.Write(group.SecondaryGlow);
  }
 }
}
File.Move(tmp,output,true);
Console.WriteLine($"Resource {assetId:X8}; source archive {record.ArchiveIndex}; mesh count {meshes.Count}");
foreach(var mesh in meshes)
 Console.WriteLine($"  {mesh.Name}: {mesh.Vertices.Length/3:N0} vertices / {mesh.Indices.Length/3:N0} triangles");
Console.WriteLine($"Derived geometry preview: {output}");
return 0;

internal sealed class MeshBridge : IDisposable
{
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private unsafe delegate nint ReadGr2(int length,byte* data);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private delegate nint GetInfo(nint file);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private delegate void Free(nint file);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private delegate int MeshInt(nint mesh);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private unsafe delegate void CopyVertices(nint mesh,nint layout,float* dest);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private unsafe delegate void CopyIndices(nint mesh,int bytesPerIndex,int* dest);
 [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
 private delegate nint MeshPtr(nint mesh);

 private readonly nint _module;
 private readonly ReadGr2 _read;
 private readonly GetInfo _info;
 private readonly Free _free;
 private readonly MeshInt _vertexCount,_indexCount;
 private readonly CopyVertices _copyVertices;
 private readonly CopyIndices _copyIndices;
 private readonly nint _p3Layout;
 private readonly MeshPtr _vertexType,_rawVertices;
 private readonly MeshInt _typeSize,_memberUnitSize;
 internal sealed record MaterialGroup(string Name,int FirstTriangle,int TriangleCount,uint Diffuse,uint Normal,uint Detail,uint Diffuse2,uint Specular,
  bool FlagsKnown,bool DiffuseEnabled,bool NormalEnabled,bool SpecularEnabled,bool DetailEnabled,bool TwoSided,int MaterialType,int AlphaMode,
  float SecondaryGlossiness,float SecondaryFresnel,float SecondaryGlow);
 internal sealed record ModelMesh(string Name,float[] Vertices,float[] Uvs,float[] Uv2,bool HasUv2,float[] Normals,float[] Tangents,bool HasSourceFrame,int[] Indices,MaterialGroup[] Groups);
 static nint Ptr(nint p,int off)=>p==0?0:Marshal.ReadIntPtr(p,off);
 static int Int(nint p,int off)=>p==0?0:Marshal.ReadInt32(p,off);
 static string Str(nint ptr)=>ptr==0?"":Marshal.PtrToStringAnsi(ptr)??"";
 static uint TextureId(nint tex)
 {
  if(tex==0)return 0;
  nint ext=Ptr(tex,84);
  return ext==0?0:unchecked((uint)Int(ext,0));
 }
 static uint ChannelId(nint mat,string channel)
 {
  if(mat==0)return 0;
  int count=Int(mat,8);nint maps=Ptr(mat,12);
  if(count<1||count>64||maps==0)return 0;
  for(int i=0;i<count;i++)
  {
   nint m=maps+i*16;
   if(!Str(Ptr(m,0)).Equals(channel,StringComparison.OrdinalIgnoreCase))continue;
   return TextureId(Ptr(Ptr(m,8),20));
  }
  return 0;
 }
 private sealed record SourceSettings(
  bool Known,bool Diffuse,bool Normal,bool Specular,bool Detail,
  bool TwoSided,int MaterialType,int AlphaMode,
  float SecondaryGlossiness,float SecondaryFresnel,float SecondaryGlow);
 private SourceSettings ReadSourceSettings(nint material)
 {
  var unknown=new SourceSettings(false,true,true,false,false,false,-1,-1,0,0,0);
  nint type=Ptr(material,28),obj=Ptr(material,36);
  if(type==0||obj==0)return unknown;
  var fields=new Dictionary<string,(int Type,int Offset)>(StringComparer.OrdinalIgnoreCase);
  int offset=0;
  for(int i=0;i<96;i++)
  {
   nint member=type+i*44;
   int kind=Int(member,0);
   if(kind==0)break;
   int width=Int(member,20);
   if(width==0)width=1;
   if(width<1||width>32)return unknown;
   int unit=_memberUnitSize(member);
   if(unit<=0||unit>2048||offset>4096-unit*width)return unknown;
   string name=Str(Ptr(member,4));
   if(name.Length>0)fields.TryAdd(name,(kind,offset));
   offset+=unit*width;
  }
  int? Flag(string key)
  {
   if(!fields.TryGetValue(key,out var field) ||
      (field.Type!=19&&field.Type!=20))return null;
   return Int(obj,field.Offset);
  }
  float Scalar(string key)
  {
   if(!fields.TryGetValue(key,out var field)||field.Type!=10)return 0;
   float value=Marshal.PtrToStructure<float>(obj+field.Offset);
   return float.IsFinite(value)?value:0;
  }
  int? diffuse=Flag("bDiffuseEnable"),normal=Flag("bNormalEnable"),
       specular=Flag("bSpecularEnable"),detail=Flag("bDetailBlendEnable");
  int? twoSided=Flag("twoSided"),matType=Flag("materialType"),alpha=Flag("alpha");
  bool? ValidFlag(int? v)=>v is 0 or 1;
  if(ValidFlag(diffuse)!=true||ValidFlag(normal)!=true||
     ValidFlag(specular)!=true||ValidFlag(detail)!=true||
     ValidFlag(twoSided)!=true||matType is null||alpha is null)
     return unknown;
  return new SourceSettings(true,diffuse==1,normal==1,specular==1,
    detail==1,twoSided==1,matType.Value,alpha.Value,
    Scalar("ZosGlossiness2"),Scalar("ZosFresnel2"),Scalar("ZosGlow2"));
 }
 public MeshBridge(string path)
 {
  _module=NativeLibrary.Load(path);
  T Load<T>(string name) where T:Delegate =>
      Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module,name));
  _read=Load<ReadGr2>("GrannyReadEntireFileFromMemory");
  _info=Load<GetInfo>("GrannyGetFileInfo");
  _free=Load<Free>("GrannyFreeFile");
  _vertexCount=Load<MeshInt>("GrannyGetMeshVertexCount");
  _indexCount=Load<MeshInt>("GrannyGetMeshIndexCount");
  _copyVertices=Load<CopyVertices>("GrannyCopyMeshVertices");
  _copyIndices=Load<CopyIndices>("GrannyCopyMeshIndices");
  _p3Layout=Marshal.ReadIntPtr(NativeLibrary.GetExport(_module,"GrannyP3VertexType"));
  _vertexType=Load<MeshPtr>("GrannyGetMeshVertexType");
  _rawVertices=Load<MeshPtr>("GrannyGetMeshVertices");
  _typeSize=Load<MeshInt>("GrannyGetTotalObjectSize");
  _memberUnitSize=Load<MeshInt>("GrannyGetMemberUnitSize");
  if(_p3Layout==0)throw new InvalidDataException("Granny P3 layout unavailable");
 }
 public unsafe List<ModelMesh> Parse(byte[] payload)
 {
  fixed(byte* p=payload)
  {
   nint file=_read(payload.Length,p);
   if(file==0)throw new InvalidDataException("Granny could not parse model");
   try
   {
    nint root=_info(file);
    if(root==0)throw new InvalidDataException("Granny file info empty");
    int n=Marshal.ReadInt32(root,84);
    nint array=Marshal.ReadIntPtr(root,88);
    if(n<0||n>256||array==0)throw new InvalidDataException($"Mesh table invalid: {n}");
    var output=new List<ModelMesh>();
    for(int i=0;i<n;i++)
    {
     nint mesh=Marshal.ReadIntPtr(array,i*IntPtr.Size);
     if(mesh==0)continue;
     int count=_vertexCount(mesh),indices=_indexCount(mesh);
     if(count<=0||count>2_000_000||indices<3||indices>6_000_000||indices%3!=0)
     {Console.WriteLine($"  skipping malformed mesh #{i}: {count} vertices / {indices} indices");continue;}
     string name=Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(mesh))??$"Mesh {i}";
     float[] vertices=new float[count*3];
     int[] topology=new int[indices];
     fixed(float* dst=vertices)_copyVertices(mesh,_p3Layout,dst);
     fixed(int* idx=topology)_copyIndices(mesh,4,idx);
     if(topology.Any(k=>k<0||k>=count)||vertices.Any(v=>!float.IsFinite(v)))
     {Console.WriteLine($"  skipping unusable mesh: {name}");continue;}
     float[] uv=new float[count*2];
     bool hasUv=TryRecoverSourceUvs(mesh,count,uv,0);
     float[] uv2=new float[count*2];
     bool hasUv2=TryRecoverSourceUvs(mesh,count,uv2,1);
     var normals=new float[count*3];
     var tangents=new float[count*4];
     bool hasSourceFrame=TryRecoverSourceFrame(mesh,count,normals,tangents);
     Console.WriteLine($"  source tangent frame: {(hasSourceFrame?"verified":"unavailable")}");

     int bindingCount=Int(mesh,36);
     nint bindings=Ptr(mesh,40),topo=Ptr(mesh,28);
     int groupCount=Int(topo,0);
     nint groupsPtr=Ptr(topo,4);
     var groups=new List<MaterialGroup>();
     if(groupCount>0 && groupCount<=256 && bindingCount>=0 && bindingCount<=256 && groupsPtr!=0)
     {
      for(int g=0;g<groupCount;g++)
      {
       nint group=groupsPtr+g*12;
       int matIndex=Int(group,0),triFirst=Int(group,4),triCount=Int(group,8);
       if(matIndex<0||matIndex>=bindingCount||bindings==0||
          triFirst<0||triCount<=0||triFirst+triCount>indices/3)
          continue;
       nint mat=Ptr(bindings,matIndex*8);
       var settings=ReadSourceSettings(mat);
       groups.Add(new MaterialGroup(Str(Ptr(mat,0)),triFirst,triCount,
          hasUv && settings.Diffuse?ChannelId(mat,"diffuse"):0,
          settings.Normal?ChannelId(mat,"normal"):0,
          hasUv2?ChannelId(mat,"detail"):0,
          hasUv?ChannelId(mat,"diffuse2"):0,
          ChannelId(mat,"specular"),
          settings.Known,settings.Diffuse,settings.Normal,
          settings.Specular,settings.Detail,settings.TwoSided,
          settings.MaterialType,settings.AlphaMode,
          settings.SecondaryGlossiness,settings.SecondaryFresnel,settings.SecondaryGlow));
      }
     }
     if(groups.Count==0)groups.Add(new MaterialGroup("No source material",0,indices/3,0,0,0,0,0,false,true,true,false,false,false,-1,-1,0,0,0));
     string uvStats=!hasUv?"not supplied":
         $"({uv.Where((_,u)=>u%2==0).Min():F2}..{uv.Where((_,u)=>u%2==0).Max():F2}, "+
         $"{uv.Where((_,u)=>u%2==1).Min():F2}..{uv.Where((_,u)=>u%2==1).Max():F2})";
     Console.WriteLine($"  source UV0: {uvStats}, UV1={(hasUv2?"verified":"unavailable")}, material groups: {groups.Count}");
     foreach(var group in groups.Take(12))
         Console.WriteLine($"   triangles {group.FirstTriangle}..{group.FirstTriangle+group.TriangleCount} mat '{group.Name}' diffuse=0x{group.Diffuse:X8} normal=0x{group.Normal:X8} flagsKnown={group.FlagsKnown} type={group.MaterialType} alpha={group.AlphaMode} twoSided={group.TwoSided} diffuseEnabled={group.DiffuseEnabled} normalEnabled={group.NormalEnabled} detailEnabled={group.DetailEnabled} specularEnabled={group.SpecularEnabled}");
     float[] lo={float.MaxValue,float.MaxValue,float.MaxValue},hi={float.MinValue,float.MinValue,float.MinValue};
     for(int k=0;k<vertices.Length;k++){int axis=k%3;lo[axis]=Math.Min(lo[axis],vertices[k]);hi[axis]=Math.Max(hi[axis],vertices[k]);}
     Console.WriteLine($"  mesh {name} original XYZ: [{lo[0]:F2},{lo[1]:F2},{lo[2]:F2}] to [{hi[0]:F2},{hi[1]:F2},{hi[2]:F2}]");
     output.Add(new ModelMesh(name,vertices,uv,uv2,hasUv2,normals,tangents,hasSourceFrame,topology,groups.ToArray()));
    }
    return output;
   }
   finally {_free(file);}
  }
 }
 private static float DecodeOneAxis(short s)
 {
  float packed=(s+32768f)/32768f;
  float fraction=packed-MathF.Floor(packed);
  return (2f*fraction-1f)*(32769f/32767f);
 }
 private static float DecodeNormalSign(short s)
 {
  float packed=(s+32768f)/32768f;
  return MathF.Floor(packed)*2f-1f;
 }
 private static System.Numerics.Vector3 ReadNormal16(byte[] src,int at)
 {
  short a=BitConverter.ToInt16(src,at),b=BitConverter.ToInt16(src,at+2);
  float x=DecodeOneAxis(a),y=DecodeOneAxis(b);
  float z=MathF.Sqrt(MathF.Max(0,1-x*x-y*y))*DecodeNormalSign(a);
  return new System.Numerics.Vector3(x,y,z);
 }
 private static System.Numerics.Vector3 ReadNormal32(byte[] src,int at) =>
  new(BitConverter.ToSingle(src,at),BitConverter.ToSingle(src,at+4),BitConverter.ToSingle(src,at+8));
 private bool TryRecoverSourceFrame(nint mesh,int count,float[] normals,float[] tangents)
 {
  nint layout=_vertexType(mesh),vertexData=_rawVertices(mesh);
  if(layout==0||vertexData==0)return false;
  int stride=_typeSize(layout);
  if(stride<=0||stride>512||(long)stride*count>96_000_000)return false;
  string[] wanted={"Normal","Tangent","Binormal"};
  var offsets=new int[]{-1,-1,-1};
  var formats=new int[3];
  int offset=0;
  for(int i=0;i<64;i++)
  {
   nint member=layout+i*44;
   int type=Int(member,0);
   if(type==0)break;
   int width=Int(member,20);
   if(width is <1 or >32)return false;
   int bytes=type switch {10 or 19 or 20=>4,11 or 12 or 13 or 14=>1,15 or 16 or 17 or 18 or 21=>2,_=>0};
   if(bytes==0)return false;
   string name=Str(Ptr(member,4));
   for(int f=0;f<wanted.Length;f++)
   {
    if(!name.Equals(wanted[f],StringComparison.OrdinalIgnoreCase))continue;
    if((type==21&&width==2)||(type==10&&width==3))
    {offsets[f]=offset;formats[f]=type;}
   }
   offset+=width*bytes;
   if(offset>stride)return false;
  }
  if(offset!=stride||offsets.Any(o=>o<0)){Console.WriteLine($"  Frame offsets {string.Join(",",offsets)} stride={stride} size={offset}");return false;}
  var source=new byte[stride*count];
  Marshal.Copy(vertexData,source,0,source.Length);
  int weak=0,positive=0,negative=0,parallel=0;
  for(int i=0;i<count;i++)
  {
   var vec=new System.Numerics.Vector3[3];
   for(int f=0;f<3;f++)
    vec[f]=formats[f]==21?ReadNormal16(source,i*stride+offsets[f]):
        ReadNormal32(source,i*stride+offsets[f]);
   if(vec.Any(v=>!float.IsFinite(v.X)||!float.IsFinite(v.Y)||!float.IsFinite(v.Z)||
        v.LengthSquared()<0.6f||v.LengthSquared()>1.4f)){Console.WriteLine($"  Frame invalid vector at vertex {i}: {string.Join(" ",vec.Select(x=>x.ToString()))}");return false;}
   var n=System.Numerics.Vector3.Normalize(vec[0]);
   var u=System.Numerics.Vector3.Normalize(vec[1]);
   var v=System.Numerics.Vector3.Normalize(vec[2]);
   // The original MaterialPS calls GetNormalMapping(normal, N, -U, -V).
   // Godot expects tangent XYZ and the handedness of cross(N,T).
   var t=-u;
   t-=System.Numerics.Vector3.Dot(n,t)*n;
   if(t.LengthSquared()<0.05f)
   {
    // A few original vertices contain a degenerate U tangent.
    // Use the source binormal and geometry normal where possible.
    parallel++;
    t=System.Numerics.Vector3.Cross(-v,n);
    if(t.LengthSquared()<0.05f)
    {
     var axis=Math.Abs(n.Y)<0.9f?System.Numerics.Vector3.UnitY:System.Numerics.Vector3.UnitX;
     t=System.Numerics.Vector3.Cross(n,axis);
    }
   }
   t=System.Numerics.Vector3.Normalize(t);
   float handedness=System.Numerics.Vector3.Dot(System.Numerics.Vector3.Cross(n,t),-v);
   if(Math.Abs(handedness)<0.1f)weak++;
   else if(handedness>0)positive++;
   else negative++;
   normals[i*3]=n.X;normals[i*3+1]=n.Y;normals[i*3+2]=n.Z;
   tangents[i*4]=t.X;tangents[i*4+1]=t.Y;tangents[i*4+2]=t.Z;
   tangents[i*4+3]=handedness>0?1f:-1f;
  }
  Console.WriteLine($"  Source frame chirality: positive={positive} negative={negative} weak={weak} parallel={parallel}");
  return weak<=Math.Max(8,count/20) && parallel<=Math.Max(8,count/20);
 }
 private bool TryRecoverSourceUvs(nint mesh,int count,float[] uv,int channel)
 {
  nint layout=_vertexType(mesh),ptr=_rawVertices(mesh);
  if(layout==0||ptr==0)return false;
  int stride=_typeSize(layout);
  if(stride<=0||stride>512||((long)stride*count)>96_000_000)return false;
  int offset=0,uvOffset=-1,uvFormat=0;
  for(int i=0;i<64;i++)
  {
   nint member=layout+i*44;
   int type=Int(member,0);
   if(type==0)break;
   int width=Int(member,20);
   if(width<1)width=1;
   if(width>32)return false;
   int bytes=type switch
   {
    10 or 19 or 20=>4,
    11 or 12 or 13 or 14=>1,
    15 or 16 or 17 or 18 or 21=>2,
    _=>0
   };
   if(bytes==0)return false;
   string name=Str(Ptr(member,4));
   if(name.Equals("TextureCoord"+channel,StringComparison.OrdinalIgnoreCase)||
      name.Equals("TextureCoordinates"+channel,StringComparison.OrdinalIgnoreCase))
   {
    if(width>=2){uvOffset=offset;uvFormat=type;}
   }
   offset+=width*bytes;
   if(offset>stride)return false;
  }
  if(uvOffset<0||offset!=stride||uvOffset+4>stride)return false;
  if(uvFormat!=21&&uvFormat!=10)return false;
  var source=new byte[stride*count];
  Marshal.Copy(ptr,source,0,source.Length);
  int invalidCount=0;
  for(int i=0;i<count;i++)
  {
   int at=i*stride+uvOffset;
   if(uvFormat==21)
   {
    // ESO's 16-bit TextureCoord streams are signed 1/1024 fixed point,
    // NOT IEEE float16 despite the Granny member's generic Real16 type.
    uv[i*2]=BitConverter.ToInt16(source,at)/1024f;
    uv[i*2+1]=BitConverter.ToInt16(source,at+2)/1024f;
   }
   else
   {
    uv[i*2]=BitConverter.ToSingle(source,at);
    uv[i*2+1]=BitConverter.ToSingle(source,at+4);
   }
   if(!float.IsFinite(uv[i*2])||!float.IsFinite(uv[i*2+1])||
       Math.Abs(uv[i*2])>100000||Math.Abs(uv[i*2+1])>100000)
   {invalidCount++;}
  }
  if(invalidCount!=0)Console.WriteLine($"  Invalid UV vertices: {invalidCount}/{count}");
  return invalidCount==0;
 }
 public void Dispose()=>NativeLibrary.Free(_module);
}
