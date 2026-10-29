# Auridon identification and terrain-hole diagnostics

Research update: 26 October 2026. All source ESO assets are read directly from the original MNF/DAT installation in memory. No game assets are exported or committed. The comparison screenshots stored under ignored `.research/` are temporary debug captures.

## A proven source map, but no proven terrain mapping

The project's original `AuridonPreview` loads **all four ESO Auridon source map tiles**, from the named DDS entries:

| Original source asset | ZOSFT file ID |
|---|---|
| `/art/maps/auridon/auridon_base_0.dds` | `0x0002BC8D` |
| `/art/maps/auridon/auridon_base_1.dds` | `0x0002BC8F` |
| `/art/maps/auridon/auridon_base_2.dds` | `0x0002BC91` |
| `/art/maps/auridon/auridon_base_3.dds` | `0x0002BC93` |

They stitch into an identifiable long north-south island with offshore islets, providing a grounded visual reference. Their filenames alone do **not** identify which of the 1,276 terrain descriptors is Auridon.

## Diagnosing the holes instead of guessing missing data

`TerrainGapDiagnostics.Scan()` now builds a tile-by-tile **in-memory coverage image** from the original decoded 65×65 heights. Colour meanings are about data availability and the renderer's zero-sample rule, **not inferred land/water**:

- Green: source tile fully nonzero
- Yellow: zero samples only on tile borders
- Orange: zero samples in tile interior
- Red: all original samples exactly zero
- Grey: expected tile position absent from the archive
- Purple: unreadable/corrupt source heightfield

The original low-detail renderer omits a 4×4-sample quad if *any* of its four elevation vertices is exactly zero. That causes holes but **zero is not yet proven to be a universal missing-geometry sentinel**. We deliberately haven't replaced zero heights with guessed land/sea elevations.

Verified original source readings:

| Section | Heightfield files | Unreadable | Zero samples | Border-only zero tiles | Entirely zero tiles | Discarded preview quads |
|---|---:|---:|---:|---:|---:|---:|
| 233 | 1,750 | 0 | 5,140 (0.07%) | 84 | 0 | 1,269 / 448,000 (0.28%) |
| 899 | 3,600 | 0 | 172,292 (1.13%) | 260 partly zero (incl. interior) | 31 | 13,530 / 921,600 (1.47%) |
| 1391 | 3,500 | 0 | 20,502 (0.14%) | some | 0 | 3,136 / 896,000 (0.35%) |

For source 233, all zero samples fall **only on tile edges**. This means its most visible broad differences from the coloured Auridon map are not just missing source heightfields. They may be different geography, terrain/LOD coverage, or land absent because it is not a heightfield at all. Source 899 has 31 fully zero tiles, which must not be called missing archive files.

**Neighbour-source recovery test:** a direct comparison of zero-valued tile-edge samples to matching positions on adjacent original heightfields found only **4/5,140** repairable zero samples in section 233, **1,341/172,292** in section 899, and **5/20,502** in section 1391. Most zeros either lie at the outer border of the source package, coincide with zero values in adjacent tiles, or (in sections 899/1391) occur inside a tile where border recovery is irrelevant. Therefore replacing every zero with interpolated ground would create unsupported geography; no such fill is applied.

Click **Inspect holes: original tile coverage** while viewing a source to see the actual coverage map and counts. The image is created directly from installed ESO DAT data in memory, not written into a game-data cache.

## Compare terrain elevations with original Auridon map

Godot now offers **Compare terrain elevation with Auridon map**:
- Displays Auridon's four source DDS tiles on the right.
- Generates a thumbnail of the selected terrain section's **relative height map** on the left, by sampling 8×8 points per original 65×65 tile, directly from DAT data.
- Normalises colour against the section's own 5th and 95th elevation percentiles, using deep blue-green for lower elevations and beige for higher elevations.
- Allows switching among up to 85 sizable north-south-ish terrain sections (shape-based screening only, **not** a confident match score).
- Provides **Render selected section** to return to the 3D source geometry.

**The elevation thumbnail is not a coastline mask.** It does not identify the sea level, water surface, ground material or zone map origin, so apparent silhouettes need independent corroboration.

We visually inspected screenshots comparing 204, 233, 739, 991, 1095 and 1272 against the Auridon source map. None is yet an independently verified match. Temporary top-down and comparison captures are ignored under `.research/candidate-*.png` and `.research/compare-*.png`.

For automated debug capture only, `ESO_GAP_DIAGNOSTIC=1`, `ESO_COMPARE_AURIDON=1`, `ESO_CAPTURE_TOPDOWN=1` and `ESO_CAPTURE_AURIDON=1` help inspect data. They are optional, not prerequisites for the normal desktop shortcut.

## Next research that can actually establish Auridon

1. Identify the **zone/map/worldspace-to-terrain descriptor relationship** using original scene metadata or an independent map/world coordinate cross-reference. This is the main open blocker; a similarly shaped heightmap alone isn't proof of Auridon.
2. Find the terrain's **actual water plane/sea-level convention**, scene/LOD geometry and the meaning of zero height samples, then distinguish legitimate open water from rendering cuts.
3. Once a candidate has both a credible source reference and shape/landmark agreement, test Vulkhel Guard (south), Skywatch (centre-north), and Firsthold (north). Do not overwrite the working source explorer with an unverified guess.
4. Implement a verified, coast-aware scene streamer with materials, water and other world geometry read directly from the original files, with no intermediate asset output.
