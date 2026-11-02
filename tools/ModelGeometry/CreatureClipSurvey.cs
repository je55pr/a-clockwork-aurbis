using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EsoTest;

/// <summary>
/// Source-only matching of archived Granny transform-track names to original
/// creature skeletons. Names alone are discovery evidence, NOT a proven
/// source animation association; native pose playback remains a separate test.
/// </summary>
internal static class CreatureClipSurvey
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadMemory(int length,byte* ptr);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Info(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Free(nint handle);
    private static int Int(nint p,int o)=>p==0?0:Marshal.ReadInt32(p,o);
    private static nint Ptr(nint p,int o)=>p==0?0:Marshal.ReadIntPtr(p,o);
    private static string Name(nint p)=>p==0?"":Marshal.PtrToStringAnsi(p)??"";
    private static string Named(nint p)=>Name(Ptr(p,0));
    private static nint First(nint root,int c,int a)
    {
        int count=Int(root,c);nint ptr=Ptr(root,a);
        return count is >=1 and <=8 && ptr!=0?Ptr(ptr,0):0;
    }
    public static unsafe int Run(string[] args)
    {
        if(args.Length<5)
        {
            Console.Error.WriteLine("Usage: --find-original-creature-clips <granny.dll> <manifest> <oodle.dll> <animation-names-all.tsv> <rig-id> [rig-ids...]");
            return 2;
        }
        var clips=new List<(uint Id,string Name)>();
        foreach(var row in File.ReadLines(args[3]))
        {
            var fields=row.Split('\t');
            if(fields.Length<5)continue;
            string name=fields[1];
            if(!(name.Contains("Walk",StringComparison.OrdinalIgnoreCase)||
                 name.Contains("Run",StringComparison.OrdinalIgnoreCase)||
                 name.Contains("Idle",StringComparison.OrdinalIgnoreCase)))continue;
            if(uint.TryParse(fields[0],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint id))
                clips.Add((id,name));
        }
        var rigIds=args.Skip(4).Select(s=>uint.Parse(s,NumberStyles.HexNumber,CultureInfo.InvariantCulture)).Distinct().ToArray();
        var wanted=clips.Select(c=>c.Id).Concat(rigIds).ToHashSet();
        using var archive=new EsoArchive(args[1]);
        using var oodle=OodleDecoder.FromEnvironment(args[2])
            ??throw new FileNotFoundException("Installed ESO Oodle decoder unavailable");
        var records=new Dictionary<uint,EsoRecord>();
        for(int i=0;i<archive.Count;i++)
        {
            var record=archive.GetRecord(i);
            if(wanted.Contains(record.FileId)&&!records.ContainsKey(record.FileId)&&
               archive.GetIdIndexFields(record.Ordinal).Flags==0)
                records.Add(record.FileId,record);
        }
        byte[] Load(uint id)
        {
            if(!records.TryGetValue(id,out var r)||r.UncompressedSize is <64 or >32_000_000)
                throw new InvalidDataException("Original GR2 unavailable");
            var b=archive.ReadDecoded(r,oodle);
            if(b.Length<64||b[0]!=0xE5||b[1]!=0x9B)
                throw new InvalidDataException("Resource not Granny");
            return b;
        }
        nint library=NativeLibrary.Load(args[0]);
        try
        {
            T Get<T>(string s) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library,s));
            var read=Get<ReadMemory>("GrannyReadEntireFileFromMemory");
            var info=Get<Info>("GrannyGetFileInfo");
            var free=Get<Free>("GrannyFreeFile");
            nint Open(byte[] data){fixed(byte* p=data)return read(data.Length,p);}
            var rigNames=new Dictionary<uint,HashSet<string>>();
            foreach(uint id in rigIds)
            {
                nint handle=Open(Load(id));
                if(handle==0)throw new InvalidDataException("Original creature skeleton unreadable");
                try
                {
                    nint skeleton=First(info(handle),48,52);
                    int n=Int(skeleton,8);nint bones=Ptr(skeleton,12);
                    if(n is <20 or >512||bones==0)
                        throw new InvalidDataException("Invalid native creature skeleton");
                    var names=new HashSet<string>(StringComparer.Ordinal);
                    for(int i=0;i<n;i++)names.Add(Named(bones+i*164));
                    rigNames.Add(id,names);
                    Console.WriteLine($"RIG 0x{id:X8} {Named(skeleton)} bones={n}");
                }
                finally{free(handle);}
            }
            var scored=rigIds.ToDictionary(i=>i,i=>new List<(string Name,uint Id,int Hit,int Total)>());
            int scanned=0,errors=0;
            foreach(var (id,name) in clips)
            {
                if(!records.ContainsKey(id))continue;
                nint handle=0;
                try
                {
                    handle=Open(Load(id));
                    if(handle==0){errors++;continue;}
                    nint animation=First(info(handle),120,124);
                    if(animation==0)continue;
                    int groups=Int(animation,20);
                    nint list=Ptr(animation,24);
                    if(groups is <1 or >8 ||list==0)continue;
                    var tracks=new HashSet<string>(StringComparer.Ordinal);
                    for(int g=0;g<groups;g++)
                    {
                        nint group=Ptr(list,g*8);
                        int count=Int(group,20);nint array=Ptr(group,24);
                        if(count is <15 or >512||array==0)continue;
                        for(int t=0;t<count;t++)
                            tracks.Add(Named(array+t*60));
                    }
                    if(tracks.Count<15)continue;
                    scanned++;
                    foreach(var (rig,available) in rigNames)
                    {
                        int hit=tracks.Count(available.Contains);
                        if(hit<Math.Max(16,(int)(tracks.Count*0.80f)))continue;
                        scored[rig].Add((name,id,hit,tracks.Count));
                    }
                }
                catch(Exception ex) when(ex is InvalidDataException or IOException or NotSupportedException)
                {errors++;}
                finally{if(handle!=0)free(handle);}
            }
            Console.WriteLine($"CLIPS_INDEXED={clips.Count} DECODABLE={scanned} FAILURES={errors}");
            foreach(var (rig,found) in scored)
            {
                Console.WriteLine($"RIG 0x{rig:X8} candidate-clips={found.Count} (track coverage; not verified animation assignment)");
                foreach(var (name,id,hit,total) in found
                    .OrderByDescending(x=>x.Hit/(double)x.Total)
                    .ThenByDescending(x=>x.Hit).Take(15))
                    Console.WriteLine($" CLIP 0x{id:X8} {name} track-match={hit}/{total}");
            }
        }
        finally{NativeLibrary.Free(library);}
        return 0;
    }
}
