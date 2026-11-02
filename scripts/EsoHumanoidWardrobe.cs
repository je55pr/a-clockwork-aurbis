using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EsoTest;

/// <summary>
/// Source geometry wardrobe experiments. Each entry is a complete six-part
/// male/female GR2 selection, NOT an assertion about an NPC's canonical ESO
/// equipment or an identified player-craftable armor set. Race and gender
/// are user-linked source facts; outfit selection is a deterministic test seed.
/// Source assets/decoders are read from the user's installed ESO depot only.
/// </summary>
internal static class EsoHumanoidWardrobe
{
    public const int PartCount=6;
    public const int KitCount=4;
    public static readonly string[] KitNames =
        { "Town clothes", "Travel gear", "Armoured gear", "Ceremonial gear" };
    private static readonly uint[][][] Kits =
    {
        new[]
        {
            new uint[] {0x000DEE23,0x000DEE5B,0x000DEE9D,0x000DEF03,0x000DEDDB,0x000DEDA1},
            new uint[] {0x000DEE4F,0x000DEE7F,0x000DEECD,0x000DEF17,0x000DEDFF,0x000DEDC1},
            new uint[] {0x0015E3D7,0x0011FB11,0x0011FECD,0x0011FB05,0x0011FB09,0x0011FD05},
            new uint[] {0x00100E21,0x000E0CEB,0x000E0CD5,0x000DEF3D,0x000E1A6B,0x000E0C6F}
        },
        new[]
        {
            new uint[] {0x000DEE25,0x000DEE5D,0x000DEE9F,0x000DEF05,0x000DEDDD,0x000DEDA3},
            new uint[] {0x000DEE51,0x000DEE81,0x000DEECF,0x000DEF19,0x000DEE01,0x000DEDC3},
            new uint[] {0x0015E3CB,0x0011FB0F,0x0011F8E1,0x0011FB03,0x0011FB07,0x0011FE97},
            new uint[] {0x00100E1F,0x000E0CE9,0x000E0CD3,0x000DEF3F,0x000E1A69,0x000E0C6D}
        }
    };

    // Two actually named ESO style accessory pairs. The six base pieces
    // remain test combinations, so do not claim these are complete set kits.
    // [gender][named style][helmet (0) or shoulders (1)]
    private static readonly uint[][][] StyleAccessories =
    {
        new[]
        {
            new uint[] {0x0023F697,0x0023F693}, // Legion Zero
            new uint[] {0x0015F6D9,0x0015F6E5} // Honor Guard Heavy
        },
        new[]
        {
            new uint[] {0x0023F699,0x0023F695},
            new uint[] {0x0015F6DB,0x0015F6E7}
        }
    };
    public static readonly string[] AccessoryStyles={"Legion Zero","Honor Guard"};

    public static uint Source(int gender,int kit,int part) => Kits[gender][kit][part];
    public static uint Accessory(int gender,int style,int part) =>
        StyleAccessories[gender][style][part];
    public static IEnumerable<uint> AccessorySources =>
        StyleAccessories.SelectMany(g=>g).SelectMany(style=>style).Distinct();
    public static int ArmourStyle(string id) =>
        (PopulationSpawnPlanner.StableHash(id+":named-helm")/127)%3;
    public static bool TryGetAccessoryName(uint id,out string expected)
    {
        for(int gender=0;gender<2;gender++)
        for(int style=0;style<2;style++)
        for(int part=0;part<2;part++)
        {
            if(Accessory(gender,style,part)!=id)continue;
            string prefix=style==0?"LegionZero":"HonorGuardHvy";
            string suffix=part==0?"Helmet_A":"Shoulders_A";
            expected=prefix+"_"+(gender==1?"FEM":"")+suffix;
            return true;
        }
        expected="";
        return false;
    }

    public static IEnumerable<uint> Sources =>
        Kits.SelectMany(genders=>genders).SelectMany(parts=>parts).Distinct();

    public static bool TryGetPart(uint id,out int part)
    {
        foreach(var variant in Kits)
        foreach(var kit in variant)
        for(int i=0;i<PartCount;i++)
            if(kit[i]==id){part=i;return true;}
        part=-1;
        return false;
    }

    public static int Choose(string id,JsonElement profile)
    {
        // NPC role gives a weak bias, but all 4 experimental appearances
        // remain possible. This is for visual testing, not simulation gear.
        int seed=PopulationSpawnPlanner.StableHash(id+":visual-kit");
        int kit=seed%KitCount;
        if(profile.ValueKind==JsonValueKind.Object)
        {
            string role="";
            if(profile.TryGetProperty("service",out var service)
                && service.ValueKind==JsonValueKind.String)
                role=service.GetString()??"";
            if(profile.TryGetProperty("name",out var name)
                && name.ValueKind==JsonValueKind.String)
                role+=" "+name.GetString();
            if(role.Contains("guard",StringComparison.OrdinalIgnoreCase)
                || role.Contains("soldier",StringComparison.OrdinalIgnoreCase))
                kit=seed%3==0 ? 1 : 2;
            else if(role.Contains("merchant",StringComparison.OrdinalIgnoreCase)
                || role.Contains("farmer",StringComparison.OrdinalIgnoreCase))
                kit=seed%3==0 ? 1 : 0;
        }
        return kit;
    }
}
