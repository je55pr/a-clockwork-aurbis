using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EsoTest;

/// <summary>
/// Read-only search for source skeletons that can natively bind original
/// creature meshes. Catalog candidates are independent original GR2 files.
/// This is a discovery audit, NOT proof of matching bind pose/creature animation.
/// Never substitutes a humanoid rig or accepts Granny's unmapped fallback.
/// </summary>
internal static class CreatureRigSurvey
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadMemory(int length,byte* payload);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint FileInfo(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateBinding(nint mesh,nint from,nint to);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint BindingPtr(nint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BindingCount(nint binding);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeBinding(nint binding);

    private static int Int(nint p,int off)=>p==0?0:Marshal.ReadInt32(p,off);
    private static nint Ptr(nint p,int off)=>p==0?0:Marshal.ReadIntPtr(p,off);
    private static string Text(nint p)=>p==0?"":(Marshal.PtrToStringAnsi(p)??"")[..Math.Min(128,(Marshal.PtrToStringAnsi(p)??"").Length)];
    private static string FirstName(nint obj)=>Text(Ptr(obj,0));
    private static nint First(nint root,int countOffset,int ptrOffset)
    {
        int count=Int(root,countOffset);
        nint array=Ptr(root,ptrOffset);
        return count is >=1 and <=16 && array!=0?Ptr(array,0):0;
    }
    private static string Decode(string source)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(source)); }
        catch(FormatException) { return ""; }
    }
    public static unsafe int Run(string[] args)
    {
        if(args.Length<5)
        {
            Console.Error.WriteLine("Usage: --find-original-creature-rigs <granny.dll> <manifest> <oodle> <unlinked-model-names.tsv> <mesh-hex-id> [additional-ids...]");
            return 2;
        }
        var ids=args.Skip(4).Select(s=>uint.Parse(s,NumberStyles.HexNumber,CultureInfo.InvariantCulture)).Distinct().ToArray();
        var candidates=new HashSet<uint>();
        foreach(string row in File.ReadLines(args[3]))
        {
            var fields=row.Split('\t');
            if(fields.Length<13||fields[5]!="OK"||fields[11]!="0")continue;
            string root=Decode(fields[7]);
            if(root is not ("GrannyRootBone" or "Bip01"))continue;
            if(uint.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint id))
                candidates.Add(id);
        }
        var wanted=new HashSet<uint>(candidates);
        foreach(var id in ids)wanted.Add(id);
        using var archive=new EsoArchive(args[1]);
        using var oodle=OodleDecoder.FromEnvironment(args[2])
            ??throw new FileNotFoundException("Original ESO Oodle decoder missing");
        var records=new Dictionary<uint,EsoRecord>();
        for(int ordinal=0;ordinal<archive.Count;ordinal++)
        {
            var r=archive.GetRecord(ordinal);
            if(wanted.Contains(r.FileId)&&!records.ContainsKey(r.FileId)&&
               archive.GetIdIndexFields(r.Ordinal).Flags==0)
                records.Add(r.FileId,r);
        }
        byte[] Load(uint id)
        {
            if(!records.TryGetValue(id,out var r) || r.UncompressedSize is <64 or >32_000_000)
                throw new InvalidDataException($"GR2 0x{id:X8} not in installed archive");
            byte[] b=archive.ReadDecoded(r,oodle);
            if(b.Length<64||b[0]!=0xE5||b[1]!=0x9B)
                throw new InvalidDataException($"Not an original Granny model: 0x{id:X8}");
            return b;
        }
        nint library=NativeLibrary.Load(args[0]);
        try
        {
            T Get<T>(string symbol) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,symbol));
            var open=Get<ReadMemory>("GrannyReadEntireFileFromMemory");
            var info=Get<FileInfo>("GrannyGetFileInfo");
            var free=Get<FreeFile>("GrannyFreeFile");
            var make=Get<CreateBinding>("GrannyNewMeshBinding");
            var toIndices=Get<BindingPtr>("GrannyGetMeshBindingToBoneIndices");
            var bindingCount=Get<BindingCount>("GrannyGetMeshBindingBoneCount");
            var delete=Get<FreeBinding>("GrannyFreeMeshBinding");

            nint Parse(byte[] raw)
            {
                fixed(byte* ptr=raw)return open(raw.Length,ptr);
            }
            foreach(var meshId in ids)
            {
                nint meshFile=Parse(Load(meshId));
                if(meshFile==0)throw new InvalidDataException("Native Granny rejected creature mesh");
                try
                {
                    nint mesh=First(info(meshFile),84,88);
                    if(mesh==0)throw new InvalidDataException("Creature GR2 has no mesh");
                    int bindingTotal=Int(mesh,48);
                    nint sourceBindings=Ptr(mesh,52);
                    if(bindingTotal is <1 or >256 || sourceBindings==0)
                        throw new InvalidDataException("Creature mesh has no usable skin table");
                    string[] names=Enumerable.Range(0,bindingTotal)
                        .Select(i=>Text(Ptr(sourceBindings+i*44,0))).ToArray();
                    if(names.Any(string.IsNullOrEmpty)||names.Distinct(StringComparer.Ordinal).Count()!=names.Length)
                        throw new InvalidDataException("Invalid source creature skin binding names");
                    Console.WriteLine($"CREATURE 0x{meshId:X8} {FirstName(mesh)} original-bindings={bindingTotal}");
                    var results=new List<(uint Id,string Name,int Bones,int Matched,int NativeExact)>();
                    int decoded=0,failures=0;
                    foreach(uint candidateId in candidates.OrderBy(x=>x))
                    {
                        if(!records.ContainsKey(candidateId))continue;
                        nint file=0;
                        try
                        {
                            byte[] raw=Load(candidateId);
                            file=Parse(raw);
                            if(file==0){failures++;continue;}
                            nint skeleton=First(info(file),48,52);
                            if(skeleton==0)continue;
                            int count=Int(skeleton,8);
                            nint bones=Ptr(skeleton,12);
                            if(count is <1 or >512||bones==0)continue;
                            var available=new HashSet<string>(StringComparer.Ordinal);
                            for(int i=0;i<count;i++)available.Add(Text(Ptr(bones+i*164,0)));
                            int matched=names.Count(available.Contains);
                            if(matched<Math.Max(10,bindingTotal/3))continue;
                            int exact=0;
                            // Native confirm only high-confidence candidates:
                            // source mesh with skeleton supplied externally.
                            if(matched==bindingTotal)
                            {
                                nint binding=make(mesh,skeleton,skeleton);
                                if(binding!=0)
                                {
                                    try
                                    {
                                        int n=bindingCount(binding);
                                        nint target=toIndices(binding);
                                        if(n==bindingTotal&&target!=0)
                                        {
                                            exact=n;
                                            for(int i=0;i<n;i++)
                                            {
                                                int index=Int(target,i*4);
                                                if(index<0||index>=count||names[i]!=Text(Ptr(bones+index*164,0)))
                                                {exact=0;break;}
                                            }
                                        }
                                    }
                                    finally{delete(binding);}
                                }
                            }
                            results.Add((candidateId,FirstName(skeleton),count,matched,exact));
                            decoded++;
                        }
                        catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException)
                        {failures++;}
                        finally{if(file!=0)free(file);}
                    }
                    foreach(var r in results.OrderByDescending(x=>x.NativeExact)
                        .ThenByDescending(x=>x.Matched).Take(15))
                        Console.WriteLine($" RIG 0x{r.Id:X8} {r.Name} bones={r.Bones} name-match={r.Matched}/{bindingTotal} native-exact={r.NativeExact}/{bindingTotal}");
                    Console.WriteLine($" CANDIDATES={candidates.Count} QUALIFIED={results.Count} SKIPPED_OR_FAILED={failures}");
                }
                finally{free(meshFile);}
            }
        }
        finally{NativeLibrary.Free(library);}
        return 0;
    }
}
