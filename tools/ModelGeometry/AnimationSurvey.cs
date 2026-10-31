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
    private sealed record SurveyInfo(string SourceId, int MeshCount, MeshSkinInfo[] Meshes,
        int ModelCount, int SkeletonCount, SkeletonInfo[] Skeletons,
        int StandaloneTrackGroups, int AnimationCount, AnimationInfo[] Animations);

    public static unsafe int Run(string[] args)
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
            fixed (byte* data = payload)
            {
                nint file = open(payload.Length, data);
                if (file == 0) throw new InvalidDataException("Granny could not decode this file.");
                try
                {
                    nint info = get(file);
                    if (info == 0) throw new InvalidDataException("No file metadata.");
                    var meshes = new List<MeshSkinInfo>();
                    foreach (nint mesh in References(info, 84, 88, 256))
                    {
                        if (mesh == 0) continue;
                        nint layout = vertexType(mesh);
                        int stride = layout == 0 ? 0 : objectSize(layout);
                        int vertices = Int(mesh, 12);
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
                    }
                    var skeletons = new List<SkeletonInfo>();
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
                        Int(info, 120), animations.ToArray());
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
