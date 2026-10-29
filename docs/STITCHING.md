# First verified multi-sector ESO landscape

**Historical 5×5-corner experiment:** The parent terrain packages often contain hundreds or thousands of tiles. This early merger used only the first 25 of each and is NOT evidence of a correct full-region join. The current full coverage and world-placement findings supersede it. See [TERRAIN_COVERAGE.md](TERRAIN_COVERAGE.md).

This research project now builds a **single indexed Godot ArrayMesh** from **two adjoining original ESO archive sectors**, IDs 868 and 869, with **50 source heightfield records** (10 x 5 tiles, 65 x 65 samples per tile). It reads original PES2 DAT archives directly in memory at runtime. No exported or intermediary game-data files are required.

## Evidence that these sectors genuinely join

The earlier analysis of section neighbours compared the first non-edge samples (column 63 vs column 1). That was useful for contour similarity but could not prove a seam. A new test compares the *actual shared edge* (column 64 of one section vs column 0 of the next).

- IDs 868 and 869 are adjacent in the 61-column archive-sector grid. Their coordinates are (14,14) and (15,14).
- Four of five shared tile strips contain **252/252 exactly matching, nonzero edge vertices**.
- The fifth strip has a zero-padded edge in the right-hand section, while its first *nonzero interior* samples continue the left-hand terrain contours. The in-memory merge uses the nonzero source values at shared overlapping positions.
- When all 50 source tiles are merged, the overlapping vertex checks report **5,423 matches, 66 zero-padding repairs, and zero conflicting nonzero values**.
- These shared vertices are stored only once in the final mesh: **641 x 321 grid vertices = 205,761 vertices**. Triangle faces are indexed; sparse zero-padding sentinel triangles are omitted.
- **No arbitrary vertical offset** is used to join the sections, and no continuous terrain is fabricated between them.

This validates *this particular pair*. It does NOT verify that the archive-grid coordinate system describes one continuous overworld, nor that the pair corresponds to Auridon. Its geographical zone name is unknown.

A full candidate scan (first one central edge strip and then actual border vertices) covered **2,181 nominal neighbouring section relationships** and found only three compelling shared-edge pair candidates under its conservative filter:

- **868 to 869 (east)**: 252/252 active border vertices match, with one zero-padded strip; the chosen join, which has meaningful topographic variation.
- **1351 to 1412 (south)**: 315/315 active samples match, but their height variance is tiny. This isn't a useful demonstrator of large terrain.
- **350 to 351 (east)**: 257/315 match with a small RMS error, but it also contains nonzero conflicts, so the strict in-memory joining code would refuse to merge it.

Most other archive-grid neighbours fail the same tests. It is unsafe to stitch those just because their IDs are numerically adjacent.

## Rendering technique

1. Load original sections' 25 `terrain-height` records each from PES2 in memory, decompress using the locally available compatible decoder.
2. Validate each `BEEF000C` 65 x 65 heightfield and its tile-index metadata.
3. Place both sectors in an indexed shared grid using the archive sector coordinates and the 5 x 5 tile positions.
4. Match shared vertices. Missing/zero-padded overlaps are resolved using their existing nonzero counterpart. A single conflicting nonzero overlap is a hard error, preventing an invalid stitch.
5. Omit sparse zero-padding triangles at the perimeter, leaving actual nonzero source elevations unchanged.
6. Build a single Godot `ArrayMesh` and give it a diagnostic elevation colour shader.

The project uses a **provisional 500-units-per-sector horizontal scale** because the terrain descriptor begins with three 500 values; the exact ESO world-space scale and altitude semantics have not yet been independently verified.

Use desktop shortcut **ESO Test** and inspect the initial joined landscape. Click **View joined landscape (868 + 869)** to restore it after browsing the atlas. **H** hides the UI for an unobstructed orbit camera. **Show / hide archive grid** toggles the diagnostic sector overview.

## Remaining research

- Determine whether the 61-column archive sector grid has any direct mapping to individual ESO zones/real worldspaces, and what metadata associates it with Auridon.
- Investigate `0x17` fixture/placement records, terrain LOD, and any world-level scene descriptors for *genuine global transforms*.
- Find additional groups where actual nonzero border vertices match, or identify independently encoded transforms that make apparently nonjoining sections fit.
- Decode real ground textures/blend maps and vegetation/structure placements, and implement spatial streaming. The current colour is diagnostic, not original ESO terrain art.

No ESO game files were changed. The external binary decompressor and all reverse-engineering probes remain Git-ignored under `.research`.
