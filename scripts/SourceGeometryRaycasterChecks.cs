using System;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

/// <summary>Small deterministic geometry and spawn regression checks, no ESO data required.</summary>
public static class SourceGeometryRaycasterChecks
{
    private static void Expect(bool value,string label)
    {
        if(!value)throw new InvalidOperationException("Population raycast check failed: "+label);
    }

    public static void Run()
    {
        Expect(!SourcePropLayer.BlocksNpcSpawn("ActiveRunes"),
            "visual runes must not make entire town impassable");
        Expect(SourcePropLayer.BlocksNpcSpawn("ROK_GRY_Auridon_Hillside006"),
            "real rock still blocks NPCs");
        Expect(SourcePropLayer.BlocksNpcSpawn("ALT_STR_GuildMage001"),
            "real building still blocks NPCs");
        // Overhanging rock ceiling/roof must block a ground spawn.
        var stone=new SourceGeometryRaycaster();
        var roof=new[]{
            new Vector3(-5,5,-5),new Vector3(5,5,-5),new Vector3(5,5,5),
            new Vector3(-5,5,-5),new Vector3(5,5,5),new Vector3(-5,5,5)
        };
        stone.AddMesh(roof,new[]{Transform3D.Identity});
        Expect(stone.MeshCount==1 && stone.InstanceCount==1,"registered mesh instance");
        Expect(!stone.OpenSkyToTerrain(0,0,0),"block roof overhead");
        Expect(stone.OpenSkyToTerrain(8,8,0),"allow exposed ground");
        Expect(stone.Intersects(new Vector3(0,10,0),new Vector3(0,0,0)),
            "raycast hits roof on downward pass");
        Expect(!stone.Intersects(new Vector3(8,10,8),new Vector3(8,0,8)),
            "raycast misses roof outside bounds");
        // The same geometry instanced at a translated location must obey its
        // source scene transform, not be tested at the model origin.
        var translated=new SourceGeometryRaycaster();
        translated.AddMesh(roof,new[]{
            new Transform3D(Basis.Identity,new Vector3(20f,0f,-6f))
        });
        Expect(!translated.OpenSkyToTerrain(20f,-6f,0f),
            "transformed roof blocks at its placed position");
        Expect(translated.OpenSkyToTerrain(0f,0f,0f),
            "transformed roof does not block at original mesh origin");
        // Two-sided test and direct body-level crossing of an upright wall.
        var wall=new SourceGeometryRaycaster();
        var wallTriangles=new[]{
            new Vector3(0,0,-3),new Vector3(0,3,-3),new Vector3(0,3,3),
            new Vector3(0,0,-3),new Vector3(0,3,3),new Vector3(0,0,3)
        };
        wall.AddMesh(wallTriangles,new[]{Transform3D.Identity});
        Expect(wall.Intersects(new Vector3(-10,1.5f,0),new Vector3(10,1.5f,0)),
            "wall blocks horizontal camera view");
        Expect(wall.Intersects(new Vector3(10,1.5f,0),new Vector3(-10,1.5f,0)),
            "two-sided wall intersection");
        // Footprint ring must detect an obstruction even if the centre ray misses.
        var lip=new SourceGeometryRaycaster();
        var smallRoof=new[]{
            new Vector3(0.45f,3,-0.35f),new Vector3(1,3,-0.35f),
                new Vector3(1,3,0.35f),
            new Vector3(0.45f,3,-0.35f),new Vector3(1,3,0.35f),
                new Vector3(0.45f,3,0.35f)
        };
        lip.AddMesh(smallRoof,new[]{Transform3D.Identity});
        Expect(!lip.Intersects(new Vector3(0,10,0),new Vector3(0,0,0)),
            "centre ray deliberately misses overhang");
        Expect(!lip.OpenSkyToTerrain(0,0,0),
            "body-radius perimeter rejects that same overhang");
        // Reject all positions underneath a covering rock rather than spawning inside.
        var inside=new[]{new PopulationSpawnPlanner.Candidate(
            "npc:test","Test Resident","High Elf","Test",0,0)};
        var blocked=PopulationSpawnPlanner.Generate(inside,(x,z)=>0f,
            Array.Empty<SourcePropLayer.PropFootprint>(),stone,
            radius:3f,maxAttempts:60);
        Expect(blocked.Stats.Placed==0 && blocked.Stats.Rejected==1,
            "fully covered region cannot generate an NPC");
        // No scenery: multiple nearby characters retain nonoverlap spacing.
        var free=new SourceGeometryRaycaster();
        var people=new List<PopulationSpawnPlanner.Candidate>();
        for(int i=0;i<24;i++)people.Add(new PopulationSpawnPlanner.Candidate(
            "npc:"+i,"Citizen"+i,"Nord","Market",0,0));
        var placed=PopulationSpawnPlanner.Generate(people,(x,z)=>0f,
            Array.Empty<SourcePropLayer.PropFootprint>(),free);
        Expect(placed.Stats.Placed==people.Count,"free population placement");
        for(int i=0;i<placed.Placements.Count;i++)
        for(int j=i+1;j<placed.Placements.Count;j++)
        {
            var a=placed.Placements[i].Position;
            var b=placed.Placements[j].Position;
            float x=a.X-b.X,z=a.Z-b.Z;
            Expect(x*x+z*z>=2.6f*2.6f-0.002f,"NPC separation");
        }
        // Regression: a location with safe ground only 65m away may NOT send
        // its resident across town if its allowed walking radius is 25m.
        var restricted=new[]{new PopulationSpawnPlanner.Candidate(
            "npc:restricted","Manor Resident","Altmer","Manor",
            0f,0f,25f,"source_named_fixture")};
        var farOpen=PopulationSpawnPlanner.Generate(restricted,
            (x,z)=>x>65f ? 0f : null,
            Array.Empty<SourcePropLayer.PropFootprint>(),free,radius:90f);
        Expect(farOpen.Stats.Placed==0,
            "resident not relocated beyond a named place just to hit spawn count");
        var localOpen=new[]{new PopulationSpawnPlanner.Candidate(
            "npc:local","Guard","Altmer","Mages Guild",
            80f,0f,25f,"source_named_fixture")};
        var nearby=PopulationSpawnPlanner.Generate(localOpen,
            (x,z)=>x>65f ? 0f : null,
            Array.Empty<SourcePropLayer.PropFootprint>(),free,radius:110f);
        Expect(nearby.Stats.Placed==1,"resident allowed nearby open ground");
        var landed=nearby.Placements[0].Position;
        var diff= new Vector2(landed.X-80f,landed.Z);
        Expect(diff.LengthSquared() <= 25f*25f+0.001f,
            "maximum displacement enforced at placement");
        GD.Print("POPULATION RAYCAST SELFTEST PASSED: roof, wall, body radius, blocked ground, crowd spacing, strict home radius");
    }
}
