# ESO terrain/world hierarchy investigation (26 October 2026)

**Superseded counts:** This early investigation mistakenly read only 25 heightfields per descriptor. The full manifest now yields **253,922 heightfields in 1,276 sections**. The section size can be thousands of tiles; archived figures referring to 'complete 25-tile layouts' and early sector adjacency are historical. See [the corrected coverage analysis](TERRAIN_COVERAGE.md).

This audit was performed against the installed ESO archives in `C:\ESO`. Data was decoded in memory; no game assets were exported or rewritten. Work-in-progress binary probes and the local compatible decoder are ignored under `.research`.

## The hierarchy we can independently verify

```
ESO installed client
├─ depot/eso.mnf: MES2 index, 1,258,203 resource records
│  └─ 258 PES2 DAT archives (depot/esoXXXX.dat)
│     └─ 73 DAT containers with recognizable terrain descriptors
│        └─ 1,266 identifiable terrain sections
│           └─ 1,259 complete 25-tile height layouts
│              └─ 65 × 65 height samples per tile, plus colour/blend and fixture records
└─ game/client/game.mnf: 13,340 separately indexed small client records
```

Manifest-only height fingerprinting identifies **1,189 unique complete height layouts**. **99 sections** are members of an identical *resource-hash layout* group. Note that this identifies byte-equivalent archive resources only; a separate full decoded-height comparison proved sections 868 and 995 have identical geometry across all 25 tiles, even though other package resources differ. Hash fingerprints are informative, but they are not a map-position database.

The 583-byte section descriptor has version/resource-type metadata and a catalogue of channels (`terrain-height`, `terrain-color`, `terrain-normal`, `terrain-lod`, `minimap`, `fixtures`, etc.). We now parse its dimensions correctly: two little-endian 32-bit size values followed by a 16-bit field of 500. For instance, sector 868 declares 2,500 × 2,500, sector 869 declares 1,500 × 1,500. The unit system remains unverified.

The archives preserve the local tile coordinates in the 32-bit height-resource ID:
`tileX = id >> 16; tileZ = id & 0xFFFF`. This is enough to reconstruct a section or selectively join matching parts of several sections.

**The 61-column descriptor-ID grid used by the earlier archive atlas is an archive-organisational hypothesis, not a verified world-space projection.** Sections that have far-apart IDs can have exact matching edges or be copies of the same original geometry. Do not use descriptor IDs as global game coordinates.

## Tests for a *parent* worldspace placement record

- 65×65 `BEEF000C` land records were decoded and section-table fields compared across selected tiles from different DATs. They contain height, colour/blend and normal data, but no identified, confirmed global-origin field.
- The adjacent `0x17` fixture files include probable object transforms/XYZ floats, usually within the descriptor's local size bounds. We do **not** yet know their parent transform, region name, instance/zone ID or complete schema.
- `game/client/game.mnf` has 13,340 records. Correction after deeper probing: representative records **do decompress with the original compatible Oodle decoder**. The inflated data includes signed client UI Lua and shaders, not a verified worldspace placement table. Separately, the first 20,447,232 bytes of a large `depot/eso0000.dat` record can now be decoded from 78 quanta; a decoder failure at the following quantum prevents full reconstruction. That prefix contains a FAFAEBEB metadata index with 150,931 complete zlib entries. See [the detailed format investigation](METADATA_DECODING.md).
- The 92,656 filename entries decoded from the ESO ZOSFT table mostly cover assets like textures, models, audio, and UI, and did not reveal human-readable filenames for terrain placement containers.

The **authoritative parent-world hierarchy**, map origins and mapping to named zones (Auridon included) remain unresolved. Assembling all 1,266 sections into a physically correct regional terrain with current evidence would be speculation.

## Relative-placement results independent of archive ID distance

A resource-hash scan over 1,252 candidate sections and 31,299 height-like records found 25,681 distinct hash/size values and many repeated flat/zero tiles. Filtering to compressed payloads over 4 KB shrank false-positive overlaps dramatically: the remaining 66 potential section-offset matches overwhelmingly used **the same local coordinates** (copied terrain layouts), not spatial offsets expanding a map.

A separate decoded-height scan compared original edge profiles over 1,264 sections and identified a small number of strict joins between nonconsecutive archive IDs. Their **entire boundary** was retested, discarding matches that only covered some strips.

The project now provides six independently checked in-memory 3D configurations:

| Relative connection | Source tiles | Shared edge observation |
|---|---:|---|
| Three-part 996 / 961 / 955 | 55 | Only 10 north + 20 east + 25 centre tiles retained, with no conflicting nonzero overlaps |
| 868 east + 869 west | 50 | 252/252 nonzero edge samples identical, one padded strip |
| 771 south + 492 north | 50 | 325/325 actual border samples identical |
| 1049 east + 1108 west | 50 | 325/325 actual border samples identical |
| 1316 south + 948 north | 50 | 325/325 actual border samples identical |
| 1021 east + 1376 west | 50 | 324/324 nonzero samples identical, one padded sample |

**Every configuration was tested under Godot**, and each produces zero conflicting nonzero vertices at the merge. These are *relative placements derived from original terrain geometry*, not confirmed large named ESO zones. Some are copies, submaps or overlapping areas, rather than additions to one giant contiguous overworld.

## Code added

- `TerrainHierarchyIndex.cs`: read-only hierarchy `DAT -> section -> 25 height records`, computes duplicate-layout groups from original manifest resource hashes.
- `TerrainConnections.cs`: explicit source-supported relative placements, with no game assets stored in code.
- `TerrainBrowser.cs`: a selector for six strict relative-connection layouts, with runtime conflict checking via `CompositeTerrain`. Reuses the existing desktop launcher's source-fingerprint rebuild.
- `CompositeTerrain.cs`: original DAT-based, in-memory shared-vertex merge; refuses conflicting nonzero heights rather than making up geometry.

## Next investigation with the greatest potential value

1. Decode the rest of the archive-0 FAFAEBEB container and establish the schema of its individual numeric records. Representative `game.mnf` entries are already decoded, but so far contain UI Lua/shaders. Search for zone/worldspace links only after identifying a plausible consumer/format. See [metadata research](METADATA_DECODING.md).
2. Look for parent scene/world transforms in the `fixtures` resource category and decoded global data, not just local per-object XYZs.
3. Cross-check a zone's mini-map and original fixture landmarks against the named ESO world map without inferring from descriptor numbers alone.
4. Only then build an auto-assembled multi-zone streamer or an authoritative Auridon scene. **Do not force-fit arbitrary chunks**.

Source-format tooling context: [UESP EsoExtractData](https://github.com/uesp/uesp-esoapps), [eso-tools](https://github.com/eso-tools/eso-tools). Source archives remain unmodified.
