# Original ESO creature materials and population rendering

This is an isolated Godot laboratory. It uses original meshes, skeletons, sampled Granny Walk clips and **actual original DDS image bytes extracted from the locally installed ESO archive**. It never commits original game graphics or the proprietary Granny/Oodle binaries. The live Auridon renderer remains untouched.

## Original source mesh and animation verification

| Creature | Mesh | Skeleton | Native Walk | Vertices | Exact skin bindings | Bones |
| --- | --- | --- | --- | ---: | ---: | ---: |
| Clannfear | 000815D7 | 00082309 | 00000E14 | 2,424 | 54/54 | 104 |
| Daedroth (armoured) | 000A0A61 | 00046563 | 0000121C | 7,716 | 52/52 | 102 |
| Guar (Xitahran pet) | 0029B14F | 00024A33 | 00024A69 | 4,876 | 52/52 | 113 |

All 27 independently sampled original Walk poses for each rig match Godot's world joint locations within 0.0000014 metres. An additional ten mesh variants have validated original geometry and native binding to compatible skeletons, in `data/eso_creature_variants.tsv`. Native compatibility does not establish a unique ESO runtime actor-to-rig relationship when multiple compatible skeletons exist.

## Source DDS texture candidate coverage

The material loader (`scripts/EsoOriginalCreatureTextures.cs`) reads an **ID-only** candidate index from `data/eso_creature_texture_candidates.tsv` and merges optional local overrides from the ignored `.research/npc-rigs/creature-texture-candidates.tsv` (local entries take precedence). Thus a stale local index can no longer hide the larger tracked source catalogue. With neither index, the viewer uses neutral diagnostic materials. Columns are:

`mesh ID <TAB> diffuse DDS ID <TAB> normal DDS ID <TAB> specular DDS ID <TAB> provenance`

The expanded index has **59 distinct source mesh-to-DDS candidates**: the original 9 plus **50 newly researched creature meshes**. Across the whole index, 57 original packed-normal and 57 specular candidates, comprising **146 distinct genuine source DDS files**, pass the source-only batch audit. Of the currently animated 13 curated meshes, **9 have diffuse candidates, 7 have packed normals and 7 now have specular shaders**. Four still lack diffuse assignments: basic Clannfear, champion Clannfear, second basic Clannfear and basic Daedroth.

| Creature mesh | Diffuse DDS | Packed normal DDS | Specular DDS | Provenance |
| --- | --- | --- | --- | --- |
| Daedroth armoured 000A0A61 | 000A0A63 | unknown | 000A0A67 | archive neighbour |
| Guar Xitahran 0029B14F | 0029B14D | 0029B151 | 0029B153 | archive neighbour |
| Clannfear armoured 000A0A3F | 000A0A4D | 000A0A4F | unknown (mismatched candidate rejected) | archive neighbour |
| Clannfear boss 00023B09 | 00023B03 | 00023B05 | 00023B07 | adjacent triple |
| Guar basic 00023B3D | 00023B37 | 00023B39 | 00023B3B | adjacent triple |
| Guar variant C 00023C73 | 00023C6B | 00023C6D | 00023C6F | adjacent triple |
| Guar Scalecaller 00123257 | 00123251 | 00123253 | 00123255 | adjacent triple |
| Guar Peryite 0031A83B | 0031A833 | 0031A835 | unknown | archive neighbour |
| Daedroth armoured green 000AA37D | 000AA377 | unknown | 000AA37B | archive neighbour |

**Critical evidence boundary:** all of these DDS bytes are original ESO assets, but **none of the mesh-to-DDS assignments has been proven from ESO's authoritative runtime material table**. Being adjacent in archive-ID order or having matching colour/shape is evidence for a *candidate*, not proof of the final in-game material. The Clannfear armoured assignment is particularly uncertain because the colour atlas resembles architectural/ornamental artwork. No ID is marked source-verified. The user-facing renderer explicitly labels these as candidates.

The loader supports original DXT1, DXT3 and DXT5 DDS content and fixes nonstandard pitch/linear-size fields in an **in-memory copy only**. ESO creature tangent-space normals store X in alpha and Y in green. Before conversion to Godot normal textures, a sample verifies that the source green/alpha channels have the expected packed-normal distributions. This rejected two wrong earlier assignments: `000A0A67` and `000AA37B` are *not* packed-normal maps, and their source IDs are now retained in the **specular** candidate column rather than forced into the wrong normal slot.

