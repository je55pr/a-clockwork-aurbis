# Auridon near-field materials: palette investigation and source-colour comparison

Research and implementation, 27 October 2026. All original ESO assets are read from the installed DAT/MNF files into memory. No converted texture files are created or checked in.

## Confirmed: source RGBA colour maps, distinct from terrain paint weights

A terrain-height record (`BEEF000C`) contains separate original subrecords:

- ID 0: 65×65 terrain heights
- ID 1: **128×128 original RGBA terrain-color**
- IDs 2–5: **four 128×128 RGBA terrain-blend weight groups** (16 channels possible)
- Other per-tile normal, hole and surface subrecords

Each 128×128 block is formatted as `BEEF0001`, two dimension fields, 512 row bytes, then 128 rows containing a 16-bit byte count followed by 128 RGBA pixels. Ignoring the two-byte prefixes produces **incorrect channel statistics**. The source-colour alpha is 255 in representative land samples; the RGB values vary spatially, typically near white with some darker/cooler authored shading.

This map is **not** a substitute for the near-field grass/stone/sand diffuse texture palette. It is likely a per-pixel modifier used by the game's terrain rendering.

## Confirmed: blended weights are almost-normalised

Over a source Auridon 5×5 patch centred on original tile (23,25), exact reading of all original blend blocks measured:

- 25 original heightfield tiles, 409,600 original 128×128 paint pixels
- **291,476** pixels have all 16 weights summing to **255**
- Another **88,505** total between **252 and 256 inclusive**, not counting exact-255 pixels
- Remaining **29,619** differ more (including 28 all-zero pixels)
- Active layers in this sample: **0 through 8**; channels 9–15 are all zero for this patch

This strongly supports that the four source arrays encode colour/material layer weights (with rounding/normalisation differences), but **does not** identify the original texture assigned to any layer or establish the exact blending formula.

The Godot paint-mask inspector now shows **active channels and the exact-255 count** for any selected 5×5 patch alongside an individual channel's monochrome source mask. It never assigns guessed material names.

## Implemented: direct original colour-map comparison

`TerrainNearFieldColors.cs` loads the original ID-1 `terrain-color` subrecords for a 5×5 close-up directly into a 25-layer in-memory Godot `Texture2DArray` (128×128 each).

`AutoRegionRenderer.cs` supplies original per-tile `UV2` independently of the four-tile LOD DDS UVs. `TerrainLodTextures.cs` can now sample the source colour array on those tile coordinates and multiply it into the existing baked LOD diffuse in a configurable-strength diagnostic step.

In the UI, turn on **Original per-tile colour modulation (experimental)** while viewing a 5×5 full-resolution patch. It is **off by default**, because the existing baked LOD diffuse could already contain the colour tint. Double application would darken the original source terrain. Graphic comparisons were inspected for both states on section 233, patch (23,25); source colours visibly affect the rocky/sandy areas, but we have **not** established which state reproduces ESO's actual shader.

The source-colour sampler is disabled unless that exact 5×5 patch's source colour array is loaded, so toggling in the full-region view cannot accidentally sample an unbound texture.

## Shader palette investigation: still incomplete

A publicly available research project, [doodlum/eso-shader-tools](https://github.com/doodlum/eso-shader-tools), documents shader HLSL and material definition paths in older ESO `game.mnf` builds. Its source was inspected in a local ignored research clone, **not executed**.

Our installed `game/client/game.mnf` contains 13,340 records, without the standard `0xFFFFFF` filename table expected in the depot. The directly decoded first records begin with DER/ASN.1-looking binary structures, **not ordinary readable HLSL or XML text**. We have not established how to map the current entries to the previously documented shader source names. We did not attempt decryption or circumvent access controls.

The terrain section's `terrain-map` channel (10) contains 450 `0xDEBEEF01` records laid out over a 2-tile grid, not DDS textures. The relationship of these records to material identities is not yet decoded. Source territory membership and texture ID assignments remain unknown.

**Update, 27 October 2026:** The game.mnf contents were wrapped in signed headers, not unreadable shader binaries! The real terrain HLSL sources and game ZOSFT filenames were decoded. Shader material slots use BGRA ordering; raw active channels 0–8 correspond to **shader slots 0–7 and 10**. This earlier investigation is superseded by [SHADER_PALETTE_RESEARCH.md](SHADER_PALETTE_RESEARCH.md).

**Next technical milestone:** locate the authoritative landscape material palette (texture-resource IDs and per-layer UV scaling) in a legitimate readable source, then use these now-verified 16-channel paint weights to reproduce the near-field ESO shader. Until then the original baked diffuse/normal renderer and optional source colour comparison remain honestly labelled experimental, not in-game-perfect terrain.
