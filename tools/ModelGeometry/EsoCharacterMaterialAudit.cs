using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

/// <summary>
/// Audits material metadata that actually occurs in ESO's original GR2.
/// This is a read-only and intentionally conservative inspector: it prints
/// names, material map structure, and simple scalar extended-data members.
/// It does NOT treat arbitrary numbers as texture IDs, construct substitute
/// textures, or infer material names not present in the source.
/// </summary>
internal static class EsoCharacterMaterialAudit
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadEntireFile(int length, byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetFileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MemberUnitSize(nint member);
    private static int Int(nint p,int off)=>p==0?0:Marshal.ReadInt32(p,off);
    private static nint Ptr(nint p,int off)=>p==0?0:Marshal.ReadIntPtr(p,off);
    private static float Float(nint p,int off)=>p==0?0:Marshal.PtrToStructure<float>(p+off);
    private static string Str(nint p)=>p==0?"":Marshal.PtrToStringAnsi(p)??"";
    private static string Named(nint obj)=>Str(Ptr(obj,0));

    private static void DescribeExtended(nint type,nint obj,MemberUnitSize getSize)
    {
        if(type==0||obj==0)
        {
            Console.WriteLine("  extended-data: absent");
            return;
        }
        Console.WriteLine("  extended-data members:");
        int offset=0, seen=0;
        for(int i=0;i<96;i++)
        {
            nint member=type+i*44;
            int kind=Int(member,0);
            if(kind==0)break;
            int width=Int(member,20);
            if(width==0)width=1;
            if(width is <1 or >64)
            {
                Console.WriteLine("    [unrecognized array width; stop]");
                break;
            }
            int unit=getSize(member);
            if(unit<1||unit>512||(long)offset+unit*width>8192)
            {
                Console.WriteLine("    [unknown member size; stop]");
                break;
            }
            string name=Str(Ptr(member,4));
            if(name.Length>100)name=name[..100];
            string display="";
            if(kind==8 && unit==IntPtr.Size)
            {
                var values=new List<string>();
                for(int j=0;j<Math.Min(width,12);j++)
                {
                    nint value=Ptr(obj,offset+j*unit);
                    string text=value==0?"<null>":Str(value);
                    if(text.Length>320)text=text[..320];
                    values.Add(text);
                }
                display=string.Join(",",values);
            }
            else if(kind is 19 or 20 && unit==4)
            {
                var values=new List<string>();
                for(int j=0;j<Math.Min(width,12);j++)
                    values.Add(Int(obj,offset+j*unit).ToString(CultureInfo.InvariantCulture));
                display=string.Join(",",values);
            }
            else if(kind==10&&unit==4)
            {
                var values=new List<string>();
                for(int j=0;j<Math.Min(width,12);j++)
                {
                    float v=Float(obj,offset+j*unit);
                    values.Add(float.IsFinite(v)?v.ToString("G8",CultureInfo.InvariantCulture):"non-finite");
                }
                display=string.Join(",",values);
            }
            else if(kind is 12 or 13 && unit==1)
            {
                var values=new List<string>();
                for(int j=0;j<Math.Min(width,12);j++)
                    values.Add(Marshal.ReadByte(obj,offset+j).ToString(CultureInfo.InvariantCulture));
                display=string.Join(",",values);
            }
            Console.WriteLine($"    {name} [kind={kind},size={unit},count={width}] "+
                (display.Length>0?"value="+display:"(complex/unknown)"));
            offset+=unit*width;
            seen++;
        }
        Console.WriteLine($"  extended-data examined: {seen} members");
    }

    private static void Describe(nint material,MemberUnitSize getSize,string indent,int depth)
    {
        if(material==0)return;
        string name=Named(material);
        Console.WriteLine($"{indent}original-material name='{name}'");
        nint texture=Ptr(material,20);
        if(texture!=0)
        {
            // TextureSourceId may be stored in extended metadata on
            // GrannyTexture, but we must not call unrelated ID bytes a
            // verified external texture reference.
            Console.WriteLine($"{indent}  embedded GrannyTexture name='{Named(texture)}' "+
                "(external source ID not established)");
        }
        int count=Int(material,8);
        nint maps=Ptr(material,12);
        Console.WriteLine($"{indent}  native channel maps={count}");
        if(count is >=1 and <=24 && maps!=0 && depth<2)
            for(int i=0;i<count;i++)
            {
                nint usage=maps+i*16;
                string channel=Str(Ptr(usage,0));
                nint nested=Ptr(usage,8);
                Console.WriteLine($"{indent}    channel[{i}]='{channel}', nested="+
                    (nested!=0?Named(nested):"<none>"));
                if(nested!=0)Describe(nested,getSize,indent+"      ",depth+1);
            }
        DescribeExtended(Ptr(material,28),Ptr(material,36),getSize);
    }

    public static unsafe int Run(string[] args)
    {
        if(args.Length<4)
        {
            Console.Error.WriteLine(
                "Usage: --audit-character-material <granny.dll> <id> <manifest> <oodle.dll>");
            return 2;
        }
        uint id=uint.Parse(args[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        using var archive=new EsoArchive(args[2]);
        using var oodle=OodleDecoder.FromEnvironment(args[3])??
            throw new FileNotFoundException("Original ESO decoder missing");
        var item=archive.FindId(id).FirstOrDefault(r=>
            archive.GetIdIndexFields(r.Ordinal).Flags==0);
        if(item.UncompressedSize is <64 or >32_000_000)
            throw new InvalidDataException("Requested ESO model missing");
        byte[] payload=archive.ReadDecoded(item,oodle);
        nint granny=NativeLibrary.Load(args[0]);
        try
        {
            T Load<T>(string name) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(granny,name));
            var read=Load<ReadEntireFile>("GrannyReadEntireFileFromMemory");
            var info=Load<GetFileInfo>("GrannyGetFileInfo");
            var free=Load<FreeFile>("GrannyFreeFile");
            var unitSize=Load<MemberUnitSize>("GrannyGetMemberUnitSize");
            fixed(byte* raw=payload)
            {
                nint file=read(payload.Length,raw);
                if(file==0)throw new InvalidDataException("Native Granny rejected source");
                try
                {
                    nint root=info(file);
                    int sourceMaterials=Int(root,36);
                    int sourceTextures=Int(root,24);
                    int meshCount=Int(root,84);
                    nint meshes=Ptr(root,88);
                    Console.WriteLine($"GR2 0x{id:X8} sourceMaterialCount={sourceMaterials}, "+
                        $"embeddedGrannyTextures={sourceTextures}, meshes={meshCount}");
                    for(int i=0;i<meshCount&&i<12;i++)
                    {
                        nint mesh=Ptr(meshes,i*8);
                        if(mesh==0)continue;
                        int count=Int(mesh,36);
                        nint bindings=Ptr(mesh,40);
                        Console.WriteLine($" mesh[{i}]='{Named(mesh)}' native-material-bindings={count}");
                        if(count is >=1 and <=24 && bindings!=0)
                            for(int m=0;m<count;m++)
                            {
                                Console.WriteLine($"  mesh material[{m}]");
                                Describe(Ptr(bindings,m*8),unitSize,"  ",0);
                            }
                    }
                    return 0;
                }
                finally{free(file);}
            }
        }
        finally{NativeLibrary.Free(granny);}
    }
}
