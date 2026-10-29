using System;
using System.Collections.Generic;

namespace EsoTest;

/// <summary>
/// Source-validated relative placements, NOT ESO's authoritative world hierarchy.
/// Each entry was tested against original decoded border vertices. The composite
/// loader independently rejects any nonzero conflicts before showing a region.
/// These declarative placements contain no copied/re-encoded ESO game data.
/// </summary>
public static class TerrainConnections
{
    public sealed record Candidate(string Label, string Evidence, CompositeTerrain.Patch[] Patches);

    public static IReadOnlyList<Candidate> Confirmed { get; } = new[]
    {
        new Candidate("Three-part region | 961, 955, 996 (55 tiles)",
            "Partial source-verified joins; 5813 matching overlaps, zero conflicts.",
            new[] {
                new CompositeTerrain.Patch(961,0,0,0,4,0,4),
                new CompositeTerrain.Patch(955,5,0,0,4,0,3),
                new CompositeTerrain.Patch(996,0,-5,0,1,0,4)
            }),
        new Candidate("868 + 869 | east/west (50 tiles)",
            "Original shared border: 252 active vertices match exactly; one strip is padded.",
            new[] {
                new CompositeTerrain.Patch(868,0,0,0,4,0,4),
                new CompositeTerrain.Patch(869,5,0,0,4,0,4)
            }),
        new Candidate("771 + 492 | north/south (50 tiles)",
            "Full southern-to-northern shared edge: 325/325 active vertices match.",
            new[] {
                new CompositeTerrain.Patch(771,0,0,0,4,0,4),
                new CompositeTerrain.Patch(492,0,5,0,4,0,4)
            }),
        new Candidate("1049 + 1108 | east/west (50 tiles)",
            "Full east-to-west shared edge: 325/325 active vertices match.",
            new[] {
                new CompositeTerrain.Patch(1049,0,0,0,4,0,4),
                new CompositeTerrain.Patch(1108,5,0,0,4,0,4)
            }),
        new Candidate("1316 + 948 | north/south (50 tiles)",
            "Full southern-to-northern shared edge: 325/325 active vertices match.",
            new[] {
                new CompositeTerrain.Patch(1316,0,0,0,4,0,4),
                new CompositeTerrain.Patch(948,0,5,0,4,0,4)
            }),
        new Candidate("1021 + 1376 | east/west (50 tiles)",
            "Full east-to-west shared edge: 324/324 active samples match, 1 padded.",
            new[] {
                new CompositeTerrain.Patch(1021,0,0,0,4,0,4),
                new CompositeTerrain.Patch(1376,5,0,0,4,0,4)
            })
    };
}
