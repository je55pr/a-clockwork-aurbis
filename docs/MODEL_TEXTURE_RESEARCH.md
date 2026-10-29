# ESO GR2 mesh materials and diffuse texture recovery (27 October 2026)

## Result

**Source-backed diffuse textures are working for original ESO GR2 models in the Godot Asset Library.** The viewer uses original mesh triangle groups, exact local material binding names, diffuse DDS file IDs and recovered per-vertex UVs. It does **not** choose textures by searching filenames, applying arbitrary images, or visually matching materials. Missing mappings stay neutrally shaded.

The original model-related `granny_file_info` pointers expose several essential relationships:
- The `Meshes` collection points to `granny_mesh` instances with the original `PrimaryTopology` and `MaterialBindings`.
- `granny_tri_topology.Groups[]` supplies `MaterialIndex`, `TriFirst` and `TriCount` for **triangle** ranges, not vertex index positions. Multiply `TriFirst` and `TriCount` by three for Godot's indices.
- Each mesh material binding points to a `granny_material`, whose `Maps[]` entries identify the channel by a source literal: **diffuse**, **normal**, **detail**, **diffuse2**, or **specular**.
- Every nested texture map's `granny_texture.ExtendedData` variant has one typed field **`FileIndex`**, an original little-endian `uint32` archive file ID. These IDs resolve in the installed depot directly to original DDS resources. Texture filenames and width/height fields in GR2 are often empty/zero; this is why looking up filenames did not work.

The packed x64 ABI from `granny211.h`, confirmed with the compatible separately supplied SDK:
- `granny_mesh`: name at 0; topology pointer 28; material binding count 36; binding array pointer 40.
- `granny_tri_topology`: group count at 0; groups pointer at 4, each group 12 bytes.
- `granny_material`: name at 0; `MapCount` 8; map array pointer 12; each map 16 bytes (usage string pointer 0, nested material pointer 8).
- `granny_material`: texture pointer 20. The nested material's texture pointer reaches `granny_texture`.
- `granny_texture`: `ExtendedData.Type` pointer 76 and `ExtendedData.Object` pointer 84; its first 4 bytes are the **FileIndex**.
- `granny_file_info`: mesh count/array at 84/88.

### Verified named material → texture mappings

| Original GR2 source | Original material binding | Diffuse DDS | Normal DDS | Source triangles |
|---|---|---|---|---:|
| `0x0009F7B7` ROK_GRY_MRKCliff001 | ROK_GRY_MRKLrg002 | `0x0009EFBB` | `0x0009EFEF` | 2,666 |
| same | ROK_GRY_MRKCliff001 | `0x0009F7F1` | `0x0009F7F3` | 1,794 |
| `0x0028F157` ROK_BAD_DeadlandsLGDoor001 | ROK_BAD_Deadlands005 | `0x002797B5` | `0x002797B7` | 186 |
| `0x002938BB` SYS_STR_GuildMages001 | WallStoneFoundation | `0x002924DB` | `0x002924DD` | 5,498 |
| same | RoofTiles001 | `0x002927D9` | `0x002927DB` | 20,607 |
| same | WoodFloor | `0x00293265` | `0x00293267` | 434 |

The Guild Mages building has **13** original material groups with independently resolved source diffuse and normal pairs. All 13 original diffuse groups rendered successfully in Godot. The cliff rendered both original diffuse groups, and the door rendered its single diffuse group. All referenced sample DDS IDs decoded successfully from the installed DAT files, and no guessed substitute texture was used.

## Original model UV encoding

The material names and IDs alone are insufficient: applying a source diffuse DDS requires the matching original per-vertex UVs. Two source GR2 vertex layouts were verified:

