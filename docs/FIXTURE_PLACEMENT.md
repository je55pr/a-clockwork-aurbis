# Fixture-derived ESO section placement: first automatic region reconstruction

**Correction, 26 October 2026:** The earlier 25-tile per-section cap was a reader bug. The full archive contains 253,922 tagged land records in 1,276 sections. The same 26-section fixture graph now maps 14,366 original tile references onto 5,150 distinct coordinate cells, with 3,382 ambiguous overlaps under strict comparison. Earlier 525/473 tile results in this historical note are obsolete. See [TERRAIN_COVERAGE.md](TERRAIN_COVERAGE.md).

**Status:** exploratory, 26 October 2026. All data is read directly and read-only from the installed Elder Scrolls Online DAT/MNF archives. No extracted terrain files are written to disk or checked in.

## Key discovery: original placed-fixture records

The MES2 ID-index identifies section children via their flags/group keys (see [METADATA_DECODING.md](METADATA_DECODING.md)):

- Section parent descriptor: `Flags 0x4400`, `Field2 = 0`, 583-byte payload.
- Fixture position records: `Flags 0x4800`, `Field2 = (sectionId << 5) | 1` or `| 2`.
- Fixture payload header: little-endian `uint32 version=23`, `uint32 count`, followed by **count records of 96 bytes each** starting at offset 8.
- Each record: fixture instance ID at offset `+0` (`uint32 LE`), local X/Y/Z at `+28/+32/+36` (`float32 LE`). Extra data and trailing structures have not been fully decoded.

Direct validation found 9,114/9,114 plausible fixture IDs and local XYZ triples in one large section-1049 file, and analogous results across section types.

The *same fixture instance ID* can occur in several section files, with its recorded coordinates differing by exact increments of **500 units** in X and/or Z, while Y remains unchanged. Representative independent examples:

| Fixture ID | Sections | Difference in local coordinates |
|---|---|---|
| `0x00035C90` | 771, 492 | ΔZ 500 |
| `0x00A87AAD` | 996, 961 | ΔZ 500 |
| `0x00C3A233` | 1021, 1376 | ΔX 500 |
| `0x0098BEDD` | 868, 995 | ΔX 0, ΔZ 0; copied terrain |

This is an **explicit source-based spatial relationship derived from matching unique fixture IDs**. It does *not* yet name the parent worldspace or distinguish every instanced/alternate terrain state.

## Automatic placement-graph scan

`scripts/FixturePlacementGraph.cs` indexes fixture positions in memory and votes for relationships matching a constant whole-number offset in 500-unit increments, with 0.06-unit tolerance for X, Y, Z.

The audited installation yielded:

- 1,349 parent section descriptors inspected
- 1,578 parsed fixture files; 259 unreadable/failed reads
- **1,294,806 original positioned fixture entries** (excluding malformed or out-of-bounds coordinates)
- **178,363 repeated-fixture witnesses** agreeing on one 500-unit relative transform
- 330 distinct candidate section relationships
- At least 20 independent source witnesses for each chosen graph edge: 200 nonzero links forming 29 components
- 13 components with 3 or more sections and **zero fixture-graph cycle contradictions**
- Largest cycle-consistent component: **26 positioned sections**

Note that the first scan counts matching fixture-instance occurrences and may include non-independent copies. The source tests also report some inconsistent matches and contradictory cycles outside the selected groups. They must not be silently combined.

## Automatic 3D viewer: first 26-section candidate

`scripts/AutoRegionRenderer.cs` reads the selected sections' original terrain heightfields, places candidate height tiles relative to the fixture-derived section origins, and **compares overlapping nonzero height samples** before including a cell.

It applies the provisional mapping of one fixture origin step (500 units) to a 5-tile terrain section, informed by previous seam tests. **The exact global conversion is not independently established**, and package dimensions differ. This needs further research; do not interpret the view as a verified Tamriel map.

For the largest 26-section group the first test reported:

| Validation | Result |
|---|---:|
| Fixture-positioned sections | 26 |
| Candidate distinct terrain tile cells | 525 |
| Original terrain cells kept | **473** |
| Cells omitted due to conflicting nonzero source heights | **52** |
| Unreadable cells | 0 |
| Remaining mismatched seams between adjacent retained cells | **5** |
| Low-detail triangles built from original elevation samples | 241,092 |

Many included cells contain only padding/no displayable nonzero terrain or are geographically separated. **The graphical result looks like scattered islands**, not a complete continuous region; the safe cells, missing terrain, and unverified global transform must not be mistaken for a finished map. A real screenshot was rendered in Godot and inspected.

We skip triangles touching zero-valued height padding, and omit conflicting tiles rather than averaging heights, bridging gaps or inventing terrain. Only every fourth original height sample is rendered to make the experimental wider overview fast. The original heightfield files are neither rewritten nor exported.

The viewer retains the established, fully visible 55-tile three-section landscape **as its startup default**. Click **Discover larger regions from original fixture IDs**, then choose any of the contradiction-free components in the dropdown. Alternatively, use `ESO_AUTO_REGION=1` for automated testing. The existing desktop shortcut automatically builds C# when project files change.

## The large depot metadata container is probably not the placement hierarchy

The 26,409,859-byte `FAFAEBEB` resource under `eso0000.dat` decompresses up to 20,447,232 bytes using the open-source Kraken implementation before its 79th quantum fails. A separate UESP-supplied unsigned `oo2core_8_win64.dll` was tested **locally in ignored research files** and did not successfully decompress the full resource either; that binary is **not redistributed in the project**.

However, the 150,931 successfully decoded nested records contain numerically keyed entries that map to ordinary ZOSFT asset IDs, including `/esoui/art/icons/ability_templar_blazing_shield.dds`. This strongly suggests a **general asset catalogue**, not the sought world placement table. Fixing decompression remains a separate codec-research question, not the highest-value placement task.

## What remains before genuine full-zone rendering

1. Distinguish worldspaces/phase variants: some source sections share fixtures at a common location but have substantially different original terrain heights. **Do not merge phases into one world.**
2. Recover the authoritative per-worldspace origin/scale and membership, potentially via fixture record trailing references, scene or client world metadata. A single fixture-translation graph does not identify the parent world.
3. Validate candidate scale and orientation using independently grounded scene coordinates and map landmarks.
4. Once terrain phase/group and transforms are known, assemble a whole zone in Godot without hiding errors or creating intermediate asset exports.
