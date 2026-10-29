using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

if(args.Contains("--help")||args.Length==0)
{
    Console.WriteLine("""
    ESO Model Name Indexer (read-only, opt-in third-party Granny DLL)
      dotnet run --project tools/ModelNameIndexer -- \
        --manifest "C:/ESO/The Elder Scrolls Online/depot/eso.mnf" \
        --granny-dll "C:/path/to/granny2_x64.dll" \
        --oodle-dll ".research/ooz.dll" \
        --output ".research/model-names.tsv" --limit 500
    Options: --limit N (attempts per run, default 500); --archive-min N;
             --archive-max N; --retry (retry failed matching records).
    Reruns skip cached source-matching records and pick up where they left off.
    Derived names only; original installed archives are never modified.
    """);
    return;
}
string Opt(string key,string fallback="")
{
    int i=Array.IndexOf(args,key);
    return i>=0&&i+1<args.Length?args[i+1]:fallback;
}
int Number(string key,int fallback) =>
    int.TryParse(Opt(key),out int n)?n:fallback;
string manifest=Opt("--manifest","C:/ESO/The Elder Scrolls Online/depot/eso.mnf");
string granny=Opt("--granny-dll",Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??"");
string oodle=Opt("--oodle-dll",".research/ooz.dll");
string output=Path.GetFullPath(Opt("--output",".research/model-names.tsv"));
int limit=Math.Clamp(Number("--limit",500),1,100000);
int minArchive=Number("--archive-min",0),maxArchive=Number("--archive-max",int.MaxValue);
bool retry=args.Contains("--retry");
if(granny.Length==0||!File.Exists(granny))
    throw new ArgumentException("Pass an existing, locally supplied --granny-dll file. No native libraries are bundled.");
if(!File.Exists(manifest))
    throw new FileNotFoundException("Manifest unavailable",manifest);
if(!File.Exists(oodle))
    throw new FileNotFoundException("Decoder unavailable",oodle);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var archive=new EsoArchive(manifest);
using var decoder=OodleDecoder.FromEnvironment(oodle)!;
var catalogue=archive.FindId(49).Where(r=>{
    var fields=archive.GetIdIndexFields(r.Ordinal);
    return fields.Flags==0x6000 && fields.Field2==0;
}).OrderByDescending(r=>r.Ordinal).First();
byte[] metadata=archive.ReadDecoded(catalogue,decoder);
var table=DepotMetadataContainer.Parse(metadata,metadata.Length);
var linkedIds=new HashSet<uint>();
foreach(int i in Enumerable.Range(0,table.Entries.Count))
{
    byte[] row=table.ReadEntry(i);
    if(row.Length>=4)linkedIds.Add(BinaryPrimitives.ReadUInt32BigEndian(row));
}
var prior=EsoModelNameIndex.Load(output);
Console.WriteLine($"Source records: {archive.Count:N0}; linked model IDs: {linkedIds.Count:N0}; index rows previously saved: {prior.Count:N0}.");
using var library=new GrannyBridge(granny);
if(!File.Exists(output) || new FileInfo(output).Length==0)
    File.WriteAllText(output,EsoModelNameIndex.Header+Environment.NewLine);
else if(File.ReadLines(output).FirstOrDefault()!=EsoModelNameIndex.Header)
    throw new InvalidDataException("Index version mismatch. Choose a new --output path.");