1. **Environment meshes:** `TextureCoord0` and `TextureCoord1` have `GrannyReal16Member` type with 16-bit packed components. Treating these as IEEE half-floats yields near-zero values and spurious NaNs. Native sample vectors show that `TextureCoord1` is `TextureCoord0 × 8` plus an integer-aligned offset of approximately `-7 × 1024` in V. This confirms ESO's stored values are **signed 16-bit fixed point divided by 1024**, preserving negative and greater-than-one UVs for wrapping. For example, source bits `0213/01A6` become (0.51855, 0.41211); source second channel `1099/F12D` corresponds roughly to 8× the first plus a -7.0 V shift. Interpreting 0xF12D as a negative signed short is essential.
2. **Some equipment meshes:** `TextureCoordinates0` can have `GrannyReal32Member` type, storing direct little-endian float32 UVs. The inspector supports those without scaling. An equipment GR2 with no diffuse material reference remains neutrally shaded rather than receiving an invented texture.

The viewer enumerates the original Granny vertex layout fields (packed 44-byte `granny_data_type_definition` entries), checks stride via `GrannyGetTotalObjectSize`, reads the original vertex stream from `GrannyGetMeshVertices`, and extracts UV0 according to the verified layout. If the mapping cannot be validated, no diffuse ID is assigned.

These UVs are the **original authored coordinates**. They do not yet implement every ESO material shader variant or special effect.

## Correcting the source DDS in memory

Many original DDS assets have an incorrect/nonstandard `dwPitchOrLinearSize` despite valid DXT blocks and mipmaps. Godot strictly validates that header, initially rejecting the model DDSs as `ERR_PARSE_ERROR`. The project's near-field terrain decoder already had a verified workaround. The model preview now uses the same source-backed correction for DXT1, DXT3 and DXT5: set that field **in the decoded in-memory byte array only** to `ceil(width/4) × ceil(height/4) × blockSize`, retaining the original authored compressed DDS blocks unchanged. Godot then loads the image, decompresses to RGBA8, and applies it as each material group's albedo texture. No compressed resources or edits are written to the ESO depot.

Original DDS sizes in the verified examples include **1024² and 2048² DXT5** as well as **1024² DXT1**. Any unsupported, missing, incomplete or invalid DDS leaves just its material group neutrally shaded. Source data and index browsing remain available while the depot is updating.

## Implementation and verified Godot previews

- `tools/ModelGeometry/Program.cs` now writes an isolated **ESM2** preview with per-mesh positions, UVs, original triangle indices and original material group ranges and diffuse/normal DDS IDs.
- `scripts/EsoModelPreview.cs` validates the ESM2 resource, creates original per-material triangle surfaces and reads each referenced original DDS directly from the archive (decoded into memory). Repeated diffuse IDs are loaded once per preview. The local ignored cache filenames include `-v2` to prevent mixing ESM1 mesh-only caches and ESM2 textured source meshes.
- The native Granny DLL remains isolated in the CLI helper process, never loaded by Godot. All preview cache data remains in ignored `.research/model-previews/`.
- Godot 4.7.2 Mono screenshots at 1280×720 verified:
  - `ROK_GRY_MRKCliff001`: **3,316 vertices, 4,460 triangles, 2 original diffuse groups, 0 neutral fallbacks**, rocky texture surfaces visibly reconstructed.
  - `0x002938BB` Guild Mages building: **63,705 vertices, 57,113 triangles, 13 original diffuse groups, 0 neutral fallbacks**, distinct roofs, walls and trim.
  - `0x0028F157` Deadlands door: **173 vertices, 186 triangles, 1 original diffuse group, 0 neutral fallbacks**.
  - `BRE_LSB_SignGuildMages001`: **156 vertices, 120 triangles, 1 original diffuse group, 0 neutral fallbacks**, recognizable decorative source artwork.

## Remaining visual fidelity work

The current result is a **correct source diffuse texture assignment**, not a fully recreated ESO material shader. Missing pieces include original packed normal reconstruction (model normal maps likely require channel unpacking rather than direct RGB), specular/roughness and detail/diffuse2 handling, vertex colour tint/alpha and special transparency, tiling/wrapping flags for each shader variant, rigging/skin weights and animation. Do not apply approximate normal packing or stylistic replacement textures and label them exact.

Next investigate the original model HLSL material shader (not terrain HLSL) and source DDS channel data, then implement only verified normal/specular/secondary material rules. Exact gameplay equipment/NPC identities remain a separate database-linking problem.
