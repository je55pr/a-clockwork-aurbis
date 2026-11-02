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

The material loader (`scripts/EsoOriginalCreatureTextures.cs`) now accepts an **ID-only** candidate index in `data/eso_creature_texture_candidates.tsv`. A user can override it by creating the ignored file `.research/npc-rigs/creature-texture-candidates.tsv`. With neither index, the viewer uses neutral diagnostic materials. Columns are:

`mesh ID <TAB> diffuse DDS ID <TAB> normal DDS ID <TAB> specular DDS ID <TAB> provenance`

The tracked list has **9 of 13 distinct original source meshes with diffuse DDS candidates**, **7 packed-normal candidates**, and **8 specular DDS candidates recorded for future shader work**. All 24 unique DDS IDs were decoded and validated against the installed ESO archive; the four absent diffuse assignments are basic Clannfear, champion Clannfear, second basic Clannfear and basic Daedroth.

| Creature mesh | Diffuse DDS | Packed normal DDS | Specular DDS | Provenance |
| --- | --- | --- | --- | --- |
| Daedroth armoured 000A0A61 | 000A0A63 | unknown | 000A0A67 | archive neighbour |
| Guar Xitahran 0029B14F | 0029B14D | 0029B151 | 0029B153 | archive neighbour |
| Clannfear armoured 000A0A3F | 000A0A4D | 000A0A4F | 000A0A51 | archive neighbour |
| Clannfear boss 00023B09 | 00023B03 | 00023B05 | 00023B07 | adjacent triple |
| Guar basic 00023B3D | 00023B37 | 00023B39 | 00023B3B | adjacent triple |
| Guar variant C 00023C73 | 00023C6B | 00023C6D | 00023C6F | adjacent triple |
| Guar Scalecaller 00123257 | 00123251 | 00123253 | 00123255 | adjacent triple |
| Guar Peryite 0031A83B | 0031A833 | 0031A835 | unknown | archive neighbour |
| Daedroth armoured green 000AA37D | 000AA377 | unknown | 000AA37B | archive neighbour |

**Critical evidence boundary:** all of these DDS bytes are original ESO assets, but **none of the mesh-to-DDS assignments has been proven from ESO's authoritative runtime material table**. Being adjacent in archive-ID order or having matching colour/shape is evidence for a *candidate*, not proof of the final in-game material. The Clannfear armoured assignment is particularly uncertain because the colour atlas resembles architectural/ornamental artwork. No ID is marked source-verified. The user-facing renderer explicitly labels these as candidates.

The loader supports original DXT1, DXT3 and DXT5 DDS content and fixes nonstandard pitch/linear-size fields in an **in-memory copy only**. ESO creature tangent-space normals store X in alpha and Y in green. Before conversion to Godot normal textures, a sample verifies that the source green/alpha channels have the expected packed-normal distributions. This rejected two wrong earlier assignments: `000A0A67` and `000AA37B` are *not* packed-normal maps, and their source IDs are now retained in the **specular** candidate column rather than forced into the wrong normal slot.

Specular candidate DDS IDs are currently **recorded but not fed into a guessed Godot PBR slot**. The currently rendered Godot StandardMaterial3D reproduces the original diffuse and compatible packed-normal textures, but it is not ESO's exact original shader and does not recreate its gloss/tint/emission pipeline.

Original GR2 material metadata includes artist paths such as `Characters\Monsters\Clannfear\Textures\Clannfear_D.tga`, `Art\Character\textures\Clannfear_CH_armored_D.dds`, and the Guar Xitahran pet texture source paths. The ZOSFT path index does not provide their final installed DDS archive-ID association. Two older Guar models store embedded Granny texture **metadata**, but the native images have dimensions 0×0 and zero embedded pixels, so those GR2 records cannot directly supply diffuse artwork.

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
./Launch-ESOTest.ps1 -OriginalCreatureVarietyLab
```

Run the Godot scene and UI smoke tests for the original creature gallery, creature crowd and creature variety under `tests/Godot/`. The variety UI test asserts **9 loaded original DDS diffuse maps**, the **7 appropriate packed normal maps** and preservation of original skinned mesh/rig identity through toggle and sharing. It also passes using the tracked source-ID index alone when no `.research/` override exists.

No proprietary source image or archive is committed to Git. Newly found texture candidates must remain labelled provisional until ESO's authoritative runtime references can be recovered.
