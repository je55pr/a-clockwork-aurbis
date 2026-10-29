# ESO near-field terrain shader: source-backed reconstruction notes

Research verified against the user's installed archives on 27 October 2026.

**Goal:** reproduce Auridon source section 233 with ESO's correct close-range terrain textures, weights, transforms and normals. **Not achieved yet:** the per-zone source-to-texture palette and its parameters are missing. No substitute texture set has been introduced.

## Corrected game.mnf parsing: shaders were there all along

The initial investigation mistakenly treated the content of every `game/client/game.mnf` entry as just ASN.1/binary data. Each ordinary resource we inspected is actually wrapped:

- An eight-byte prefix with a big-endian length in bytes 4–7
- An ASN.1 / DER-style metadata envelope of that declared length
- A further big-endian length (40 bytes in observed samples) and a 40-byte digest or identifier
- Then the **original Lua, XML, DDS or readable HLSL/FXH resource**. A few resources use slightly different layouts and must be checked.

The second-last `game.mnf` record is an unwrapped `ZOSFT` filename table, ID 0, with thousands of filenames, including the whole `/shaders/` tree. This resolves the shader files by original name without blind scanning, extraction to the game's directory or modifying the installed archives.

We decoded and inspected the real shader sources using the existing read-only runtime decoder and an ignored in-memory/local Probe script. Relevant named original assets include:

- `/shaders/terrainsinglelayerps.hlsl`
- `/shaders/terrainmultilayerps.hlsl`
- `/shaders/terrainsinglelayerminimapps.hlsl`
- `/shaders/terrainlodps.hlsl`
- `/shaders/terrainvs.hlsl`
- `/shaders/zoterraincommon.fxh`
- `/shaders/zoterrainmaterialcommon.fxh`
- `/shaders/codegen/zoterraincommonsamplerbindings.fxh`
- `/shaders/materialdefinitions.xml`
- `/shaders/zoshadertechniques.xml`

