using System;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>
/// CPU raycaster for the actual meshes in SourcePropLayer's MultiMeshes.
/// Godot's physics raycasts ignore MultiMeshInstance3D unless colliders are
/// explicitly created. This raycaster tests both faces of each source triangle.
/// Its spatial grid indexes INSTANCES and its BVH indexes triangles per mesh.
/// </summary>
public sealed class SourceGeometryRaycaster
{
    private const float GridSize = 12f;
    private readonly List<Instance> _instances = new();
    private readonly Dictionary<long, List<int>> _cells = new();
    private float _topY = float.NegativeInfinity;
    public int InstanceCount => _instances.Count;
    public int MeshCount { get; private set; }
    public int TriangleCount { get; private set; }
    public float TopY => float.IsFinite(_topY) ? _topY : 1000f;

    private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;
    private static int Cell(float v) => Mathf.FloorToInt(v / GridSize);

    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C)
    {
        public Vector3 Centroid => (A + B + C) / 3f;
        public Aabb Bounds
        {
            get
            {
                var min = A.Min(B).Min(C);
                var max = A.Max(B).Max(C);
                return new Aabb(min, max - min);
            }
        }
    }

    private sealed class Node
    {
        public Aabb Bounds;
        public Node? Left, Right;
        public int First, Count;
        public bool Leaf => Left == null && Right == null;
    }

    private sealed class Geometry
    {
        public readonly Triangle[] Faces;
        public readonly int[] Ordered;
        public readonly Node Root;
        public readonly Aabb Bounds;

        public Geometry(Vector3[] faces)
        {
            if (faces.Length == 0 || faces.Length % 3 != 0)
                throw new ArgumentException("Mesh faces must be a nonempty triangle list.");
            Faces = new Triangle[faces.Length / 3];
            Ordered = new int[Faces.Length];
            for (int i = 0; i < Faces.Length; i++)
            {
                Faces[i] = new Triangle(faces[i * 3], faces[i * 3 + 1], faces[i * 3 + 2]);
                Ordered[i] = i;
            }
            Root = Build(0, Ordered.Length);
            Bounds = Root.Bounds;
        }

        private Node Build(int first, int count)
        {
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = first; i < first + count; i++)
            {
                var bounds = Faces[Ordered[i]].Bounds;
                min = min.Min(bounds.Position);
                max = max.Max(bounds.End);
            }
            var node = new Node { Bounds = new Aabb(min, max - min), First = first, Count = count };
            if (count <= 18) return node;
            Vector3 span = max - min;
            int axis = span.X > span.Y && span.X > span.Z ? 0 : span.Y > span.Z ? 1 : 2;
            // A full sort at every BVH level requires O(n log^2 n) work
            // across the million-plus triangles in the town scenery.
            // Spatial midpoint partitioning is O(n) at each level while
            // preserving every triangle, with a median fallback if nearly
            // all centroids fall on one side (avoids pathological depth).
            float mid = Component(min,axis)+Component(span,axis)*0.5f;
            int left=first,right=first+count-1;
            while(left<=right)
            {
                if(Component(Faces[Ordered[left]].Centroid,axis)<mid)
                    left++;
                else
                {
                    (Ordered[left],Ordered[right])=(Ordered[right],Ordered[left]);
                    right--;
                }
            }
            int n=left-first;
            if(n<=count/8 || n>=count-count/8)
            {
                Array.Sort(Ordered,first,count,Comparer<int>.Create((a,b)=>
                    Component(Faces[a].Centroid,axis).CompareTo(
                        Component(Faces[b].Centroid,axis))));
                n=count/2;
            }
            node.Left = Build(first, n);
            node.Right = Build(first + n, count - n);
            return node;
        }

        public bool Intersects(Vector3 origin, Vector3 direction, float distance)
            => Visit(Root, origin, direction, distance);

        private bool Visit(Node node, Vector3 origin, Vector3 dir, float distance)
        {
            if (!IntersectsAabb(node.Bounds, origin, dir, distance)) return false;
            if (!node.Leaf)
                return (node.Left != null && Visit(node.Left, origin, dir, distance)) ||
                    (node.Right != null && Visit(node.Right, origin, dir, distance));
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                var triangle = Faces[Ordered[i]];
                if (RayTriangle(origin, dir, triangle.A, triangle.B, triangle.C, distance))
                    return true;
            }
            return false;
        }
    }

    private readonly record struct Instance(Geometry Mesh, Transform3D Inverse, Aabb Bounds);

    private static float Component(Vector3 v, int axis)
        => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    private static bool IntersectsAabb(Aabb bounds, Vector3 start, Vector3 dir, float maxDistance)
    {
        float low = 0f, high = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float p = Component(start, axis), d = Component(dir, axis);
            float a = Component(bounds.Position, axis), b = Component(bounds.End, axis);
            if (MathF.Abs(d) < 1e-7f)
            {
                if (p < a - 0.0001f || p > b + 0.0001f) return false;
                continue;
            }
            float t0 = (a - p) / d, t1 = (b - p) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            low = Math.Max(low, t0);
            high = Math.Min(high, t1);
            if (low > high) return false;
        }
        return true;
    }

    /// <summary>Two-sided Moller-Trumbore ray/triangle intersection.</summary>
    public static bool RayTriangle(Vector3 start, Vector3 dir,
        Vector3 a, Vector3 b, Vector3 c, float maxDistance)
    {
        var e1 = b - a;
        var e2 = c - a;
        var p = dir.Cross(e2);
        float determinant = e1.Dot(p);
        if (MathF.Abs(determinant) < 1e-7f) return false;
        float inv = 1f / determinant;
        var t = start - a;
        float u = t.Dot(p) * inv;
        if (u < -0.00005f || u > 1.00005f) return false;
        var q = t.Cross(e1);
        float v = dir.Dot(q) * inv;
        if (v < -0.00005f || u + v > 1.00005f) return false;
        float distance = e2.Dot(q) * inv;
        return distance >= -0.00005f && distance <= maxDistance + 0.00005f;
    }

    // A ray is transformed into model-local coordinates; meshes are never duplicated per NPC.
    // A 12 m spatial hash excludes almost all of the thousands of unrelated prop instances.
    public void AddMesh(Vector3[] faces, IReadOnlyList<Transform3D> transforms)
    {
        if (transforms.Count == 0 || faces.Length == 0) return;
        var mesh = new Geometry(faces);
        MeshCount++;
        TriangleCount += faces.Length / 3;
        foreach (var transform in transforms)
        {
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            var bounds = mesh.Bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                var local = bounds.Position + new Vector3(
                    (corner & 1) == 0 ? 0 : bounds.Size.X,
                    (corner & 2) == 0 ? 0 : bounds.Size.Y,
                    (corner & 4) == 0 ? 0 : bounds.Size.Z);
                var world = transform * local;
                minX = Math.Min(minX, world.X); minY = Math.Min(minY, world.Y); minZ = Math.Min(minZ, world.Z);
                maxX = Math.Max(maxX, world.X); maxY = Math.Max(maxY, world.Y); maxZ = Math.Max(maxZ, world.Z);
            }
            if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minY) ||
                !float.IsFinite(maxY) || !float.IsFinite(minZ) || !float.IsFinite(maxZ))
                continue;
            _topY = Math.Max(_topY, maxY);
            var worldBox = new Aabb(new Vector3(minX, minY, minZ),
                new Vector3(maxX - minX, maxY - minY, maxZ - minZ));
            // The population test never extends beyond a 430 m local radius.
            // Extremely large world-space props only index the relevant cells.
            int minCellX = Cell(Math.Max(-460f, minX));
            int maxCellX = Cell(Math.Min(460f, maxX));
            int minCellZ = Cell(Math.Max(-460f, minZ));
            int maxCellZ = Cell(Math.Min(460f, maxZ));
            if (minCellX > maxCellX || minCellZ > maxCellZ) continue;
            int index = _instances.Count;
            _instances.Add(new Instance(mesh, transform.AffineInverse(), worldBox));
            for (int x = minCellX; x <= maxCellX; x++)
            for (int z = minCellZ; z <= maxCellZ; z++)
            {
                long key = CellKey(x, z);
                if (!_cells.TryGetValue(key, out var list))
                    _cells.Add(key, list = new List<int>());
                list.Add(index);
            }
        }
    }

    public bool Intersects(Vector3 from, Vector3 to)
    {
        Vector3 segment = to - from;
        float length = segment.Length();
        if (length < 0.001f) return false;
        Vector3 dir = segment / length;
        int xMin = Cell(Math.Max(-460f, Math.Min(from.X, to.X)));
        int xMax = Cell(Math.Min(460f, Math.Max(from.X, to.X)));
        int zMin = Cell(Math.Max(-460f, Math.Min(from.Z, to.Z)));
        int zMax = Cell(Math.Min(460f, Math.Max(from.Z, to.Z)));
        if (xMin > xMax || zMin > zMax) return false;
        var visited = new HashSet<int>();
        for (int x = xMin; x <= xMax; x++)
        for (int z = zMin; z <= zMax; z++)
        {
            if (!_cells.TryGetValue(CellKey(x, z), out var list)) continue;
            foreach (int index in list)
            {
                if (!visited.Add(index)) continue;
                var instance = _instances[index];
                if (!IntersectsAabb(instance.Bounds, from, dir, length)) continue;
                // Affine transform preserves the segment parameter, even for model scaling.
                Vector3 localFrom = instance.Inverse * from;
                Vector3 localTo = instance.Inverse * to;
                Vector3 localSegment = localTo - localFrom;
                float localLength = localSegment.Length();
                if (localLength < 0.00001f) continue;
                if (instance.Mesh.Intersects(localFrom,
                    localSegment / localLength, localLength)) return true;
            }
        }
        return false;
    }

    public bool OpenSkyToTerrain(float x, float z, float terrainY,
        float clearance = 0.68f)
    {
        // Sample the vertical sky-to-land segment at the centre and two body
        // rings. An overhead bridge, rock formation, roof or canopy rejects
        // the entire spawn, even if another part of that same model is open.
        float top = Math.Max(terrainY + 10f, TopY + 10f);
        if (Intersects(new Vector3(x, top, z),
            new Vector3(x, terrainY + 0.03f, z))) return false;
        for (int ring = 0; ring < 2; ring++)
        {
            float radius = clearance * (ring == 0 ? 0.48f : 1f);
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.PI / 4f;
                float dx = MathF.Cos(angle) * radius;
                float dz = MathF.Sin(angle) * radius;
                if (Intersects(new Vector3(x + dx, top, z + dz),
                    new Vector3(x + dx, terrainY + 0.03f, z + dz))) return false;
            }
        }
        // Vertical rays alone do not catch narrow upright walls beside a
        // character. Horizontal short casts reject wall intersections through
        // the torso and head within the character radius.
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.PI / 4f;
            var delta = new Vector3(MathF.Cos(angle) * clearance, 0f,
                MathF.Sin(angle) * clearance);
            foreach(float height in new[]{0.75f,1.50f})
            {
                var centre = new Vector3(x, terrainY + height, z);
                if (Intersects(centre, centre + delta)) return false;
            }
        }
        return true;
    }

}
