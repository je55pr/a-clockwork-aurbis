using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Standalone first-person test stand for original ESO meshes. The native Granny
/// decoder runs only in the existing isolated ModelGeometry helper process.
/// ESM5 is static bind-pose geometry; clip names are metadata, not playback.
/// </summary>
public partial class EsoModelStage : Node3D
{
    private EsoArchive? _archive;
    private OodleDecoder? _decoder;
    private Node3D _model = null!;
    private LineEdit _idField = null!;
    private Label _status = null!;
    private Label _animations = null!;
    private readonly Dictionary<uint, Texture2D?> _textures = new();
    private Dictionary<uint, EsoModelNameIndex.Record> _names = new();
    private readonly object _sourceLock = new();
    private int _request;
    private volatile bool _closing;

    public override void _Ready()
    {
        _model = new Node3D { Name = "ImportedMeshes" };
        AddChild(_model);
        var panel = new PanelContainer
        {
            AnchorLeft = 0.58f, AnchorRight = 0.99f,
            AnchorTop = 0.1f, AnchorBottom = 0.1f,
            OffsetLeft = 0, OffsetRight = 0, OffsetTop = 0, OffsetBottom = 300
        };
        var ui = new CanvasLayer { Name = "ModelControls" };
        AddChild(ui);
        var layout = new Control();
        layout.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        ui.AddChild(layout);
        layout.AddChild(panel);
        var column = new VBoxContainer();
        panel.AddChild(column);
        column.AddChild(new Label { Text = "ESO source-model test stand   (Esc to edit)" });
        var row = new HBoxContainer();
        column.AddChild(row);
        _idField = new LineEdit { Text = "0015F475", PlaceholderText = "GR2 asset ID (hex)", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddChild(_idField);
        var button = new Button { Text = "Load model" };
        row.AddChild(button);
        button.Pressed += () => BeginLoad(_idField.Text);
        _idField.TextSubmitted += BeginLoad;
        var examples = new HBoxContainer();
        column.AddChild(examples);
        foreach (var (caption, asset) in new[]
        {
            ("Animated limestone", "0015F475"),
            ("Skinned helmet A", "0029A89F"),
            ("Skinned helmet B", "0017F3F3")
        })
        {
            var sample = new Button { Text = caption };
            sample.Pressed += () => { _idField.Text = asset; BeginLoad(asset); };
            examples.AddChild(sample);
        }
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Initializing ESO source archive..." };
        column.AddChild(_status);
        _animations = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "Animations: not inspected" };
        column.AddChild(_animations);
        try
        {
            string root = System.Environment.GetEnvironmentVariable("ESO_ROOT")
                ?? "C:/ESO/The Elder Scrolls Online";
            _archive = new EsoArchive(Path.Combine(root, "depot", "eso.mnf"));
            _decoder = OodleDecoder.FromEnvironment(
                ProjectSettings.GlobalizePath("res://.research/ooz.dll"));
            _names = EsoModelNameIndex.Load(
                ProjectSettings.GlobalizePath("res://.research/model-names.tsv"));
            if (_decoder == null)
                throw new FileNotFoundException("Oodle decoder unavailable. Set ESO_OOZ_DLL.");
            BeginLoad(_idField.Text);
        }
        catch (Exception e)
        {
            _status.Text = "Cannot open ESO source: " + e.Message;
        }
    }

    private void BeginLoad(string text)
    {
        if (_closing || _archive == null || _decoder == null) return;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
        {
            _status.Text = "Enter an 8-digit hexadecimal source model ID.";
            return;
        }
        _request++;
        int requested = _request;
        _status.Text = $"Extracting original GR2 0x{id:X8}...";
        _animations.Text = "Reading indexed animation metadata...";
        _ = Task.Run(() =>
        {
            string path = "", error = "", survey = "";
            lock (_sourceLock)
            {
                if (_closing) return;
                try
                {
                    path = EnsureGeometry(id);
                    try { survey = InspectAnimations(id); }
                    catch (Exception e) { survey = "Animation survey unavailable: " + e.Message; }
                }
                catch (Exception e) { error = e.Message; }
            }
            if (!_closing) CallDeferred(nameof(FinishLoad), requested, id, path, survey, error);
        });
    }

