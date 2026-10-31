using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// First source-backed humanoid visual pass. Seven genuine ESO customization
/// body-part meshes share their authoring-space bind pose (0..2.2m).
/// No character appearance or animation is inferred from a wiki NPC name.
/// The files and licensed native decoders remain outside the Git repository.
/// </summary>
public partial class EsoHumanoidVisualLayer : Node3D
{
    // These IDs were verified against the installed depot's GR2 mesh names,
    // anatomy bounds, and original bone-weight/indices vertex streams.
    private static readonly uint[][] Parts =
    {
        new uint[] { 0x000DEE23, 0x000DEE5B, 0x000DEE9D, 0x000DEF03,
            0x000DEDDB, 0x000DEDA1, 0x000DEE57, 0x0009EC43 },
        new uint[] { 0x000DEE25, 0x000DEE5D, 0x000DEE9F, 0x000DEF05,
            0x000DEDDD, 0x000DEDA3, 0x000DEE59, 0x0009FB4D }
    };
    private static readonly string[] Names =
        { "Feet", "Lower legs", "Upper legs", "Torso", "Upper arms", "Lower arms", "Head", "Hair" };
    private static readonly string[] SourcePrefixes =
        { "Feet_", "LegsLower_", "LegsUpper_", "Torso_", "ArmsUpper_", "ArmsLower_", "Head_", "HAR_" };
    private readonly ConcurrentQueue<(uint Id, string Path, string Error)> _prepared = new();
    private readonly Dictionary<uint, ArrayMesh> _meshes = new();
    private readonly List<PopulationSpawnPlanner.Placement> _placements = new();
    private readonly Dictionary<string, JsonElement> _people = new();
    private readonly HashSet<string> _renderedIds = new();
    private (uint Id, EsoRecord Source)[] _source = Array.Empty<(uint, EsoRecord)>();
    private string _root = "";
    private string _manifest = "";
    private string _oodle = "";
    private volatile bool _workerFinished, _stopping;
    private bool _built;
    public bool HasOriginalBodies { get; private set; }
    public int OriginalBodyCount { get; private set; }
    public int SourcePartTypes => _meshes.Count;
    public bool HasRenderedPerson(string id) => _renderedIds.Contains(id);
    public event Action? Completed;

