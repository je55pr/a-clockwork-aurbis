using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using EsoTest;

/// <summary>
/// Read-only inspection of Granny animation linkage. Runs in the same isolated
/// helper process as mesh extraction, never in the Godot editor/runtime.
/// Packed x64 offsets are specific to the locally supplied Granny library.
/// </summary>
internal static class AnimationSurvey
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nint ReadEntireFile(int length, byte* bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetFileInfo(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFile(nint file);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetVertexType(nint mesh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetMeshVertices(nint mesh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetMeshVertexCount(nint mesh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetObjectSize(nint type);

    private static nint Ptr(nint address, int offset) =>
        address == 0 ? 0 : Marshal.ReadIntPtr(address, offset);
    private static int Int(nint address, int offset) =>
        address == 0 ? 0 : Marshal.ReadInt32(address, offset);
    private static float Float(nint address, int offset) =>
        address == 0 ? 0 : Marshal.PtrToStructure<float>(address + offset);
    private static string Name(nint address)
    {
        if (address == 0) return "";
        var s = Marshal.PtrToStringAnsi(address) ?? "";
        return s.Length <= 160 ? s : s[..160];
    }
    private static string Str(nint obj) => obj == 0 ? "" : Name(Ptr(obj, 0));
    private static nint[] References(nint root, int countOffset, int listOffset, int maxCount)
    {
        int count = Int(root, countOffset);
        if (count <= 0 || count > maxCount) return Array.Empty<nint>();
        nint list = Ptr(root, listOffset);
        if (list == 0) return Array.Empty<nint>();
        var refs = new nint[count];
        for (int i = 0; i < count; i++)
            refs[i] = Ptr(list, i * IntPtr.Size);
        return refs;
    }

    private sealed record MeshSkinInfo(string Name, int VertexCount, int BoneBindings,
        int VertexStride, string[] VertexMembers);
    private sealed record SkeletonInfo(string Name, int BoneCount, string[] SampleBones);
    private sealed record GroupInfo(string Name, int VectorTracks, int TransformTracks,
        int TextTracks, string[] SampleTransformNames);
    private sealed record AnimationInfo(string Name, float DurationSeconds,
        float TimeStepSeconds, float Oversampling, int LoopCount, int Flags,
        GroupInfo[] Groups);
    private sealed record RigVertex(int[] Indices,float[] Weights);
    private sealed record RigMesh(string Name,string[] BindingNames,RigVertex[] Vertices);
    private sealed record RigBone(string Name,int Parent,float[] Translation,
        float[] Orientation,float[] ScaleShear,float[] InverseWorld);
    private sealed record RigSkeleton(string Name,RigBone[] Bones);
    private sealed record SurveyInfo(string SourceId, int MeshCount, MeshSkinInfo[] Meshes,
        int ModelCount, int SkeletonCount, SkeletonInfo[] Skeletons,
        int StandaloneTrackGroups, int AnimationCount, AnimationInfo[] Animations,
        RigMesh[]? RigMeshes=null,RigSkeleton[]? RigSkeletons=null,
        int TextureCount=0,int MaterialCount=0);

    private static float[] Floats(nint address,int offset,int count)
    {
        var values=new float[count];
        for(int i=0;i<count;i++)values[i]=Float(address,offset+i*4);
        if(values.Any(f=>!float.IsFinite(f)||Math.Abs(f)>10000000f))
            throw new InvalidDataException("Non-finite or unreasonable skeleton transform");
        return values;
    }

    private static RigMesh RecoverRig(nint mesh,nint layout,int stride,int count,nint raw)
    {
        int bindings=Int(mesh,48);
        nint table=Ptr(mesh,52);
        if(bindings is <1 or >512 || table==0)
            throw new InvalidDataException("Source skin binding table missing");
        var names=new string[bindings];
        const int bindingStride=44;
        for(int i=0;i<bindings;i++)
        {
            names[i]=Str(table+i*bindingStride);
            if(names[i].Length is <1 or >120)
                throw new InvalidDataException($"Invalid bone binding {i}");
        }
        if(raw==0)throw new InvalidDataException("Source mesh vertex stream missing");
        int weightOffset=-1,indexOffset=-1,weightKind=0,indexKind=0;
        int cursor=0;
        for(int i=0;i<64;i++)
        {
            nint member=layout+i*44;
            int kind=Int(member,0);
            if(kind==0)break;
            int width=Int(member,20);
            int unit=kind switch {
                10 or 19 or 20=>4,
                11 or 12 or 13 or 14=>1,
                15 or 16 or 17 or 18 or 21=>2,_=>0};
            if(unit==0||width is <1 or >32)
                throw new InvalidDataException("Unexpected Granny vertex stream");
            string label=Name(Ptr(member,4));
            if(label=="BoneWeights"){weightOffset=cursor;weightKind=kind;}
            if(label=="BoneIndices"){indexOffset=cursor;indexKind=kind;}
            cursor+=unit*width;
        }
        if(cursor!=stride||weightOffset<0||indexOffset<0||
            weightKind!=14||indexKind!=12)
            throw new InvalidDataException($"Unsupported skin stream kinds {weightKind}/{indexKind}; layout {cursor}/{stride}");
        if(count is <1 or >100000||stride is <1 or >512 ||
            (long)count*stride>48_000_000)
            throw new InvalidDataException("Source vertex stream exceeds safe bounds");
        var bytes=new byte[count*stride];
        Marshal.Copy(raw,bytes,0,bytes.Length);
        var vertices=new RigVertex[count];
        for(int v=0;v<count;v++)
        {
            var ids=new int[4];var weights=new float[4];
            int total=0;
            for(int k=0;k<4;k++)
            {
                ids[k]=bytes[v*stride+indexOffset+k];
                int rawWeight=bytes[v*stride+weightOffset+k];
                if(rawWeight==0)ids[k]=0; // Unused slots are valid indices too.
                total+=rawWeight;
                weights[k]=rawWeight/255f;
                if(rawWeight!=0&&ids[k]>=bindings)
                    throw new InvalidDataException($"Invalid bone index {ids[k]}/{bindings} at vertex {v}");
            }
            if(total is <245 or >265)
                throw new InvalidDataException($"Skin vertex {v} weights total {total}, not near 255");
            for(int k=0;k<4;k++)weights[k]*=255f/total;
            vertices[v]=new RigVertex(ids,weights);
        }
        return new RigMesh(Str(mesh),names,vertices);
    }

    public static unsafe int Run(string[] args,bool includeRig=false)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ModelGeometry --inspect-animation <granny-dll> <hex-resource-id> [manifest] [oodle-dll]");
            return 2;
        }
        if (!uint.TryParse(args[1], NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint id))
            throw new ArgumentException("Invalid hexadecimal resource ID");
        string grannyDll = Path.GetFullPath(args[0]);
        string manifest = args.Length > 2 ? args[2] : "C:/ESO/The Elder Scrolls Online/depot/eso.mnf";
        string oodleDll = args.Length > 3 ? args[3] : ".research/ooz.dll";
        using var archive = new EsoArchive(manifest);
        using var decoder = OodleDecoder.FromEnvironment(oodleDll)
            ?? throw new FileNotFoundException("Oodle decoder unavailable");
        var source = archive.FindId(id).FirstOrDefault(r =>
            archive.GetIdIndexFields(r.Ordinal).Flags == 0);
        if (source.UncompressedSize is < 64 or > 32_000_000)
            throw new InvalidDataException($"Source 0x{id:X8} missing or too large");
        byte[] payload = archive.ReadDecoded(source, decoder);
        if (payload.Length < 64 || !(
            (payload[0] == 0xE5 && payload[1] == 0x9B) ||
            (payload[0] == 0xB8 && payload[1] == 0x67)))
            throw new InvalidDataException("Source file is not GR2");

        nint module = NativeLibrary.Load(grannyDll);
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<ReadEntireFile>(
                NativeLibrary.GetExport(module, "GrannyReadEntireFileFromMemory"));
            var get = Marshal.GetDelegateForFunctionPointer<GetFileInfo>(
                NativeLibrary.GetExport(module, "GrannyGetFileInfo"));
            var free = Marshal.GetDelegateForFunctionPointer<FreeFile>(
                NativeLibrary.GetExport(module, "GrannyFreeFile"));
            var vertexType = Marshal.GetDelegateForFunctionPointer<GetVertexType>(
                NativeLibrary.GetExport(module, "GrannyGetMeshVertexType"));
            var objectSize = Marshal.GetDelegateForFunctionPointer<GetObjectSize>(
                NativeLibrary.GetExport(module, "GrannyGetTotalObjectSize"));
            var getRaw=Marshal.GetDelegateForFunctionPointer<GetMeshVertices>(
                NativeLibrary.GetExport(module,"GrannyGetMeshVertices"));
            var getCount=Marshal.GetDelegateForFunctionPointer<GetMeshVertexCount>(
                NativeLibrary.GetExport(module,"GrannyGetMeshVertexCount"));
            fixed (byte* data = payload)
            {
                nint file = open(payload.Length, data);
                if (file == 0) throw new InvalidDataException("Granny could not decode this file.");
                try
                {
                    nint info = get(file);
                    if (info == 0) throw new InvalidDataException("No file metadata.");
                    var meshes = new List<MeshSkinInfo>();
                    var rigMeshes = new List<RigMesh>();
                    foreach (nint mesh in References(info, 84, 88, 256))
                    {
                        if (mesh == 0) continue;
                        nint layout = vertexType(mesh);
                        int stride = layout == 0 ? 0 : objectSize(layout);
                        int vertices = getCount(mesh);
                        // The material binding count is at 36; the separate bone binding
                        // table begins at offset 48 in this packed x64 layout.
                        int bindings = Int(mesh, 48);
                        var members = new List<string>();
                        if (layout != 0 && stride is > 0 and <= 512)
                        {
                            for (int i = 0; i < 64; i++)
                            {
                                nint member = layout + i * 44;
                                int kind = Int(member, 0);
                                if (kind == 0) break;
                                int width = Int(member, 20);
                                string label = Name(Ptr(member, 4));
                                members.Add($"{label} ({kind} x {width})");
                            }
                        }
                        meshes.Add(new MeshSkinInfo(Str(mesh), vertices, bindings,
                            stride, members.ToArray()));
                        if(includeRig)rigMeshes.Add(RecoverRig(mesh,layout,stride,vertices,getRaw(mesh)));
                    }
                    var skeletons = new List<SkeletonInfo>();
                    var rigSkeletons=new List<RigSkeleton>();
                    foreach (nint skel in References(info, 48, 52, 128))
                    {
                        if (skel == 0) continue;
                        int boneCount = Int(skel, 8);
                        nint bones = Ptr(skel, 12);
                        var names = new List<string>();
                        if (boneCount is > 0 and <= 1024 && bones != 0)
                        {
                            // Granny bone: name + parent + local transform (68 bytes)
                            // + 4x4 inverse world + LOD error + extended variant.
                            const int boneStride = 164;
                            for (int i = 0; i < Math.Min(8, boneCount); i++)
                            {
                                nint bone = bones + i * boneStride;
                                names.Add($"{i}:{Str(bone)} (parent {Int(bone, 8)})");
                            }
                        }
                        skeletons.Add(new SkeletonInfo(Str(skel), boneCount, names.ToArray()));
                        if(includeRig)
                        {
                            if(boneCount is <1 or >512 || bones==0)
                                throw new InvalidDataException("GR2 rig skeleton missing bones");
                            var full=new RigBone[boneCount];
                            for(int i=0;i<boneCount;i++)
                            {
                                nint bone=bones+i*164;
                                int parent=Int(bone,8);
                                if(parent>=i || parent< -1)
                                    throw new InvalidDataException("GR2 bone parent ordering invalid");
                                full[i]=new RigBone(Str(bone),parent,
                                    Floats(bone,16,3),Floats(bone,28,4),
                                    Floats(bone,44,9),Floats(bone,80,16));
                            }
                            rigSkeletons.Add(new RigSkeleton(Str(skel),full));
                        }
                    }

                    var animations = new List<AnimationInfo>();
                    foreach (nint animation in References(info, 120, 124, 1024))
                    {
                        if (animation == 0) continue;
                        float duration = Float(animation, 8), timestep = Float(animation, 12);
                        int groupCount = Int(animation, 20);
                        nint groupPointers = Ptr(animation, 24);
                        var groups = new List<GroupInfo>();
                        if (groupCount is > 0 and <= 512 && groupPointers != 0)
                        {
                            for (int i = 0; i < Math.Min(groupCount, 64); i++)
                            {
                                nint group = Ptr(groupPointers, i * IntPtr.Size);
                                if (group == 0) continue;
                                int vectorCount = Int(group, 8), transformCount = Int(group, 20),
                                    textCount = Int(group, 44);
                                nint tracks = Ptr(group, 24);
                                var names = new List<string>();
                                if (transformCount is > 0 and <= 4096 && tracks != 0)
                                {
                                    // GrannyTransformTrack: name + flags + three 16-byte curve variants.
                                    const int trackStride = 60;
                                    for (int t = 0; t < Math.Min(12, transformCount); t++)
                                        names.Add(Str(tracks + t * trackStride));
                                }
                                groups.Add(new GroupInfo(Str(group), vectorCount, transformCount,
                                    textCount, names.ToArray()));
                            }
                        }
                        animations.Add(new AnimationInfo(Str(animation), duration, timestep,
                            Float(animation, 16), Int(animation, 32), Int(animation, 36),
                            groups.ToArray()));
                    }

                    var report = new SurveyInfo($"0x{id:X8}",
                        Int(info, 84), meshes.ToArray(), Int(info, 96),
                        Int(info, 48), skeletons.ToArray(), Int(info, 108),
                        Int(info, 120), animations.ToArray(),
                        includeRig?rigMeshes.ToArray():null,
                        includeRig?rigSkeletons.ToArray():null,
                        Int(info,24),Int(info,36));
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
                    return 0;
                }
                finally { free(file); }
            }
        }
        finally { NativeLibrary.Free(module); }
    }
}