Specular candidate DDS IDs are now decoded and sampled in a **diagnostic Godot ShaderMaterial**, modulating per-pixel dielectric highlights with the source RGB specular luminance. This shader also applies the original diffuse and converted packed-normal maps. It is an approximation for source texture inspection, **not ESO's exact original ZOS shader**, gloss/tint/emission equations or a verified DDS-to-mesh assignment. The two currently rendered diffuse candidates without matching specular IDs use StandardMaterial3D instead. The loader also refuses to apply normal/specular textures whose dimensions disagree with the diffuse DDS. Hardware OpenGL rendering has been checked locally with all 13 source meshes; source captures remain under ignored `.research/`.

**Two basic Clannfear meshes are a proven material family:** the native Clannfear A mesh `000815D7` and Clannfear C mesh `0008230B` have byte-identical original UV coordinates and triangle indices, and all three GR2-authored diffuse, normal and specular paths match exactly. The test `tests/check_original_clannfear_material_family.ps1` verifies this against the installed source. Once a **verified** runtime DDS texture set for one is found, its source authoring data supports investigating the same set for the other. Neither DDS ID is currently resolved.

Original GR2 material metadata includes artist paths such as `Characters\Monsters\Clannfear\Textures\Clannfear_D.tga`, `Art\Character\textures\Clannfear_CH_armored_D.dds`, and the Guar Xitahran pet texture source paths. The ZOSFT path index does not provide their final installed DDS archive-ID association. Two older Guar models store embedded Granny texture **metadata**, but the native images have dimensions 0×0 and zero embedded pixels, so those GR2 records cannot directly supply diffuse artwork.

## Expanded source texture research

The independent source-only `--survey-original-creature-texture-triplets` survey inspected **1,888 Bip01-root mesh records** and recovered **393 structurally consistent DDS triples across 290 meshes**. Of these, **225** had one possible triple and **68** were within six resource IDs of the matching mesh. The DXT5 compressed mipmap checker confirmed that all accepted normal channels use an ESO-compatible packed distribution and rejected previously mistaken dark non-normal maps. A further native Granny material audit confirmed that **52 of those 68 models explicitly name diffuse, normal and specular authoring texture paths**. One of those 52, the Guar Scalecaller, was already in the initial index. A second, Iron Atronach, was rejected because the proposed diffuse ID is shared with a Fell Sentry despite conflicting original authoring paths. This leaves **50 additional IDs**. The new entries all carry `archive-triplet-authoring-provisional` evidence, never `source-verified`. A wider survey of the 225 unique structural candidates found 182 with three authoring channels, but ten reused DDS triples across unrelated original artist paths; candidates outside the strict radius therefore remain research leads, not automatic renderer assignments.

The batch source index audit (`--audit-original-creature-texture-index`) validates **all 59 candidate associations and all 146 distinct DDS resources** in one native source reader, including dimensions and packed normals. It found a mismatched proposed specular DDS for the older armoured Clannfear (`000A0A51`); that assignment has been disabled rather than falsely presented as original. A new Godot runtime smoke test (`tests/Godot/eso_original_creature_texture_coverage_smoke.tscn`) successfully **decodes and shader-binds all three original DDS channels for 12 newly mapped creature families**: giant spider, werewolf, troll, Mantikora, Fell Sentry, thorn gecko, spider daedra, Dreugh, giant wasp, senche, Guar mount and bear cub. No game graphics were committed and no runtime mesh-to-texture association has been proven from an authoritative actor material table. These additional 50 models are **texture-mapped candidates for future general rendering**, not yet newly animated models in the 13-mesh gallery.

## Static original creature UV/material preview

The noninteractive Godot test scene `tests/Godot/eso_original_creature_static_texture_preview.tscn` loads **four further original ESO creature meshes** from the installed local archive (not approximated mannequins) and applies the source DDS candidate diffuse, packed normal, and specular maps to their original vertex UVs: Giant Spider (2,790 native vertices), Werewolf (3,958), Troll (7,815), and Mantikora (6,030). The source-native rest geometry is shown **without invented skeletal animation**. A local OpenGL hardware render confirmed coherent surface UVs on all four; this is a visual plausibility check, **not proof of ESO's authoritative runtime material mapping**. The matching fail-closed test scene `tests/Godot/eso_original_creature_static_texture_preview_smoke.tscn` checks source identity, original mesh bounds, genuine DDS decoding and all required shader channels. Captured reference images stay in ignored `.research/npc-rigs/`, never Git.

