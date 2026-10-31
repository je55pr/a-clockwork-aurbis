using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using EsoTest;

/// <summary>
/// Sample real ESO GR2 animation transform curves with the installed Granny
/// runtime. Source-only JSON records, never committed or bundled with the game.
/// This retains source track names; applying them to character customization
/// bones requires an explicit, verified retarget mapping (mshBn != defBn).
/// </summary>
internal static class GrannyClipSamples
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadEntireFile(int length,byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetFileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDimension(nint curve);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void Evaluate(int dimension,
        [MarshalAs(UnmanagedType.I1)] bool normalize,
        [MarshalAs(UnmanagedType.I1)] bool backwardsLoop,
        nint curve,
        [MarshalAs(UnmanagedType.I1)] bool forwardsLoop,
        float duration,float time,float* result,float* identity);
    private static nint Ptr(nint p,int offset)=>
        p==0?0:Marshal.ReadIntPtr(p,offset);
    private static int Int(nint p,int offset)=>
        p==0?0:Marshal.ReadInt32(p,offset);
    private static float Float(nint p,int offset)=>
        p==0?0:Marshal.PtrToStructure<float>(p+offset);
    private static string Name(nint obj)
    {
        if(obj==0)return "";
        nint ptr=Ptr(obj,0);
        if(ptr==0)return "";
        string name=Marshal.PtrToStringAnsi(ptr)??"";
        return name[..Math.Min(name.Length,150)];
    }
    private sealed record Frame(float Time,float[] Position,
        float[] Orientation,float[] ScaleShear);
    private sealed record BoneTrack(string Name,int Flags,Frame[] Frames);
    private sealed record Clip(string Id,string Name,float DurationSeconds,
        int FramesPerSecond,string TrackGroup,BoneTrack[] Tracks);

    public static unsafe int Run(string[] args)
    {
        if(args.Length<6)
        {
            Console.Error.WriteLine("Usage: --sample-animation <granny-dll> <hex-ID> <manifest> <oodle> <output-json> <fps>");
            return 2;
        }
        uint id=uint.Parse(args[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        int fps=Math.Clamp(int.Parse(args[5],CultureInfo.InvariantCulture),2,60);
        using var archive=new EsoArchive(args[2]);
        using var decoder=OodleDecoder.FromEnvironment(args[3])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        var record=archive.FindId(id).FirstOrDefault(r=>
            archive.GetIdIndexFields(r.Ordinal).Flags==0);
        if(record.UncompressedSize is <128 or >32_000_000)
            throw new InvalidDataException("Source animation ID unavailable");
        byte[] payload=archive.ReadDecoded(record,decoder);
        if(payload.Length<64||payload[0]!=0xE5||payload[1]!=0x9B)
            throw new InvalidDataException("Selected source asset is not a GR2 animation");
        nint module=NativeLibrary.Load(args[0]);
        try
        {
            var read=Marshal.GetDelegateForFunctionPointer<ReadEntireFile>(
                NativeLibrary.GetExport(module,"GrannyReadEntireFileFromMemory"));
            var get=Marshal.GetDelegateForFunctionPointer<GetFileInfo>(
                NativeLibrary.GetExport(module,"GrannyGetFileInfo"));
            var free=Marshal.GetDelegateForFunctionPointer<FreeFile>(
                NativeLibrary.GetExport(module,"GrannyFreeFile"));
            var dimension=Marshal.GetDelegateForFunctionPointer<GetDimension>(
                NativeLibrary.GetExport(module,"GrannyCurveGetDimension"));
            var evaluate=Marshal.GetDelegateForFunctionPointer<Evaluate>(
                NativeLibrary.GetExport(module,"GrannyEvaluateCurveAtT"));
            fixed(byte* bytes=payload)
            {
                nint file=read(payload.Length,bytes);
                if(file==0)throw new InvalidDataException("Granny failed to read GR2 source");
                try
                {
                    nint info=get(file);
                    if(Int(info,120)!=1)
                        throw new InvalidDataException("Expected one original animation");
                    nint anim=Ptr(Ptr(info,124),0);
                    float duration=Float(anim,8);
                    if(!float.IsFinite(duration)||duration<0.01f||duration>60)
                        throw new InvalidDataException("Unexpected animation length");
                    if(Int(anim,20)!=1)
                        throw new InvalidDataException("Expected one source track group");
                    nint group=Ptr(Ptr(anim,24),0);
                    int n=Int(group,20);
                    nint tracks=Ptr(group,24);
                    if(n is <1 or >1000||tracks==0)
                        throw new InvalidDataException("Source transform tracks not available");
                    var output=new List<BoneTrack>();
                    int frames=Math.Clamp((int)Math.Ceiling(fps*duration)+1,2,3601);
                    var identityP=new float[]{0,0,0};
                    var identityQ=new float[]{0,0,0,1};
                    var identityS=new float[]{1,0,0,0,1,0,0,0,1};
                    float[] Sample(nint curve,float[] identity,float time)
                    {
                        int len=dimension(curve);
                        if(len==0)return (float[])identity.Clone(); // identity curve
                        if(len!=identity.Length)
                            throw new InvalidDataException($"Unexpected animation curve dimension {len}/{identity.Length}");
                        var result=new float[len];
                        fixed(float* dest=result, original=identity)
                            evaluate(len,false,false,curve,false,duration,time,dest,original);
                        if(result.Any(f=>!float.IsFinite(f)||Math.Abs(f)>1000000f))
                            throw new InvalidDataException("Invalid native animation sample");
                        return result;
                    }
                    for(int i=0;i<n;i++)
                    {
                        nint track=tracks+i*60;
                        string name=Name(track);
                        if(name.Length==0)continue;
                        var poses=new Frame[frames];
                        for(int frame=0;frame<frames;frame++)
                        {
                            float time=Math.Min(duration,frame/(float)fps);
                            var p=Sample(track+28,identityP,time);
                            var q=Sample(track+12,identityQ,time);
                            var s=Sample(track+44,identityS,time);
                            float mag=(float)Math.Sqrt(q.Sum(v=>v*v));
                            if(mag<0.001f||Math.Abs(mag-1)>0.12f)
                                throw new InvalidDataException($"Quaternion invalid for {name} frame {frame}");
                            for(int k=0;k<q.Length;k++)q[k]/=mag;
                            poses[frame]=new Frame(time,p,q,s);
                        }
                        output.Add(new BoneTrack(name,Int(track,8),poses));
                    }
                    var sample=new Clip(id.ToString("X8"),Name(anim),duration,
                        fps,Name(group),output.ToArray());
                    string path=Path.GetFullPath(args[4]);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    string tmp=path+".tmp";
                    File.WriteAllText(tmp,JsonSerializer.Serialize(sample,
                        new JsonSerializerOptions{WriteIndented=false}));
                    File.Move(tmp,path,true);
                    Console.WriteLine($"GR2 clip {id:X8} '{sample.Name}': "+
                        $"{sample.DurationSeconds:F3}s, {output.Count} original bone tracks, "+
                        $"{frames} source-sampled frames at {fps}Hz; {path}");
                    return 0;
                }
                finally{free(file);}
            }
        }
        finally{NativeLibrary.Free(module);}
    }
}