    private string EnsureGeometry(uint id)
    {
        var archive = _archive!;
        var decoder = _decoder!;
        var source = archive.FindId(id).FirstOrDefault(r =>
            archive.GetIdIndexFields(r.Ordinal).Flags == 0);
        if (source.UncompressedSize < 64) throw new FileNotFoundException($"GR2 0x{id:X8} not found.");
        string root = ProjectSettings.GlobalizePath("res://");
        string cache = Path.Combine(root, ".research", "model-previews");
        Directory.CreateDirectory(cache);
        string output = Path.Combine(cache,
            $"{id:X8}-a{source.ArchiveIndex}-o{source.Offset}-c{source.CompressedSize}-h{source.Hash:X8}-u{source.UncompressedSize}-v5.esm");
        if (File.Exists(output) && new FileInfo(output).Length > 32) return output;
        string helper = Path.Combine(root, "tools", "ModelGeometry", "bin", "Debug", "net8.0", "ModelGeometry.dll");
        string granny = System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL") ??
            Path.Combine(root, ".research", "uesp-esoapps", "EsoExtractData", "EsoExtractData", "granny2_x64.dll");
        if (!File.Exists(helper) || !File.Exists(granny))
            throw new FileNotFoundException("Build tools/ModelGeometry and configure ESO_GRANNY_DLL.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { helper, granny, id.ToString("X8"), output, archive.SourcePath, decoder.LibraryPath })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("Could not start isolated GR2 helper.");
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("GR2 helper exceeded 30 seconds.");
        }
        if (process.ExitCode != 0)
        {
            string failure = process.StandardError.ReadToEnd();
            throw new InvalidDataException("GR2 helper failed: " + failure[..Math.Min(400, failure.Length)]);
        }
        if (!File.Exists(output)) throw new IOException("GR2 helper returned no geometry.");
        return output;
    }

