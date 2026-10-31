using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

/// <summary>
/// Read-only use of ESO's installed Granny runtime to measure whether native
/// mesh binding can map a customization mesh onto an animation skeleton.
/// Lives exclusively inside ModelGeometry's isolated child process.
/// Nothing is changed in the running ESO client. Never invent a bone map.
/// </summary>
internal static class NativeGrannyBindingProbe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint OpenMemory(int count,byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NewBinding(nint mesh,nint from,nint to);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetBindingPointer(nint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetBindingCount(nint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBinding(nint binding);
    private static int Int(nint p,int offset)=>p==0?0:Marshal.ReadInt32(p,offset);
    private static nint Ptr(nint p,int offset)=>p==0?0:Marshal.ReadIntPtr(p,offset);
    private static string Name(nint p)
    {
        if(p==0)return "";
        string s=Marshal.PtrToStringAnsi(p)??"";
        return s[..Math.Min(s.Length,100)];
    }
    private static string SkeletonName(nint ptr)=>Name(Ptr(ptr,0));
    private static nint First(nint info,int countOff,int arrayOff)
    {
        int count=Int(info,countOff);
        if(count<1||count>1024)throw new InvalidDataException("Source GR2 resource type missing");
        nint array=Ptr(info,arrayOff);
        if(array==0)throw new InvalidDataException("GR2 file references missing");
        nint obj=Ptr(array,0);
        if(obj==0)throw new InvalidDataException("GR2 first source entry missing");
        return obj;
    }
    public static unsafe int Run(string[] args)
    {
        if(args.Length<5)
        {
            Console.Error.WriteLine("Usage: --inspect-native-binding <granny.dll> <meshId> <animationSkeletonId> <manifest> <oodle.dll>");
            return 2;
        }
        uint meshId=uint.Parse(args[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        uint rigId=uint.Parse(args[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        using var archive=new EsoArchive(args[3]);
        using var decoder=OodleDecoder.FromEnvironment(args[4])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        byte[] ReadResource(uint id)
        {
            var record=archive.FindId(id).FirstOrDefault(r=>
                archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(record.UncompressedSize is <64 or >32_000_000)
                throw new InvalidDataException($"GR2 0x{id:X8} unavailable");
            byte[] raw=archive.ReadDecoded(record,decoder);
            if(raw.Length<64||raw[0]!=0xE5||raw[1]!=0x9B)
                throw new InvalidDataException("Not a native ESO GR2");
            return raw;
        }
        byte[] meshRaw=ReadResource(meshId),rigRaw=ReadResource(rigId);
        nint library=NativeLibrary.Load(args[0]);
        try
        {
            T Load<T>(string name) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,name));
            var open=Load<OpenMemory>("GrannyReadEntireFileFromMemory");
            var info=Load<FileInfo>("GrannyGetFileInfo");
            var free=Load<FreeFile>("GrannyFreeFile");
            var make=Load<NewBinding>("GrannyNewMeshBinding");
            var getTo=Load<GetBindingPointer>("GrannyGetMeshBindingToBoneIndices");
            var getFrom=Load<GetBindingPointer>("GrannyGetMeshBindingFromBoneIndices");
            var count=Load<GetBindingCount>("GrannyGetMeshBindingBoneCount");
            var delete=Load<FreeBinding>("GrannyFreeMeshBinding");
            fixed(byte* mbytes=meshRaw,rbytes=rigRaw)
            {
                nint meshFile=open(meshRaw.Length,mbytes),rigFile=open(rigRaw.Length,rbytes);
                if(meshFile==0||rigFile==0)
                    throw new InvalidDataException("Granny cannot open source assets");
                try
                {
                    nint mi=info(meshFile),ri=info(rigFile);
                    nint mesh=First(mi,84,88);
                    nint fromSkeleton=First(mi,48,52);
                    nint targetSkeleton=First(ri,48,52);
                    int bones=Int(mesh,48);
                    nint bindings=Ptr(mesh,52);
                    int toBoneCount=Int(targetSkeleton,8);
                    nint targetBoneData=Ptr(targetSkeleton,12);
                    if(bones is <1 or >512||bindings==0||toBoneCount is <1 or >512)
                        throw new InvalidDataException("Native mesh/rig tables out of bounds");
                    Console.WriteLine($"NATIVE ESO MESH 0x{meshId:X8}; source={SkeletonName(fromSkeleton)}, {Int(fromSkeleton,8)} bones; target animation 0x{rigId:X8}={SkeletonName(targetSkeleton)}, {toBoneCount} bones");
                    // 'from' is the GR2 mesh's own source skeleton; 'to'
                    // is the actual, separately stored animation skeleton.
                    nint binding=make(mesh,fromSkeleton,targetSkeleton);
                    if(binding==0)
                    {
                        Console.WriteLine("Native GrannyNewMeshBinding returned null (no compatible mapping)");
                        return 1;
                    }
                    try
                    {
                        int n=count(binding);
                        nint toArray=getTo(binding),fromArray=getFrom(binding);
                        if(n is <1 or >512 || toArray==0 || fromArray==0)
                            throw new InvalidDataException("Native mesh binding returned unusable arrays");
                        int exact=0,unmapped=0,mismatch=0;
                        for(int i=0;i<n;i++)
                        {
                            int to=Int(toArray,4*i),from=Int(fromArray,4*i);
                            string part=Name(Ptr(bindings,i*44));
                            string mapped=to>=0&&to<toBoneCount
                                ?Name(Ptr(targetBoneData+to*164,0)):"(unmapped)";
                            if(to<0){unmapped++;continue;}
                            if(to>=toBoneCount)
                                throw new InvalidDataException("Native target index invalid");
                            if(part==mapped)exact++;
                            else mismatch++;
                            if(i<48)
                                Console.WriteLine($"  {i,2} {part,-23} src={from,3} -> dst={to,3} ({mapped})");
                        }
                        Console.WriteLine($"NATIVE_BINDING_RESULT target={rigId:X8} total={n}, exact={exact}, name-differing={mismatch}, index-unmapped={unmapped}");
                        if(mismatch>0 || unmapped>0)
                        {
                            Console.WriteLine("INCOMPATIBLE_SOURCE_RIG: Granny returned mismatched/fallback bone indices. " +
                                "Never attach these costume vertices to this skeleton without a genuine source mapping.");
                            return 3;
                        }
                        Console.WriteLine("COMPATIBLE_SOURCE_RIG: every source mesh binding name resolves to its original target bone");
                    }
                    finally{delete(binding);}
                }
                finally{free(meshFile);free(rigFile);}
            }
        }
        finally{NativeLibrary.Free(library);}
        return 0;
    }
}
