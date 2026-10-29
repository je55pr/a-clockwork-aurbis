# ESO binary hierarchy and metadata decoding (26 October 2026)

This note records **in-file structure discovered by direct inspection**, not a claim that ESO's world placement table has been found. All game files under `C:\ESO` remain untouched. Experiments decompress into memory only; no intermediate game-asset files are produced.

## 1. Parent-to-resource relationships in MES2 (confirmed)

Every entry in the MNF manifest ID table is **8 bytes**, not just the 4-byte ID formerly exposed by `EsoArchive.GetRecord`. The remaining fields are two 16-bit values:
- bytes 0–3: little-endian record ID
- bytes 4–5: little-endian group/channel
- bytes 6–7: little-endian record flags

**1,349** recognizable terrain descriptors have exactly `Field2 = 0`, `Flags = 0x4400` and size 583, with section ID in the first four bytes.

The associated terrain-height tiles carry `Flags = 0x4000` and a `Field2` key:

```
Field2 = (sectionID << 5) | 1
```

Examples verified against real files:

| Section ID | Section descriptor | Height channel key | Terrain data |
|---|---|---|---|
| 381 | `0x4400 / 0x0000` | `0x2FA1` | DAT 0046 |
| 868 | `0x4400 / 0x0000` | `0x6C81` | DAT 0045 |
| 869 | `0x4400 / 0x0000` | `0x6CA1` | DAT 0046 |
| 955 | `0x4400 / 0x0000` | `0x7761` | DAT 0071 |
| 961 | `0x4400 / 0x0000` | `0x7821` | DAT 0077 |
| 996 | `0x4400 / 0x0000` | `0x7C81` | DAT 0051 |

Several other section-linked resource records carry `Flags = 0x4800` and keys `(sectionID << 5)|0`, `|1`, or `|2`, apparently for channel descriptors and fixtures.

**Important scope limitation:** The 16-bit `Field2` range is reused in other manifest contexts. An arbitrary `Flags=0x4000` record with the same upper eleven bits does *not* prove section ownership. The loader conservatively checks the 583-byte parent signature, archive grouping, channel tag, payload size, and original decoded land header. It does **not** equate all 490,000 flag-0x4000/0x4800 resources with terrain. Among the reliable terrain records, the tag is a real parent/channel relationship. It is **not** a north/east/south/west neighbour reference.

The app now uses these fields in `EsoArchive.GetIdIndexFields()` and `TerrainBundleIndex.Discover()`. Verified behaviour: still detects **1,266** drawable terrain bundles and still renders the default **55-tile three-part landscape** with no conflicting source vertices.

## 2. The large depot archive-0 metadata record (partially decoded)

`depot/eso.mnf` record ordinal 0 refers to a compressed 7,778,591-byte resource whose declared uncompressed length is 26,409,859 bytes. It starts with Oodle-Kraken header `8C 06`.

The locally built open-source decoder successfully decompresses its **first 78 consecutive Oodle quanta**, or **20,447,232 bytes**. It then fails at compressed offset **6,055,322**, with another apparent Oodle continuation header (`0C 06`). An instrumented decoder in ignored `.research/ooz-src/ooz_probe.cpp` produced the offset and made the partial-memory decoding reproducible. The original app decoder wasn't modified to hide this failure.

The decoded prefix has an explicit nested record container:

```
Offset  Size  Encoding              Meaning
0       4     big endian            Magic 0xFAFAEBEB
4       4     big endian            Version 23
8       4     big endian            Declared 151,593 records
12      4     big endian            Unknown field 558
16...   var    repeated 12-byte header  two unpacked-size fields + zlib size
        var    zlib stream           zlib-compressed record data
```

From the fully decoded prefix, we can parse **150,931 complete zlib records**, leaving **662 records** beyond the decoder's stopping point. Records are roughly 644–670 bytes inflated, most with a monotonic-looking 32-bit big-endian ID in the first four bytes. They contain almost entirely numeric metadata; no text identifying Auridon, a worldspace, or terrain neighbours was found in the outer first 20 MB.

`DepotMetadataContainer.cs` is a **bounded in-memory parser** for this form. It checks the big-endian header, counts, record lengths and zlib streams, and refuses truncated data by default. The optional `permitTruncated` research mode only exposes complete records from a decoded prefix. Tests read records 0, 1, 4, 10000, 90000 and 150930 and correctly reject an incomplete record table.

**These records' schema and purpose are not yet known.** Their sheer count is *not* evidence of a global placement table. The remaining 662 records and the contents of these individual numeric entries need investigation before such a claim.

## 3. Smaller game/client archive

`game/client/game.mnf` has 13,340 records. The regular local Oodle decoder already decompresses representative records successfully (the original investigation mistook binary signing prefixes for failed decoding). Sampled contents include ESO UI Lua (`ZO_PregameLocalization`, trading-house/UI templates) and shaders. We did **not** find a world-placement hierarchy in that sample. It remains an alternative target only if specific, grounded metadata indicates relevant files.

## Still unresolved

- **Authoritative global placement and zone names:** No verified top-level worldspace-to-sector coordinate or transform table yet.
- What the `FAFAEBEB` record schema represents beyond an ordered index of numeric entries.
- Why the locally built decoder fails at quantum 79 of archive-0 record 0, despite valid preceding data.
- Whether an explicit neighbour table exists locally at all, as opposed to derived scene/world transforms.

## Recommended next technical direction

1. Trace the *consumer* of the `FAFAEBEB` data or locate a published parser for the record payloads; examine which numeric IDs correspond to terrain groups versus unrelated assets. Do not assume it is a placement table.
2. Recover the missing global scene transform/resource schema using references from the client executable or additional recognised section channel records, rather than manual seam fingerprinting.
3. When a verified parent transform is available, use the tag-linked section resources to build a zone-level terrain streamer and cross-check against the original mini-map/fixture placements.

**No new complete named ESO region has been reconstructed in this pass.** The discovery is the manifest's explicit section/channel linkage and a decoded, indexed portion of the deeper metadata record.