    private string InspectAnimations(uint id)
    {
        string root = ProjectSettings.GlobalizePath("res://");
        string helper = Path.Combine(root, "tools", "ModelGeometry", "bin", "Debug", "net8.0", "ModelGeometry.dll");
        string granny = System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL") ??
            Path.Combine(root, ".research", "uesp-esoapps", "EsoExtractData", "EsoExtractData", "granny2_x64.dll");
        if (_archive == null || _decoder == null || !File.Exists(helper) || !File.Exists(granny))
            return "Native animation survey helper unavailable.";
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { helper, "--inspect-animation", granny,
                     id.ToString("X8"), _archive.SourcePath, _decoder.LibraryPath })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) return "Cannot launch native animation survey.";
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            return "Native animation survey timed out.";
        }
        string json = output.GetAwaiter().GetResult();
        string failure = errors.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || json.Length > 2_000_000)
            return "Native animation survey failed: " + failure[..Math.Min(140, failure.Length)];
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement;
        int skeletonCount = data.GetProperty("SkeletonCount").GetInt32();
        string bones = string.Join(", ",
            data.GetProperty("Skeletons").EnumerateArray().Take(2)
                .Select(s => s.GetProperty("BoneCount").GetInt32().ToString()));
        var clips = new List<string>();
        foreach (var animation in data.GetProperty("Animations").EnumerateArray().Take(12))
        {
            int tracks = animation.GetProperty("Groups").EnumerateArray()
                .Sum(g => g.GetProperty("TransformTracks").GetInt32());
            clips.Add(animation.GetProperty("Name").GetString() +
                $" {animation.GetProperty("DurationSeconds").GetSingle():0.00}s ({tracks} tracks)");
        }
        var sourceMeshes = data.GetProperty("Meshes").EnumerateArray().ToArray();
        int skinned = sourceMeshes.Count(m =>
        {
            var fields = m.GetProperty("VertexMembers").EnumerateArray()
                .Select(f => f.GetString() ?? "").ToArray();
            return fields.Any(f => f.StartsWith("BoneWeights", StringComparison.Ordinal)) &&
                   fields.Any(f => f.StartsWith("BoneIndices", StringComparison.Ordinal));
        });
        string bindings = string.Join(", ", sourceMeshes.Select(m =>
            m.GetProperty("BoneBindings").GetInt32().ToString()));
        return $"Native survey: {skeletonCount} skeleton(s), bone counts [{bones}].\n" +
            $"Skinnable vertex streams: {skinned}/{sourceMeshes.Length} meshes; " +
            $"source bone bindings [{bindings}] (not yet exported to Godot).\n" +
            (clips.Count == 0 ? "No clips in this GR2." : string.Join(" | ", clips));
    }

    private void FinishLoad(int request, uint id, string path, string survey, string error)
    {
        if (_closing || request != _request) return;
        if (error.Length != 0)
        {
            _status.Text = error;
            _animations.Text = "No model loaded.";
            return;
        }
        try
        {
            LoadEsm5(path, out int meshCount, out int triangles);
            string name = _names.TryGetValue(id, out var record)
                ? EsoModelNameIndex.PreferredName(record) : "unindexed model";
            _status.Text = $"0x{id:X8}: {name}   |   {meshCount} meshes, {triangles:N0} triangles (bind pose)";
            _animations.Text = record == null
                ? "Animation index unavailable. No animation playback yet."
                : $"Source animations ({record.AnimationCount}): " +
                  (record.AnimationCount == 0 ? "none in this file" : record.AnimationNames) +
                  "\nSkeleton/weights/tracks are NOT imported yet.";
            if (!string.IsNullOrWhiteSpace(survey))
                _animations.Text += "\n" + survey;
        }
        catch (Exception e)
        {
            _status.Text = "Rejected model: " + e.Message;
            _animations.Text = "Model could not be displayed.";
        }
    }

    private void LoadEsm5(string path, out int meshCount, out int triangleCount)
    {
        if (new FileInfo(path).Length > 72_000_000) throw new InvalidDataException("Mesh exceeds 72 MB.");
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "ESM5") throw new InvalidDataException("Unsupported mesh file.");
        meshCount = reader.ReadInt32();
        if (meshCount is < 1 or > 128) throw new InvalidDataException("Invalid mesh count.");
        triangleCount = 0;
        int verticesTotal = 0, indicesTotal = 0;
        var instances = new List<MeshInstance3D>();
        Vector3 low = new(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 high = new(float.MinValue, float.MinValue, float.MinValue);
        for (int m = 0; m < meshCount; m++)
        {
            string name = reader.ReadString();
            int nv = reader.ReadInt32(), ni = reader.ReadInt32(), ng = reader.ReadInt32();
            bool hasFrame = reader.ReadBoolean(), hasUv2 = reader.ReadBoolean();
            verticesTotal += nv; indicesTotal += ni;
            if (nv is < 3 or > 400000 || ni is < 3 or > 1200000 || ni % 3 != 0 ||
                ng is < 1 or > 256 || verticesTotal > 600000 || indicesTotal > 1800000)
                throw new InvalidDataException("Mesh exceeds source validation bounds.");
            var positions = new Vector3[nv];
            for (int i = 0; i < nv; i++)
            {
                positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (!positions[i].IsFinite()) throw new InvalidDataException("Nonfinite vertex.");
                low = low.Min(positions[i]); high = high.Max(positions[i]);
            }
            var uv = new Vector2[nv];
            var uv2 = new Vector2[nv];
            for (int i = 0; i < nv; i++) uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            for (int i = 0; i < nv; i++) uv2[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            var normals = new Vector3[nv];
            for (int i = 0; i < nv; i++) normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var tangents = new float[nv * 4];
            for (int i = 0; i < tangents.Length; i++) tangents[i] = reader.ReadSingle();
            var indices = new int[ni];
            for (int i = 0; i < ni; i++)
            {
                indices[i] = reader.ReadInt32();
                if (indices[i] < 0 || indices[i] >= nv) throw new InvalidDataException("Invalid triangle index.");
            }
            if (!hasFrame)
            {
                for (int i = 0; i < ni; i += 3)
                {
                    int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                    Vector3 n = (positions[b] - positions[a]).Cross(positions[c] - positions[a]);
                    normals[a] += n; normals[b] += n; normals[c] += n;
                }
                for (int i = 0; i < nv; i++) normals[i] =
                    normals[i].LengthSquared() > 0.000001f ? normals[i].Normalized() : Vector3.Up;
            }
            var mesh = new ArrayMesh();
            for (int g = 0; g < ng; g++)
            {
                _ = reader.ReadString();
                int first = reader.ReadInt32(), count = reader.ReadInt32();
                uint diffuse = reader.ReadUInt32();
                for (int k = 0; k < 4; k++) _ = reader.ReadUInt32();
                bool flagsKnown = reader.ReadBoolean();
                for (int k = 0; k < 4; k++) _ = reader.ReadBoolean();
                bool twoSided = reader.ReadBoolean();
                _ = reader.ReadInt32(); _ = reader.ReadInt32();
                for (int k = 0; k < 3; k++) _ = reader.ReadSingle();
                if (first < 0 || count < 1 || (long)(first + count) * 3 > ni)
                    throw new InvalidDataException("Invalid source material range.");
                var part = new int[count * 3];
                Array.Copy(indices, first * 3, part, 0, part.Length);
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = positions;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                if (hasFrame) arrays[(int)Mesh.ArrayType.Tangent] = tangents;
                arrays[(int)Mesh.ArrayType.TexUV] = uv;
                if (hasUv2) arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
                arrays[(int)Mesh.ArrayType.Index] = part;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                var mat = new StandardMaterial3D
                {
                    AlbedoColor = new Color("#a9c8d5"),
                    Roughness = 0.88f,
                    CullMode = flagsKnown && !twoSided
                        ? BaseMaterial3D.CullModeEnum.Front
                        : BaseMaterial3D.CullModeEnum.Disabled
                };
                Texture2D? texture = LoadDiffuse(diffuse);
                if (texture != null) { mat.AlbedoTexture = texture; mat.AlbedoColor = Colors.White; }
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, mat);
                triangleCount += count;
            }
            instances.Add(new MeshInstance3D { Name = name, Mesh = mesh });
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Unexpected trailing data.");
        Vector3 bounds = high - low;
        if (!bounds.IsFinite() || bounds.LengthSquared() < 0.000001f)
            throw new InvalidDataException("Degenerate model bounds.");
        // Keep source units for the mesh; a test-stand transform scales only the
        // display group. Character dimensions will be calibrated separately.
        float maxDimension = Math.Max(bounds.X, Math.Max(bounds.Y, bounds.Z));
        float scale = Math.Clamp(3.5f / maxDimension, 0.001f, 100f);
        var replacement = new Node3D { Name = "ImportedMeshes", Scale = Vector3.One * scale };
        var offset = new Vector3(-(low.X + high.X) * 0.5f, -low.Y, -(low.Z + high.Z) * 0.5f);
        foreach (var instance in instances)
        {
            instance.Position = offset;
            replacement.AddChild(instance);
        }
        RemoveChild(_model);
        _model.QueueFree();
        _model = replacement;
        AddChild(_model);
    }

    private Texture2D? LoadDiffuse(uint id)
    {
        if (id == 0 || _archive == null || _decoder == null) return null;
        if (_textures.TryGetValue(id, out var cached)) return cached;
        if (_textures.Count >= 48) return null;
        Texture2D? texture = null;
        try
        {
            var source = _archive.FindId(id).FirstOrDefault(r =>
                _archive.GetIdIndexFields(r.Ordinal).Flags == 0 && r.UncompressedSize is >= 128 and <= 20_000_000);
            if (source.UncompressedSize == 0) return null;
            byte[] bytes = _archive.ReadDecoded(source, _decoder);
            if (bytes.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x20534444) return null;
            uint width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16));
            uint height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
            uint codec = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84));
            uint block = codec == 0x31545844 ? 8u : codec is 0x33545844 or 0x35545844 ? 16u : 0u;
            if (block == 0 || width is 0 or > 4096 || height is 0 or > 4096) return null;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (flags | 0x80000u) & ~0x8u);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20),
                ((width + 3u) / 4u) * ((height + 3u) / 4u) * block);
            using var image = new Image();
            if (image.LoadDdsFromBuffer(bytes) == Error.Ok && !image.IsEmpty())
                texture = ImageTexture.CreateFromImage(image);
        }
        catch (Exception e) { GD.Print($"DDS 0x{id:X8} unavailable: {e.Message}"); }
        _textures[id] = texture;
        return texture;
    }

    public override void _ExitTree()
    {
        _closing = true;
        _request++;
        // Do not dispose native decoder/archive handles during an in-flight read.
        _ = Task.Run(() =>
        {
            lock (_sourceLock)
            {
                _decoder?.Dispose();
                _archive?.Dispose();
            }
        });
    }
}
