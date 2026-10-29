# A Clockwork Aurbis

An experimental, **fan-made world-simulation RPG project**, currently in the form of a Godot 4.7.2 (.NET/C#) **Elder Scrolls Online local asset browser and 3D terrain explorer**. This is an independent, unofficial research project. Bethesda and ZeniMax own the Elder Scrolls intellectual property and game assets; none are included in this repository. Godot reads `C:/ESO/The Elder Scrolls Online/depot/eso.mnf` and the original DAT files **directly at runtime**, indexes named assets and decoded metadata, previews source DDS images and generates terrain meshes in memory. Optional 3D model previews use a separate decoder process and cache temporary derived mesh/UV/material-reference previews under ignored `.research/model-previews/`. Neither original ESO content nor converted preview assets are committed or redistributed.

## Current result (27 October 2026)

**Source-backed asset browser with recovered 3D model names.** The application opens to a categorized catalogue of **1,102 worldspaces, 2,642 named map-image sets, 3,501 weapon equipment icons, 5,049 armor equipment icons, 54,055 model-linked resource IDs and 95,798 named files** in the current local manifest. A separate resumable read-only indexer has now recovered **9,747 original GR2 model/mesh names** into an ignored local cache. These names make the Model records category searchable by recognizable asset names without running ESO or loading a native Granny DLL inside Godot. **3D model preview is now available:** select any model record, choose **Preview source 3D geometry**, then drag to orbit and scroll to zoom. These previews now use the **actual original GR2 material triangle groups, diffuse DDS IDs and authored UV0 coordinates**. Original textures are decoded in memory from ESO's DAT archives. Original packed normal maps and source vertex tangent frames are reconstructed. We also recovered original per-material diffuse/normal/specular/detail enable flags, alpha and sidedness settings, numeric material types and secondary gloss/Fresnel/glow parameters. Sidedness is applied with correct ESO-to-Godot winding; secondary/detail shader selection, exact specular, transparency and animation remain under investigation. Decoding is isolated in a separate helper process. Browse source IDs, names and DDS previews; click a worldspace to open its original 3D terrain when the installed DAT files are available. Enemy/NPC entity classifications are **not yet extracted**; artwork is clearly labelled as artwork. See [asset browser and extraction roadmap](docs/ASSET_BROWSER.md).

**Full-sized original terrains are working when the installed depot is complete.** The source reader now indexes **253,922 heightfield tile records from 1,276 terrain sections**, instead of incorrectly taking only the first 25 tiles per section. Each tile is a 65×65 heightfield; its original local X/Z is encoded in the manifest file ID. Section descriptors give dimensions in 100-unit tile increments.

The well-tested Auridon landscape, **section 233**, contains a complete **35×50 grid (1,750 tiles)**. It renders in Godot with **zero reported height disagreements along adjacent tile boundaries**. We've graphically tested it. Its in-game zone identity is now confirmed as **Auridon** by the original category-60 section-to-zone mapping (`233 → 381`).

Other larger original sources can also be rendered:

| ID | Size | Tile records | Tested seams |
|---|---|---:|---:|
| 233 | 3,500 × 5,000 | 1,750 | 0 mismatches |
| 899 | 6,000 × 6,000 | 3,600 | 9 mismatches |
| 1391 | 7,000 × 5,000 | 3,500 | 14 mismatches |
| 1149 | 6,000 × 5,500 | 3,300 | 108 mismatches |
| 1207 | 5,500 × 5,500 | 3,025 | 50 mismatches |
| 1332 | 6,000 × 5,000 | 3,000 | 15 mismatches |

These are **original terrain packages**; a subset now has confirmed named zone mappings, including Auridon (233), Summerset (899) and Blackwood (1149). Full-section rendering continues to use the original baked LOD DDS. **The 5×5 close-up now renders the real per-tile diffuse and normal DDS materials blended by ESO's own paint maps**, discovered by decoding the land-tile palette and global material metadata category 41. Exact UV transforms and lighting remain provisional. See [original terrain material discovery](docs/ORIGINAL_TERRAIN_MATERIALS.md).

For details, including the critical fix to the 25-tile truncation, see **[full terrain coverage and placement correction](docs/TERRAIN_COVERAGE.md)**.

## Launch

**Run locally:** open `project.godot` in Godot **4.7.2 .NET**, build C#, then press **F5**. On Windows, `Launch-ESOTest.ps1` builds changed sources and starts the editor. Set `ESO_TEST_GODOT_EXE` to your Godot .NET editor path, or create an ignored local file `.research/godot-exe.txt` containing the full editor path. Godot can also be resolved from your PATH.

Controls:
- **Model previews:** in **Model records**, choose **Preview source 3D geometry** to inspect the original GR2 mesh and its source DDS diffuse textures. Drag to rotate, wheel to zoom; neutral surfaces mean no verified diffuse asset, and original normal maps are enabled when source vertex frames validate, while special ESO secondary/specular/tint shader variants are still pending.
- **Library / World viewer (B):** switch between two **separate full-screen workspaces**, never overlapping panels. Library has a persistent category sidebar, searchable results and a selected-asset preview/details pane. Select a worldspace and choose **Open in World viewer**, or use the top navigation tab. World viewer has a dedicated **Back to asset library** button. Enemies and friendly-NPC categories remain unclassified, not populated with unrelated icons.
- **Free-fly camera (replaces orbit):** hold **right mouse button** to look around; **WASD** moves relative to your view, including forward/back; **E / Space** moves up and **Q / C** moves down; **Shift** flies 5× faster and **Alt** flies 5× slower. **Mouse wheel** adjusts fly speed, not zoom. **Esc** releases captured mouse; **H** hides/shows controls. There is no zoom/distance floor or terrain collision, so you can inspect individual DDS texels from right above the ground.
- **ESO original terrain LOD textures:** toggle original source diffuse DDS layers on complete sections (default: on).
- **Original DXT5 terrain surface normals:** toggle ESO's matching baked terrain normals (alpha/green channel packing) with real height-derived mesh normals and a simple local lighting rig (default: on).
- **Original per-tile colour tint (near-field):** apply ESO's authentic 128×128 terrain-colour map to original close-up material diffuse. On by default for the recovered close-up shader; the baked LOD fallback may already contain this tint.
- **Close-up tile X / Z + View 5x5 tiles at FULL heightfield resolution:** inspect a 25-tile patch with all 65×65 original height samples, its authentic 16-slot local material palettes, original DDS diffuse and normal pairs, and authored blend masks. The free camera starts **2.5 world units above the centre land tile's original height**, with no zoom limit; tested Auridon coordinates: (23,25) and (14,19).
- **Original paint channel / Inspect:** read the original 128×128 RGBA per-tile blend weights for 16 potential material channels; display their source-painted shapes in grayscale, the active layer IDs and how often pixel weights total 255. For a tested Auridon 5×5 patch, shader slots 0–7 and 10 are present, and 291,476/409,600 pixels total exactly 255. This diagnostic labels its channels in the original shader's BGRA order; the new near-field renderer uses the matching raw paint-channel to palette-entry association. The actual texture IDs are now recovered.
- **World viewer advanced tools:** the long list of research actions, old multi-region dropdowns, source landscape picker, map comparisons, diagnostic paint/coverage modes and texture toggles are collapsed under **Advanced tools**. Everyday terrain navigation, 5×5 close-up and full-section rendering stay visible.
- **Inspect holes: original tile coverage:** read source DAT heightfields and display a colour-coded map of missing tiles, zero-height borders, zero-height interiors and entirely zero tiles. Counts explain exactly how many low-detail quads are being dropped.
- **Compare terrain elevation with Auridon map:** show an original-source elevation overview against the original four-tile Auridon map, switch between 85 plausible size/aspect candidates, and render the selected terrain in 3D. Similar shape alone does *not* identify the ESO zone.
- **Render all tiles for selected section:** load the full package currently selected by its bundle ID, rather than its small legacy preview.
- **View large section 233:** render the 1,750-tile Auridon source landscape on demand; the asset browser is now the default launch screen.
- **Previous/Next, Go, archive atlas:** browse sections and their legacy five-by-five preview. The atlas indexes descriptor IDs, **not** real world geography.
- **Discover larger regions from original fixture IDs:** inspect automatically inferred relative placements. Overlapping incompatible sources remain ambiguous and are omitted; this mode is experimental.
- **Legacy comparisons:** the earlier 50- and 55-tile stitched test scenes are retained for reference, **not** evidence of complete map placement.
- **Toggle Auridon 2D reference:** compare the original four-part Auridon map texture.

Environment variables:
- `ESO_ASSET_CATEGORY=Weapons` and `ESO_ASSET_SEARCH=akaviri_staff`: optionally open a specific browser category and filter at launch; `ESO_ASSET_SELECT_FIRST=1` selects the first result for repeatable screenshot tests.
- `ESO_START_TERRAIN=1`: retain the previous automatic Auridon 3D load. The asset catalogue is now the default landing view instead of forcing a full terrain render while the depot downloads.
- `ESO_ROOT`: override the default ESO installation root (read-only).
- `ESO_FULL_SECTION=899`: open a specific complete terrain package at startup.
- `ESO_TERRAIN_BUNDLE=id:381`: open a small single-section diagnostic preview.
- `ESO_AUTO_REGION=1`: start the experimental multi-package fixture-placement scanner.
- `ESO_CAPTURE_SCREENSHOT=1`: save a debug screenshot in ignored `.research/terrain-preview.png` and exit.
- `ESO_FULL_SECTION=233` with `ESO_DETAIL_PATCH=23,25`: open a material-textured 5×5 Auridon patch directly with the free camera at ground level.

The launch script is `Launch-ESOTest.ps1`; build/launch logs are in `.godot/eso-test-launch.log`. The compatible `ooz` decompressor was compiled locally in ignored `.research/ooz.dll`; the app also accepts `ESO_OODLE_DLL` or `ESO_OOZ_DLL`. Decoder binaries, extracted game assets and local research checkouts are **not** distributed by this repository.

## Architecture

- `EsoAssetCatalogue.cs`: build the source-backed browse indices from ZOSFT filenames plus global metadata categories 60 (worldspaces) and 49 (model-linked assets), enhanced with recovered source-validated GR2 names when present.
- `EsoModelNameIndex.cs` and `tools/ModelNameIndexer/`: optional **resumable model-name indexing**, using an independently supplied compatible Granny2 library only in the CLI. It caches readable model, mesh and animation names, not mesh geometry. See [model indexer usage](tools/ModelNameIndexer/README.md).
- `EsoModelPreview.cs` and `tools/ModelGeometry/`: on-demand **original GR2 3D mesh preview**. An isolated helper reads original source model vertices and triangles through a separately supplied native Granny DLL, converts into ignored derived `.esm` geometry, and Godot displays it with orbit and zoom. Their original diffuse textures are now mapped to the correct source material groups and UV0s, including complex 13-material buildings. Source packed normal maps are now also rendered. Detail/diffuse2/specular IDs, the second UV channel and original material enable flags are extracted. The separate detail-material shader has a corrected experimental path, but the exporter-to-runtime material-type mapping is not yet proven, so secondary blending remains opt-in. See [model shader investigation](docs/MODEL_SHADER_RESEARCH.md), [material/UV recovery](docs/MODEL_TEXTURE_RESEARCH.md), and [geometry preview guide](tools/ModelGeometry/README.md).
- `EsoAssetBrowser.cs`: searchable, paginated Godot dock with per-resource provenance, original DDS image preview and optional 3D terrain jump.
- `EsoArchive.cs`: read-only MES2/PES2 manifest and original DAT access, including section/channel flags.
- `TerrainBundleIndex.cs`: enumerate **all** heightfields belonging to each descriptor, without a fixed tile cap.
- `LandTile.cs`: decode each original 65×65 terrain-height record.
- `TerrainGapDiagnostics.cs`: exact in-memory per-tile source coverage / zero-sample diagnostic map.
- `TerrainElevationOverview.cs`: relative-elevation thumbnail for original source terrain, built directly from DAT data and never used as an intermediate asset.
- `AutoRegionRenderer.cs`: quarter-resolution in-memory terrain mesh generation, retaining original source-coordinate UVs and counting mismatched edges.
- `TerrainLodTextures.cs`: load original section-linked DXT1/BC1 terrain LOD diffuse **and** original DXT5/BC3 normal DDS into compressed arrays, and sample using the actual terrain tile/chunk coordinates.
- `TerrainPaintMasks.cs`: decode and inspect original per-tile blend weight channels, 128×128 RGBA with two-byte row prefixes and normalisation analysis.
- `TerrainNearFieldColors.cs`: read original per-tile `terrain-color` RGBA arrays directly from DAT and make them available for an optional shader comparison, without intermediate files.
- `TerrainNearFieldMaterials.cs`: recover each land tile's 16 material IDs, resolve category-41 original DDS diffuse/normal pairs, decode in memory and blend authentic textures using the tile's original paint weights.
- `TerrainBrowser.cs`: full-section view, legacy comparisons and interactive camera/navigation.
- `FixturePlacementGraph.cs`: relative section transforms inferred from repeated 96-byte fixture records and matching local object IDs.
- `TerrainHierarchyIndex.cs`: archive/section/resource organization and equivalent resource-hash layouts.
- `CompositeTerrain.cs`, `StitchedTerrain.cs`, `TerrainConnections.cs`: earlier small-scale stitching research.
- `AuridonPreview.cs`: the original Auridon 2D DDS map reference.
- `ZosftTable.cs`, `DepotMetadataContainer.cs`: resource names and metadata container research.

## Remaining research

0. **Complete the asset/entity browser:** map icons and original model-linked IDs to real equipment definitions and 3D geometry; recover creature/character archetypes and trustworthy friendly/hostile NPC data without inventing classifications. See [asset browser roadmap](docs/ASSET_BROWSER.md).
1. **Determine how ESO chooses the authoritative worldspace/instance/terrain revision.** The fixture graph links sections at 500-unit offsets but many larger packages overlap with conflicting heightfields. Their parent worldspace and phase memberships remain unknown.
2. Investigate the mismatched seams in otherwise complete large sources and identify any missing padding/LOD handling.
3. **Refine near-field fidelity:** original per-tile palette IDs and their diffuse/normal DDS images are now recovered and rendered. The original shader's vertex UV and 128×128 paint-map texel-centre correction are verified, but runtime diffuse UV scale/bias/rotation, packed specular handling and ESO's lighting/projection still require research. Section 233 is independently identified as Auridon (zone 381). See [source material discovery](docs/ORIGINAL_TERRAIN_MATERIALS.md) and [focused UV investigation](docs/TERRAIN_UV_INVESTIGATION.md).
4. Introduce region streaming and selectable terrain layers without saving intermediate asset files.

See [the original ESO terrain shader source and material-palette investigation](docs/SHADER_PALETTE_RESEARCH.md), [the source-colour investigation](docs/MATERIAL_PALETTE_INVESTIGATION.md), [Auridon source identification and hole diagnostics](docs/AURIDON_SEARCH.md), [fixture placement research](docs/FIXTURE_PLACEMENT.md), [metadata format](docs/METADATA_DECODING.md), and [historical stitching tests](docs/STITCHING.md).

## Licensing and scope

Format research references include [eso-tools](https://github.com/eso-tools/eso-tools), [UESP extraction tools](https://github.com/uesp/uesp-esoapps) and [ooz](https://github.com/powzix/ooz). ESO assets and proprietary executables are not committed. Using or distributing ESO content in another project may be restricted by applicable copyright and game licence terms.
