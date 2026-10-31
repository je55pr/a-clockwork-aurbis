using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

public partial class EsoHumanoidSmoke : Node3D
{
    private EsoArchive? _archive;
    private OodleDecoder? _decoder;

    public override async void _Ready()
    {
        try
        {
            string root = System.Environment.GetEnvironmentVariable("ESO_ROOT") ??
                "C:/ESO/The Elder Scrolls Online";
            _archive = new EsoArchive(Path.Combine(root, "depot", "eso.mnf"));
            _decoder = OodleDecoder.FromEnvironment(
                ProjectSettings.GlobalizePath("res://.research/ooz.dll"))
                ?? throw new FileNotFoundException("Local Oodle library unavailable");
            var poses = new List<PopulationSpawnPlanner.Placement>();
            using var doc = JsonDocument.Parse(
                """{"male":{"gender":"Male"},"female":{"gender":"Female"}}""");
            var people = new Dictionary<string,JsonElement>();
            for(int gender=0;gender<2;gender++)
            {
                var profile=doc.RootElement.GetProperty(gender==0?"male":"female").Clone();
                for(int kit=0;kit<EsoHumanoidWardrobe.KitCount;kit++)
                {
                    string id="";
                    for(int seed=0;seed<4000;seed++)
                    {
                        string candidate=$"test:source:{gender}:{seed}";
                        if(EsoHumanoidWardrobe.Choose(candidate,profile)==kit &&
                            (PopulationSpawnPlanner.StableHash(candidate+":hair-model")/127)%4==kit &&
                            (kit!=2 || EsoHumanoidWardrobe.ArmourStyle(candidate)==gender))
                        {id=candidate;break;}
                    }
                    if(id.Length==0)throw new Exception($"Could not select wardrobe kit {kit}");
                    poses.Add(new PopulationSpawnPlanner.Placement(id,
                        $"{(gender==0?"Man":"Woman")} kit {kit}",
                        gender==0?"Altmer":"Dunmer","Test",
                        new Vector3((kit-1.5f)*1.65f,0,gender==0?-5f:-8.2f),
                        0f,false));
                    people[id]=profile;
                }
            }
            poses.Add(new PopulationSpawnPlanner.Placement("test:beast",
                "Khajiit","Khajiit","Test",new Vector3(5,0,-4),0f,false));
            if (EsoHumanoidVisualLayer.HeadSourceId(0) != 0x000E2CFF ||
                EsoHumanoidVisualLayer.HeadSourceId(1) != 0x000E2D77)
                throw new Exception("Skull-like Head_502/Head_503 mistakenly selected instead of Head_004");
            if (!EsoHumanoidVisualLayer.CanUseHumanoidBody("High Elf") ||
                !EsoHumanoidVisualLayer.CanUseHumanoidBody("Wood Elf") ||
                !EsoHumanoidVisualLayer.CanUseHumanoidBody("Dark Elf") ||
                EsoHumanoidVisualLayer.CanUseHumanoidBody("Khajiit") ||
                EsoHumanoidVisualLayer.CanUseHumanoidBody("Argonian"))
                throw new Exception("TES race aliases/unsupported anatomy rules invalid");
            var model = new EsoHumanoidVisualLayer { Name="OriginalESOHumanoidParts" };
            model.Setup(_archive, _decoder, poses, people);
            AddChild(model);
            for (int i = 0; i < 2400 && !model.HasOriginalBodies; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!model.HasOriginalBodies || model.OriginalBodyCount != 8 ||
                model.SourcePartTypes != 66)
                throw new Exception($"Source bodies incomplete: {model.OriginalBodyCount} people, {model.SourcePartTypes} parts");
            if (people.Keys.Any(id=>!model.HasRenderedPerson(id)) ||
                model.HasRenderedPerson("test:beast"))
                throw new Exception("Source humanoid race/gender selection wrong");
            for(int kit=0;kit<EsoHumanoidWardrobe.KitCount;kit++)
                if(model.OutfitCounts[kit]!=2)
                    throw new Exception($"Outfit kit {kit} did not render both genders");
            int meshes = 0, totalInstances = 0;
            foreach (var node in model.GetChildren())
                if (node is MultiMeshInstance3D instance)
                {
                    meshes++;
                    totalInstances += instance.Multimesh.InstanceCount;
                    var aabb = instance.Multimesh.Mesh.GetAabb();
                    if (instance.Name.ToString().StartsWith("ESO_", StringComparison.Ordinal) &&
                        (aabb.End.Y > 2.6f || aabb.Position.Y < -0.20f))
                        throw new Exception("Original source body part geometry not at bind pose");
                }
            if (meshes != 70 || totalInstances != 130)
                throw new Exception($"Expected 48 outfits, 2 heads, 6 hairstyles, 4 named armor accessories and 10 faces, got {meshes} batches, {totalInstances} instances");
            for(int gender=0;gender<2;gender++)
            {
                var preferred=gender==0?"LegionZero_":"HonorGuardHvy_FEM";
                string helm=gender==0?"LegionZero_Helmet_A":"HonorGuardHvy_FEMHelmet_A";
                uint source=EsoHumanoidWardrobe.Accessory(gender,gender,0);
                if(!EsoHumanoidWardrobe.TryGetAccessoryName(source,out string expected) ||
                    expected!=helm || !model.GetChildren().Any(n=>
                    n.Name.ToString().Contains("ESO_Helmet_Kit2_Variant"+gender)))
                    throw new Exception($"Named source helmet {helm} not rendered");
            }
            GD.Print("PASS 4 ESO source wardrobe variations, named Legion Zero/Honor Guard accessories, 4 hairstyles, fallback and GPU batching");
            string? screenshot = System.Environment.GetEnvironmentVariable("ESO_NPC_PREVIEW_PATH");
            if (!string.IsNullOrWhiteSpace(screenshot) && DisplayServer.GetName() != "headless")
            {
                AddChild(new DirectionalLight3D
                {
                    RotationDegrees = new Vector3(-40,25,0),
                    LightEnergy = 1.75f
                });
                var environment = new Godot.Environment
                {
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = Colors.White,
                    AmbientLightEnergy = 1.3f,
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color("#344353")
                };
                AddChild(new WorldEnvironment { Environment = environment });
                var camera = new Camera3D { Current = true, Fov = 48f,
                    Position = new Vector3(0f,2.4f,4.5f) };
                AddChild(camera);
                camera.LookAt(new Vector3(0f,1.1f,-6.5f));
                for (int i = 0; i < 4; i++)
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var image=GetViewport().GetTexture().GetImage();
                GD.Print($"NPC PREVIEW {image.SavePng(screenshot)} {image.GetWidth()}x{image.GetHeight()} {screenshot}");
            }
            GetTree().Quit(0);
        }
        catch(Exception e)
        {
            GD.PushError("ESO humanoid integration failed: " + e);
            GetTree().Quit(1);
        }
    }
    public override void _ExitTree()
    {
        _decoder?.Dispose();
        _archive?.Dispose();
    }
}
