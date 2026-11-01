using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

/// <summary>
/// Read-only inspection of ESO head diffuse vertex channels. Does not
/// interpret palette indices as skin textures or invent colour values.
/// </summary>
internal static class FaceVertexColorInspector
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadFile(int length,byte* buffer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetMeshPtr(nint mesh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetMeshInt(nint mesh);
    private static int Int(nint ptr,int off)=>ptr==0?0:Marshal.ReadInt32(ptr,off);
    private static nint Ptr(nint ptr,int off)=>ptr==0?0:Marshal.ReadIntPtr(ptr,off);
    private static string Str(nint ptr)=>ptr==0?"":Marshal.PtrToStringAnsi(ptr)??"";
    private static int SizeOfMember(int kind)=>kind switch
    {
        10 or 19 or 20=>4,
        11 or 12 or 13 or 14=>1,
        15 or 16 or 17 or 18 or 21=>2,
        _=>0
    };
    public static unsafe int Run(string[] args)
    {
        if(args.Length<4)
        {
            Console.Error.WriteLine(
                "Usage: --inspect-face-colors <granny.dll> <resource id> <manifest> <oodle.dll>");
            return 2;
        }
        if(!uint.TryParse(args[1],NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,out uint id))
            throw new ArgumentException("Invalid original ESO resource ID");
        using var archive=new EsoArchive(args[2]);
        using var decoder=OodleDecoder.FromEnvironment(args[3])
            ??throw new FileNotFoundException("Missing original Oodle decoder");
        var record=archive.FindId(id).FirstOrDefault(r=>
            archive.GetIdIndexFields(r.Ordinal).Flags==0);
        if(record.UncompressedSize is <64 or >32_000_000)
            throw new InvalidDataException("Original source model unavailable");
        var raw=archive.ReadDecoded(record,decoder);
        if(raw.Length<64 ||
           !((raw[0]==0xE5&&raw[1]==0x9B)||(raw[0]==0xB8&&raw[1]==0x67)))
            throw new InvalidDataException("Original source is not GR2");
        nint lib=NativeLibrary.Load(args[0]);
        try
        {
            T Load<T>(string name) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib,name));
            var read=Load<ReadFile>("GrannyReadEntireFileFromMemory");
            var getInfo=Load<FileInfo>("GrannyGetFileInfo");
            var free=Load<FreeFile>("GrannyFreeFile");
            var getType=Load<GetMeshPtr>("GrannyGetMeshVertexType");
            var getRaw=Load<GetMeshPtr>("GrannyGetMeshVertices");
            var getCount=Load<GetMeshInt>("GrannyGetMeshVertexCount");
            var getSize=Load<GetMeshInt>("GrannyGetTotalObjectSize");
            fixed(byte* buffer=raw)
            {
                nint file=read(raw.Length,buffer);
                if(file==0)throw new InvalidDataException("Native Granny could not parse GR2");
                try
                {
                    nint info=getInfo(file);
                    int meshCount=Int(info,84);
                    nint meshRefs=Ptr(info,88);
                    if(meshCount<1||meshCount>8||meshRefs==0)
                        throw new InvalidDataException("Expected original head source mesh");
                    Console.WriteLine($"SOURCE_HEAD 0x{id:X8} meshes={meshCount} "+
                        $"embeddedTextures={Int(info,24)} embeddedMaterials={Int(info,36)}");
                    for(int i=0;i<meshCount;i++)
                    {
                        nint mesh=Ptr(meshRefs,i*8);
                        if(mesh==0)continue;
                        nint type=getType(mesh),source=getRaw(mesh);
                        int vertices=getCount(mesh),stride=getSize(type);
                        if(vertices is <1 or >50_000||stride is <4 or >512||
                           source==0||type==0)
                            throw new InvalidDataException("Original face vertex layout invalid");
                        int cursor=0,offset=-1,kind=0;
                        for(int member=0;member<64;member++)
                        {
                            nint field=type+member*44;
                            int format=Int(field,0);
                            if(format==0)break;
                            int count=Int(field,20);
                            int unit=SizeOfMember(format);
                            if(unit==0||count is <1 or >32)
                                throw new InvalidDataException("Unknown face vertex format");
                            if(Str(Ptr(field,4))=="DiffuseColor0")
                            {
                                offset=cursor;kind=format;
                                if(count!=4)
                                    throw new InvalidDataException("Source face colour not RGBA");
                            }
                            cursor+=unit*count;
                        }
                        if(cursor!=stride||offset<0||kind is not (10 or 12))
                            throw new InvalidDataException("Original face colour stream unavailable");
                        if((long)vertices*stride>32_000_000)
                            throw new InvalidDataException("Source head vertex stream too large");
                        var bytes=new byte[vertices*stride];
                        Marshal.Copy(source,bytes,0,bytes.Length);
                        var minimum=Enumerable.Repeat(1.0,4).ToArray();
                        var maximum=new double[4];
                        var mean=new double[4];
                        var colors=new Dictionary<string,int>(StringComparer.Ordinal);
                        for(int v=0;v<vertices;v++)
                        {
                            var values=new double[4];
                            for(int channel=0;channel<4;channel++)
                            {
                                int at=v*stride+offset+(kind==10?channel*4:channel);
                                double value=kind==10?BitConverter.ToSingle(bytes,at):
                                    bytes[at]/255.0;
                                if(!double.IsFinite(value)||value<0||value>1.001)
                                    throw new InvalidDataException("Original face colour out of bounds");
                                values[channel]=value;
                                minimum[channel]=Math.Min(minimum[channel],value);
                                maximum[channel]=Math.Max(maximum[channel],value);
                                mean[channel]+=value;
                            }
                            string key=string.Join(",",values.Select(x=>
                                Math.Clamp((int)Math.Round(x*255),0,255)));
                            colors[key]=colors.TryGetValue(key,out int count)?count+1:1;
                        }
                        Console.WriteLine($"  {Str(Ptr(mesh,0))} originalVerts={vertices} "+
                            $"colourKind={(kind==10?"float32":"uint8")} uniqueRGBA={colors.Count}");
                        Console.WriteLine($"  minRGBA={string.Join(",",minimum.Select(x=>x.ToString("F4",CultureInfo.InvariantCulture)))} "+
                            $"maxRGBA={string.Join(",",maximum.Select(x=>x.ToString("F4",CultureInfo.InvariantCulture)))}");
                        Console.WriteLine($"  meanRGBA={string.Join(",",mean.Select(x=>(x/vertices).ToString("F4",CultureInfo.InvariantCulture)))}");
                        Console.WriteLine($"  mostCommon={string.Join("; ",colors.OrderByDescending(p=>p.Value).Take(6).Select(p=>$"{p.Key}:{p.Value}"))}");
                    }
                    return 0;
                }
                finally{free(file);}
            }
        }
        finally{NativeLibrary.Free(lib);}
    }
}
