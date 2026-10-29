# Full terrain coverage and the overlap/placement correction

Research conducted against the installed ESO archives on 26 October 2026. All game data is read **directly from the original MES2/PES2 DAT files**, and decoded only in memory. No intermediate ESO asset files are generated, rewritten or committed.

## The central correction: terrain packages are not fixed 5×5 sections

The original proof-of-concept indexed just the first **25** heightfield resources following a 583-byte terrain descriptor and stopped scanning after roughly 80 archive records. That was a **bug in our reader** and made most regions look like small, disconnected five-by-five sections.

The actual MES2 record association is explicit:

- Parent descriptor: `0x4400` flag, with section ID
- Terrain channel: `0x4000` flag, group key `(sectionID << 5) | 1`
- Each terrain-height record's 32-bit file ID encodes its local X and Z tile indices: high 16 bits = X, low 16 bits = Z.
- Descriptors contain two horizontal dimensions, often multiples of 500, which correspond to grids of **100 local units per tile** (one 65×65 heightfield). In many fully populated sections, descriptor dimension divided by 100 equals tile count on that axis.
- Some packages have only a rectangular subset of the declared extent and missing or deliberately unpopulated cells. Do not forcibly rebase the smallest tile coordinate to (0,0): e.g., section 202's Z coordinate begins at 4.

After scanning all properly flagged heightfield records to the next parent descriptor, the source index now finds **1,276 drawable terrain sections** containing **253,922 terrain-height records**. The previous measurements of 1,266 sections and 25 records per section are superseded.

### Verified source examples

| Section ID | Descriptor width × height | Original heightfield records | Observable result |
|---|---|---:|---|
| **233** | 3,500 × 5,000 | **1,750** (35×50) | Fully populated, **0** height mismatches across neighbouring tile edges |
| 899 | 6,000 × 6,000 | 3,600 (60×60) | Rendered; 9 mismatched adjacent edges |
| 1391 | 7,000 × 5,000 | 3,500 (70×50) | Rendered; 14 mismatched adjacent edges |
| 1149 | 6,000 × 5,500 | 3,300 (60×55) | Rendered; 108 mismatched adjacent edges |
| 1207 | 5,500 × 5,500 | 3,025 (55×55) | Rendered; 50 mismatched adjacent edges |
| 1332 | 6,000 × 5,000 | 3,000 (60×50) | Rendered; 15 mismatched adjacent edges |
| 868 | 2,500 × 2,500 | 625 (25×25) | Formerly displayed only its first 25 tiles |
| 431 | 2,000 × 2,000 | 250 | Partial coverage in the original grid |
| 484 | 1,000 × 1,000 | 100 (10×10) | Smaller source at an overlapping fixture-derived position |
| 203 | 5,500 × 3,500 | 1,430 | Partial coverage, extending far beyond its formerly displayed first 25 tiles |
| 141 | 4,000 × 5,000 | 1,748 | A large partially populated terrain |
| 1473 | 4,000 × 5,000 | 1,748 | An overlapping, similar-but-not-identical source variant |

**A full original package is not automatically a named ESO zone.** We still need the parent worldspace/instance identifier and independent zone metadata.

### Why the 26-section fixture experiment looked fragmented

In the earlier renderer, 26 fixture-linked sections were reduced to just 525 visible local tile cells, by treating each section as a fixed 5×5 tile region. The first-pass checker omitted 52 conflicting cells and rendered 473.

With all source heightfields indexed and **their original local coordinates preserved**, the *same* fixture-relative graph instead contains:

- **14,366 source tile references**
- **5,150 different tile positions** on its inferred grid
- **4,098 positions with two or more overlapping source records**
- **1,768 cells** whose overlapping source heights pass the current strict checker
- **3,382 ambiguous cells** where at least two overlapping sources disagree
- 21 mismatched seams between the retained neighbouring cells in the current low-detail experiment

The many overlaps are not merely a bad offset. Some records represent different terrain layouts at the same local or overlapping geographical coordinates. Examples:

- Sections 141 and 1473 overlap over **1,748** cells, with 1,094 matching/compatible sampled cells and 654 differing cells (sampled 9×9 points per tile).
- Sections 204 and 233 overlap over **1,350** cells and disagree at the sampled elevations throughout that overlap.
- Sections 431 and 484 cover different extents (**2,000×2,000** vs **1,000×1,000**); the prior first-25-tile comparison could not establish whether their relationship was a phase/instance difference, or merely a different sampling area.
- Co-located sections 479 and 520 have very similar terrain, with 221/225 sampled cells compatible. Some border differences remain.

**The overlaps need a source-version/worldspace selection rule.** Arbitrarily selecting one layer, smoothing away conflicts, or discarding all sections that have any differences is not yet a safe way to reconstruct a named worldspace.

## Working Godot changes

- `TerrainBundleIndex.Discover` now scans to the **actual next descriptor**, never truncates after 25 heightfields, and keeps each original tile X/Z.
- `AutoRegionRenderer` now positions original local tiles without subtracting the minimum coordinate of a partially populated source. It conservatively omits conflicting overlapping cells.
- `TerrainBrowser` has a **full-section renderer**, using every source heightfield at a quarter-sample preview resolution. Choose a section by ID, then click **Render all tiles for selected section**.
- The startup scene now opens the continuous **1,750-tile section 233**. The viewer also offers a **25-largest-regions** dropdown and retains older small-section comparisons.
- The large, experimental fixture-derived multi-package viewer remains accessible. It is deliberately not described as a solved/complete zone.

The 1,750-tile scene has been rendered in graphical Godot on JESS-PC and verified with a screenshot. Godot's headless test reported zero internal height-edge conflicts.

## Next step

Use the original section/channel descriptors and fixture records to distinguish **worldspace, instance, phase or revision membership**. The scale of each source can now be checked against its declared dimensions. After a trustworthy source-layer decision, we can compose same-version packages into a much larger, continuous renderer and eventually stream around the camera.

Avoid the old inference that descriptor IDs form a geographical 61-column grid, or that the first five tiles represent the entire terrain package.
