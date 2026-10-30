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
        string Place, float AnchorX, float AnchorZ,
        float MaxDisplacement=65f, string AnchorKind="map_estimate");
    public readonly record struct Placement(string Id, string Name, string Race,
        string Place, Vector3 Position, float Facing, bool Shifted);
    public readonly record struct Summary(int Requested, int Placed, int Rejected,
        int Shifted, int BlockedByScenery, int BlockedByTerrain, int BlockedByCrowd,
        int FallbackPlacements, int OpenGroundPool);
    public delegate bool TerrainSampler(float localX, float localZ, out float sourceElevation);

    private const float CellSize=6f;
    private static int Bucket(float x)=>Mathf.FloorToInt(x/CellSize);
    private static long Key(int x,int z)=>((long)x<<32)|(uint)z;

    public static (List<Placement> Placements, Summary Stats) Generate(
        IReadOnlyList<Candidate> candidates, Func<float,float,float?> heightAt,
        IReadOnlyList<SourcePropLayer.PropFootprint> obstacles,
        SourceGeometryRaycaster raycaster,
        float minSeparation=2.6f, float footprintMargin=0.8f,
        float radius=430f, int maxAttempts=260)
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
        var missing=new List<Candidate>();
        foreach(var item in candidates)
        {
            float maxTravel=Math.Min(item.MaxDisplacement,radius);
            if(!float.IsFinite(maxTravel)||maxTravel<=0f)
                throw new ArgumentOutOfRangeException(nameof(candidates),
                    $"Invalid maximum spawn displacement for {item.Id}");
            var rand=new Random(StableHash(item.Id));
            bool found=false;
            for(int attempt=0;attempt<maxAttempts;attempt++)
            {
                // Favour locations near the named landmark; broaden only when needed.
                float spread=Math.Min(maxTravel,
                    attempt<70 ? 12f : attempt<170 ? 30f : maxTravel);
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
            if(!found)missing.Add(item);
        }
        // Second pass: bounded, uniformly sampled open ground across the
        // rendered source patch. This avoids repeatedly testing only the
        // same cliff or occupied square near an approximate wiki map anchor.
        int fallbackPlaced=0;
        var pool=new List<Vector3>();
        if(missing.Count>0)
        {
            float spacing=radius<=10f?2.8f:9f;
            var random=new Random(0x41fbe2);
            for(float x=-radius+spacing/2f;x<=radius-spacing/2f;x+=spacing)
            for(float z=-radius+spacing/2f;z<=radius-spacing/2f;z+=spacing)
            {
                float px=x+((float)random.NextDouble()-0.5f)*spacing*0.55f;
                float pz=z+((float)random.NextDouble()-0.5f)*spacing*0.55f;
                var h=heightAt(px,pz);
                if(h is null||!float.IsFinite(h.Value))continue;
                int cx=Bucket(px),cz=Bucket(pz);
                bool blocked=false;
                if(props.TryGetValue(Key(cx,cz),out var nearby))
                    foreach(var box in nearby)
                        if(px>=box.MinX-footprintMargin&&px<=box.MaxX+footprintMargin&&
                            pz>=box.MinZ-footprintMargin&&pz<=box.MaxZ+footprintMargin)
                        {blocked=true;break;}
                if(!blocked && raycaster.OpenSkyToTerrain(px,pz,h.Value))
                    pool.Add(new Vector3(px,h.Value,pz));
            }
            // Allocate from each NPCs closest unoccupied safe source-ground
            // cell. The maximum shift is deliberately finite; a character
            // without clear ground near their place remains unspawned.
            foreach(var item in missing)
            {
                // Never solve a missing location by sending an unrelated NPC
                // across town. The same cap applies in both sampling passes.
                float maxReach=Math.Min(item.MaxDisplacement,radius);
                float closest=maxReach*maxReach;
                int best=-1;
                for(int i=0;i<pool.Count;i++)
                {
                    var point=pool[i];
                    float dx=point.X-item.AnchorX,dz=point.Z-item.AnchorZ;
                    float distance=dx*dx+dz*dz;
                    if(distance>=closest)continue;
                    int cx=Bucket(point.X),cz=Bucket(point.Z);
                    bool occupiedHere=false;
                    for(int nz=-1;nz<=1&&!occupiedHere;nz++)
                    for(int nx=-1;nx<=1&&!occupiedHere;nx++)
                    {
                        if(!occupied.TryGetValue(Key(cx+nx,cz+nz),out var others))continue;
                        foreach(var other in others)
                            if(other.DistanceSquaredTo(new Vector2(point.X,point.Z))<
                                minSeparation*minSeparation)
                            {occupiedHere=true;break;}
                    }
                    if(occupiedHere)continue;
                    closest=distance;best=i;
                }
                if(best<0)
                {
                    GD.Print($"Population: skipped {item.Name} ({item.Place}); no source-clear ground within district radius.");
                    continue;
                }
                var choice=pool[best];
                int gx=Bucket(choice.X),gz=Bucket(choice.Z);
                if(!occupied.TryGetValue(Key(gx,gz),out var list))
                    occupied[Key(gx,gz)]=list=new();
                list.Add(new Vector2(choice.X,choice.Z));
                // Recheck clearance to guard against future pool changes.
                if(!raycaster.OpenSkyToTerrain(choice.X,choice.Z,choice.Y))
                    throw new InvalidOperationException("Unsafe fallback pool landing");
                placements.Add(new Placement(item.Id,item.Name,item.Race,item.Place,
                    choice,StableHash(item.Id)*0.000001f%Mathf.Tau,true));
                fallbackPlaced++;shifted++;
            }
        }
        return (placements,new Summary(candidates.Count,placements.Count,
            candidates.Count-placements.Count,shifted,scenery,terrain,crowd,
            fallbackPlaced,pool.Count));
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
