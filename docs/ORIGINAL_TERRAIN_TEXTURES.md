# Original ESO terrain textures in Godot

Research and implementation notes, 26 October 2026.

## Working: original baked terrain LOD diffuse

Section 233 is identified by the user as Auridon and the adjacent landmass, based on comparing the original Auridon map against the source terrain shape. We now directly render **the original ESO terrain LOD diffuse DDS images**, rather than using a height-coloured material or assigning unrelated textures.

The 583-byte section descriptor embeds resource-channel names, including:

- terrain-height (subresource channel 1): original 65×65 heightfield tiles
- terrain-color-raw and terrain-color
- terrain-blend0-raw through terrain-blend3-raw
- terrain-lod-diffuse (manifest 0x4000/channel 12)
- terrain-lod-normal (manifest 0x4000/channel 13)
- terrain-normal, terrain-hole, terrain-clutter, terrain-map

The land payload (`BEEF000C`) itself has ten subrecords: the heightfield, several 128×128 RGBA-like blocks, and additional surface-related arrays. The exact correspondence between the latter blocks and the final near-field diffuse/normal texture identities is **not yet established**. We do not make up those IDs or claim to have reproduced near-field blending.

### Baked LOD diffuse source format

Section 233 has **117 source diffuse records** at manifest group `(233 << 5) | 12`, flagged `0x4000`. Their 32-bit file ID encodes LOD chunk `(x,z)`: the first 16 bits are X; the last 16 bits are Z.

Each record:
- Begins with a 12-byte ESO wrapper: `0x12345678`, then two 32-bit payload-size fields.
- Has a genuine `DDS ` resource at offset 12.
- Is **512×512 DXT1 / BC1**, 10 mip levels. The original DDS bytes decode using Godot's `Image.LoadDdsFromBuffer` without image conversion/export to disk.
- Corresponds to an approximately **4×4 group of original 100-unit height tiles**.

For section 233, the 35×50 heightfield becomes a **9×13 LOD grid = 117 complete DDS layers**. The LOD group is complete with no missing or duplicated coordinates.

Manifest group `(233 << 5) | 13` separately contains 117 original **256×256 DXT5 / BC3** normal textures, also with ESO wrappers. Those are verified source resources but are *not yet used for shading*, pending normal-channel interpretation and tangent-space checks.

Other complete source LOD diffuse groups were tested successfully:

| Section | Original height tiles | Original LOD diffuse layers |
|---|---:|---:|
| 233 | 1,750 | 117 |
| 899 | 3,600 | 225 |
| 1391 | 3,500 | 234 |
| 1149 | 3,300 | 210 |

### Godot mapping

- `scripts/TerrainLodTextures.cs` reads channel-12 DDS records directly from the installed `esoXXXX.dat` files and constructs an in-memory `Texture2DArray` of the original compressed images.
- `scripts/AutoRegionRenderer.cs` emits per-vertex UV coordinates derived from each original heightfield's X/Z tile ID, divided by four for the LOD chunk grid. These UVs are independent of centring the mesh in Godot.
- The shader computes `floor(UV)` to select the original LOD DDS layer, and `fract(UV)` for the original per-chunk diffuse UV. Images are indexed in source Z-major order (`layer = Z * gridWidth + X`).
- `TerrainBrowser.cs` enables source textures by default on complete original sections and exposes **ESO original terrain LOD textures** as a toggle. Disable it to return to the height-coloured diagnostic view.
- If a section lacks a complete original LOD DDS grid or a DDS is unreadable, the viewer **reports that and falls back to elevation colouring** rather than synthesizing replacements.
- The existing multi-source fixture-join diagnostic continues to use its separate known-good height-colour shader.

Graphical test of section 233 confirmed **117/117 original DDS layers loaded** and all 1,750 original height tiles rendered. There are recognizable original baked green/rock/sand areas. Some angular boundaries and brown/tan sea or unpainted areas remain because **ocean rendering, high-detail terrain materials and ESO's exact illumination are not reconstructed**. The LOD diffuse map isn't equivalent to the final close-up appearance.

## What is needed for the exact ESO near-field look

1. Identify what **each splat-channel byte actually means** in the original per-tile 128×128 subrecords. The input records exhibit multiple independently varying channels but the layer encoding is not yet validated.
2. Recover the source **terrain texture layer palette**, linking each splat layer to ESO's original diffuse/normal DDS assets and UV repeat rates. The ZOSFT table does not provide straightforward filename entries for all terrain material resources.
3. Decode the exact blending and material response in a Godot shader, using actual texture references instead of guessing grass/rock materials.
4. Decode and apply the original LOD normal (channel 13) and near-field normals with correct handedness and scale.
5. Reconstruct water, scene meshes and biome clutter independently. They are not part of the baked terrain diffuse itself.

**No third-party game assets or intermediate data files are distributed by this work.** The render-time data is read from the user's own ESO installation. This project is a local experiment; external redistribution or usage remains subject to ESO's applicable license terms.