## What broad texture searches actually established

The read-only `--count-original-dds-atlases` probe counted 1,291,975 manifest records and 99,226 records with common DDS atlas sizes. The narrower `--search-original-dds-atlases` scan examined 10,455 likely BC1 diffuse atlases by decoded mipmap structure against original Clannfear boss art `00023B03`. The source correctly matched itself (score 1.0), but subsequent hits scored at most roughly 0.32. **No distinct runtime match was established, and no new texture ID was invented.** The rankings are stored only in ignored local `.research` files.

A separate `--audit-original-resource-hashes` check compared 24 named ESO resources against ordinary CRC32, Adler32, FNV1a, DJB2, Jenkins, Murmur3 and xxHash32 of filename variants, decoded data and compressed data. The standard hash implementations also pass known-vector self-tests. None matched the manifest field; it is not currently a safe shortcut to recover names. This is a bounded negative result, not proof that no other scheme exists.

The source atlas search can be reproduced with `--count-original-dds-atlases <manifest>`, or with `--search-original-dds-atlases <manifest> <oodle> 00023B03 .research/clannfear-boss-diffuse-atlas-similarity.tsv 15000 45 colour-only`. This never writes proprietary DDS bytes to the repository and never creates authoritative associations. The hash audit likewise uses `--audit-original-resource-hashes <manifest> <oodle> .research/asset-paths.tsv 24` against the separately generated local filename index.

## Scaling the original creature instances

Each individual creature owns its original Skeleton3D. Identical source instances share immutable mesh/skin geometry, decoded DDS texture resources and sampled native animations. The current test caps continuous original-source pose updates at 72 nearby crowd copies, with the rest remaining visible and temporarily frozen.

| Launcher flag | Distinct source meshes | Total instances | Mesh-sharing copies |
| --- | ---: | ---: | ---: |
| `-OriginalCreatureWalkGallery` | 3 | 3 | 0 |
| `-OriginalCreatureCrowdLab` | 3 | 300 | 297 |
| `-OriginalCreatureVarietyLab` | 13 | 312 | 299 |

The 300-creature **headless construction** test took about 1.7 seconds on the test PC, but this is not an actual rendered GPU frame-rate benchmark. No creature browser or other unrelated UI has been added. The minimal gallery is only a test harness.

Controls: drag to orbit, wheel zoom, Space pause, Left/Right original Walk frame stepping, 1-3 first three source types, PageUp/PageDown other source types, 0 show all, **T toggle actual archived DDS candidates versus neutral materials**, Esc quit.

## Validation

```powershell
dotnet build tools/ModelGeometry/ModelGeometry.csproj
dotnet build EsoTest.csproj
./tests/check_original_creature_source_animations.ps1
./tests/check_original_creature_texture_candidates.ps1
./tests/check_original_creature_texture_triplet_survey.ps1
./tests/check_original_creature_texture_authoring.ps1
./tests/check_original_clannfear_material_family.ps1
$godot=(Get-Content .research/godot-exe.txt -TotalCount 1).Trim().Replace('.exe','_console.exe')
& $godot --headless --path . res://tests/Godot/eso_original_creature_static_texture_preview_smoke.tscn --quit-after 450
./Launch-ESOTest.ps1 -OriginalCreatureVarietyLab
```

Run the Godot scene and UI smoke tests for the original creature gallery, creature crowd and creature variety under `tests/Godot/`. The variety UI test asserts **9 loaded original DDS diffuse maps**, the **7 appropriate packed normal maps**, **7 sampled specular texture materials**, the **59-entry source-ID catalogue** and preservation of original skinned mesh/rig identity through toggle and sharing. It also passes using the tracked source-ID index alone when no `.research/` override exists.

No proprietary source image or archive is committed to Git. Newly found texture candidates must remain labelled provisional until ESO's authoritative runtime references can be recovered.
