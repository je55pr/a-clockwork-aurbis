# Original ESO Telvanni Light material reconstruction

Status: **source-backed offline material investigation; NO verified live ESO material-ID mapping or wearable NPC assembly**.

The controlled ESO character-selection capture contained the original artist texture names
`TelvanniLight_A_U_D/N/S/T`, but the presence of those paths in the game's memory
**does not prove the selected character used this armour**. The useful part was
cross-referencing those names against actual archived Granny meshes.

## Original source models and material families

| Genuine GR2 model | Asset ID | Original vertices | Source material art |
| --- | --- | ---: | --- |
| `TelvanniLight_Helmet_B` | `001086BD` | 510 | `TelvanniLight_B_U_D/N/S/T` |
| `TelvanniLight_Helmet_A` | `001086D9` | 680 | `TelvanniLight_A_U_D/N/S/T` |
| `TelvanniLight_Belt_A` | `001086B5` | 163 | `TelvanniLight_A_U_D/N/S/T` |
| `TelvanniLight_Shoulders_A` | `00108795` | 784 | `TelvanniLight_A_U_D/N/S/T` |

The native Granny material records for all four meshes independently specify
their **diffuse, packed-normal, specular and tint-mask authoring paths**.
The corresponding source meshes retain their original UV coordinates.

Two complete DDS quartets are present in the installed archive adjacent to
the original Telvanni equipment family:

| Channel | Provisional B group | Provisional A group | Image format |
| --- | --- | --- | --- |
| Diffuse with original alpha | `001086C5` | `001086CD` | 576×256 DXT5 |
| Original packed normal | `001086C7` | `001086CF` | 576×256 DXT5 |
| Specular | `001086C9` | `001086D1` | 576×256 DXT5 |
| RGB/alpha tint mask | `001086CB` | `001086D3` | 576×256 uncompressed BGRA8 |

The source-only contact sheet shows two coherent armour atlases, including
large original helmet and armour UV islands, native packed normals and
distinct per-channel tint masks. The original helmets, belt and shoulders
render with recognisable engraved material placement on their source UVs
in Godot. **These associations are structural and visually plausible,
not proven from actual runtime draw calls**. They remain
`archive-triplet-authoring-provisional` in
`data/eso_telvanni_light_material_candidates.tsv`.

The original Granny `ZosMaterial` records also supply real colour parameters
for all four source meshes, extracted and validated at runtime:

- `f4DiffuseTintPrimary = (128, 120, 112, 255) / 255`
- `f4DiffuseTintSecondary = (89, 89, 89, 255) / 255`
- `f4DiffuseTintAccent = (61, 71, 76, 255) / 255`

The Godot material diagnostic **does not invent a dye equation**. The default
source shader samples the original diffuse, normal and specular images.
It uses the real source diffuse alpha for a **provisional alpha cutout**;
ESO's exact cutoff and material/shader configuration are not yet known.
Press **M** to cycle through the actual tint-mask R, G, B and A channels
as greyscale diagnostics, then back to the source diffuse. The three
original dye colours are recorded and tested, but they are not yet mixed
into the shader.

## Deliberately rejected attachments and texture mappings

**`TelvanniLight_Tassets_A` (`001086B1`) is excluded.** Its original
Granny material does reference `TelvanniLight_A_U_D/N/S/T`, but GPU
tests found that its original UV0 samples empty/transparent areas of
**both** candidate quartets. Applying either quartet through the
diagnostic source alpha cutout makes the tassets disappear; without
cutout they form black silhouettes. This indicates either an
incorrect archived DDS relationship, missing texture-coordinate
semantics, or a material-specific rendering rule. It is *not*
acceptable evidence for assigning those resource IDs to the tassets.
The source-file audit independently checks that the rejected mapping
does not enter the index.

Neither original Telvanni helmet, belt, shoulders nor tassets
can be source-exact attached to our existing `004` or `601`
customization body. Comparing their original binding names, parents
and **16-component inverse-world skin matrices** found no exact matching
skin references, with rest discrepancies of up to about **0.123** matrix
component units. The helmets also use an original `Helmet`
bone absent from those merged body skeletons. No arbitrary bone aliases,
manually positioned armour attachments, or invented runtime retargeting
have been implemented. The four source meshes appear separately for
inspection.

The separate `Head_004` skin-material experiment was also kept local:
an adjacent DDS atlas rendered as streaky brown material rather than
credible skin; no source-backed high-detail face texture mapping was
established. That atlas is **not** promoted to a head or skin material.

## Reproduce and validate

From `C:\Repos\a-clockwork-aurbis-godot-lab`:

```powershell
.\Launch-ESOTest.ps1 -OriginalTelvanniMaterialLab
.\tests\check_original_telvanni_material_sources.ps1
$g=(Get-Content .research\godot-exe.txt -TotalCount 1).Trim().Replace('.exe','_console.exe')
& $g --headless --path . res://tests/Godot/eso_original_telvanni_material_smoke.tscn --quit-after 450
& $g --headless --path . --script res://tests/Godot/eso_original_telvanni_material_ui_smoke.gd --quit-after 450
```

The native source test checks original model identity, A/B
four-channel authoring paths, original dye-colour parameter values,
all eight genuine DDS images and the rejected tassets association.
Godot tests verify exact source vertices, all four texture channels,
the tint channel comparison and the source-only camera.

Extracted meshes, game DLLs, DDS images, screenshots, alternative
quartet experiments and local compatibility surveys stay under ignored
`.research/`. Only code, identified source IDs and test data are
committed.

**Next real blocker:** ESO's authoritative model/material resource
mapping, followed by the customization-to-animation body/armour assembly
and authentic face/skin/eye shader data.
