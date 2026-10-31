using System;
using System.Collections.Generic;
using System.IO;
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
            var poses = new[]
            {
                new PopulationSpawnPlanner.Placement("test:male","Source man","Altmer",
                    "Test",new Vector3(-0.9f,0,-4),0f,false),
                new PopulationSpawnPlanner.Placement("test:female","Source woman","Dunmer",
                    "Test",new Vector3(0.9f,0,-4),0f,false),
                new PopulationSpawnPlanner.Placement("test:beast","Source Khajiit","Khajiit",
                    "Test",new Vector3(3,0,-4),0f,false)
            };
            using var doc = JsonDocument.Parse(
                """{"male":{"gender":"Male"},"female":{"gender":"Female"}}""");
            var people = new Dictionary<string,JsonElement>
            {
                ["test:male"] = doc.RootElement.GetProperty("male").Clone(),
                ["test:female"] = doc.RootElement.GetProperty("female").Clone()
            };
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
            if (!model.HasOriginalBodies || model.OriginalBodyCount != 2 ||
                model.SourcePartTypes != 16)
                throw new Exception($"Source bodies incomplete: {model.OriginalBodyCount} people, {model.SourcePartTypes} parts");
            if (!model.HasRenderedPerson("test:male") ||
                !model.HasRenderedPerson("test:female") ||
                model.HasRenderedPerson("test:beast"))
                throw new Exception("Source humanoid race/gender selection wrong");
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
            if (meshes != 26 || totalInstances != 32)
                throw new Exception($"Expected 16 source parts plus 10 face-detail batches, got {meshes} batches, {totalInstances} instances");
            GD.Print("PASS original ESO 8-part humanoids (two variants), gender metadata, race fallback and GPU batching");
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
                var camera = new Camera3D { Current = true, Fov = 38f,
                    Position = new Vector3(-0.9f,2.12f,-2.15f) };
                AddChild(camera);
                camera.LookAt(new Vector3(-0.9f,2.07f,-4));
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
