# ESO source asset browser (27 October 2026)

## Working application

**ESO Archive Lab now starts as a searchable, read-only source asset catalogue**, with a separate full-screen World viewer. Press **B** to switch between Library and World viewer, or use the top navigation. The **Back to asset library** action is always visible in the World view, and **H** hides the terrain control panel only while using that view. Worldspaces with indexed terrain bundles have a **View original terrain in 3D** action. The 3D scene no longer loads automatically on launch, because the depot may be actively updating; explicit `ESO_FULL_SECTION`, `ESO_DETAIL_PATCH`, and `ESO_START_TERRAIN=1` retain terrain launch support.

The Library uses three consistent areas: **left category navigation**, **centre search and paginated results**, and **right DDS preview / original source metadata**. There is no overlapping terrain panel. It filters by filename, resource path, decimal resource/zone IDs and hexadecimal file IDs. Results are **paged 100 at a time**, with source metadata and a **read-only, in-memory DDS preview** when supported. Scrollbar thumbs have been enlarged and high-contrast styling is shared across the list and details. The World viewer keeps the section selector, close-up controls, and camera help in a compact sidebar; legacy tools, experimental render options and research diagnostics are behind **Advanced tools**. The images are read directly from the installed DAT records. No source DDS, executable or other game assets are copied to the repository.

The implementation separates **entity evidence** from **artwork evidence**. In particular, an icon of a sword is not identified as a 3D equipment mesh and a creature-loot icon is not a hostile NPC definition.

### Source-backed categories in the current local installation

| Category | Count observed | Source and meaning |
|---|---:|---|
| Worldspaces | 1,102 | Original FAFAEBEB global metadata category 60, with section IDs, source labels, ESO zone IDs and section dimensions. Confirmed mappings include 233/Auridon/381 and 899/Summerset/1011. |
| Map locations | 2,642 | Distinct map-name sets grouped from 28,606 original `/art/maps/` DDS paths. Map images, not automatic links to section placement. |
| Weapons | 3,501 | Gear item **inventory icons**, from `/esoui/art/icons/gear_*` filenames matching specific weapon-type tokens. Not individual weapon instance definitions or 3D models. |
| Armor | 5,049 | Gear item **inventory icons** with identifiable armor part tokens. Not 3D clothing mesh assignments. |
| Model records | 54,055 | IDs from global FAFAEBEB category 49 present as real resource records. **9,747** currently have recovered model/mesh/animation names, validated against source fingerprints. Remaining model records stay numeric until indexed. These are not gameplay actor definitions. |
| Effect meshes | 2,725 | Named `/art/fx/particle/*.gr2` effect meshes. Not a creature/character mesh catalogue. |
| Creature art | 19 | Named `monster_*.dds` loot/monster-related inventory art. Not identified creature actors. |
| All named files | 95,798 | Direct original ZOSFT filename table, including UI, FX, maps, and other named resources. |
| Enemies / Friendly NPCs | Not classified yet | Intentionally empty until actual actor definitions and faction/reaction data can be linked. |

**Counts are per the local manifest and change with ESO patches**. During the 100+ GB client download, the manifest and DAT files may temporarily disagree; the browser will still show indexed resources and try individual preview decoding, reporting missing/incomplete DDSs rather than fabricating an image. Model category metadata failures also leave the other categories available.

## How to use

Launch using the existing **ESO Test** desktop shortcut (`Launch-ESOTest.ps1`). The **Library starts open**. Pick a category from the left sidebar, type to filter, click an entry to view its original path and file ID, and use Back/Next to page. Click a worldspace with a terrain bundle and use **Open in World viewer** to load its original source terrain. Switch between views using the header and the World view's back control, or press **B**. Open **Advanced tools** in the World viewer only when you need the old diagnostics.

Examples to search:
- Worldspaces: `Auridon`, `Summerset`, `381`.
- Map locations: `Auridon`, `Vulkhel`, `Skywatch`.
- Weapons: `akaviri staff` or `akaviri_staff`, `dagger`, `bow`.
- Model records: `0028F157` (a category-49-linked resource with a confirmed Granny-format file header in the sampled local files).

The optional `ESO_ASSET_CATEGORY`, `ESO_ASSET_SEARCH` and `ESO_ASSET_SELECT_FIRST=1` variables deep-link into the catalogue. These are useful for repeatable screenshot tests. `ESO_CAPTURE_SCREENSHOT=1` still writes a screenshot only to ignored `.research/terrain-preview.png` and exits.

## Architecture

