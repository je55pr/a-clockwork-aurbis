# Original ESO creatures: source materials and population scaling

This isolated Godot laboratory reads genuine original ESO meshes, native skeletons, Walk clips and optional DDS files from the locally installed game. It does not alter the Auridon population renderer or commit proprietary assets.

## Verified source rigs and native movement

| Creature | Source mesh ID | Original rig | Walk clip | Vertices | Native skin binds | Bones |
| --- | --- | --- | --- | ---: | ---: | ---: |
| Clannfear | 000815D7 | 00082309 | 00000E14 | 2,424 | 54/54 | 104 |
| Armoured Daedroth | 000A0A61 | 00046563 | 0000121C | 7,716 | 52/52 | 102 |
| Guar (Xitahran pet) | 0029B14F | 00024A33 | 00024A69 | 4,876 | 52/52 | 113 |

All 27 native pose samples for each original Walk clip were evaluated with installed Granny at 15fps. Godot's source joint positions match within 0.0000014m. The ten further variants are in `data/eso_creature_variants.tsv`; each has original mesh metadata and exact native Granny bone bindings to one chosen source skeleton. A compatible rig is not proof of ESO's unique runtime rig choice when multiple skeletons share binding names.

## Local original DDS candidates

`EsoOriginalCreatureTextures` loads genuine ESO DDS resources from the installed depot. It supports DXT1/3/5, fixes nonstandard header fields in a temporary buffer, and interprets original packed normal X in alpha and Y in green. DDS data is never committed. An optional ignored local manifest, `.research/npc-rigs/creature-texture-candidates.tsv`, has fields `meshId<TAB>diffuseId<TAB>normalId<TAB>evidence`.

| Source mesh | Candidate diffuse | Candidate normal | Evidence |
| --- | --- | --- | --- |
| Guar 0029B14F | 0029B14D | 0029B151 | UV-provisional |
| Armoured Daedroth 000A0A61 | 000A0A63 | not resolved | UV-provisional |
| Armoured Clannfear 000A0A3F | 000A0A4D | 000A0A4F | UV-provisional |

**Important:** these files are genuine installed ESO DDS images, but archive proximity and visual appearance do not prove ESO's actual runtime mesh-to-texture association. No verified character texture-ID lookup has been recovered. Clannfear's basic diffuse remains unknown. The current Godot StandardMaterial3D is a diagnostic approximation, not ESO's actual shader. Press `T` in the viewer to compare candidates with neutral materials.

Source GR2 material strings identify original authoring artwork including `Characters\Monsters\Clannfear\Textures\Clannfear_D.tga`, `Art\Character\textures\Clannfear_CH_armored_D.dds`, and Guar Xitahran pet material paths. They are not resolved runtime resource IDs. The ZOSFT named index does not include their source-art paths.

## Crowd renderer

The catalogue-driven source loader verifies original mesh identity, 1:1 native bone binding, correct archive rig, and original clip hierarchy. Each visible individual owns a Skeleton3D, while every copy of a source shares its immutable ArrayMesh, Skin, source pose arrays and optional decoded texture. Only up to 72 nearby copies receive continuous animation updates at 15fps. Other copies remain visibly frozen rather than incurring animation costs.

| Launcher flag | Unique original meshes | Creature instances | Shared geometry copies |
| --- | ---: | ---: | ---: |
| `-OriginalCreatureWalkGallery` | 3 | 3 | 0 |
| `-OriginalCreatureCrowdLab` | 3 | 300 | 297 |
| `-OriginalCreatureVarietyLab` | 13 | 312 | 299 |

The three-species 300-instance **headless construction** test completed in roughly 1.7 seconds on the development PC. Both crowd scenes passed native pose, source instance-sharing and UI tests. This is NOT a rendered FPS benchmark. Proper production LOD, GPU-frame profiling and texture-material batching are future work.

Controls: drag to orbit, wheel zoom, Space pause, Left/Right source frame stepping, 1-3 first three species, PageUp/PageDown cycle species, 0 show all, T source DDS candidate comparison, Esc exit.

## Build and tests

Run `dotnet build EsoTest.csproj`, `dotnet build tools/ModelGeometry/ModelGeometry.csproj`, and `./tests/check_original_creature_source_animations.ps1`. Headless Godot smoke scenes are `eso_original_creature_walk_gallery_smoke.tscn`, `eso_original_creature_crowd_smoke.tscn`, and `eso_original_creature_variety_smoke.tscn` in `tests/Godot/`. UI checks are the corresponding `_ui_smoke.gd` scripts. Source assets and extracted research remain only under ignored `.research/`.
