# ESO native terrain UV investigation (27 October 2026)

**Purpose:** separate the exact, original source UV formula from the remaining unknown runtime layer scale/bias and rotation parameters. Uses only the installed game shaders and source depot. No guessed UV rotations have been applied.

## Original vertex coordinates: confirmed

The original `game/client/game.mnf` shader `/shaders/terrainvs.hlsl` contains:

```hlsl
float vertSpacing = 1.5625f;
float4 position = inPosition.argb;
position.xz *= vertSpacing;
vOut.f2TexCoord0 = inPosition.xz / 100.0;
```

The packed heightfield has 65 vertices per side (64 gaps of **1.5625** units), so `64 * 1.5625 = 100` world units per land tile. The near-field pixel shader therefore receives **local 0–1 UVs per land tile**, not section-wide UVs. The current Godot renderer's `UV2` records `tile coordinate + sample/64`, and its `fract(UV2)` recovers exactly that original local coordinate (ignoring edge-selection ambiguity at tile boundaries).

The original `/shaders/zoterraincommon.fxh` defines the **precise texture-layer transform**:

```hlsl
float2 GetTerrainUVTransform(float2 uv, float2 offset, float scale, float2 rotationPair)
{
    float2 p = uv - 0.5;
    float2x2 r = { rotationPair.y, -rotationPair.x,
                   rotationPair.x,  rotationPair.y };
    p = mul(p, r) + 0.5;
    return p * scale + offset;
}
```

The 16 layer-specific runtime shader constants are `f4TerrainUVScaleBias[16]`, with scale in `.x` and two offsets in `.yz`. The packed rotation pairs are in `f4TerrainUVRotation[8]` (two 2-component rotations per vector). The **formula is exact**, but the contents of these runtime constants are **not present in the extracted HLSL**.

## Recovered source material parameter, with remaining uncertainty

Archive-0 `FAFAEBEB` material metadata category 41 contains 1,373 entries, all 54 bytes in the examined latest copy. Each material defines:
- A numeric texture-size-like float at **byte offset 25, little-endian float32**. There are 62 distinct values; most common are **4 (569 entries)**, **3 (523)**, **2 (54)**, **5 (27)**, **1.5 (25)**, **2.5 (22)** and **3.5 (22)**. Auridon materials commonly use 3, 4 and 5. This distribution is consistent with authored world-unit texture widths, but we have not independently observed the exact runtime scale-constant calculation.
- At offset 33 and 37, original diffuse and normal DDS resource IDs (big-endian uint32), already independently verified.
- A float at **offset 41** (0.5 in 905 materials; 0.3 in 134, 0.2 in 116, etc.), consistent with authored glossiness.
- A float at **offset 45** (zero in 1,214; otherwise typically 0.01, 0.02, etc.), consistent with another shader material scalar.
- At **offset 49**, a value that is essentially zero or a few flag/sentinel bits in all 1,373 records, **not a 16-layer rotation array**.

**Current Godot assumption:** `scale = 100 / category41.textureSize` with `rotation=(0,1)` (identity) and `offset=(0,0)`. The original vertex UV span is now verified to be 100 world units; the inference that the texture-size float is measured directly in world units is plausible **but not proven**. Do not label these as ESO-exact runtime constants.

## Verified tile header metadata: not UV rotation or bias

The original `BEEF000C` tile record contains:
- Bytes 100–101: little-endian `0x0040` length;
- Bytes 102–165: sixteen **little-endian material IDs** aligned with the source paint channels;
- Bytes 166–181: four **big-endian float32** values representing the terrain tile's **3D bounding sphere**: centre X, centre Y, centre Z, radius.

For Auridon, X/Z are **50/50** (a tile spans 100x100 source units), while centre Y and radius vary with elevation. An independent recomputation from **all 65×65 height samples, including zero-valued samples**, matched the source bounding-sphere data across **116 sampled Auridon tiles**: maximum Y-centre error **0.0073** units and maximum radius error **0.0090** units. This unambiguously rules out those last 16 header bytes as per-layer UV transforms. In other zone revisions, including some degenerate edge tiles, a few bounding records do not match the naïve full-grid reconstruction and merit separate terrain-bounds investigation, but Auridon's source mapping is solid.

The land palette does **not** include explicit scale/rotation/bias fields alongside each material ID. If ESO varies the layer transforms per tile, it must derive them from additional metadata or generate them procedurally at runtime.

## One original UV calculation recovered and applied

The near-field shader `/shaders/terrainsinglelayerps.hlsl` has an **exact 128×128 paint/blend UV correction**:

