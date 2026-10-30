using System;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>
/// Picks a single lightweight mannequin from a batched MultiMesh population.
/// The mannequins have no physics bodies, so Godot physics raycasts cannot select them.
/// </summary>
public static class NpcHoverPicker
{
    public readonly record struct Target(string Id, Vector3 Feet);

    public readonly record struct Hit(int Index, Vector3 Point, float Distance);

    private static float Component(Vector3 value, int axis) =>
        axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;

    /// <summary>Slab intersection against one standing NPC's clickable body.</summary>
    public static bool TryHitBody(Vector3 origin, Vector3 ray, Vector3 feet,
        out float distance, float maxDistance = 650f)
    {
        // The mannequins are about 1.96m high, with a 0.31m torso radius.
        // A slightly padded box also catches the head and shoulders.
        var lower = feet + new Vector3(-0.38f, -0.03f, -0.38f);
        var upper = feet + new Vector3(0.38f, 2.10f, 0.38f);
        float nearest = 0f;
        float farthest = maxDistance;
        for (int axis = 0; axis < 3; axis++)
        {
            float start = Component(origin, axis);
            float dir = Component(ray, axis);
            float low = Component(lower, axis);
            float high = Component(upper, axis);
            if (MathF.Abs(dir) < 1e-7f)
            {
                if (start < low || start > high) { distance = 0; return false; }
                continue;
            }
            float t0 = (low - start) / dir;
            float t1 = (high - start) / dir;
            if (t0 > t1) (t0, t1) = (t1, t0);
            nearest = Math.Max(nearest, t0);
            farthest = Math.Min(farthest, t1);
            if (nearest > farthest) { distance = 0; return false; }
        }

        // Never accept mannequins behind the camera or farther than the viewer.
        if (farthest <= 0f) { distance = 0; return false; }
        distance = nearest;
        return true;
    }

    public static Hit? Pick(Vector3 origin, Vector3 direction,
        IReadOnlyList<Target> targets, Func<Vector3, Vector3, bool>? sceneryBlocks = null,
        float maxDistance = 650f)
    {
        if (direction.LengthSquared() < 1e-6f) return null;
        Vector3 ray = direction.Normalized();
        int bestIndex = -1;
        float bestDistance = maxDistance;
        Vector3 bestPoint = default;

        for (int i = 0; i < targets.Count; i++)
        {
            if (!TryHitBody(origin, ray, targets[i].Feet, out float distance, bestDistance))
                continue;
            Vector3 point = origin + ray * distance;
            if (sceneryBlocks != null && distance > 0.08f &&
                sceneryBlocks(origin, point - ray * 0.07f))
                continue;
            bestIndex = i;
            bestDistance = distance;
            bestPoint = point;
        }

        return bestIndex < 0 ? null : new Hit(bestIndex, bestPoint, bestDistance);
    }
}
