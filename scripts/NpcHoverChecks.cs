using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>Fast headless checks for source-card formatting and ray selection.</summary>
public static class NpcHoverChecks
{
    private static void Check(bool okay, string message)
    {
        if (!okay) throw new InvalidOperationException("NPC hover regression failed: " + message);
    }

    public static void Run()
    {
        var first = new NpcHoverPicker.Target("npc:first", new Vector3(0, 0, -5));
        var second = new NpcHoverPicker.Target("npc:second", new Vector3(0, 0, -12));
        var targets = new[] { second, first }; // nearest, not iteration order
        var origin = new Vector3(0, 1.3f, 0);

        var nearest = NpcHoverPicker.Pick(origin, Vector3.Back * -1f, targets);
        Check(nearest is { Index: 1 }, "select nearest mannequin in crowded line of sight");

        Check(NpcHoverPicker.Pick(origin, Vector3.Right, targets) is null,
            "do not select mannequins away from the cursor");
        Check(NpcHoverPicker.Pick(origin, Vector3.Back * -1f, targets,
            (a,b)=>true) is null, "scenery blocks NPC hover selection");

        var single = new[] { new NpcHoverPicker.Target("npc:far", new Vector3(0, 0, -800)) };
        Check(NpcHoverPicker.Pick(origin, Vector3.Back * -1f, single) is null,
            "no click-through from arbitrarily far away");

        using var doc = JsonDocument.Parse("""
        {
          "name":"Eshaba","race":"Khajiit","gender":"Female","service":"Merchant",
          "faction":"Eyes of the Queen","reaction":"Friendly",
          "town":{"kind":"source_named_fixture","place":"Guard's Forge"},
          "places":[{"name":"Guard's Forge","city":"Vulkhel Guard","zone":"Auridon"}],
          "details":{
            "wikiTitle":"Online:Eshaba",
            "sourcePage":"https://en.uesp.net/wiki/Online:Eshaba",
            "revisionId":44,
            "sourceFields":{"health":"{{ESO Health|fa}}","store":"Guard's Forge"},
            "summaryVariants":[{"number":1,"fields":{"race":"Khajiit","sells":"Merchant"}}],
            "quests":[{"quest_label":"A Hostile Situation"}],
            "standardized":{
              "combat":{"health":null,"healthRaw":"{{ESO Health|fa}}"},
              "references":{"race":{"lore":{"wikiUrl":"https://en.uesp.net/wiki/Lore:Khajiit"}}}
            },
            "articleWikitext":"'''Eshaba''' is a Khajiit merchant.\\n==Dialogue==\\nTest."
          }
        }
        """);
        var placement = new PopulationSpawnPlanner.Placement("npc:eshaba", "Eshaba",
            "Khajiit", "Guard's Forge", new Vector3(-80f,3f,10f),0f,false);
        var card = NpcHoverDetails.Build(doc.RootElement, placement);
        foreach (var expected in new[] {
            "Eshaba","Khajiit","Merchant","Eyes of the Queen","Guard's Forge",
            "A Hostile Situation","{{ESO Health|fa}}",
            "https://en.uesp.net/wiki/Lore:Khajiit","Raw NPC infobox appearances",
            "Position in local preview"
        })
            Check(card.Body.Contains(expected,StringComparison.Ordinal) ||
                card.Subtitle.Contains(expected,StringComparison.Ordinal) ||
                card.Title.Contains(expected,StringComparison.Ordinal),
                "profile omitted "+expected);
        Check(card.SourceUrl=="https://en.uesp.net/wiki/Online:Eshaba",
            "source URL preserved");
        Check(card.ArticleWikitext is {Length:>0},
            "full original wiki article retained separately for lazy expansion");
        Check(!card.Body.Contains("==Dialogue==",StringComparison.Ordinal),
            "hover does not render huge wikitext until requested");
        GD.Print("NPC HOVER SELFTEST PASSED: closest visible mannequin, distance, occlusion, source fields, quests, lore, lazy wiki");
    }
}