    public static bool CanUseHumanoidBody(string race) =>
        race.Equals("Altmer", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("High Elf", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Bosmer", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Wood Elf", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Dunmer", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Dark Elf", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Breton", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Nord", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Redguard", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Reachman", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Imperial", StringComparison.OrdinalIgnoreCase) ||
        race.Equals("Orc", StringComparison.OrdinalIgnoreCase);

    public void Setup(EsoArchive archive, OodleDecoder decoder,
        IReadOnlyList<PopulationSpawnPlanner.Placement> placements,
        IReadOnlyDictionary<string, JsonElement> people)
    {
        _root = ProjectSettings.GlobalizePath("res://");
        _manifest = archive.SourcePath;
        _oodle = decoder.LibraryPath;
        _placements.AddRange(placements);
        foreach (var kv in people) _people[kv.Key] = kv.Value;
        var sources = new List<(uint, EsoRecord)>();
        foreach (uint id in Parts.SelectMany(x => x).Distinct())
        {
            var record = archive.FindId(id).FirstOrDefault(r =>
                archive.GetIdIndexFields(r.Ordinal).Flags == 0);
            if (record.UncompressedSize is < 64 or > 32_000_000)
                throw new FileNotFoundException($"ESO character source 0x{id:X8} unavailable");
            sources.Add((id, record));
        }
        _source = sources.ToArray();
    }

    public override void _Ready()
    {
        _ = Task.Run(Prepare);
    }

    private void Prepare()
    {
        foreach (var (id, source) in _source)
        {
            if (_stopping) break;
            try { _prepared.Enqueue((id, EnsureGeometry(id, source), "")); }
            catch (Exception e) { _prepared.Enqueue((id, "", e.Message)); }
        }
        _workerFinished = true;
    }

    private string EnsureGeometry(uint id, EsoRecord record)
    {
        string directory = Path.Combine(_root, ".research", "npc-geometry");
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory,
            $"{id:X8}-a{record.ArchiveIndex}-o{record.Offset}-c{record.CompressedSize}-" +
            $"h{record.Hash:X8}-u{record.UncompressedSize}-v5.esm");
        if (File.Exists(output) && new FileInfo(output).Length > 32) return output;
        string helper = Path.Combine(_root, "tools", "ModelGeometry", "bin",
            "Debug", "net8.0", "ModelGeometry.dll");
        string granny = System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL") ??
            Path.Combine(_root, ".research", "uesp-esoapps", "EsoExtractData",
                "EsoExtractData", "granny2_x64.dll");
        if (!File.Exists(helper) || !File.Exists(granny))
            throw new FileNotFoundException("Build ModelGeometry and configure a local Granny decoder.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (string arg in new[] { helper, granny, id.ToString("X8"), output,
            _manifest, _oodle })
            process.StartInfo.ArgumentList.Add(arg);
        if (!process.Start()) throw new IOException("Could not start source geometry process");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Character geometry 0x{id:X8} exceeded 45 seconds");
        }
        string error = stderr.GetAwaiter().GetResult();
        _ = stdout.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(output))
            throw new InvalidDataException($"Character geometry 0x{id:X8}: " +
                error[..Math.Min(error.Length, 240)]);
        return output;
    }

    public override void _Process(double delta)
    {
        if (_stopping || _built) return;
        // Only one CPU mesh upload per rendered frame.
        if (_prepared.TryDequeue(out var item))
        {
            try
            {
                if (item.Error.Length > 0) throw new InvalidDataException(item.Error);
                _meshes[item.Id] = LoadMesh(item.Path, SourcePartPrefix(item.Id));
            }
            catch (Exception e)
            {
                GD.PushWarning($"ESO humanoid part 0x{item.Id:X8} unavailable: {e.Message}");
            }
        }
        if (!_workerFinished || !_prepared.IsEmpty) return;
        _built = true;
        RenderHumanoids();
        Completed?.Invoke();
        SetProcess(false);
    }

    private static string SourcePartPrefix(uint id)
    {
        for(int variant=0;variant<Parts.Length;variant++)
            for(int part=0;part<Parts[variant].Length;part++)
                if(Parts[variant][part]==id)return SourcePrefixes[part];
        throw new InvalidDataException($"Unknown source body-part ID 0x{id:X8}");
    }

    private static ArrayMesh LoadMesh(string path,string expectedPrefix)
    {
        if (new FileInfo(path).Length is < 64 or > 72_000_000)
            throw new InvalidDataException("Invalid character mesh cache size");
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "ESM5")
            throw new InvalidDataException("Unsupported source character mesh");
        int meshCount = reader.ReadInt32();
        if (meshCount != 1)
            throw new InvalidDataException("Expected a single customization body part");
        string sourceName = reader.ReadString();
        if(!sourceName.StartsWith(expectedPrefix,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected {expectedPrefix} but source named {sourceName}");
        int nv = reader.ReadInt32(), ni = reader.ReadInt32(), ng = reader.ReadInt32();
        bool frame = reader.ReadBoolean(), uv2Present = reader.ReadBoolean();
        if (nv is < 3 or > 100000 || ni is < 3 or > 600000 || ni % 3 != 0 ||
            ng is < 1 or > 40) throw new InvalidDataException("Character geometry limits");
        var vertices = new Vector3[nv];
        for (int i = 0; i < nv; i++)
        {
            vertices[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!vertices[i].IsFinite()) throw new InvalidDataException("Nonfinite vertex");
        }
        var uv = new Vector2[nv];
        for (int i = 0; i < nv; i++) uv[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var uv2 = new Vector2[nv];
        for (int i = 0; i < nv; i++) uv2[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var normals = new Vector3[nv];
        for (int i = 0; i < nv; i++)
            normals[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        var tangents = new float[nv * 4];
        for (int i = 0; i < tangents.Length; i++) tangents[i] = reader.ReadSingle();
        var indices = new int[ni];
        for (int i = 0; i < ni; i++)
        {
            indices[i] = reader.ReadInt32();
            if (indices[i] < 0 || indices[i] >= nv)
                throw new InvalidDataException("Character source triangle index out of range");
        }
        var model = new ArrayMesh();
        for (int g = 0; g < ng; g++)
        {
            _ = reader.ReadString();
            int first = reader.ReadInt32(), triangles = reader.ReadInt32();
            for (int k = 0; k < 5; k++) _ = reader.ReadUInt32();
            for (int k = 0; k < 6; k++) _ = reader.ReadBoolean();
            _ = reader.ReadInt32(); _ = reader.ReadInt32();
            for (int k = 0; k < 3; k++) _ = reader.ReadSingle();
            if (first < 0 || triangles < 1 || (long)(first + triangles) * 3 > ni)
                throw new InvalidDataException("Character source material range");
            var part = new int[triangles * 3];
            Array.Copy(indices, first * 3, part, 0, part.Length);
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            if (uv2Present) arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
            if (frame)
            {
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            }
            arrays[(int)Mesh.ArrayType.Index] = part;
            model.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException("Trailing character geometry data");
        var bounds = model.GetAabb();
        // The seven classes share original ESO humanoid authoring coordinates.
        if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() ||
            bounds.Size.Y > 2.7f || bounds.End.Y > 2.6f ||
            bounds.Position.Y < -0.2f)
            throw new InvalidDataException("Humanoid source bounds outside shared bind pose");
        return model;
    }

    private static int Variant(PopulationSpawnPlanner.Placement spawn,
        IReadOnlyDictionary<string, JsonElement> people)
    {
        if (people.TryGetValue(spawn.Id, out var source) &&
            source.TryGetProperty("gender", out var gender) &&
            gender.ValueKind == JsonValueKind.String)
        {
            string value = gender.GetString() ?? "";
            if (value.Equals("Female", StringComparison.OrdinalIgnoreCase)) return 1;
            if (value.Equals("Male", StringComparison.OrdinalIgnoreCase)) return 0;
        }
        return PopulationSpawnPlanner.StableHash(spawn.Id) % 2;
    }

    private static Color SkinColour(string race)
    {
        return race.ToLowerInvariant() switch
        {
            "altmer" or "high elf" => new Color("#c4a16d"),
            "bosmer" or "wood elf" => new Color("#b98c61"),
            "dunmer" or "dark elf" => new Color("#777e96"),
            "redguard" => new Color("#86533c"),
            "orc" => new Color("#8f9870"),
            "nord" => new Color("#d6a58c"),
            "reachman" => new Color("#b08a72"),
            _ => new Color("#bd917b")
        };
    }

    private static Color HairColour(string id)
    {
        Color[] shades =
        {
            new("#302924"), new("#5d4633"), new("#957452"),
            new("#bb9a67"), new("#7d5140"), new("#b0a79d")
        };
        return shades[PopulationSpawnPlanner.StableHash(id + ":hair") % shades.Length];
    }

    private static Color OutfitColour(string id, int part)
    {
        Color[] cloth =
        {
            new("#536c7e"), new("#8b5c44"), new("#586c4e"),
            new("#74637e"), new("#a28955"), new("#65636a")
        };
        int hash = PopulationSpawnPlanner.StableHash(id + ":outfit");
        int at = (hash / 7 + (part <= 2 ? 1 : 0)) % cloth.Length;
        return cloth[at];
    }

    private void RenderHumanoids()
    {
        for (int variant = 0; variant < Parts.Length; variant++)
        {
            if (Parts[variant].Any(id => !_meshes.ContainsKey(id))) continue;
            var matches = _placements.Where(p => CanUseHumanoidBody(p.Race) &&
                Variant(p, _people) == variant).ToArray();
            if (matches.Length == 0) continue;
            for (int part = 0; part < Names.Length; part++)
            {
                var multi = new MultiMesh
                {
                    Mesh = _meshes[Parts[variant][part]],
                    TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                    UseColors = true,
                    InstanceCount = matches.Length
                };
                for (int i = 0; i < matches.Length; i++)
                {
                    var spawn = matches[i];
                    float size = 0.86f +
                        (PopulationSpawnPlanner.StableHash(spawn.Id + ":size") % 10) * 0.007f;
                    var basis = Basis.FromEuler(new Vector3(0f, spawn.Facing, 0f))
                        .Scaled(Vector3.One * size);
                    multi.SetInstanceTransform(i, new Transform3D(basis, spawn.Position));
                    multi.SetInstanceColor(i, part == 7 ? HairColour(spawn.Id) :
                        part is 6 or 5 ? SkinColour(spawn.Race) :
                        OutfitColour(spawn.Id, part));
                }
                var material = new StandardMaterial3D
                {
                    Roughness = 0.88f,
                    AlbedoColor = Colors.White,
                    VertexColorUseAsAlbedo = true,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled
                };
                AddChild(new MultiMeshInstance3D
                {
                    Name = $"ESO_{Names[part].Replace(" ", "")}_Variant{variant}",
                    Multimesh = multi,
                    MaterialOverride = material
                });
            }
            foreach (var person in matches) _renderedIds.Add(person.Id);
            OriginalBodyCount += matches.Length;
        }
        HasOriginalBodies = OriginalBodyCount > 0;
        GD.Print($"ESO HUMANOID VISUALS: {OriginalBodyCount}/{_placements.Count} " +
            $"people assembled from {_meshes.Count} original GR2 part meshes (incl. hair); " +
            "static original bind poses, provisional garment colours, race-specific heads not imported");
    }

    public override void _ExitTree() { _stopping = true; }
}