```hlsl
const float pixelwidth = 1.0f / 128.0f;
float2 uvBlend = vIn.f2TexCoord0 * (1.0f - pixelwidth) + 0.5f * pixelwidth;
```

This is distinct from the diffuse/normal layer transform. Previously, Godot sampled each original RGBA paint map directly at `local_uv`. It now uses the native correction for **all four 128×128 paint maps**, placing sample coordinates at texel centres at the tile boundaries. No source images, paint weights or texture identities were changed.

Godot 4.7.2 Mono verification on section 233 / Auridon 5×5 close-up centred on **(23,25)**: compiled **0 errors / 0 warnings**, loaded **11 original DDS pairs**, rendered and saved the original-source screenshot successfully, with no shader or runtime errors.

## What is still required for exact near-field UVs

The difficult unresolved link is the **runtime producer of `f4TerrainUVScaleBias` and `f4TerrainUVRotation`**, not the shader's consumer (which is fully known). The installed `eso64.exe` exposes those shader constant names, but static symbol scans found sampler initialisation rather than the texture-coordinate parameter producer, and relevant runtime methods are not reliably readable as normal on-disk machine code. We will not infer exact rotation or randomisation from arbitrary materials by eye.

Higher-value next avenues:
1. Cross-check the authored texture-size field against the source game or an authorised GPU frame capture to confirm whether `scale = 100 / textureSize` or another conversion.
2. Determine whether rotation/offset depends on tile coordinates, section/world placement, material ID or a separate dataset. No such values were found in the tile header or the category-41 material record.
3. If an allowed terrain render capture is possible, read the shader's **actual constant buffer** on at least two adjacent tiles with a common material. This would reveal values for all active layers and whether the scale, bias and rotation are fixed or tile-dependent. Do not use protected client injection or bypass tooling.
4. Only then encode the recovered transform in Godot, retaining the verified source maps and original local vertex UVs.

The entire original installed ESO depot and client remained read-only. Research scripts and any captures are in ignored `.research/`.

## 27 October follow-up: full shader-source audit and depot update interference

A direct read of `game/client/game.mnf` yielded **406 shader-related source files** (`.hlsl`, `.fxh`, `.xml`, `.glsl`). Exactly **nine** of them reference `f4TerrainUVScaleBias`, `f4TerrainUVRotation`, `GetTerrainUVTransform` or `TerrainTextureDef`: the two relevant single-layer shaders, the relevant multilayer shaders and variants, the HLSL/GLSL terrain-common helpers, and their shader-constant declarations. The extracted sources **define the consumer and parameter layout**, but contain **no function that fills the sixteen runtime transforms**. There is also a separate four-layer `/shaders/terrainlayerps.hlsl` path with different uniform names (`f4UVScale`, `fTextureUVBias1/2`, `fTextureUVRotation1/2`); its defaults cannot be assumed valid for the 16-material terrain renderer.

A quantitative check of the original baked LOD diffuse versus its original paint weights on Auridon chunks `lod-5-6`, `lod-3-5`, `lod-4-4`, `lod-3-4` and `lod-2-2` found **extremely low high-frequency image information in pure-painted patches**: adjacent-pixel mean absolute brightness changes generally measured **0.12–1.4 RGB levels**, with high-pass RMSE generally **0.25–2**. These low-frequency diffuse textures are not reliable enough to back-fit exact native repeating DDS phase and rotation. Code and materials remain unchanged.

**Environmental caveat discovered mid-probe:** the *installed ESO depot began updating while the investigation was ongoing*. Previously working manifest had 1,258,203 records and 92,656 known filenames; the new `depot/eso.mnf`, modified **2026-10-27 11:21:31 UTC**, has **1,291,975** records and **95,798** filenames. At the time of this check there were only **257 completed `.dat` files**, plus **`eso0003.dat.solidpartial`** (growing download), while the manifest still described 258 archives. Many older DAT files were last modified in April. Consequently, attempting to read land tiles through the current *new* manifest and the still-partial DAT set returned Oodle decoder failures; a Godot smoke test also failed to construct the requested terrain. **Do not attribute this to a shader, renderer or decoder code regression** or try to bypass/update the install. Wait until the external game installation completes and validate manifest/DAT consistency first. No repo or ESO source files were altered by the diagnostic probes.

Next: when the installed game files are consistent again, continue testing the per-tile runtime scale/bias and rotation producer using stable archives or authorised client shader constant inspection. Do not apply guessed rotation/bias or use LOD image autocorrelation as proof.