using var writer=new StreamWriter(output,true){AutoFlush=true};
int attempted=0,ok=0,failed=0,nonGr2=0,skipped=0;
var handled=new HashSet<uint>();
for(int i=0;i<archive.Count && attempted<limit;i++)
{
    var original=archive.GetRecord(i);
    if(original.ArchiveIndex<minArchive||original.ArchiveIndex>maxArchive||
        archive.GetIdIndexFields(i).Flags!=0||
        !linkedIds.Contains(original.FileId)||!handled.Add(original.FileId))
        continue;
    if(prior.TryGetValue(original.FileId,out var old) &&
       EsoModelNameIndex.MatchesSource(old,original.ArchiveIndex,original.Offset,
           original.CompressedSize,original.UncompressedSize) &&
       (old.Status=="OK" || (!retry && old.Status=="NOT_GR2")))
    {skipped++;continue;}
    attempted++;
    EsoModelNameIndex.Record current;
    try
    {
        if(original.UncompressedSize is <64 or >32_000_000)
            throw new InvalidDataException("Invalid source model size");
        byte[] bytes=archive.ReadDecoded(original,decoder);
        if(bytes.Length<16 || !((bytes[0]==0xE5&&bytes[1]==0x9B)||
                                (bytes[0]==0xB8&&bytes[1]==0x67)))
        {
            nonGr2++;
            current=new(original.FileId,original.ArchiveIndex,original.Offset,
                original.CompressedSize,original.UncompressedSize,
                "NOT_GR2","","","","",0,0,0);
        }
        else
        {
            var result=library.Read(bytes);
            current=new(original.FileId,original.ArchiveIndex,original.Offset,
                original.CompressedSize,original.UncompressedSize,
                result.HasValue?"OK":"INVALID_GR2",result?.Source??"",
                result?.Model??"",result?.Mesh??"",result?.Animations??"",
                result?.ModelCount??0,result?.MeshCount??0,result?.AnimationCount??0);
            if(result.HasValue)ok++;else failed++;
        }
    }
    catch(Exception ex)
    {
        failed++;
        current=new(original.FileId,original.ArchiveIndex,original.Offset,
            original.CompressedSize,original.UncompressedSize,
            "ERROR_"+ex.GetType().Name,"","","","",0,0,0);
    }
    writer.WriteLine(EsoModelNameIndex.Serialize(current));
    prior[current.Id]=current;
    if(attempted%100==0)Console.WriteLine(
        $"Attempted {attempted:N0}; named {ok:N0}; other/non-GR2 {nonGr2:N0}; errors {failed:N0}; skipped {skipped:N0}.");
}
Console.WriteLine($"FINISHED: examined {attempted:N0}, decoded {ok:N0}, not GR2 {nonGr2:N0}, errors {failed:N0}, skipped cached {skipped:N0}; output: {output}");

internal sealed class GrannyBridge : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadGr2(int length,byte* data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(nint file);
    private readonly nint _module;
    private readonly ReadGr2 _read;
    private readonly GetInfo _info;
    private readonly Free _free;
    internal readonly record struct Details(string Source,string Model,string Mesh,
        string Animations,int ModelCount,int MeshCount,int AnimationCount);
    public GrannyBridge(string path)
    {
        _module=NativeLibrary.Load(Path.GetFullPath(path));
        _read=Marshal.GetDelegateForFunctionPointer<ReadGr2>(
            NativeLibrary.GetExport(_module,"GrannyReadEntireFileFromMemory"));
        _info=Marshal.GetDelegateForFunctionPointer<GetInfo>(
            NativeLibrary.GetExport(_module,"GrannyGetFileInfo"));
        _free=Marshal.GetDelegateForFunctionPointer<Free>(
            NativeLibrary.GetExport(_module,"GrannyFreeFile"));
    }
    public unsafe Details? Read(byte[] payload)
    {
        fixed(byte* p=payload)
        {
            nint file=_read(payload.Length,p);
            if(file==0)return null;
            try
            {
                nint root=_info(file);
                if(root==0)return null;
                // Verified packed x64 granny_file_info layout, not default struct packing.
                string source=SafeString(Marshal.ReadIntPtr(root,16));
                string model=FirstNames(root,96,100,1);
                string mesh=FirstNames(root,84,88,1);
                string animations=FirstNames(root,120,124,16);
                int modelCount=Marshal.ReadInt32(root,96);
                int meshCount=Marshal.ReadInt32(root,84);
                int animCount=Marshal.ReadInt32(root,120);
                return new Details(source,model,mesh,animations,modelCount,meshCount,animCount);
            }
            finally {_free(file);}
        }
    }
    private static string FirstNames(nint root,int countOffset,int pointerOffset,int max)
    {
        int count=Marshal.ReadInt32(root,countOffset);
        nint array=Marshal.ReadIntPtr(root,pointerOffset);
        if(count<1||count>100000||array==0)return "";
        var results=new List<string>();
        for(int i=0;i<Math.Min(count,max);i++)
        {
            nint obj=Marshal.ReadIntPtr(array,i*IntPtr.Size);
            if(obj==0)continue;
            string value=SafeString(Marshal.ReadIntPtr(obj));
            if(value.Length>0&&!results.Contains(value,StringComparer.Ordinal))results.Add(value);
        }
        return string.Join(" | ",results);
    }
    private static string SafeString(nint pointer)
    {
        if(pointer==0)return "";
        // Read up to a sane limit in case the model embeds strange metadata.
        string value=Marshal.PtrToStringAnsi(pointer)??"";
        return value.Length>512?value[..512]:value;
    }
    public void Dispose(){NativeLibrary.Free(_module);}
}
