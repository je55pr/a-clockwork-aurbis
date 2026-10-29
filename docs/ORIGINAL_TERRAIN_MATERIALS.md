# Original ESO near-field terrain materials (27 October 2026)

**Status: working original material ID → diffuse/normal DDS mapping in the 5×5 Godot close-up view.** This supersedes the negative findings in earlier terrain-palette research. The full-section distant view still uses the authentic baked LOD images. UV scale, bias, rotation, full specular processing and game lighting are **not yet exact**.

## Evidence chain from the original game archives

1. The original `BEEF000C` land record contains a 64-byte, 16-entry **little-endian material ID table**, beginning at offset **102**. A two-byte `0x0040` length appears at offset 100. Every active raw paint channel corresponds to the same-index material-table entry. Channels are physically stored as four raw RGBA subrecords (land subrecords 2–5); the earlier paint-inspector GUI labels channels differently to match the HLSL's BGRA sampler swizzle.
2. The archive-0 `FAFAEBEB` global metadata **category 41** contains material entries (54 bytes for these materials). Its first big-endian `uint32` is the material ID. At entry offsets **33** and **37** are big-endian `uint32` DDS **diffuse** and packed **normal** resource IDs, verified by reading the original `Flags=0x0000` DAT records. Fields at offset 25 and 41 are numerical material parameters, with 4–5 and 0.5 common respectively; the latter matches the game's documented 0.5 default glossiness. Their definitive parameter semantics and exact UV application require further validation.
3. The original DDSs are typically **512×512 DXT5/BC3 with 10 mip levels**. The decoded diffuse RGB depicts sand, paths, soil, grass, moss and stone; the second DDS has characteristic AG-packed normal data. A few legitimate material IDs refer to smaller placeholder source textures.
4. Source validation sampled 7,725 land records across Auridon (section 233; **1,750** tiles), Bangkorai (204; 1,750), Summerset (899; 3,600), and Clockwork City (868; 625). **Every** paint channel with a meaningful cumulative weight matched a nonzero same-index local material ID. For Auridon, 20 distinct IDs cover the locally selected terrain materials: `5,103,273,454,516,517,518,520,526,527,528,529,530,531,532,533,534,556,568,569`.
5. The separate category-60 `FAFAEBEB` zone table identifies section `233` as ESO zone `381`, **Auridon**, and section 899 as Summerset.

Representative source-backed pairs:

| Material ID | Diffuse DDS file ID | Normal DDS file ID |
|---:|---:|---:|
| 516 | `0x2052B` | `0x20531` |
| 517 | `0x2052D` | `0x20533` |
| 527 | `0x20A2D` | `0x20A2F` |
| 528 | `0x20A31` | `0x20A33` |
| 530 | `0x20A83` | `0x20A85` |
| 534 | `0x20D99` | `0x20D9D` |

## Working renderer

`scripts/TerrainNearFieldMaterials.cs` reads the **original installed ESO archives at runtime**, collects a 5×5 tile patch's local 16-slot material IDs and four 128×128 RGBA paint subrecords per tile, resolves all material IDs against global category 41, and loads original diffuse/normal DDS pairs into in-memory Godot `Texture2DArray` textures. A 16×25 lookup texture maps paint channel and land tile to the corresponding material texture array layer.

The source DDS header often incorrectly declares a DXT5 linear image size of **2048** for a 512×512 texture, rather than the correct **262,144**. Godot strictly rejects it, so the loader corrects that **in memory only** before calling `Image.LoadDdsFromBuffer`; the source DAT files and compressed image payloads are unchanged. Loaded images are decompressed to RGBA8 only in memory. There are **no exported or committed ESO texture copies**.

The current Godot near-field shader blends real diffuse textures using the original weights, samples the 128×128 blend masks at **ESO's exact source texel-centred UVs**, reconstructs normal Z from packed alpha/green and uses the original per-tile colour map as an optional modulator. It interprets the category-41 size scalar as a source-world-unit repeat width, **provisionally**; rotation, translation, per-layer derivatives, exact specular packed channels, GPU blending math, lighting and steep-slope projection are not yet verified against a live client.

Validation using Godot **4.7.2 Mono, OpenGL 3.3**:
- Source section 233, full-resolution 5×5 patch centred on `(23,25)`: **25/25** land tiles, zero mesh seam mismatches, **11** distinct original material DDS diffuse/normal pairs, shader rendered and PNG captured with no runtime errors.
- Source section 233, patch centred on `(14,19)`: **25/25**, **12** original material pairs, successful shader and PNG capture.
- Smoke screenshots on the machine are in ignored `.research/auridon-near-23-25.png` and `.research/auridon-near-14-19.png`. The free-fly camera now starts 2.5 world units above the original centre land-tile height on every 5×5 close-up; it supports right-mouse look, WASD movement, E/Space up, Q/C down, Shift sprint, Alt slow travel, wheel speed adjustment and Esc to release the mouse. There is no orbit-distance restriction.

To try it: launch using `Launch-ESOTest.ps1` or the existing desktop shortcut. Leave **ESO original terrain LOD textures** on (it now means the authentic texture mode), choose **section 233**, select land-tile coordinates **23,25** or **14,19**, and click **View 5x5 tiles at FULL heightfield resolution**. The close-up automatically uses original material DDSs; rendering the full section still uses baked LOD diffuse.

## Prioritised next checks

- Recover the precise runtime `f4TerrainUVScaleBias[16]` and `f4TerrainUVRotation[8]` parameters. The original 100-unit vertex UV domain and native 128×128 paint texel-centre correction have now been independently verified and implemented, but `100 / textureSize`, rotation and offsets still require confirmation. See [focused original UV research](TERRAIN_UV_INVESTIGATION.md).
- Compare authentic diffuse/detail frequencies and world-coordinate seams against ESO. The present render shows noticeable cliff/slope stretching and incomplete game-like lighting, partly because no placed rock/clutter geometry or world weather has been reconstructed.
- Decode the category-41 remaining flags and packed diffuse alpha/specular representation, then use the recovered native shaders' blending law rather than approximate Godot Burley/PBR.

All research and screenshots remain under ignored `.research/`. The code uses game files read-only.