- `scripts/EsoModelNameIndex.cs` validates and loads optional recovered GR2 internal model, mesh and animation names against the exact source record fingerprint. `tools/ModelNameIndexer/` is an opt-in resumable CLI that builds a derived index without adding a Granny DLL to the Godot process. See [model indexer guide](../tools/ModelNameIndexer/README.md).
- `scripts/EsoAssetCatalogue.cs` is the source-provenance and classification layer. It reads the original filename table provided by `ZosftTable`, builds deduplicated map file groups, conservative equipment icon categories, and consumes global `FAFAEBEB` tables 60 and 49 for worldspaces and unclassified model IDs. File/record IDs are not derived from the display labels.
- `scripts/EsoAssetBrowser.cs` is the Godot browse/search/page/details/preview dock, deliberately independent of any successful terrain mesh render.
- `scripts/EsoModelPreview.cs` is the modal 3D model inspector. `tools/ModelGeometry/` decodes the original GR2 positions, packed UVs, material triangle groups and original texture FileIndex IDs in a separate process. Godot validates the derived ESM5 preview and loads original diffuse plus correctly unpacked normal DDS materials directly from the depot. Source UV1, detail/diffuse2/specular references and original authoring flags (material type, alpha mode, sidedness and secondary gloss/Fresnel/glow) are retained. The renderer now respects source diffuse/normal enablement and properly converted face winding. The exact exporter-to-runtime shader variant mapping is still being investigated. See [original mesh material/UV research](MODEL_TEXTURE_RESEARCH.md) and [model shader research](MODEL_SHADER_RESEARCH.md).
- `scripts/ArchiveWorld.cs` composes the catalogue and terrain browser from the same read-only archive and decoder.
- `scripts/TerrainBrowser.cs` exposes a narrow existing 3D section-opening action to worldspace records, and remains responsible for terrain rendering and free camera.

**Model-name indexing now works (27 October):** 9,747 decoded source GR2 models have readable internal model/mesh/animation names in the local ignored index. The **Model records** category displays these names rather than numeric placeholders and supports searching them by text, while retaining original IDs and provenance. A separate read-only, resumable CLI generates the index; Godot never loads the native Granny DLL. The incomplete late-DLC files still produce retryable decode errors during the update. See [model-name recovery research](MODEL_NAME_RESEARCH.md) and [indexer guide](../tools/ModelNameIndexer/README.md). **The native 3D geometry inspector is now working**: select any Model records entry and click **Preview source 3D geometry**. The isolated CLI decodes the original GR2 vertices/triangles to an ignored temporary geometry cache while Godot displays them with orbit and zoom. Verified a Deadlands door (173 vertices / 186 triangles), an Arcane Master helmet (458 / 558) and `ROK_GRY_MRKCliff001` (3,316 / 4,460). Previews now use **original source diffuse and correctly unpacked normal DDS textures**, material triangle groups, signed fixed-point UVs and source vertex tangent frames. Original specular/detail shader flags and exact effects, rigging and animation are still outstanding. See [geometry preview guide](../tools/ModelGeometry/README.md). Internal model names do not yet establish actual friendly/hostile NPC status or item identities.

## What is still required for genuine weapon, enemy and friendly NPC lists

1. **Item/gameplay records:** identify authoritative item definitions and assign them to icon + actual model IDs, style variants, weapon classes and stats. Don't label an icon as a complete item until cross-referenced.
2. **Finish 3D model fidelity:** original GR2 geometry, material groups, diffuse/normal DDS, authored UVs and vertex normals/tangents are now verified and rendered. Next recover material-variant flags and original detail/diffuse2/specular, transparency, tint/vertex colours and rig/skeleton/animation.
3. **Character and creature archetypes:** locate the original actor templates, model references and skeleton/animation information and classify animals, daedra, humanoids, hostile creatures etc with source evidence.
4. **NPC identities and disposition:** distinguish the client-side appearance/template data from server-selected NPC names, dialogue, faction affiliations, hostile/friendly reactions and precise world placements. These may require runtime/game-data references not currently decoded. **No assumption** that every desired NPC field is available offline.
5. **Relationships and browsing:** add linked asset records and provenance paths, e.g. Worldspace → area/map → fixture/actor; Item → equipped mesh → textures; NPC → appearance/model → animation. Keep uncertainties explicit.
6. **Performance and storage:** continue to load source files read-only and keep indexes derived from the source rather than committing copyrighted resources. Use lazy previews, paging and optional caching for very large catalogues.

### Smoke tests

On 27 October 2026, built with Godot 4.7.2 Mono (**0 warnings / 0 errors**) and launched against the updating depot:

- Godot enumerated every category listed above and displayed 1,102 worldspaces without attempting to render unavailable terrain at startup.
- Weapon search `akaviri_staff` returned three real gear entries (`a`, `b`, `c`), and the first one's original DDS icon rendered in the details pane. This is read directly from the installed archives.
- Screenshot-only tests were saved under ignored `.research/terrain-preview.png`. No game files were modified.
