using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EsoTest;

/// <summary>
/// Batch-inspects ORIGINAL ESO GR2 character model relationships with the
/// installed native Granny reader, in an isolated process. The scan never
/// changes or contacts the running game. Metadata remains in ignored research.
///
/// Purpose: identify source meshes that actually contain BOTH animation and
/// customization skeleton branches; do not guess defBn/mshBn correspondences.
/// </summary>
internal static class CharacterAssemblyCatalogue
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadEntireFile(int length,byte* data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetFileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetMeshVertexCount(nint mesh);
    private static int Int(nint ptr,int offset)=>ptr==0?0:Marshal.ReadInt32(ptr,offset);
    private static nint Ptr(nint ptr,int offset)=>ptr==0?0:Marshal.ReadIntPtr(ptr,offset);
    private static string Str(nint ptr)
    {
        if(ptr==0)return "";
        nint name=Ptr(ptr,0);
        if(name==0)return "";
        string result=Marshal.PtrToStringAnsi(name)??"";
        return result[..Math.Min(result.Length,160)];
    }
    private sealed record ModelIndex(uint Id,string IndexedModelName,string IndexedMeshName);
    private sealed record MeshSource(string Name,int Vertices,string[] Bindings);
    private sealed record SkeletonSource(string Name,int Bones,int MshBones,int DefBones,
        int CommonAnimationBones,string[] MatchingAnimationNames);
    private sealed record AssemblySource(string Id,string IndexedModel,string IndexedMesh,
        string[] ModelNames,MeshSource[] Meshes,SkeletonSource[] Skeletons,
        string? Error);
    public static unsafe int Run(string[] args)
    {
        if(args.Length<5)
        {
            Console.Error.WriteLine("Usage: --scan-character-assemblies <granny.dll> <manifest> <oodle> <model-index.tsv> <out.json>");
            return 2;
        }
        string GetB64(string b)
        {
            try{return Encoding.UTF8.GetString(Convert.FromBase64String(b));}
            catch(FormatException){return "";}
        }
        var candidates=new List<ModelIndex>();
        foreach(string row in File.ReadLines(args[3]).Skip(1))
        {
            var cols=row.Split('\t');
            if(cols.Length<12||cols[5]!="OK")continue;
            string model=GetB64(cols[7]);
            if(model!="Bip01")continue;
            if(!uint.TryParse(cols[0],NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,out uint id))continue;
            candidates.Add(new ModelIndex(id,model,GetB64(cols[8])));
        }
        var wanted=candidates.Select(c=>c.Id).ToHashSet();
        using var archive=new EsoArchive(args[1]);
        using var decoder=OodleDecoder.FromEnvironment(args[2])
            ??throw new FileNotFoundException("Oodle source decoder unavailable");
        var records=new Dictionary<uint,EsoRecord>();
        for(int i=0;i<archive.Count;i++)
        {
            var record=archive.GetRecord(i);
            if(wanted.Contains(record.FileId)&&!records.ContainsKey(record.FileId)&&
               archive.GetIdIndexFields(i).Flags==0)
                records.Add(record.FileId,record);
        }
        nint granny=NativeLibrary.Load(args[0]);
        try
        {
            T Load<T>(string name) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(
                    NativeLibrary.GetExport(granny,name));
            var read=Load<ReadEntireFile>("GrannyReadEntireFileFromMemory");
            var info=Load<GetFileInfo>("GrannyGetFileInfo");
            var free=Load<FreeFile>("GrannyFreeFile");
            var vertexCount=Load<GetMeshVertexCount>("GrannyGetMeshVertexCount");
            var result=new List<AssemblySource>();
            int errors=0,hybrid=0,mshMesh=0;
            foreach(var candidate in candidates)
            {
                if(!records.TryGetValue(candidate.Id,out var record)||
                   record.UncompressedSize is <64 or >32_000_000)
                {
                    result.Add(new AssemblySource(candidate.Id.ToString("X8"),
                        candidate.IndexedModelName,candidate.IndexedMeshName,
                        Array.Empty<string>(),Array.Empty<MeshSource>(),
                        Array.Empty<SkeletonSource>(),"Archive source unavailable"));
                    errors++;
                    continue;
                }
                try
                {
                    var raw=archive.ReadDecoded(record,decoder);
                    if(raw.Length<64||raw[0]!=0xE5||raw[1]!=0x9B)
                        throw new InvalidDataException("Not GR2");
                    fixed(byte* bytes=raw)
                    {
                        nint file=read(raw.Length,bytes);
                        if(file==0)throw new InvalidDataException("Native Granny rejected GR2");
                        try
                        {
                            nint fileInfo=info(file);
                            if(fileInfo==0)throw new InvalidDataException("Missing file info");
                            var modelNames=new List<string>();
                            int modelCount=Int(fileInfo,96);
                            nint modelList=Ptr(fileInfo,100);
                            if(modelCount is >0 and <=32&&modelList!=0)
                                for(int i=0;i<modelCount;i++)
                                {
                                    nint model=Ptr(modelList,i*8);
                                    if(model!=0)modelNames.Add(Str(model));
                                }
                            var meshes=new List<MeshSource>();
                            int meshCount=Int(fileInfo,84);
                            nint meshList=Ptr(fileInfo,88);
                            if(meshCount is >0 and <=64&&meshList!=0)
                                for(int i=0;i<meshCount;i++)
                                {
                                    nint mesh=Ptr(meshList,i*8);
                                    if(mesh==0)continue;
                                    int bindings=Int(mesh,48);
                                    nint table=Ptr(mesh,52);
                                    if(bindings<0||bindings>256||
                                       (bindings>0&&table==0))
                                        throw new InvalidDataException("Invalid mesh binding table");
                                    var names=new string[bindings];
                                    for(int b=0;b<bindings;b++)
                                        names[b]=Str(table+b*44);
                                    int verts=vertexCount(mesh);
                                    meshes.Add(new MeshSource(Str(mesh),verts,names));
                                }
                            var skeletons=new List<SkeletonSource>();
                            int skelCount=Int(fileInfo,48);
                            nint skeletonList=Ptr(fileInfo,52);
                            if(skelCount is >0 and <=64&&skeletonList!=0)
                                for(int i=0;i<skelCount;i++)
                                {
                                    nint skel=Ptr(skeletonList,i*8);
                                    if(skel==0)continue;
                                    int count=Int(skel,8);
                                    nint bones=Ptr(skel,12);
                                    if(count is <0 or >512||(count>0&&bones==0))
                                        throw new InvalidDataException("Invalid skeleton bone count");
                                    int msh=0,def=0,known=0;
                                    var named=new List<string>();
                                    for(int b=0;b<count;b++)
                                    {
                                        string name=Str(bones+b*164);
                                        if(name.StartsWith("msh",StringComparison.Ordinal))msh++;
                                        if(name.StartsWith("def",StringComparison.Ordinal))def++;
                                        if(name.StartsWith("mshBn_",StringComparison.Ordinal)||
                                           name.StartsWith("mshCtrl_",StringComparison.Ordinal))
                                        {
                                            named.Add(name);
                                            known++;
                                        }
                                    }
                                    skeletons.Add(new SkeletonSource(Str(skel),count,msh,def,known,
                                        named.ToArray()));
                                }
                            if(skeletons.Any(s=>s.MshBones>0&&s.DefBones>0))
                                hybrid++;
                            if(meshes.Any(m=>m.Bindings.Any(b=>
                                b.StartsWith("mshBn_",StringComparison.Ordinal))))
                                mshMesh++;
                            result.Add(new AssemblySource(candidate.Id.ToString("X8"),
                                candidate.IndexedModelName,candidate.IndexedMeshName,
                                modelNames.ToArray(),meshes.ToArray(),skeletons.ToArray(),null));
                        }
                        finally{free(file);}
                    }
                }
                catch(Exception e)
                {
                    errors++;
                    result.Add(new AssemblySource(candidate.Id.ToString("X8"),
                        candidate.IndexedModelName,candidate.IndexedMeshName,
                        Array.Empty<string>(),Array.Empty<MeshSource>(),
                        Array.Empty<SkeletonSource>(),e.GetType().Name+": "+e.Message));
                }
            }
            string output=Path.GetFullPath(args[4]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output+".tmp",JsonSerializer.Serialize(result,
                new JsonSerializerOptions{WriteIndented=true}));
            File.Move(output+".tmp",output,true);
            Console.WriteLine($"Native ESO GR2 character assembly catalog: " +
                $"{candidates.Count} BIP01 model sources, {result.Count-errors} inspected, " +
                $"{hybrid} hybrid skeletons, {mshMesh} mesh sources referencing mshBn, " +
                $"{errors} failures. Metadata-only output: {output}");
            // Only report the actual combined animation/customization rigs
            // on stdout; detailed names for all other game assets live in
            // the ignored research JSON rather than flooding the terminal.
            foreach(var row in result.Where(r=>r.Skeletons.Any(s=>
                s.MshBones>0&&s.DefBones>0)))
            {
                Console.WriteLine($"{row.Id} {row.IndexedMesh} "+
                    $"BONES={string.Join(",",row.Skeletons.Select(s=>$"{s.Bones}/{s.MshBones}msh/{s.DefBones}def"))} "+
                    $"SKIN={string.Join(",",row.Meshes.Select(m=>string.Join("/",m.Bindings)))}");
            }
            return errors>candidates.Count/10?1:0;
        }
        finally{NativeLibrary.Free(granny);}
    }
}
