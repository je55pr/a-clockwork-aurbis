using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Streams Godot physics shapes for the real, source-derived GR2 prop meshes
/// around a first-person player. The visual MultiMeshes and the source
/// placements stay authoritative. No collider is manufactured for an
/// unresolved model, and a shared triangle shape is reused for each placement.
/// </summary>
public partial class SourcePropCollisions : Node3D
{
    private const float CellSize = 16f;
    private const float LoadRadius = 30f;
    private const float UnloadRadius = 40f;
    private const int MaxActive = 260;
    private const int AddsPerUpdate = 18;
    private const double UpdateSeconds = 0.20;

    private sealed record Candidate(ArrayMesh Mesh, Transform3D Transform, Aabb Bounds);
    private readonly List<Candidate> _candidates = new();
    private readonly Dictionary<long, List<int>> _grid = new();
    private readonly Dictionary<int, CollisionShape3D> _active = new();
    private readonly Dictionary<ArrayMesh, ConcavePolygonShape3D> _shapes = new();
    private readonly HashSet<ArrayMesh> _badShapes = new();
    private StaticBody3D _body = null!;
    private Node3D? _target;
    private double _elapsed;
    private Vector3 _lastPosition = new(float.PositiveInfinity, 0, 0);
    private bool _dirty = true;

    public int RegisteredInstances => _candidates.Count;
    public int ActiveInstances => _active.Count;
    public int DecodedShapeTypes => _shapes.Count;

    private static long Key(int x, int z) => ((long)x << 32) | (uint)z;
    private static int Cell(float point) => Mathf.FloorToInt(point / CellSize);

    public override void _Ready()
    {
        _body = new StaticBody3D { Name = "NearbySourceObjectBody" };
        AddChild(_body);
    }

    public void Track(Node3D target)
    {
        _target = target;
        _dirty = true;
    }

    // Some visual-only assets (grass, smoke, decals, magical VFX) must not
    // become walls. Trees, rocks, architecture, docks and ordinary props remain
    // eligible, including their actual curved/angled source faces.
    public static bool IsSolidModel(string name)
    {
        return !name.StartsWith("FX_", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("VFX_", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Rune", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Decal", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Smoke", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Particle", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Grass", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Flower", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Leaf", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("Water", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("FireFX", StringComparison.OrdinalIgnoreCase);
    }

    private static Aabb WorldBounds(Aabb local, Transform3D t)
    {
        var low = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var high = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var v = local.Position + new Vector3(
                (i & 1) == 0 ? 0 : local.Size.X,
                (i & 2) == 0 ? 0 : local.Size.Y,
                (i & 4) == 0 ? 0 : local.Size.Z);
            var world = t * v;
            low = low.Min(world);
            high = high.Max(world);
        }
        return new Aabb(low, high - low);
    }

    public void Register(ArrayMesh mesh, IReadOnlyList<Transform3D> placements, string sourceName)
    {
        if (!IsSolidModel(sourceName) || placements.Count == 0) return;
        var local = mesh.GetAabb();
        if (!local.Position.IsFinite() || !local.Size.IsFinite() ||
            local.Size.LengthSquared() < 0.0025f ||
            local.Size.X > 350f || local.Size.Y > 350f || local.Size.Z > 350f)
            return;
        foreach (var t in placements)
        {
            var bounds = WorldBounds(local, t);
            if (!bounds.Position.IsFinite() || !bounds.Size.IsFinite() ||
                bounds.Size.X > 350f || bounds.Size.Z > 350f)
                continue;
            int minX = Cell(Math.Max(-500, bounds.Position.X));
            int maxX = Cell(Math.Min(550, bounds.End.X));
            int minZ = Cell(Math.Max(-500, bounds.Position.Z));
            int maxZ = Cell(Math.Min(550, bounds.End.Z));
            if (maxX < minX || maxZ < minZ) continue;
            int index = _candidates.Count;
            _candidates.Add(new Candidate(mesh, t, bounds));
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                long key = Key(x, z);
                if (!_grid.TryGetValue(key, out var cell))
                    _grid.Add(key, cell = new List<int>());
                cell.Add(index);
            }
        }
        _dirty = true;
    }

    private static float HorizontalDistanceSquared(Aabb bounds, Vector3 point)
    {
        float dx = Math.Max(bounds.Position.X - point.X, Math.Max(0, point.X - bounds.End.X));
        float dz = Math.Max(bounds.Position.Z - point.Z, Math.Max(0, point.Z - bounds.End.Z));
        return dx * dx + dz * dz;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_target == null || !GodotObject.IsInstanceValid(_target)) return;
        _elapsed += delta;
        if (_elapsed < UpdateSeconds) return;
        _elapsed = 0;
        var point = ToLocal(_target.GlobalPosition);
        if (!_dirty && point.DistanceSquaredTo(_lastPosition) < 2.25f) return;
        _lastPosition = point;
        _dirty = false;
        Refresh(point);
    }

    private void Refresh(Vector3 point)
    {
        var nearby = new HashSet<int>();
        int cx0 = Cell(point.X - UnloadRadius), cx1 = Cell(point.X + UnloadRadius);
        int cz0 = Cell(point.Z - UnloadRadius), cz1 = Cell(point.Z + UnloadRadius);
        for (int x = cx0; x <= cx1; x++)
        for (int z = cz0; z <= cz1; z++)
        {
            if (!_grid.TryGetValue(Key(x, z), out var indices)) continue;
            foreach (int index in indices)
            {
                var box = _candidates[index].Bounds;
                // Preserve building roofs, balconies, rock overhangs, etc.
                if (box.Position.Y > point.Y + 16f || box.End.Y < point.Y - 6f)
                    continue;
                if (HorizontalDistanceSquared(box, point) <= UnloadRadius * UnloadRadius)
                    nearby.Add(index);
            }
        }

        var selected = nearby.OrderBy(i => HorizontalDistanceSquared(_candidates[i].Bounds, point))
            .ThenBy(i => i).Take(MaxActive).ToHashSet();
        foreach (int index in _active.Keys.ToArray())
        {
            if (selected.Contains(index)) continue;
            var collider = _active[index];
            _body.RemoveChild(collider);
            collider.QueueFree();
            _active.Remove(index);
        }

        int added = 0;
        foreach (int index in selected.OrderBy(i =>
            HorizontalDistanceSquared(_candidates[i].Bounds, point)).ThenBy(i => i))
        {
            if (_active.ContainsKey(index)) continue;
            var candidate = _candidates[index];
            if (HorizontalDistanceSquared(candidate.Bounds, point) > LoadRadius * LoadRadius)
                continue;
            if (!_shapes.TryGetValue(candidate.Mesh, out var shape))
            {
                if (_badShapes.Contains(candidate.Mesh)) continue;
                try
                {
                    shape = candidate.Mesh.CreateTrimeshShape();
                    shape.BackfaceCollision = true;
                    _shapes.Add(candidate.Mesh, shape);
                }
                catch (Exception e)
                {
                    _badShapes.Add(candidate.Mesh);
                    GD.PushWarning("Source prop collider could not be decoded: " + e.Message);
                    continue;
                }
            }
            var collider = new CollisionShape3D { Shape = shape, Transform = candidate.Transform };
            _body.AddChild(collider);
            _active.Add(index, collider);
            if (++added >= AddsPerUpdate)
            {
                // Revisit pending nearby shapes even when player stands still.
                _dirty = true;
                break;
            }
        }
    }
}
