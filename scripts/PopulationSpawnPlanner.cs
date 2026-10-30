using System;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>
/// Deterministic approximate placement with spatial separation, terrain sampling
/// and conservative source-geometry exclusion. Never claims an exact ESO position.
/// </summary>
public static class PopulationSpawnPlanner
{
    public readonly record struct Candidate(string Id, string Name, string Race,
        string Place, float AnchorX, float AnchorZ);
    public readonly record struct Placement(string Id, string Name, string Race,
        string Place, Vector3 Position, float Facing, bool Shifted);
    public readonly record struct Summary(int Requested, int Placed, int Rejected,
        int Shifted, int BlockedByScenery, int BlockedByTerrain, int BlockedByCrowd);
    public delegate bool TerrainSampler(float localX, float localZ, out float sourceElevation);

    private const float CellSize=6f;
    private static int Bucket(float x)=>Mathf.FloorToInt(x/CellSize);
    private static long Key(int x,int z)=>((long)x<<32)|(uint)z;

    public static (List<Placement> Placements, Summary Stats) Generate(
        IReadOnlyList<Candidate> candidates, Func<float,float,float?> heightAt,
        IReadOnlyList<SourcePropLayer.PropFootprint> obstacles,
        SourceGeometryRaycaster raycaster,
        float minSeparation=2.6f, float footprintMargin=0.8f,
        float radius=230f, int maxAttempts=260)
    {
        if(minSeparation<=0f||radius<=0f||maxAttempts<=0)
            throw new ArgumentOutOfRangeException(nameof(minSeparation));
        var props=new Dictionary<long,List<SourcePropLayer.PropFootprint>>();
        foreach(var box in obstacles)
        {
            if(!float.IsFinite(box.MinX)||!float.IsFinite(box.MaxX)||
                !float.IsFinite(box.MinZ)||!float.IsFinite(box.MaxZ))continue;
            int minX=Bucket(Math.Max(-radius,box.MinX-footprintMargin));
            int minZ=Bucket(Math.Max(-radius,box.MinZ-footprintMargin));
            int maxX=Bucket(Math.Min(radius,box.MaxX+footprintMargin));
            int maxZ=Bucket(Math.Min(radius,box.MaxZ+footprintMargin));
            if(maxX<minX||maxZ<minZ)continue;
            for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
            {
                long key=Key(x,z);
                if(!props.TryGetValue(key,out var list))props[key]=list=new();
                list.Add(box);
            }
        }
        var occupied=new Dictionary<long,List<Vector2>>();
        var placements=new List<Placement>(candidates.Count);
        int scenery=0,terrain=0,crowd=0,shifted=0;
        foreach(var item in candidates)
        {
            var rand=new Random(StableHash(item.Id));
            bool found=false;
            for(int attempt=0;attempt<maxAttempts;attempt++)
            {
                // Favour locations near the named landmark; broaden only when needed.
                float spread=attempt<70 ? 22f : attempt<170 ? 65f : 145f;
                float theta=(float)(rand.NextDouble()*Math.Tau);
                float distance=MathF.Sqrt((float)rand.NextDouble())*spread;
                float x=item.AnchorX+MathF.Cos(theta)*distance;
                float z=item.AnchorZ+MathF.Sin(theta)*distance;
                if(MathF.Abs(x)>radius||MathF.Abs(z)>radius){terrain++;continue;}
                var h=heightAt(x,z);
                if(h is null||!float.IsFinite(h.Value)){terrain++;continue;}
                var cellX=Bucket(x);var cellZ=Bucket(z);
                bool blocked=false;
                if(props.TryGetValue(Key(cellX,cellZ),out var list))
                foreach(var box in list)
                {
                    if(x>=box.MinX-footprintMargin&&x<=box.MaxX+footprintMargin&&
                        z>=box.MinZ-footprintMargin&&z<=box.MaxZ+footprintMargin)
                    {blocked=true;break;}
                }
                if(blocked){scenery++;continue;}
                var where=new Vector2(x,z);
                for(int dz=-1;dz<=1&&!blocked;dz++)
                for(int dx=-1;dx<=1&&!blocked;dx++)
                {
                    if(!occupied.TryGetValue(Key(cellX+dx,cellZ+dz),out var others))continue;
                    foreach(var other in others)if(other.DistanceSquaredTo(where)<minSeparation*minSeparation)
                    {blocked=true;break;}
                }
                if(blocked){crowd++;continue;}
                // Reject any overhead source geometry before accepting original land.
                // Centre and eight perimeter rays approximate the full body radius.
                if(!raycaster.OpenSkyToTerrain(x,z,h.Value)){scenery++;continue;}
                if(!occupied.TryGetValue(Key(cellX,cellZ),out var filled))
                    occupied[Key(cellX,cellZ)]=filled=new();
                filled.Add(where);
                placements.Add(new Placement(item.Id,item.Name,item.Race,item.Place,
                    new Vector3(x,h.Value,z),(float)(rand.NextDouble()*Math.Tau),
                    attempt>=70));
                if(attempt>=70)shifted++;
                found=true;break;
            }
            if(!found)GD.Print($"Population: skipped {item.Name} ({item.Place}); no safe, unoccupied ground.");
        }
        return (placements,new Summary(candidates.Count,placements.Count,
            candidates.Count-placements.Count,shifted,scenery,terrain,crowd));
    }

    public static int StableHash(string id)
    {
        unchecked
        {
            uint hash=2166136261;
            foreach(char c in id)hash=(hash^c)*16777619;
            return (int)(hash&0x7fffffff);
        }
    }
}
