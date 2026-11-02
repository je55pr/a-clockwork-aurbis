using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EsoTest;

/// <summary>
/// ANFT IDs lack human-readable names. Inspect a bounded slice of their
/// actual GR2 animation records, preserving source identity and track groups.
/// This is metadata only, not animation track playback or a file extractor.
/// All native memory remains inside this isolated ModelGeometry process.
/// </summary>
internal static class AnimationCatalogue
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint OpenFile(int length,byte* data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    private static nint Ptr(nint p,int off)=>
        p==0?0:Marshal.ReadIntPtr(p,off);
    private static int Int(nint p,int off)=>
        p==0?0:Marshal.ReadInt32(p,off);
    private static float Float(nint p,int off)=>
        p==0?0:Marshal.PtrToStructure<float>(p+off);
    private static string Str(nint obj)
    {
        if(obj==0)return "";
        nint name=Ptr(obj,0);
        if(name==0)return "";
        string text=Marshal.PtrToStringAnsi(name)??"";
        return text[..Math.Min(200,text.Length)];
    }

    public static unsafe int Run(string[] args)
    {
        if(args.Length<6)
        {
            Console.Error.WriteLine("Usage: --scan-animation-names <manifest> <oodle> <granny> <anft-tsv> <output-tsv> <limit>");
            return 2;
        }
        int limit=Math.Clamp(int.Parse(args[5],CultureInfo.InvariantCulture),1,40000);
        var ids=File.ReadLines(args[3]).Skip(2).Take(limit).Select(line=>
        {
            var fields=line.Split('\t');
            return fields.Length==4 ?
                uint.Parse(fields[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture) : 0u;
        }).Where(id=>id!=0).Distinct().ToArray();
        var wanted=ids.ToHashSet();
        using var archive=new EsoArchive(args[0]);
        using var decoder=OodleDecoder.FromEnvironment(args[1])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        var records=new Dictionary<uint,EsoRecord>();
        for(int i=0;i<archive.Count;i++)
        {
            uint id=archive.GetRecord(i).FileId;
            if(!wanted.Contains(id)||records.ContainsKey(id)||
                archive.GetIdIndexFields(i).Flags!=0)continue;
            records[id]=archive.GetRecord(i);
        }
        nint module=NativeLibrary.Load(args[2]);
        try
        {
            var open=Marshal.GetDelegateForFunctionPointer<OpenFile>(
                NativeLibrary.GetExport(module,"GrannyReadEntireFileFromMemory"));
            var get=Marshal.GetDelegateForFunctionPointer<GetInfo>(
                NativeLibrary.GetExport(module,"GrannyGetFileInfo"));
            var free=Marshal.GetDelegateForFunctionPointer<FreeFile>(
                NativeLibrary.GetExport(module,"GrannyFreeFile"));
            string output=Path.GetFullPath(args[4]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            int found=0,clips=0,errors=0;
            using(var writer=new StreamWriter(output,false,new UTF8Encoding(false)))
            {
                writer.WriteLine("# Original ESO animation IDs/clip names. Does not contain animation data.");
                writer.WriteLine("id\tname\tseconds\tgroups\ttracks");
                foreach(uint id in ids)
                {
                    if(!records.TryGetValue(id,out var record) ||
                        record.UncompressedSize is <64 or >32_000_000)continue;
                    try
                    {
                        byte[] raw=archive.ReadDecoded(record,decoder);
                        if(raw.Length<64 || raw[0]!=0xE5 || raw[1]!=0x9B)continue;
                        fixed(byte* bytes=raw)
                        {
                            nint file=open(raw.Length,bytes);
                            if(file==0)continue;
                            try
                            {
                                nint info=get(file);
                                if(info==0)continue;
                                int count=Int(info,120);
                                nint table=Ptr(info,124);
                                if(count is <1 or >256 || table==0)continue;
                                found++;
                                for(int c=0;c<count;c++)
                                {
                                    nint anim=Ptr(table,c*8);
                                    if(anim==0)continue;
                                    float seconds=Float(anim,8);
                                    string name=Str(anim).Replace('\t',' ').Replace('\n',' ');
                                    if(name.Length==0)name="(unnamed)";
                                    if(!float.IsFinite(seconds) || seconds<0 || seconds>3600)continue;
                                    int groupCount=Int(anim,20),tracks=0;
                                    nint groups=Ptr(anim,24);
                                    if(groupCount is >0 and <=128 && groups!=0)
                                    {
                                        for(int g=0;g<groupCount;g++)
                                        {
                                            nint group=Ptr(groups,g*8);
                                            tracks+=Math.Clamp(Int(group,20),0,4096);
                                        }
                                    }
                                    writer.WriteLine($"{id:X8}\t{name}\t{seconds:F5}\t{groupCount}\t{tracks}");
                                    clips++;
                                }
                            }
                            finally{free(file);}
                        }
                    }
                    catch(Exception){errors++;}
                }
            }
            Console.WriteLine($"ANFT sampled {ids.Length:N0} IDs: GR2 animation files={found:N0}, "+
                $"clips={clips:N0}, decode failures={errors}, catalogue={output}");
        }
        finally{NativeLibrary.Free(module);}
        return 0;
    }
}
