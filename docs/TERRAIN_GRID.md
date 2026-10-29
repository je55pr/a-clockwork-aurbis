# ESO terrain-sector connectivity investigation

Research performed against the installed ESO depot (MES2/PES2). **No game assets have been converted or exported.**

## Direct finding: a coordinate-indexed grid

The terrain bundle's 583-byte descriptor resource has an ID that follows:

```
descriptorId = sectorX + 61 * sectorY
sectorX = descriptorId % 61
sectorY = descriptorId / 61     (integer division)
```

Validated with descriptors across different archives:

| Descriptor ID | (X,Y) | DAT | Descriptor manifest row |
|---|---|---|---|
| 122 | (0,2) | 0031 | 76384 |
| 123 | (1,2) | 0032 | 80014 |
| 183 | (0,3) | 0031 | 76432 |
| 184 | (1,3) | 0032 | 80061 |
| 320 | (15,5) | 0046 | 137737 |
| 380 | (14,6) | 0045 | 131697 |
| 381 | (15,6) | 0046 | 137760 |
| 441 | (14,7) | 0045 | 131745 |
| 442 | (15,7) | 0046 | 137808 |

This disproves the earlier idea that the descriptor **381** can be assigned to Auridon from its numerical equivalence to the game's zone ID 381. Its arithmetic location is (15,6). Finding out which sections really belong to Auridon needs independent evidence.

We found **1,349** distinct, 583-byte descriptor records across the archive index (IDs range from 2 to 1482), and the original viewer detected **1,266** descriptor-associated groups with plausible heightfields. The upper bound implies 25 index rows if IDs use a 61-column grid. Not every ID has usable data in this installation.

## Inner tile coordinates

Within an individual sector, height-record IDs pack two 16-bit tile coordinates:

```
tileX = resourceId >> 16
tileY = resourceId & 0xFFFF
```

Complete groups generally contain 25 height records in a 5 x 5 arrangement, each a 65 x 65 float heightfield. The archive descriptor starts with three little-endian 500 values and includes channels named `terrain-height`, `terrain-color`, `terrain-blend`, `terrain-lod`, `minimap`, `minimap2`, `terrain-hole`, `terrain-clutter` and `fixtures`. This suggests a spatial sector configuration, but units and the global spatial transform have NOT been decoded.

Individual subtiles inside a bundle were already proven to be spatially organised; these are the basis of the 3D viewer.

## UPDATE: one exact-edge join now confirmed

The earlier seam comparison deliberately sampled one cell inward from the border. It was only a contour similarity test, **not an actual seam test**. Comparing the correct outermost vertices (tile column 64 against neighbouring tile column 0) identified one nontrivial, reliably joining pair, archive sectors **868 and 869** (coordinates (14,14) and (15,14)). Across four strips 252 active boundary vertices match exactly; one strip has a zero-padded edge. The merged in-memory mesh uses nonzero source values at overlapping pad positions, rejecting any conflicting nonzero values.

See [STITCHING.md](STITCHING.md) for the implementation and expanded full-archive survey of 2,181 nominal neighbouring-sector pairs. The numerical differences below remain accurate for the **one-cell-inward samples** but do not by themselves prove that the actual outer boundary vertices differ.

## Important limitation: most neighbouring section heights still do not join

Comparing internal samples one cell inward from adjacent section borders (to avoid the special all-zero perimeter padding):

| Pair | Expected grid adjacency | Mean absolute sample-height difference |
|---|---|---:|
| 380 to 381 | East | 184.32 |
| 381 to 442 | South | 114.78 |
| 122 to 123 | East | 121.93 |
| 123 to 184 | South | 156.90 |

The chosen pairs expose significant jumps even when the archival coordinate labels are adjacent. **Grid adjacency is supported; continuous physical terrain at a shared elevation and orientation is not.** Possible explanations include per-sector height bases/transforms, disconnected worldspaces placed into an atlas, terrain padding, missing placement metadata, or an incorrect assumption about directional orientation. None are confirmed.

The 583-byte descriptor content compared across several sections was identical, so the ID is the useful observed position marker, not an embedded X/Y field in the descriptor itself. Other resource records after the descriptor, particularly structures beginning `0x17` and the original `terrain-lod` data, still need decoding before we can responsibly stitch sectors.

## In-app grid navigator

The Godot viewer now has an archive-sector atlas with clickable cells, cardinal-direction buttons, and coordinate jumps. Selecting a sector reads its own original DAT resources only; this viewer does NOT force multiple unrelated heightfields into a fictitious seamless continent. Click a cell or enter `15,6`, `id:381`, or legacy bundle index `246`. The atlas can be hidden while viewing the 3D terrain.

## Next research

1. Decode the per-sector `0x17` placement/fixture records and land section metadata to look for explicit world-space origin, orientation and vertical offsets.
2. Determine whether the 61-column index represents geographical coordinates or an editor/package atlas, ideally by cross-referencing ESO location metadata and the original minimap assets.
3. Check boundary continuity after applying any *verified* transforms, including alternate axis orientation or relative height offsets.
4. Once a continuous grouping has been validated, stream several sectors into one Godot scene.

Research references for general file extraction and map APIs (not proof of the terrain grid formula):
- https://github.com/uesp/uesp-esoapps
- https://github.com/eso-tools/eso-tools
- https://www.esoui.com/downloads/info601-LibGPS.html

This research was derived from direct binary inspection, not from a published authoritative format specification.
