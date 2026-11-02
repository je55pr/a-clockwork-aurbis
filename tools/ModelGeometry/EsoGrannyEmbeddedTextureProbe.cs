using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

/// <summary>Inspect only original embedded Granny texture metadata, not rendered
/// IDs or guessed archive texture references. Native file-backed metadata only.</summary>
internal static class EsoGrannyEmbeddedTextureProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadFile(int length,byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(nint file);
    private static nint Ptr(nint p,int o)=>p==0?0:Marshal.ReadIntPtr(p,o);
    private static int Int(nint p,int o)=>p==0?0:Marshal.ReadInt32(p,o);
    private static string Str(nint p)=>p==0?"":Marshal.PtrToStringAnsi(p)??"";
    internal static unsafe int Run(string[] a)
    {
        if(a.Length!=4){Console.Error.WriteLine("--probe-embedded-granny-textures <dll> <hexmodelid> <manifest> <oodle>");return 2;}
        uint id=uint.Parse(a[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        using var archive=new EsoArchive(a[2]);
        using var oodle=OodleDecoder.FromEnvironment(a[3])??throw new FileNotFoundException("Original ESO decoder unavailable");
        var rec=archive.FindId(id).FirstOrDefault(r=>archive.GetIdIndexFields(r.Ordinal).Flags==0);
        if(rec.UncompressedSize is <64 or >32_000_000)throw new InvalidDataException("Original GR2 absent");
        byte[] data=archive.ReadDecoded(rec,oodle);
        nint module=NativeLibrary.Load(a[0]);
        try
        {
            T Load<T>(string s) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module,s));
            var read=Load<ReadFile>("GrannyReadEntireFileFromMemory");
            var info=Load<GetInfo>("GrannyGetFileInfo");
            var free=Load<Free>("GrannyFreeFile");
            fixed(byte* p=data)
            {
                nint file=read(data.Length,p);
                if(file==0)throw new InvalidDataException("Granny decode rejected model");
                try
                {
                    nint root=info(file);
                    int count=Int(root,24);nint arr=Ptr(root,28);
                    Console.WriteLine($"SOURCE {id:X8} embedded-textures={count}");
                    if(count is <0 or >64)throw new InvalidDataException("Unreasonable source texture count");
                    for(int i=0;i<count;i++)
                    {
                        nint tex=Ptr(arr,i*8);
                        if(tex==0)continue;
                        string filename=Str(Ptr(tex,0));
                        int type=Int(tex,8),width=Int(tex,12),height=Int(tex,16);
                        int encoding=Int(tex,20),subformat=Int(tex,24);
                        int bpp=Int(tex,28),images=Int(tex,64);
                        Console.WriteLine($" TEXTURE[{i}] filename='{filename}' size={width}x{height} type={type} enc={encoding} sub={subformat} bpp={bpp} images={images}");
                        if(images is >=1 and <=16)
                        {
                            nint list=Ptr(tex,72);
                            for(int k=0;k<images;k++)
                            {
                                nint image=list+k*16;
                                int mips=Int(image,0);
                                Console.WriteLine($"  IMAGE[{k}] mipLevels={mips}");
                                if(mips is <1 or >16)continue;
                                nint levels=Ptr(image,8);
                                for(int m=0;m<Math.Min(2,mips);m++)
                                {
                                    nint level=levels+m*16;
                                    int stride=Int(level,0),size=Int(level,4);
                                    Console.WriteLine($"   MIP[{m}] stride={stride}, pixelBytes={size}, bytesPresent={Ptr(level,8)!=0}");
                                }
                            }
                        }
                    }
                }
                finally{free(file);}
            }
        }
        finally{NativeLibrary.Free(module);}
        return 0;
    }
}
