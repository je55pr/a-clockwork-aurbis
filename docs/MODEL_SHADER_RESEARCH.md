# Original ESO GR2 normal maps and secondary-material shader investigation

Date: 2026-10-27

## Implemented and verified

The local asset browser now applies **original source normal maps** to original model geometry in addition to the previously recovered diffuse DDS and source UV0. The sources are the installed ESO archive, not guessed replacement textures.

### Original packed normal texture

The original client `game/client/game.mnf` contains the HLSL source files `/shaders/materialps.hlsl`, `/shaders/zocommon.fxh`, `/shaders/zostandardcommon.fxh` and `/shaders/standardcommonvs.hlsl`. We parsed their ZOSFT paths and decoded the original wrapped game-archive records using the read-only `.research/Probe` script. Ignored local copies for inspection are under `.research/shader-inspect/`.

`ZoCommon.fxh` explicitly documents model normal-texture channel semantics: **RGBA = (glow, normal Y, fresnel, normal X)**. `ExtractNormal` reads normal XY from `(A,G) * 2 - 1` and reconstructs Z as `sqrt(saturate(1 - x² - y²))`. `MaterialPS.hlsl` calls `GetNormalMapping` with **source geometry normal, negative U tangent, negative V tangent**. The normal DDS `FileIndex` IDs come from the *same* GR2 material map tree as the source diffuse texture IDs documented in [model texture research](MODEL_TEXTURE_RESEARCH.md).

The preview now decodes the original packed DDS to a Godot-compatible tangent-space RGB normal texture **entirely in memory**. The stored X/Y values remain source-authored and only the Z component is mathematically reconstructed, as in HLSL. Glow/fresnel are *not* mapped to colour and are reserved for later shader research.

### Original compressed vertex normal/tangent frame

`ZoStandardCommon.fxh` defines the exact shader-side decoding of the 16-bit normal, tangent, binormal pairs: a signed 1.15 packing with the **Z-sign bit embedded in the first word**, scaling `(2 * frac((word + 32768)/32768) - 1) * 32769 / 32767`, followed by the signed square root of `1-x²-y²`.

We applied that source formula to the `Normal`, `Tangent` and `Binormal` fields of the original GR2 vertex layout. These fields are stored as 2 × signed 16-bit words per vector on environment meshes; some other GR2 layouts instead contain direct float32 vectors. The normal/tangent frame is supplied to Godot when verified. The preview negates original U and V consistently with the source `GetNormalMapping` call.

For the cliff `0x0009F7B7`, **3,316 original vertex frames** were examined: chirality was clear on 3,307 vertices, nine were near zero, and two had degenerate original U tangents. We use a *localized derived frame fallback* based on the original binormal/geometry normal for those vertices; this is not exact original-vertex parity. If a mesh has more than 5% weak/degenerate frames or an unrecognised vertex layout, we do **not** enable its normal-map materials.

### Verified Godot rendering

The current ESM5 cache format carries source vertex positions, UV0, UV1, reconstructed source vertex normals, tangents, a verified-frame flag, per-mesh material triangle groups, and verified original **diffuse, normal, detail, diffuse2 and specular** DDS resource IDs. Temporary ESM4 files are ignored under `.research/model-previews/`; no original game archive data is modified.

- `ROK_GRY_MRKCliff001` (ID `0x0009F7B7`): two original diffuse and **two original normal** material groups, zero diffuse fallbacks. Godot screenshot verified.
- `SYS_STR_GuildMages001` (ID `0x002938BB`): thirteen original diffuse and **thirteen original normal** groups, zero diffuse fallbacks. Godot screenshot verified.
- Other models whose original GR2 does not provide a usable normal frame or normal DDS continue using diffuse-only geometry.

These use Godot standard material lighting, **not an exact ESO forward/deferred lighting implementation**. The material's normal axes and per-vertex direction originate from ESO, but Godot's tangent-space interpolation and BRDF still differ.

## Secondary diffuse, detail and specular

The source `MaterialPS.hlsl` samples `diffuse`, `normal`, `specular`, `detail` and `diffuse2`. Detail sampling uses **UV1**, while diffuse, normal, specular and second diffuse use **UV0**. Original vertex streams contain independently encoded `TextureCoord1` fields, which the CLI now decodes using the same fixed-point conversion as UV0. The GR2 material map tree also supplies genuine original file IDs for all five named channels. This is now stored in ESM5 alongside the authoring material flags for future validated shader work.

`ZoCommon.fxh`'s `ApplyDetailBlend` calculates:

```text
base.rgb = lerp(diffuse2.rgb, diffuse.rgb, detail.a)
base.rgb *= detail.rgb * source_vertex_color.rgb
```

