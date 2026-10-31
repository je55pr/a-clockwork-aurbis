using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using EsoTest;

/// <summary>
/// Samples ESO's original animation on its original, separately archived
/// GrannyRootBone skeleton using the *installed ESO Granny runtime*.
/// No guessed bone mapping, handmade curve interpolation, procedural gait,
/// generated appearance, or access to the running game process.
/// Output lives only under ignored .research/ and contains pose samples.
/// The native runtime owns all animation mixing and root hierarchy evaluation.
/// </summary>
internal static class GrannyOriginalPoseSampler
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint OpenMemory(int count,byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Instantiate(nint model);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeModel(nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Play(float start,nint animation,nint instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeControl(nint control);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClock(nint instance,float seconds);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NewPose(int bones);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreePose(nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PoseCount(nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint PoseTransform(nint pose,int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RestPose(nint skeleton,int first,int count,nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Sample(nint model,int first,int count,nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint NewWorldPose(int bones);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeWorldPose(nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetWorldBoneCount(nint pose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BuildWorld(nint skeleton,int first,int count,
        nint local,nint offset4x4,nint output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint World4x4(nint pose,int bone);
    private static nint Ptr(nint p,int offset)=>
        p==0?0:Marshal.ReadIntPtr(p,offset);
    private static int Int(nint p,int offset)=>
        p==0?0:Marshal.ReadInt32(p,offset);
    private static float Float(nint p,int offset)=>
        p==0?0:Marshal.PtrToStructure<float>(p+offset);
    private static string Name(nint p)
    {
        if(p==0)return "";
        string name=Marshal.PtrToStringAnsi(p)??"";
        return name[..Math.Min(name.Length,160)];
    }
    private static nint One(nint info,int countOffset,int addressOffset)
    {
        int count=Int(info,countOffset);
        if(count!=1)throw new InvalidDataException($"Expected exactly one source model/asset; found {count}");
        nint refs=Ptr(info,addressOffset);
        if(refs==0 || Ptr(refs,0)==0)
            throw new InvalidDataException("Source object unavailable");
        return Ptr(refs,0);
    }
    private sealed record PoseBone(string Name,int Parent);
    private sealed record PoseTransformData(
        int Flags,float[] Position,float[] Orientation,float[] ScaleShear);
    private sealed record PoseFrame(float Time,PoseTransformData[] Bones,float[][] WorldPositions);
    private sealed record PoseExport(string SkeletonId,string AnimationId,
        string SkeletonName,string AnimationName,int FrameRate,
        float DurationSeconds,PoseBone[] Bones,PoseFrame[] Frames);
    private static PoseTransformData Transform(nint pointer)
    {
        if(pointer==0)throw new InvalidDataException("Native pose lacks bone transform");
        int flags=Int(pointer,0);
        float[] Values(int offset,int count)
        {
            var arr=new float[count];
            for(int i=0;i<count;i++)
            {
                arr[i]=Float(pointer,offset+4*i);
                if(!float.IsFinite(arr[i])||Math.Abs(arr[i])>1_000_000f)
                    throw new InvalidDataException("Source pose contains invalid float");
            }
            return arr;
        }
        float[] p=Values(4,3),q=Values(16,4),scale=Values(32,9);
        float magnitude=MathF.Sqrt(q.Sum(v=>v*v));
        if(magnitude<0.5f||magnitude>1.5f)
            throw new InvalidDataException("Native quaternion magnitude invalid");
        return new PoseTransformData(flags,p,q,scale);
    }
    public static unsafe int Run(string[] args)
    {
        if(args.Length<7)
        {
            Console.Error.WriteLine(
                "Usage: --sample-native-pose <granny> <skeletonId> <animationId> <manifest> <oodle> <output.json> <fps>");
            return 2;
        }
        uint skeletonId=uint.Parse(args[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        uint animationId=uint.Parse(args[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        int fps=Math.Clamp(int.Parse(args[6],CultureInfo.InvariantCulture),1,60);
        using var archive=new EsoArchive(args[3]);
        using var decoder=OodleDecoder.FromEnvironment(args[4])
            ??throw new FileNotFoundException("Oodle decoder unavailable");
        byte[] ReadResource(uint id)
        {
            var source=archive.FindId(id).FirstOrDefault(r=>
                archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(source.UncompressedSize is <64 or >32_000_000)
                throw new InvalidDataException($"Missing GR2 resource 0x{id:X8}");
            var buffer=archive.ReadDecoded(source,decoder);
            if(buffer.Length<64||buffer[0]!=0xE5||buffer[1]!=0x9B)
                throw new InvalidDataException($"Resource 0x{id:X8} not GR2");
            return buffer;
        }
        byte[] skeletonRaw=ReadResource(skeletonId);
        byte[] animationRaw=ReadResource(animationId);
        nint module=NativeLibrary.Load(args[0]);
        try
        {
            T Load<T>(string name) where T:Delegate=>
                Marshal.GetDelegateForFunctionPointer<T>(
                    NativeLibrary.GetExport(module,name));
            var open=Load<OpenMemory>("GrannyReadEntireFileFromMemory");
            var get=Load<GetInfo>("GrannyGetFileInfo");
            var free=Load<FreeFile>("GrannyFreeFile");
            var instantiate=Load<Instantiate>("GrannyInstantiateModel");
            var freeModel=Load<FreeModel>("GrannyFreeModelInstance");
            var play=Load<Play>("GrannyPlayControlledAnimation");
            var freeControl=Load<FreeControl>("GrannyFreeControl");
            var setClock=Load<SetClock>("GrannySetModelClock");
            var newPose=Load<NewPose>("GrannyNewLocalPose");
            var freePose=Load<FreePose>("GrannyFreeLocalPose");
            var poseCount=Load<PoseCount>("GrannyGetLocalPoseBoneCount");
            var poseTransform=Load<PoseTransform>("GrannyGetLocalPoseTransform");
            var rest=Load<RestPose>("GrannyBuildRestLocalPose");
            var sample=Load<Sample>("GrannySampleModelAnimations");
            var newWorldPose=Load<NewWorldPose>("GrannyNewWorldPose");
            var freeWorldPose=Load<FreeWorldPose>("GrannyFreeWorldPose");
            var getWorldBoneCount=Load<GetWorldBoneCount>("GrannyGetWorldPoseBoneCount");
            var buildWorld=Load<BuildWorld>("GrannyBuildWorldPose");
            var getWorldMatrix=Load<World4x4>("GrannyGetWorldPose4x4");
            fixed(byte* sptr=skeletonRaw,aptr=animationRaw)
            {
                nint sf=open(skeletonRaw.Length,sptr);
                nint af=open(animationRaw.Length,aptr);
                if(sf==0||af==0)
                    throw new InvalidDataException("Could not open native Granny sources");
                try
                {
                    nint si=get(sf),ai=get(af);
                    nint model=One(si,96,100);
                    nint skeleton=One(si,48,52);
                    nint animation=One(ai,120,124);
                    int bones=Int(skeleton,8);
                    nint boneTable=Ptr(skeleton,12);
                    if(bones is <1 or >512||boneTable==0)
                        throw new InvalidDataException("Original skeleton is invalid");
                    string modelName=Name(Ptr(model,0));
                    string trackName=Name(Ptr(Ptr(Ptr(animation,24),0),0));
                    if(modelName!=trackName)
                        throw new InvalidDataException(
                            $"Track target '{trackName}' does not match model '{modelName}'");
                    float duration=Float(animation,8);
                    if(!float.IsFinite(duration)||duration<0.05f||duration>120f)
                        throw new InvalidDataException("Invalid native animation duration");
                    var boneList=new PoseBone[bones];
                    for(int i=0;i<bones;i++)
                    {
                        nint bone=boneTable+i*164;
                        string name=Name(Ptr(bone,0));
                        int parent=Int(bone,8);
                        if(name.Length==0||parent>=i||parent< -1)
                            throw new InvalidDataException("Native bone source invalid");
                        boneList[i]=new PoseBone(name,parent);
                    }
                    nint instance=instantiate(model);
                    if(instance==0)throw new InvalidDataException("Native model instantiate failed");
                    try
                    {
                        nint control=play(0f,animation,instance);
                        if(control==0)throw new InvalidDataException(
                            "Native Granny could not bind original animation to original model");
                        try
                        {
                            nint pose=newPose(bones);
                            if(pose==0)throw new InvalidDataException("Native local pose allocation failed");
                            try
                            {
                                if(poseCount(pose)!=bones)
                                    throw new InvalidDataException("Native local pose bone count mismatch");
                                nint world=newWorldPose(bones);
                                if(world==0)throw new InvalidDataException("Native world pose unavailable");
                                try
                                {
                                    if(getWorldBoneCount(world)!=bones)
                                        throw new InvalidDataException("Native world pose bone count mismatch");
                                    // Native Granny world-pose construction:
                                    // do not reinterpret animation axes or infer
                                    // parent/child transforms in the renderer.
                                    float[] identity={
                                        1,0,0,0,
                                        0,1,0,0,
                                        0,0,1,0,
                                        0,0,0,1
                                    };
                                    int frameCount=Math.Clamp(
                                        (int)MathF.Ceiling(duration*fps)+1,2,7201);
                                    var frames=new PoseFrame[frameCount];
                                    fixed(float* identityPtr=identity)
                                    {
                                        for(int frame=0;frame<frameCount;frame++)
                                        {
                                            float t=MathF.Min(duration,frame/(float)fps);
                                            setClock(instance,t);
                                            rest(skeleton,0,bones,pose);
                                            sample(instance,0,bones,pose);
                                            buildWorld(skeleton,0,bones,pose,(nint)identityPtr,world);
                                            var transforms=new PoseTransformData[bones];
                                            var positions=new float[bones][];
                                            for(int i=0;i<bones;i++)
                                            {
                                                transforms[i]=Transform(poseTransform(pose,i));
                                                nint m=getWorldMatrix(world,i);
                                                if(m==0)throw new InvalidDataException("Missing native world matrix");
                                                positions[i]=new[]{Float(m,48),Float(m,52),Float(m,56)};
                                                if(positions[i].Any(x=>!float.IsFinite(x)||Math.Abs(x)>1_000_000f))
                                                    throw new InvalidDataException("Invalid native world position");
                                            }
                                            frames[frame]=new PoseFrame(t,transforms,positions);
                                        }
                                    }
                                    var export=new PoseExport(skeletonId.ToString("X8"),
                                        animationId.ToString("X8"),Name(Ptr(skeleton,0)),
                                        Name(Ptr(animation,0)),fps,duration,boneList,frames);
                                    string destination=Path.GetFullPath(args[5]);
                                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                                    string temporary=destination+".tmp";
                                    File.WriteAllText(temporary,JsonSerializer.Serialize(export));
                                    File.Move(temporary,destination,true);
                                    Console.WriteLine($"NATIVE POSE 0x{skeletonId:X8} + 0x{animationId:X8}: " +
                                        $"{bones} source bones and {frameCount} native local/world-pose frames " +
                                        $"at {fps}fps, duration {duration:F3}s, output={destination}");
                                    return 0;
                                }
                                finally{freeWorldPose(world);}
                            }
                            finally{freePose(pose);}
                        }
                        finally{freeControl(control);}
                    }
                    finally{freeModel(instance);}
                }
                finally{free(sf);free(af);}
            }
        }
        finally{NativeLibrary.Free(module);}
    }
}
