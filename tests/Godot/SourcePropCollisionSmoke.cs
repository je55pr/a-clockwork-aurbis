using System;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>Deterministic local Godot-physics test for streamed source object collision.</summary>
public partial class SourcePropCollisionSmoke : Node3D
{
    public override async void _Ready()
    {
        try
        {
            var streamer = new SourcePropCollisions();
            AddChild(streamer);
            var target = new Node3D { Name = "TestPlayer" };
            AddChild(target);
            target.Position = new Vector3(0f, 1f, 0f);
            streamer.Track(target);

            var cube = new BoxMesh { Size = new Vector3(1.2f, 2f, 1.2f) };
            var surface = cube.SurfaceGetArrays(0);
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, surface);
            var placements = new List<Transform3D>
            {
                new(Basis.Identity, new Vector3(3f, 0f, 0f)),
                new(Basis.Identity, new Vector3(85f, 0f, 0f))
            };
            streamer.Register(mesh, placements, "ALT_STR_TestWall");
            if (streamer.RegisteredInstances != 2 ||
                !SourcePropCollisions.IsSolidModel("ALT_STR_TestWall") ||
                SourcePropCollisions.IsSolidModel("FX_ParticleCloud"))
                throw new Exception("Source collision candidate registration/filter failed");

            for (int i = 0; i < 30; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (streamer.ActiveInstances != 1 || streamer.DecodedShapeTypes != 1)
                throw new Exception($"Near shape count {streamer.ActiveInstances}, shared {streamer.DecodedShapeTypes}");
            var body = streamer.GetNode<StaticBody3D>("NearbySourceObjectBody");
            var shape = body.GetChild<CollisionShape3D>(0);
            if (!shape.Transform.Origin.IsEqualApprox(new Vector3(3f, 0f, 0f)) ||
                shape.Shape is not ConcavePolygonShape3D concave ||
                !concave.BackfaceCollision)
                throw new Exception("Nearby source collision shape/transform/backfaces invalid");

            // Move the target: far instances unload, newly nearby instances
            // appear using the original geometry without duplicate shape data.
            target.Position = new Vector3(85f, 1f, 0f);
            for (int i = 0; i < 30; i++)
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (streamer.ActiveInstances != 1 || streamer.DecodedShapeTypes != 1)
                throw new Exception("Collision streamer did not unload/reload correctly");
            shape = body.GetChild<CollisionShape3D>(0);
            if (!shape.Transform.Origin.IsEqualApprox(new Vector3(85f, 0f, 0f)))
                throw new Exception("Collision did not follow source placement");

            // Verify physics ray hits the *mesh* at the migrated location.
            var query = PhysicsRayQueryParameters3D.Create(
                new Vector3(85f, 6f, 0f), new Vector3(85f, -6f, 0f));
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count == 0 || (GodotObject)hit["collider"] != body)
                throw new Exception("Godot raycast did not hit streamed source mesh");
            GD.Print("PASS nearby source mesh collision, backfaces, placement and streaming");
            GetTree().Quit(0);
        }
        catch (Exception e)
        {
            GD.PushError("Source prop collision smoke failed: " + e);
            GetTree().Quit(1);
        }
    }
}