**Channel activation is conditional on material metadata AND the final shader variant.** The GR2 archive contains relevant flags, but does not supply an unambiguous final compiled shader permutation or runtime per-instance overrides. `SampleDetailMap` returns `1` unless `FX_DEF_DETAILMAP` is enabled, and `SampleDiffuse2Map` returns `0` unless `FX_DEF_DIFFUSE2MAP` is enabled.

## New discovery: complete typed GR2 material metadata

**27 October 2026, follow-up investigation:** Correctly traversing `granny_material.ExtendedData` with the native `GrannyGetMemberUnitSize` and **44-byte `granny_data_type_definition` strides** reveals the original authoring settings. Earlier probes mistakenly assumed 48-byte definitions, producing false field names and hiding these flags. The extension is a genuine `ZosMaterial` structure containing fields such as:

- `bDiffuseEnable`, `bNormalEnable`, `bSpecularEnable`, `bDetailBlendEnable`: integer 0/1 source authoring flags.
- `materialType`, `alpha`, `twoSided`, `NoShadows`, `DrawOrder`, `SoftAlphaDepth`: source material renderer settings.
- `ZosGlossiness2`, `ZosFresnel2`, `ZosGlow2`, `ZosDayGlow`, `ZosDuskGlow`, `ZosNightGlow`: original secondary shader values.
- Nine `f4DiffuseTint*`, `f4SpecularTint*`, `f4NormalTint*` vectors, tint mask source path, UV layer scroll speeds, original content filenames, export version and more.

The source `0x0009F7B7` cliff has two groups with **materialType 4, alpha 1, both sides disabled and all four b* flags enabled**. The large Guild Mages building (`0x002938BB`) has **13 validated material groups with materialType 0**, each with original DDS references and flags, but varying alpha values: 1 for most opaque-looking surfaces, 2 on wall/trim overlays and 3 on some wood/stone geometry. For `WallStonePlaster_Overlay`, source secondary values are `ZosGlossiness2 = 0.4`, `ZosFresnel2 = 0.2` and `ZosGlow2 = 0`. These are factual source properties; the runtime numeric enum mapping for `alpha` still needs verification. Do not assume 2 means blend or 3 means mask without corroboration.

The isolated decoder now validates these typed fields and includes them in a versioned **ESM5** preview. It respects original `bDiffuseEnable` and `bNormalEnable` when binding the source DDS. It also uses original `twoSided` to choose culling, with an important conversion correction: ESO's extracted triangle winding is **opposite Godot's front-facing convention**. Thus Godot's *front-face* culling produces the proper single-sided exterior. Back-face culling made the Guild Mages building hollow; comparison screenshots verified front-face culling restores the exterior. Missing/unrecognised metadata remains on the old permissive geometry preview path rather than guessing material flags.

### Separating standard and detail-material shaders

The original client `/shaders/zoshadertechniques.xml` explicitly maps `MATERIAL_TYPE_STANDARD` to `MaterialPS` and `MATERIAL_TYPE_DETAIL` to `DetailMaterialPS`. `/shaders/materialdefinitions.xml` (532,042 decoded characters) includes distinct `DETAILMAP` / `DIFFUSE2MAP` shader permutations, including alpha-blend, alpha-mask and G-buffer variants. The source's numeric GR2 exporter `materialType=4` on the cliff and door, versus `materialType=0` on the Guild Mages building, is **consistent with** those models using different material paths, but their precise exporter-enum-to-runtime-type correspondence is **not yet proven**.

The earlier experimental blend used the `MaterialPS` formula indiscriminately. The cliff's authored `detail` path actually ends in a **packed normal map** (`ROK_GRY_MRKDetail001_n.dds`), not an RGB colour modulation texture, so that experiment made the cliff too dark. The special `DetailMaterialPS` shader instead blends **diffuse2 at UV1 using diffuse alpha**, and adds a second extracted detail normal to the base normal. We implemented a **separate experimental path** for source `materialType=4`, respecting those sampling channels; a Godot screenshot now keeps recognizable cliff detail, with both authentic source normal maps present. Its lighting still omits some gamma transforms and ESO's exact BRDF.

Both material experiments remain **off by default**. Set `ESO_EXPERIMENTAL_MODEL_DETAIL=1` to compare, with an explicit experimental label in the preview status. Only the confirmed original diffuse/normal DDS plus valid authored culling and texture enablement are applied automatically. The original specular RGB/alpha, tint, alpha modes and animation remain **unimplemented** until the runtime effects can be mapped accurately.

## Next research

Map the numeric exporter `materialType` and `alpha` enum values to actual client runtime shader permutations. Locate the per-instance `g_detailValues`, UV scale/bias, tint and alpha threshold parameters, then evaluate source-authored overlays, specular RGB/gloss and transparency with side-by-side screenshots. Use the source metadata, not filename similarity, to establish flags. Rigging, animations and gameplay identity mapping remain separate projects.