The related open-source [doodlum/eso-shader-tools](https://github.com/doodlum/eso-shader-tools) confirmed likely source paths. We reviewed its source, not executed its utilities.

## Exact rules confirmed from the original shaders

**TerrainSingleLayerPS** defines up to 16 original base diffuse+specular textures and 16 matching normal textures, using four RGBA blend maps.

The original code reads each blend map in **BGRA order**, so one set of four shader texture slots corresponds to raw blend map channels **blue, green, red, alpha**. This reverses channel 0/2 relative to the earlier diagnostic labels. The `TerrainPaintMasks` inspector now uses shader texture slot numbering, not file-byte order. In the tested Auridon 5x5 patch, 409,600 weight pixels are present, 291,476 sum to exactly 255, and the active **shader** material slots are 0–7 and 10 (previously, raw byte channels 0–8).

In `TerrainSingleLayerPS`, each texture slot's UV transform is supplied by the runtime `f4TerrainUVScaleBias[16]` and `f4TerrainUVRotation[8]` shader constants. The actual transform (from `ZoTerrainCommon.fxh`) is:

1. Subtract 0.5 from tile UV
2. Rotate by the pair supplied for that layer
3. Add 0.5
4. Multiply by the layer's scale
5. Add the layer's 2D offset

The near-field pixel shader also uses a 128x128 blend-map UV correction to sample texel centres and supports a 0.1 normal/texture contribution threshold in one optimisation path. The `TerrainMultiLayerPS` variant directly applies BGRA blending for four layers.

**Colour tint:** the normal near-field terrain pixel shader multiplies the blended surface colour by the original `ColorMap` when `FX_DEF_VERT_COLOR` is enabled. This independently confirms why the separately decoded original 128x128 `terrain-color` subrecord matters. However, adding it onto **the baked LOD diffuse** may apply the same tint twice; this experiment remains optional and disabled by default.

**LOD distinction:** `TerrainLODPS` samples only one baked diffuse DDS and a corresponding baked normal DDS. That is the path the current Godot viewer approximates, separate from the 16-layer near-field material shader. The source's normal packing and lighting conventions require further comparison before claiming pixel-perfect equivalence.

## Where the actual Auridon palette might live

The shader source describes the sampler slots and constant-buffer formats, **not which 16 specific texture assets Auridon's cells should bind**. It does not provide the runtime contents of `f4TerrainUVScaleBias`, `f4TerrainUVRotation`, nor the texture handles `TerrainTexture1` through `TerrainTexture16`.

We checked the real **section 233 descriptor** (583 bytes). It contains land dimensions (3500 x 5000), a list of archive subresource names/formats and channel metadata, but no obvious 16-texture palette. The main source land payload contains height, colour, four blend arrays, normal and other heightfield data. Neither includes an immediately visible 16-resource assignment.

We tested `terrain-map` channel 10: 450 structured records, each encoding 2x2-tile geography via a `DEBEEF01` record with many grid-like numeric samples. They are **not conventional DDS textures or visibly a 16-item palette**. That record type remains incompletely decoded.

`terrain` resources in the depot ZOSFT filename list overwhelmingly refer to UI map images and FX, not the unnamed actual repeating ground materials. `TerrainTextureDef.xml` is mentioned in one of the original shader's comments but has **no matching filename in the current game ZOSFT table**. The file may be build-time/editor content, so don't presume it's shipped as that XML.

The named `materialdefinitions.xml` we have located is a **shader variant/technique definition**, not Auridon's texture palette.

## Next research priority

Recover the **runtime terrain material palette and transform parameters** from a source-owned zone/worldspace metadata structure, then use the already-decoded BGRA splat maps to bind **exact original diffuse+normal texture resources** with original per-layer UV rotation/scale/offset in Godot. Keep the current source-backed baked LOD material as fallback. Do not choose visually similar grass or rock DDS by eye or synthesize a fake palette.

Debug tools and temporary local extraction/scan logs live exclusively in ignored `.research/`; the actual Godot renderer continues to read original ESO game data directly at runtime.

## Follow-up archive probes, 27 October 2026

Further **read-only** checks against the actual installed depot and client, performed in an ignored `.research/Probe` console application:

- The depot's ZOSFT provides **92,656 names**; a case-insensitive search found **zero filenames containing `terrain`**. Ground, dirt, rock and grass substrings mostly lead to UI/maps/effects, not an identified near-field ground material library. Thus ordinary filename searching is not a sufficient discovery method.
- Section 233's additional `0x4800` group-0 record is a genuine **1024×2048 DXT5 DDS**, plausibly the minimap/overview map, not a list of individual repeating terrain materials. The `0x4800` groups 1 and 2 begin with `0x17` and contain fixture-like positions/IDs. The 280 `0x4000` channel-21 records likewise start with `0x17` and include fixture-like structs; no verified terrain texture handle list was found. These group types recur in sections 204, 899 and 1391.
- The ten subrecords of sampled section-233 `BEEF000C` land tiles are structurally consistent with height (0), authored colour (1), four 128×128 material-weight arrays (2–5), another 65×65 surface/normal data block (6) and 64×64 data/mask (8). Subrecords 7 and 9 were empty in the three inspected tiles. There was **no obvious per-tile material palette**, though no undocumented payload field should be declared impossible without a complete schema.
- Section 233's 450 `terrain-map` records are `DEBEEF01` containers with `DEAD0002` and `0x12345678` markers, structured numeric grids and repeated paired values. The two observed record lengths were **88,188** and **176,348** bytes. They are not raw DDS resources, and the sampled structures yielded no verified 16-texture binding table. Their full meaning is still unknown.
- The archived `FAFAEBEB` global metadata record can still be partially decompressed (78 Oodle quanta, **20,447,232** decoded bytes) and parsed into **150,931 / 151,593** entries. The first 32-bit big-endian identifier in each parsed entry is unique and generally increasing; neither IDs 233, 899 nor the section-233 height channel key appeared as these *first identifiers*. This does not rule out references elsewhere inside the entries, but no direct terrain-group namespace equivalence has been established.
- An ASCII-string probe of the installed `eso64.exe` found `TerrainTexture1`–`TerrainTexture16`, `f4TerrainUVScaleBias` and `f4TerrainUVRotation`, but **not** the literal `TerrainTextureDef.xml` or `MaterialPalette` strings. This is only a plaintext search, not disassembly or proof that no binary material registry exists.
- The depot does contain **unnamed original DDS resources** (one sampled ID `0x00081015` decoded to a 1024×1024 DXT5 image), but neither a terrain-material identity nor an assignment to section 233 is proven. Sampled larger resources also included meshes, RIFF/audio and undecodable Oodle payloads; size-only identification has many false positives.

**Outcome:** no valid original close-range terrain texture-to-splat-slot binding was recovered, so **no renderer or gameplay changes** were made. The next source-backed investigation should focus on the consumer/producer of per-terrain sampler bindings and UV constants, seeking pointers into section/worldspace metadata and cross-checking any candidate texture resource IDs against the blend maps. Do not assign candidate DDS assets by appearance alone.

### Asset-ID correlation and false-positive check

A second targeted test initially found **2,839** primary-ID overlaps between the partially decoded `FAFAEBEB` entries and ZOSFT filenames, including named DDS resources. **Correction: these overlaps are not evidence of asset linkage.** The metadata IDs are densely populated, and comparisons against shifted filename IDs show essentially the same overlap rate. See the controlled follow-up below. No actual terrain-texture relationship was established.

**Critical limitation:** a naive scan for other named resource IDs inside the metadata generated massive false positives, especially the ubiquitous numeric word `0x00010000`, which happens also to be a valid ZOSFT asset ID. Rechecking **specific less-common known IDs** (`0x0002BC8D`, `0x0002BC8F`, `0x00004ED6`, `0x00004F92`, `0x00009969`, `0x00081015`) yielded **zero other-entry aligned 32-bit BE/LE references** in the decoded portion. This does *not* prove that the metadata has no indirect relationships, but it **does not supply a verified direct texture-dependency graph**. Keep resource-ID correlation separate from genuine field/schema identification.

### Controlled catalogue and actual DDS-bank audit (27 October 2026)

The depot manifest includes **932 `0x6000` rows with field 0 and 932 with field 1**, with old/new copies of multiple group IDs. A usable later copy of group 0 is `FAFAEBEB` with 151,593 full entries; the corresponding field-1 resource is `FBFBECEC`. Other groups (for example 4, 8 and 24) contain 155,097, 77,321 and 32,203 numeric entries respectively, with quite different entry sizes. These are binary asset/catalogue data; their schemas remain unproven.

**False-linkage control:** For group 0's full entry index, 2,863/4,836 ZOSFT filename IDs inside its numeric range also appear as entry IDs (**59.20%**), but shifting every filename ID by +1 yields **59.64%**, +2 yields **59.51%**, and +3 yields **59.90%**. Group 4 gives **82.25%** exact and **82.41%** shifted by +1; group 8 gives **62.37%** exact and **62.49%** shifted; group 24 gives **85.98%** exact and **85.81%** shifted. The apparent overlaps are explained by dense numeric identifier coverage; they **cannot be used as texture mappings**. A search for several distinctive known DDS IDs in other metadata entries also found zero aligned cross-references.

**Archive separation:** Terrain archive **81** (section 233 and neighbouring packages) contains **5,113 records**, classified as **5,054 `0x4000` section resources, 39 `0x4800` resources and 20 `0x4400` descriptors**: **zero `0x0000` general asset records**. The same absence of general assets holds across the examined terrain DAT family (archives 31–95 and 242–252). This indicates the repeating terrain textures, if stored as independent standard DDS resources, should be sought in the separate shared-asset DAT family rather than directly adjacent to section 233's height/paint resources.

**Sampled shared DDS categories, using only the original DAT data:**
- Archives **118–128**: many smaller DDS files, with filename-backed examples of UI icons and map images.
- Archives **130–158**: predominantly 1024×1024, 2048×1024 and 2048×2048 fully mipmapped DDS. A 64-sample 1024×1024 DXT1 diagnostic showed chiefly creatures and object atlases, **not** a recognizable repeatable terrain palette.
- Archives **159–184**: 64×64 to 1024×512 DDS, many with unusual atlas dimensions; a 64-sample DXT5 diagnostic showed mainly prop/architecture texture and normal atlases.
- Archives **188–238**: numerous 256×256, 512×512, 1024×1024 DXT1/DXT5 DDS; a 64-sample 1024×1024 DXT1 diagnostic contained stone, masonry, building sheets, props and occasional ground-looking images. It does not establish material-to-landscape assignment.

There are **real consecutive texture groupings**, for instance source IDs `0x00003537` (1024×1024 DXT1), `0x00003538` (1024×1024 DXT5) and `0x00003539` (256×256 DXT5) all occur in a shared archive. This DXT1/DXT5/DXT5 triplet pattern repeats in multiple asset banks, consistent with authored material companions. Representative diffuse images from the found triples show **architectural stone/masonry surfaces**, so texture adjacency is **not** evidence of an Auridon terrain layer. Some hashes are duplicated between archives, which also means raw file counts overstate unique candidates.

**Strict result:** we have located authentic original DDS material resources and their broad archive families, but **no original per-worldspace terrain palette, texture IDs bound to slots 0–15, or per-layer UV transforms**. No speculative terrain shaders or substitute textures should be implemented. The higher-value next lead is identifying the binary **consumer of the runtime `TerrainTexture1..16` bindings and `f4TerrainUVScaleBias/Rotation` constants** or identifying a worldspace material-definition record that can independently validate a DDS ID-to-paint-channel mapping.

### Read-only client shader-symbol xrefs

A static PE inspection of the installed `game/client/eso64.exe` found `TerrainTexture1` in `.rdata` at RVA `0x0317CC10`, and `TerrainTexture16` at RVA `0x0317CD30`. Example `LEA RIP` references in executable code: `0x000BF02E` (TerrainTexture1) and `0x000BF31C` (TerrainTexture16). The nearby instructions repeatedly load a symbol name/address pair and call a common registration routine; this **looks like static shader sampler registration**, **not** assignment of a DDS file to a landscape splat layer. The uniform names `f4TerrainUVScaleBias` and `f4TerrainUVRotation` reside at `0x03183788` and `0x03183748`, but no direct `LEA RIP` code references or literal 64-bit absolute pointer references were found by the limited scan. This is **not** a complete binary disassembly; absence of those reference patterns does not establish absence of runtime use.

The shader binding code therefore gives a concrete starting point for a deeper client-code investigation, but **no runtime texture resource IDs or UV constant values were extracted**. The original game executable was only read; it was not modified or executed.

## Verified terrain-section to in-game zone ID mapping (27 October 2026)

**A significant source-backed discovery:** The depot's archive-0 `FAFAEBEB` metadata **category/file ID 60** (`MES2 Flags=0x6000, Field2=0`, newest indexed copy) contains **1,085 section records**. This is an **authoritative terrain-section catalogue**, not an inferred archive-ID adjacency map.

Each individual zlib entry has the following verified partial layout, with big-endian integer fields:

| Byte offset | Value |
|---|---|
| 0–3 | Terrain section ID |
| 25–28 | Terrain section width in source world units |
| 29–32 | Terrain section height in source world units |
| 34 | Name length, one unsigned byte |
| 35 to `35 + nameLength - 1` | ASCII name, often a numeric section label (e.g. `0233`), sometimes a named `Instance_...` |
| `52 + nameLength` to `55 + nameLength` | **ESO zone ID**, big-endian unsigned 32-bit integer |

Other bytes are present and their meanings are not yet established. **All 1,085** entry IDs occur in the original game's 583-byte terrain descriptor set, and **1,085/1,085** decoded width/height pairs match those descriptors **exactly**, including section 233's 3500×5000. This independently links the catalogue to original terrain geometry rather than relying on numerically dense-ID coincidences. There are 1,349 candidate section descriptors in the full source: **264 are not in this particular category-60 table**.

Confirmed pair examples (zone names independently cross-checked against ESO zone-ID references, not inferred from the numeric section names):

| Source terrain section | ESO zone ID | Zone |
|---|---:|---|
| **233** | **381** | **Auridon** |
| 204 | 92 | Bangkorai |
| 868 | 980 | Clockwork City |
| 899 | 1011 | Summerset |
| 1149 | 1261 | Blackwood |
| 1391 | 1502 | Solstice |

For example, section 233's entry has ID `0x000000E9`, width `0x00000DAC` (3500), height `0x00001388` (5000), ASCII label `0233` and game zone ID `0x0000017D` (381). **This finally independently proves the source section 233 is Auridon**. The previously user-identified Auridon terrain silhouette now has authoritative in-archive zone identification.

The paired `Flags=0x6000, Field2=1` category-60 resource is an 8,710-byte `FBFBECEC` index, not an immediately readable terrain layer palette. There are **466** latest usable global categories. A bounded read-only search over **430** of their `FAFAEBEB` records (each under 1 MB), totaling **395,927** zlib entries, found no plaintext terrain texture definition: `terrain` appeared only in animation/gameplay names; `texture` and `material` mentions were unrelated generic entries, and named Auridon occurrences were map-image definitions. A stronger control looked for six `(sectionID, zoneID)` pairs in the same entry: **category 60 matched all six at the same record offset; no other scanned category matched two**. This does not exclude larger categories, indirectly encoded links or a separate material registry.

### Terrain client executable disassembly: limitations

Read-only static x64 disassembly using a scratch copy of the open-source `iced-x86` disassembler in ignored `.research/disasm` found the `TerrainTexture1..16` registrations around code RVA `0x000BF02E`, named `LoadTerrainJob` at a leaf helper `0x020C84C0`, and its method table at `0x031CC098`. The sampler registrations have both initialisation and cleanup references, not a direct texture-ID binding operation. Terrain job's destructor wrapper (`0x020CBBA0`) disassembles normally, but several loading/render methods referenced by its virtual table (`0x020CBEC0`, `0x020CDCB0`) have near-random high-entropy on-disk bytes and fail to decode as plausible instructions; another render job has the same issue. This *suggests* protected/encoded code, and we do **not** assert its semantics. No runtime instrumentation or attempted bypass was performed.

**Still missing:** The exact near-field diffuse/normal DDS ID for each active paint slot and its `f4TerrainUVScaleBias`/`f4TerrainUVRotation` parameters. Even with the authoritative zone mapping, no proper material source has been recovered, so **do not change the renderer or assign texture candidates by eye**. Next: identify a worldspace material registry or another properly scoped asset reference graph, ideally using the newly decoded section/zone association to validate it.

## 27 October follow-up: native terrain-map geometry decoded and DDS-ID controls

### The deceptive `terrain-map` resource is actually mesh geometry

Six independently verified terrain sections (233 Auridon, 204 Bangkorai, 868 Clockwork City, 899 Summerset, 1149 Blackwood, 1391 Solstice) have `Flags=0x4000`, `Field2=(sectionID<<5)|10` channel `terrain-map`. Its data begins with `0xDEBEEF01`, **not** a DDS header despite the descriptor's `.dds` extension. This source is now confirmed to encode one or two triangulated grid meshes, rather than hidden material assignments.

All twelve sampled records (one of each length per section) passed exact integrity checks:
- An **88,188-byte** record contains one grid mesh; a **176,348-byte** record contains two.
- Each mesh has `0xDEAD0002` and `0x12345678` marker/header structure, **4,485 eight-byte vertices** (`69 * 65 = 4485`), and **26,112 little-endian 16-bit indices** (`(69-1)*(65-1)*2*3 = 26112`).
- Index payload is **52,224 bytes**, all values lie within [0,4484], and the observed maximum is 4484. First indices are `[0,65,1,66,1,65,1,66,2,67,2,66]`, consistent with a regular quad grid triangulated in a strip. Each grid has exactly **8,704 triangles**.
- The mesh has a closing marker, a three-float trailer (meaning not fully determined) and a closing `0xDEAD0002`; the larger resource contains the same grid structure twice.
- For Auridon's 35×50 tiles, there are **450** channel-10 records (333 single-grid and 117 double-grid), compared with 117 original LOD diffuse/normal chunks. The count of double-grid records equals LOD chunk count, but this correlation does not by itself prove the purpose of the second mesh.

The universal **583-byte section descriptor** was also compared across different terrain regions. Besides its width/height prefix, it consists of the same ordered channel names/formats (fixtures, height, colour, four blends, minimaps, terrain-map, original LOD diffuse and normal, normal, hole, clutter, extension slots). It does **not** list 16 texture resource IDs or UV scales/rotations.

### Caution: false positives in a global DDS-ID sweep

A separate read-only scan tested 430+ global `0x6000` metadata categories for references to **23,818** original DDS resource IDs from shared asset archives (restricting to candidate DDS formats and IDs above 0x20000), with multiple shifted-ID control sets.

- Initial category-39 **108 DDS-ID matches / 5 +1 controls**, including one 20-hit entry, were **misleading**: **89** of those matches were to the repeated numeric value `0x000F655F`, which is just a length-prefixed animation string like `e_...` when read at the wrong 32-bit alignment. Category 99 similarly produced **263** occurrences of `0x000F655F`. Neither group has demonstrated a landscape material palette.
- Category 101 has readable architectural/prop names such as `ALDTrimStone`, and occasional texture-ID matches. These describe **props**, not verified painted terrain material slots.
- Category 49 has **61,343 records** and numerous high-ID numeric DDS references: 420 matches against +1 control 46, +2 control 51 and +4 control 29. Crucially, the primary ID of sample category-49 record **0x002938BB** resolves to an actual `Flags=0x0000` model resource in archive 115 with a **Granny2/GR2-like binary signature** `E59B495E6F631F141E13EBA990BEEDC4`. That entry references ID **0x0029388B**, an actual DDS file from archive 206 (512×512 mipmapped DXT1). A second category-49 primary **0x002BAC6F** resolves to the same model-file signature in archive 105; its record refers to an actual diffuse texture **0x002C401B** from archive 210 (1024×1024 DXT1). Thus category 49 appears to be a useful **model/asset-to-texture reference graph**, but nothing yet connects it to Auridon's terrain paint channels. This does not turn an unrelated mesh texture into a valid ground palette.
- Additional categories 50, 22, 72 and 60 gave near-control or lower matching rates and no verified texture assignment. Category 49's object-ID references should be followed only where an independently identified **terrain** asset can be found.
- Globally the source manifest has only flags `0x0000` (general assets), `0x2000` (other/audio), `0x4000/0x4400/0x4800` (section and scene resources), and `0x6000` (metadata). There is no obvious separately flagged terrain palette collection. The per-section terrain family has been inventoried; channels 1, 10, 12, 13, 21, and scene resources 0/1/2 account for its records.

**Conclusion:** the original near-field **material sampler slots, associated diffuse+normal DDS IDs and per-layer UV transforms remain undiscovered**. No viewer feature or texture assignment was added. Investigate a separate worldspace material definition or obtain authoritative D3D terrain shader binding state through an appropriate, compatible, authorized capture route; do not mistake source mesh/prop DDS references for Auridon layers.
