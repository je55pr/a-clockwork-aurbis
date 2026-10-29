# ESO world placement investigation: relative terrain geometry

All results below were obtained by reading source records directly from the installed ESO archives. This project never exports intermediate game-resource files. Scratch comparison code and locally compiled decoder are ignored in `.research/`.

## What we now know

**The archive-sector ID is not a reliable physical neighbour locator.** Our first atlas uses `id = x + 61*y`, but this describes the *archive index organisation*, not necessarily the map's world-space layout. Across 2,181 nominally adjacent archive-sector pairs, only a handful had matching nonzero boundary heights.

The terrain descriptor starts with an ID/version followed by **two little-endian dimensions in multiples of 500**, and a third 16-bit value of **500**, consistent with a tile spacing/size parameter (units not independently confirmed). Examples:
- Sector 868: 2,500 × 2,500; section 869: 1,500 × 1,500
- Sector 995: 2,500 × 2,500
- Sectors 961, 955, 996: each 1,500 × 1,500

Fixture records beginning with the `0x17` signature contain apparent local XYZ float positions (for example, records in section 868 have positional triples well within 2,500). Their complete schemas, coordinate transforms, and authoritative world-space parents have **not** been identified. The fixture records are promising for scene objects, but are not yet evidence of a global origin.

### Exact terrain duplication

Sections **868 and 995 contain identical heightfields** at all 25 corresponding 65×65 tiles. Every nonzero height value compared matched exactly: **104,976 / 104,976**. Even though the terrain bytes themselves duplicate, their archive IDs and some additional resource hashes differ, implying that identical terrain may appear in separate map packages/instances.

An archive-wide resource-hash grouping found 18 *obvious* identical package clusters involving 76 section IDs, but this undercounts terrain-only duplicates because non-height resources can differ. The geometry-only 868/995 duplicate demonstrates that limitation.

### Matching physical edges without using archive ID adjacency

An archive-wide scan over **1,264 terrain sections** decoded **20,966 outer-tile edges**. It indexed 4,185 sufficiently variable, nonzero edge samples, finding only a small number of exact matching *distinctive* strip signatures.

Examples of probable relative placement based on exact border data:
- 868 **east** meets 869 **west** (4 matched strips, with one zero-padded strip).
- 995 **east** also meets 869 **west**, but 995's terrain duplicates 868 and therefore adds no new geography.
- 771 **south** meets 492 **north**, 5 of 5 strips matching.
- 1049 **east** meets 1108 **west**, 5 of 5 strips matching.
- 961 **east** meets 955 **west**, first 4 strips matching but final strip containing 21 conflicting nonzero vertices.
- 996 **south** meets 961 **north**, first 2 strips matching, remaining 3 partly or fully conflicting.

A matching edge is evidence of *shared original terrain geometry*. It is not by itself proof of a particular named zone, absolute coordinate, or uniquely placed section. Terrain instances can reuse the same authored source.

## Larger strict joined region: 961 + 955 + 996

We now render a **55-tile L-shaped** connected region from three archives, using only overlap areas whose original vertices agree.

| Sector | Relative placement in tile coordinates | Tiles retained |
|---|---|---:|
| 961 | centre at (0,0), tiles X 0–4 and Y 0–4 | 25 |
| 955 | east at (+5,0), tiles X 0–4 and Y 0–3 | 20 |
| 996 | north at (0,-5), tiles X 0–1 and Y 0–4 | 10 |
| **Total** | | **55** |

The source-data merge checks **5,813 overlapping vertices**, resolves 1 zero-padded sample from the original nonzero source, and finds **zero conflicting nonzero values**. It creates a single indexed Godot `ArrayMesh` with **449,792 triangles**, leaving out cells for which source geometry is missing or zero-padded. All included elevations are original values. There are no invented offsets, no interpolation of missing map areas, and no asset-export files.

The extended region is now the default when launching with the desktop shortcut. The earlier complete 868 + 869 pair is still selectable in the UI. **H** hides the controls.

**Limitation:** Its shape and relative orientation were inferred from matched terrain edges and selective inclusion of only valid segments. We have **not** established authoritative world-space coordinates or which ESO zone it corresponds to. Do not treat the sparse, unrendered areas as game terrain; they are deliberately excluded.

## Next research milestones

1. Recover authoritative global map/scene placement records that tie the archive terrain descriptors to named ESO zones.
2. Fully interpret `0x17` fixture records and identify whether any records refer to a world origin, orientation, scene container, or transform hierarchy.
3. Improve geometry deduplication using only the 25 terrain-height resource hashes, rather than package-wide fingerprinting.
4. Expand a strict connected component of edge-matched terrain while maintaining the no-conflicting-heights invariant.
5. Decode original terrain textures and blend maps, then load scenery when its placement is known.

These remain hypotheses and research tasks, not promised functionality.
