using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>Checks first-person centre-ray hover independently of an OS cursor.</summary>
public partial class NpcAimHoverSmoke : Node3D
{
    public override async void _Ready()
    {
        try
        {
            var camera = new Camera3D
            {
                Name = "PlayerCamera",
                Position = new Vector3(0, 1.6f, 0),
                Current = true
            };
            AddChild(camera);
            var inspector = new NpcHoverInspector { Name = "Inspector" };
            inspector.Setup(new SourceGeometryRaycaster(), () => true);
            inspector.SetFirstPersonAim(true);
            AddChild(inspector);
            var placements = new[]
            {
                new PopulationSpawnPlanner.Placement("near","Near citizen","Altmer","Test",
                    new Vector3(0,0,-4),0f,false),
                new PopulationSpawnPlanner.Placement("far","Far citizen","Altmer","Test",
                    new Vector3(0,0,-12),0f,false)
            };
            inspector.SetPopulation(placements, new Dictionary<string,JsonElement>());
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!inspector.IsDisplayed || inspector.HoveredId != "near")
                throw new Exception("Centre ray did not show the nearest NPC tooltip");

            camera.Rotation = new Vector3(0, Mathf.Pi / 2, 0);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (inspector.IsDisplayed)
                throw new Exception("Tooltip remained displayed after looking away");

            camera.Rotation = Vector3.Zero;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!inspector.IsDisplayed || inspector.HoveredId != "near")
                throw new Exception("Tooltip failed to return on looking back");
            GD.Print("PASS crosshair NPC tooltip, nearest selection and gaze-off dismissal");
            GetTree().Quit(0);
        }
        catch(Exception e)
        {
            GD.PushError("First-person hover smoke failed: " + e);
            GetTree().Quit(1);
        }
    }
}
