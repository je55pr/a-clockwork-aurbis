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
/// First source-backed humanoid visual pass. Eight genuine ESO customization
/// body/hair meshes share their authoring-space bind pose (0..2.3m).
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
            0x000DEDDB, 0x000DEDA1, 0x000E2CFF, 0x0009EC43 },
        new uint[] { 0x000DEE25, 0x000DEE5D, 0x000DEE9F, 0x000DEF05,
            0x000DEDDD, 0x000DEDA3, 0x000E2D77, 0x0009FB4D }
    };
    private static readonly string[] Names =
        { "Feet", "Lower legs", "Upper legs", "Torso", "Upper arms", "Lower arms", "Head", "Hair", "Helmet", "Shoulders" };
    private static readonly uint[][] HairStyles =
    {
        new uint[] {0x0009EC43,0x0029B6EB,0x0011EEE7,0x0029B2AF},
        new uint[] {0x0009FB4D,0x0029B6ED,0x0011EEE9,0x0029A87F}
    };
    private static readonly string[] SourcePrefixes =
        { "Feet_", "LegsLower_", "LegsUpper_", "Torso_", "ArmsUpper_", "ArmsLower_", "Head_004", "HAR_" };
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
    public IReadOnlyList<int> OutfitCounts => _outfitCounts;
    private readonly int[] _outfitCounts=new int[EsoHumanoidWardrobe.KitCount];
    public static uint HeadSourceId(int variant) => Parts[variant][6];
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
        foreach (uint id in Parts.SelectMany(x => x).Concat(EsoHumanoidWardrobe.Sources)
            .Concat(HairStyles.SelectMany(x=>x)).Concat(EsoHumanoidWardrobe.AccessorySources).Distinct())
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
        if(EsoHumanoidWardrobe.TryGetPart(id,out int wardrobePart))
            return SourcePrefixes[wardrobePart];
        if(HairStyles.Any(styles=>styles.Contains(id)))return "HAR_";
        if(EsoHumanoidWardrobe.TryGetAccessoryName(id,out string name))return name;
        throw new InvalidDataException($"Unknown source body-part ID 0x{id:X8}");
    }

    internal static ArrayMesh LoadMesh(string path,string expectedPrefix,EsoSourceRig? rig=null)
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
        if(rig!=null && sourceName!=rig.MeshName)
            throw new InvalidDataException("Source rig/mesh identity mismatch");
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
            rig?.AddInfluences(arrays,nv);
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

    // Original character face materials are applied by ESO's customization
    // system, not by GR2 material diffuse IDs. Until those shader inputs are
    // decoded, give the source Head_004 shell subtle, clearly non-skeletal
    // facial features. All features reuse one mesh and batch by type.
    private static readonly (string Name, Vector3[] Spots, Vector3 Scale)[] FaceFeatures =
    {
        ("EyeWhites", new [] { new Vector3(-0.035f,2.122f,0), new Vector3(0.035f,2.122f,0) },
            new Vector3(0.011f,0.0057f,0.003f)),
        ("Irises", new [] { new Vector3(-0.035f,2.121f,0.004f), new Vector3(0.035f,2.121f,0.004f) },
            new Vector3(0.0044f,0.0047f,0.0022f)),
        ("Eyebrows", new [] { new Vector3(-0.037f,2.150f,0), new Vector3(0.037f,2.150f,0) },
            new Vector3(0.020f,0.0026f,0.0025f)),
        ("Nose", new [] { new Vector3(0,2.074f,0.008f) },
            new Vector3(0.007f,0.020f,0.009f)),
        ("Lips", new [] { new Vector3(0,2.016f,0.012f) },
            new Vector3(0.017f,0.0028f,0.0024f))
    };

    private void RenderFaceFeatures(int variant,
        IReadOnlyList<PopulationSpawnPlanner.Placement> matches)
    {
        float front = variant == 0 ? 0.086f : 0.077f;
        var unitSphere = new SphereMesh { Radius=1f, Height=2f,
            RadialSegments=12, Rings=8 };
        var faceMaterial = new StandardMaterial3D
        {
            Roughness=0.93f, VertexColorUseAsAlbedo=true,
            CullMode=BaseMaterial3D.CullModeEnum.Disabled
        };
        foreach (var feature in FaceFeatures)
        {
            int count=matches.Count*feature.Spots.Length;
            var multi = new MultiMesh { Mesh=unitSphere, UseColors=true,
                TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount=count };
            for(int i=0;i<matches.Count;i++)
            {
                var person=matches[i];
                float size=0.86f +
                    (PopulationSpawnPlanner.StableHash(person.Id+":size")%10)*0.007f;
                var placement=new Transform3D(
                    Basis.FromEuler(new Vector3(0,person.Facing,0))
                        .Scaled(Vector3.One*size),person.Position);
                Color colour=feature.Name switch
                {
                    "EyeWhites" => new Color("#ded9cd"),
                    "Irises" => new Color("#343b40"),
                    "Eyebrows" => HairColour(person.Id).Darkened(0.35f),
                    "Lips" => SkinColour(person.Race).Darkened(0.28f),
                    _ => SkinColour(person.Race).Darkened(0.065f)
                };
                for(int k=0;k<feature.Spots.Length;k++)
                {
                    var position=feature.Spots[k]+new Vector3(0,0,front);
                    var local=new Transform3D(Basis.Identity.Scaled(feature.Scale),position);
                    int at=i*feature.Spots.Length+k;
                    multi.SetInstanceTransform(at,placement*local);
                    multi.SetInstanceColor(at,colour);
                }
            }
            AddChild(new MultiMeshInstance3D
            {
                Name=$"Face_{feature.Name}_Variant{variant}",
                Multimesh=multi,MaterialOverride=faceMaterial
            });
        }
    }

    private static Color WardrobeColour(string id,int part,int kit)
    {
        // Colour is a test tint only; ESO customization textures have not
        // been mapped to these GR2 part files yet.
        Color[] town={new("#556a7b"),new("#816451"),new("#63775d"),
            new("#836e80"),new("#8a7a5c")};
        Color[] travel={new("#5c573e"),new("#67624c"),new("#476453"),
            new("#795e42"),new("#585a51")};
        Color[] armour={new("#778087"),new("#757367"),new("#6d7883"),
            new("#837a6d"),new("#63686b")};
        Color[] ceremonial={new("#7a5161"),new("#466880"),new("#876c43"),
            new("#65618c"),new("#6c7e62")};
        var palette=kit switch {1=>travel,2=>armour,3=>ceremonial,_=>town};
        Color colour=palette[PopulationSpawnPlanner.StableHash(id+":fabric")%palette.Length];
        if(part==0 || part==1) return colour.Darkened(0.25f);
        if(part==4 || part==5) return colour.Darkened(0.08f);
        return colour;
    }

    private void RenderPartBatch(int gender,int kit,int part,
        IReadOnlyList<PopulationSpawnPlanner.Placement> people,uint id,int style=-1)
    {
        var multi=new MultiMesh
        {
            Mesh=_meshes[id],
            TransformFormat=MultiMesh.TransformFormatEnum.Transform3D,
            UseColors=true,
            InstanceCount=people.Count
        };
        for(int i=0;i<people.Count;i++)
        {
            var spawn=people[i];
            float size=0.86f+
                (PopulationSpawnPlanner.StableHash(spawn.Id+":size")%10)*0.007f;
            var basis=Basis.FromEuler(new Vector3(0f,spawn.Facing,0f))
                .Scaled(Vector3.One*size);
            multi.SetInstanceTransform(i,new Transform3D(basis,spawn.Position));
            Color colour=part==7 ? HairColour(spawn.Id)
                : part==6 || (part==5 && kit==0) ? SkinColour(spawn.Race)
                : WardrobeColour(spawn.Id,part,Math.Max(0,kit));
            multi.SetInstanceColor(i,colour);
        }
        bool metal=kit==2 && (part<EsoHumanoidWardrobe.PartCount || part>=8);
        var material=new StandardMaterial3D
        {
            AlbedoColor=Colors.White,
            VertexColorUseAsAlbedo=true,
            Roughness=metal?0.58f:0.86f,
            Metallic=metal?0.33f:0.02f,
            CullMode=BaseMaterial3D.CullModeEnum.Disabled
        };
        AddChild(new MultiMeshInstance3D
        {
            Name=$"ESO_{Names[part].Replace(" ","")}_Kit{kit}_Variant{gender}"+
                (style>=0?$"_Style{style}":""),
            Multimesh=multi,
            MaterialOverride=material
        });
    }

    private bool HasNamedHelmet(PopulationSpawnPlanner.Placement npc,int gender)
    {
        _people.TryGetValue(npc.Id,out var profile);
        if(EsoHumanoidWardrobe.Choose(npc.Id,profile)!=2)return false;
        int style=EsoHumanoidWardrobe.ArmourStyle(npc.Id);
        return style<2 && _meshes.ContainsKey(
            EsoHumanoidWardrobe.Accessory(gender,style,0));
    }

    private void RenderHumanoids()
    {
        for(int gender=0;gender<Parts.Length;gender++)
        {
            if(Parts[gender].Any(id=>!_meshes.ContainsKey(id)))continue;
            var rendered=new List<PopulationSpawnPlanner.Placement>();
            for(int kit=0;kit<EsoHumanoidWardrobe.KitCount;kit++)
            {
                var matches=_placements.Where(p=>
                {
                    if(!CanUseHumanoidBody(p.Race) || Variant(p,_people)!=gender)
                        return false;
                    _people.TryGetValue(p.Id,out var profile);
                    return EsoHumanoidWardrobe.Choose(p.Id,profile)==kit;
                }).ToArray();
                if(matches.Length==0)continue;
                for(int part=0;part<EsoHumanoidWardrobe.PartCount;part++)
                {
                    uint source=EsoHumanoidWardrobe.Source(gender,kit,part);
                    if(!_meshes.ContainsKey(source))
                        source=Parts[gender][part]; // Partial archive failures never dismember NPCs
                    RenderPartBatch(gender,kit,part,matches,source);
                }
                rendered.AddRange(matches);
                _outfitCounts[kit]+=matches.Length;
            }
            if(rendered.Count==0)continue;
            for(int style=0;style<2;style++)
            {
                int selectedStyle=style;
                var protectedHeads=rendered.Where(p=>HasNamedHelmet(p,gender) &&
                    EsoHumanoidWardrobe.ArmourStyle(p.Id)==selectedStyle).ToArray();
                if(protectedHeads.Length==0)continue;
                for(int accessoryPart=0;accessoryPart<2;accessoryPart++)
                {
                    uint item=EsoHumanoidWardrobe.Accessory(gender,style,accessoryPart);
                    if(_meshes.ContainsKey(item))
                        RenderPartBatch(gender,2,8+accessoryPart,protectedHeads,item,style);
                }
            }
            RenderPartBatch(gender,-1,6,rendered,Parts[gender][6]);
            for(int style=0;style<HairStyles[gender].Length;style++)
            {
                int selectedStyle=style;
                var haircut=rendered.Where(p=>!HasNamedHelmet(p,gender) &&
                    (PopulationSpawnPlanner.StableHash(p.Id+":hair-model")/127)%
                        HairStyles[gender].Length==selectedStyle).ToArray();
                if(haircut.Length==0)continue;
                uint source=HairStyles[gender][style];
                if(!_meshes.ContainsKey(source))source=Parts[gender][7];
                RenderPartBatch(gender,-1,7,haircut,source,style);
            }
            RenderFaceFeatures(gender,rendered);
            foreach(var person in rendered)_renderedIds.Add(person.Id);
            OriginalBodyCount+=rendered.Count;
        }
        HasOriginalBodies=OriginalBodyCount>0;
        GD.Print($"ESO HUMANOID VISUALS: {OriginalBodyCount}/{_placements.Count} " +
            $"people, {_meshes.Count} GR2 meshes, " +
            $"wardrobe [{string.Join(",",_outfitCounts)}]; " +
            "original static body shapes with test outfit tints and provisional face details");
    }

    public override void _ExitTree() { _stopping = true; }
}
