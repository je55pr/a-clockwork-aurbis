# Auridon terrain material and near-field investigation

**Update 27 October 2026:** Auridon's authentic original near-field material IDs and diffuse/normal DDS pairs have now been recovered and are rendered in the Godot 5×5 close-up. See [original terrain materials](ORIGINAL_TERRAIN_MATERIALS.md). The rest of this document records the earlier state of the investigation, before the palette discovery.

26 October 2026. All source assets are read **directly from the user's original ESO MNF/DAT archives at runtime**. No intermediate converted ESO data files or extracted textures are produced or committed.

## What now works in Godot

**The whole section 233 (Auridon candidate) still has 1,750 original source heightfields and 117 original baked diffuse DDS chunks.** This work adds:

1. **Original baked terrain LOD normals:** The matching 117 source channel-13 DXT5/BC3 DDS textures (256×256). Image inspection found their meaningful horizontal components in the **alpha and green channels**, while red and blue were unused. The shader unpacks alpha and green into signed XY normals and reconstructs the Z axis. The axis handedness is inferred and must still be compared with ESO's game shader. This is the source's original distant terrain normal texture, **not the missing near-field material normal atlas**.
2. **Vertex normals and tangents from original heights:** `AutoRegionRenderer` now uses the original 65×65 height grid to compute mesh normals and tangent bases even when distant terrain geometry uses every fourth sample. A plausible directional light and ambient light are added in Godot as an **approximate lighting rig**, not a reconstruction of ESO weather or illumination.
3. **Full-resolution 5×5 tile patch:** Set tile X/Z in the UI and click **View 5x5 tiles at FULL heightfield resolution**. This displays all 65×65 height vertices of each of the selected 25 source tiles instead of the distant 17×17 sampling. The original baked DDS diffuse/normal channels remain aligned to their original source coordinates. Example tested: section 233, centre (23,25), 25/25 tiles and zero seam mismatches. This is detailed **geometry**, not yet detailed diffuse materials.
4. **Paint-mask inspector:** Choose source blend channel 0–15 from the same 5×5 patch. The `BEEF000C` land record contains four `BEEF0001` 128×128 RGBA paint blocks after the colour block. Each row has a 16-bit byte count (512), followed by 128×4 unsigned-byte weights. The reader displays an in-memory grayscale mask (white = authored weight) and its statistics; it **does not pretend any material identity is known**. Section 233, centre (23,25), channel 0: 25/25 original tiles, 105,630 nonzero mask pixels and 15.96% mean normalised weight. The visible paint boundaries follow geographical features.

## User controls

In **ESO Test**, launched from the existing desktop shortcut:

- **ESO original terrain LOD textures:** enable/disable existing baked diffuse textures (default: on).
- **Original DXT5 terrain surface normals:** toggle decoded normal data on the current material (default: on); inspection lighting stays enabled.
- **Close-up tile X / Z:** select a land tile (e.g. 23,25). Click **View 5x5 tiles at FULL heightfield resolution** to load the dense mesh and move the camera near the ground.
- **Original paint channel / Inspect:** show any of the 16 authored blend channels as a monochrome overlay. The channel number is not a texture ID and it is not a claim there are 16 populated textures everywhere.
- **Render all tiles for selected section:** return to whole-region low-resolution geometry with original LOD textures.

For smoke tests only: `ESO_DETAIL_PATCH=23,25`, `ESO_PAINT_MASK=23,25,0`, and `ESO_CAPTURE_SCREENSHOT=1`. The last option saves an ignored screenshot under `.research/`, **not source game data**.

## Still missing for actual in-game close-up rendering

We have established where layer weights are, but **not** what each weight points to. Without the palette mapping, applying grass, rock or sand textures from elsewhere would be guesses. To reproduce the original game surface, determine:

- The per-world/per-tile material palette: original diffuse/normal texture IDs and their assignment to the four RGBA splat maps.
- The exact shader blending rule, texture repetitions, tangent handedness, and any colour/material modifiers.
- The game's actual terrain lighting, water and placed scene/clutter resources.

The ordinary game-like quality of ESO also depends on clutter (plants, grass), rocks, roads and buildings placed above the heightfield. Those aren't magically included by mapping the LOD DDS.

A potentially relevant source for further shader research is [doodlum/eso-shader-tools](https://github.com/doodlum/eso-shader-tools), which targets ESO's shader variants. We did **not** run those tools or claim to have found the source texture palette in this pass.

**Do not equate** the newly decoded normal maps or paint weights with a complete ESO near-field material implementation.
