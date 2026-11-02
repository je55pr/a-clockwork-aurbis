# ESO humanoid assembly: source-verified research

This tracks original game data and verified experiments. It does **not** mean
our renderer reproduces the complete ESO appearance system. Source GR2
assets and native game libraries remain in the ignored `.research/` folder
and the locally installed ESO depot, not in Git.

## Confirmed archive relationships

- Animation skeleton `0x000020A2`: original 202-bone
  `GrannyRootBone` model. The usual weapon-style walk clips contain 97
  transform tracks, 93 of which are present by exact bone name.
- Customization head `0x000E2CFF`: `Head_004`, 404 vertices, 710
  triangles, 39 customization bones and six native skin bindings.
- **Hybrid head `0x000E2D77`**: same `Head_004` mesh name, original
  topology and UV0 coordinates (no UV1 available), but different
  source vertex positions, normals and weights. Its 54-bone `Bip01`
  skeleton includes both a `CustomizationSkeleton` root and a separate
  `Bip01/mshBn_*` animation hierarchy.
- All 43 `mshCtrl_*/mshBn_*` joints in this hybrid have a matching
  name and parent relationship in skeleton `0x000020A2`. Their
  inverse-world bind matrix entries differ by no more than approximately
  0.000402, below the enforced compatibility limit of 0.0005.
- Its nine original skin bindings comprise five animation-bound names
  plus four customization-only controls (`H_jaw3`, `T_1`, `N_2`, `N`).
  About **96.5%** of the total source vertex weight is on the animated
  branch; 3.5% remains on the customization branch.
- Across seven previously extracted body pieces, 53 bone names occur
  in multiple original subset skeletons. Of these, 46 have inverse
  world matrices matching within 1e-5. The other seven vary, mostly
  around the head and neck, and cannot safely be merged unconditionally.

## Hybrid-head diagnostic

Run `./Launch-ESOTest.ps1 -HybridHeadLab`.

The lab reads the real hybrid head from the ESO archive and retains
its original vertices, UVs, topology, nine native skin bindings, weights
and 54-bone skeleton. Only the 43 animation joints with explicitly
validated names, parents and rest matrices receive original 60 Hz
`GEN_Walk` pose transforms evaluated by the installed Granny library.
Customization-only joints stay at their original rest pose.

The neutral material is explicitly diagnostic. The source mesh has no
embedded diffuse texture reference, so this does **not** reconstruct
the game's skin material, eyes, hair or facial customization. There is
also a visible deformation at the boundary of moving and stationary
skin influences, because ESO's final assembly/compositing stage has
not yet been recovered. Do not conceal this by guessing weights,
bone aliases or a synthetic gait.

## Head/neck placement audit

The native `GEN_Walk` clip is **not** a bind-pose T stance. In the original
`0x000020A2` source coordinates, at its first frame the source neck moves
approximately `X=-0.025, Y=-0.165, Z=+0.359` metres from its bind
position. The original source head moves approximately
`X=-0.050, Y=-0.181, Z=+0.400`. These are native Granny
world-pose positions, not offsets assigned by our renderer.

The **updated hybrid-head lab** shows the animated head and its
`mshBn_spine02 -> mshBn_neck -> mshBn_head` source joints in GOLD, and
the original native bind-position landmarks in BLUE. Press `N` to toggle
the source joint guides, `R` for the source rest landmarks. The HUD
shows the measured three-dimensional neck displacement each frame.
No geometry is manually translated to conceal the difference.

The Godot importer has now been checked against the *independently sampled
original native Granny world matrices for all 43 animation joints in all
70 `GEN_Walk` frames*, with a required maximum error of 0.003 metres.
No such difference occurs. So the apparent forward/low head position is
not caused by a misplaced head MeshInstance3D or a failed local-to-world
bone transform.

ESO's real idle clips also differ from its bind pose. Native
`1H_Idle` (`0x0011990D`) has a source neck position of approximately
`(-0.037,1.757,0.069)` metres at frame zero, compared with the native
bind neck at `(0,1.901,-0.085)`. Original `GEN_Walk` first-frame neck
is `(-0.025,1.736,0.274)`: about 20.5 cm further along source +Z
than that idle pose. Other tested walk families exhibit the same
general neck shift, but with different amplitudes. These recordings
do not establish ESO's in-game animation blending or IK behaviour.

## Source-backed tests

- `res://tests/Godot/eso_hybrid_head_smoke.tscn`: 404 original
  vertices, nine skin binds, matching source hierarchies and rest
  matrices, native pose changes, stationary customization controls.
- `res://tests/Godot/eso_hybrid_head_ui_smoke.gd`: actual source mesh
  display and pause/step/zoom interactions.
- Native Granny binding checks and full Auridon regression remain
  separate. No change to the live population renderer.

### Original head geometry variation

The authoring-source `Head_004` customization mesh
`0x000E2CFF` and hybrid source `0x000E2D77` have exactly
**404 vertices and 710 triangles**, identical vertex indices and UV
coordinates, but different positions, normals and skinning. A
vertex-by-vertex comparison of the extracted original positions finds a
mean **absolute component** difference of 0.004231 m, with a maximum
single-component difference of 0.024740 m. This is concrete evidence of
face-shape deformation encoded in the source geometry, not a constant
head-position offset. It does not identify the exact face customization
parameters responsible.

## Complete source-skinned humanoid-shaped diagnostic

ESO also contains **skeleton-external GR2 meshes**: the original mesh file
does not embed a skeleton. Its native binding names identify the source
bones to be supplied by a separate original skeleton model.

The new isolated viewer (`./Launch-ESOTest.ps1 -OriginalSkinnedBodyLab`)
uses **ESO-authored `VoriplasticCorpse`** (resource `0x0015D6CD`):
8,109 original vertices, 11,836 triangles, and 81 original skinned
bone bindings. The shipped Granny library reports that **all 81/81
bind names resolve exactly** to original `GrannyRootBone`
`0x000020A2`. Its related `Voriplastic_Goo` mesh (`0x0015D6CF`)
also binds exactly, **59/59**. These are game-authored creature assets,
not the ordinary customized Altmer, Breton, etc. humanoids we ultimately
need. The viewer uses the corpse alone as a proof of the
unmodified GR2 skeleton-external skinning path, and neutral diagnostic
material because the external source texture association is unresolved.

The viewer uses all 202 original bones and all 70 fully evaluated,
60 Hz native `GEN_Walk` pose samples. The original vertex/triangle
data, the original 81-name mesh skin table and all original GR2
vertex weights are transferred intact. Bind indices are explicitly
resolved by the **native Granny `GrannyNewMeshBinding` API** against
the original source rig before the viewer loads. Incompatible skeletons
are rejected; e.g. the exact same mesh resolves **0/81** names against
the original customization-only head and torso skeletons.

The Godot world-pose transforms from every original source bone on
every animation frame match Granny's independently evaluated source
positions, with a measured maximum difference below 0.000001 m.
This confirms the bone/mesh assembly and source animation pipeline for
a complete original mesh. It does **not** prove the game uses the same
rendering material, actor customization transforms, animation blends,
IK, or LOD selection.

Run the independent tests:
`res://tests/Godot/eso_original_skinned_body_smoke.tscn`,
`res://tests/Godot/eso_original_skinned_body_ui_smoke.gd`,
and `tests/check_native_character_binding.ps1`.

### Why this does not yet solve ordinary humanoids

The `--scan-character-assemblies` helper inspected **all 653**
`Bip01` GR2 model files available in the local indexed model-name
catalogue. It decoded all successfully. **597** have meshes
referencing `mshBn_*`, but only **one** model in this indexed set
has a single original skeleton containing both native `mshBn_*`
and `defBn_*` branches: the `Head_004` hybrid described above.
The normal source torso, arms, legs and head customization meshes
still bind to an incompatible `CustomizationSkeleton`, as proved
by the native API. They cannot simply be attached to the original
`GrannyRootBone`.

The assembly of customizable NPC bodies remains unsolved: need the
**actual game customization/deformation metadata and algorithm**,
not a hand-inferred `defBn_*` to `mshBn_*` map. This is why
the test keeps the world population renderer unchanged.

## Source-exact eight-piece humanoid assembly (601 family, 2026-11-02)

The first complete original humanoid customization **rest pose** is now
assembled in the isolated Godot scene
`./Launch-ESOTest.ps1 -OriginalHumanoid601AssemblyLab`.
This is a genuinely game-authored *601* body-part family, not a
procedurally built body, and **not yet a runtime-customized NPC**.

| Original GR2 asset | Source ID | Original vertices | Native skin bindings |
| --- | --- | ---: | ---: |
| `ArmsLower_601` | `001230D9` | 90 | 4 |
| `ArmsUpper_601` | `001230DB` | 218 | 10 |
| `Hands_601` | `001230DD` | 554 | 14 |
| `Torso_601` | `001230DF` | 1,462 | 22 |
| `Head_601` | `001230E1` | 1,443 | 38 |
| `LegsLower_601` | `001230E3` | 230 | 10 |
| `LegsUpper_601` | `001230E5` | 2,328 | 8 |
| `Feet_601` | `001230E7` | 426 | 6 |
| **`HAR_F_HU_0343` (hair)** | **`0029A87F`** | **2,491** | **10** |

The **eight body meshes** total **6,751 original source vertices**.
Their eight independently archived `CustomizationSkeleton` subset
hierarchies merge into **110 unique bones**, with **98 repeated bone
references** having identical named parents and inverse-world bind
matrices. The merger rejects any differing parent or bind transform; it
does not invent an intermediate `defBn_ -> mshBn_` mapping.
Godot preserves each source mesh's unmodified vertex weights and
native binding table, independently verifying rest-pose skin matrices.

A source-only hair scan tested the 265 archived `HAR_*` models and
found 14 candidates whose original skin binding names and inverse-world
rests match this merged customization skeleton within `0.00001`.
This is *only* a rest-matrix screen: several other candidates do not
pass native binding against `Head_601`. The selected
`HAR_F_HU_0343` (resource `0029A87F`) independently passes
**10/10 exact bone-name bindings** with Granny's native
`GrannyNewMeshBinding` API against `Head_601`, as well as 10
rest-matrix checks against the merged 110-bone rig. Its original
2,491-vertex mesh is shown in the neutral diagnostic viewer.

The rest scene has been GPU-rendered locally and shows a complete
source-authored character silhouette, pointed shoulders and long
skirt, with attached original hairstyle. Its grey colour is
**diagnostic only**, not ESO skin, hair, fabric or armour materials.
The source part family itself is stylised; no ordinary playable-race
identity or named NPC has been established for it.

Important negative checks:

- **`Head_600` must not be substituted**: all 44 overlapping facial
  bones exist by name, but original rest matrices differ by as much as
  about 4.195 in matrix component values. The new Godot source
  assembly smoke test rejects this cross-family combination.
- The 110 `601` customization bones and the separate 202-bone
  `GrannyRootBone` animation skeleton share **zero exact bone
  names**. The game-specific customization-to-animation bridge is
  therefore still required to make this ordinary body walk.
- We cannot identify genuine skin, eye, hair or armour DDS runtime
  references from these body-part GR2s alone.

Reproduce the source-built complete humanoid with:

- `res://tests/Godot/eso_original_humanoid_601_assembly_smoke.tscn`
  checks original geometry, all shared rest matrices, exact source
  skinning, original hair, native binding and rejected incompatible head.
- `res://tests/Godot/eso_original_humanoid_601_assembly_ui_smoke.gd`
  checks all nine actual mesh instances, the single merged source
  skeleton, skin-bind rests and orbit/zoom.
- The scene `res://scenes/eso_original_humanoid_601_assembly.tscn`
  is an isolated source inspection, not a replacement for Auridon NPCs.
- All derived mesh data, screenshots and decoder libraries stay
  under ignored `.research/`, not Git.

## Six original walking styles on the complete source-skinned creature

The `VoriplasticCorpse` source mesh (`0x0015D6CD`) is shown by UESP
under the in-game name **Voriplasmic Corpse**:
https://en.uesp.net/wiki/File:ON-npc-Voriplasmic_Corpse.jpg

Run `./Launch-ESOTest.ps1 -OriginalBodyWalkComparisonLab` for six
independently skinned copies of the **same original mesh**, one per native
walking animation:

| Clip | Original animation resource |
|---|---|
| `GEN_Walk` | `0x0004C319` |
| `1H_Walk` | `0x0000D200` |
| `1HShield_Walk` | `0x0002C3EF` |
| `2HS_Walk` | `0x0000D20E` |
| `2HL_Walk` | `0x00011BBB` |
| `Bow_Walk` | `0x0002DB0F` |

All six clips are sampled at 60 Hz through the installed Granny runtime,
preserving each original source duration. Each model uses **8,109 actual
ESO vertices, 81/81 native mesh-to-bone bindings, and its own 202-bone
skeleton**. No retarget mapping, synthesized motion, guessed weapons,
facial approximations, skin recolouring logic, or animation blending is
applied. A shared original `ArrayMesh` and separate original `Skin`/
`Skeleton3D` pairs provide six independently driven skinned figures.

The native pose smoke test compares all 202 bone origins against
Granny's independently sampled world positions across **every frame
of all six original clips**, with a maximum observed discrepancy
of approximately **0.0000011 metres**. The interactive regression
checks focus/compare, pause, original-source frame stepping, and zoom.

The reference image is an in-game appearance reference, not a source
material definition. This lab still uses a **neutral diagnostic
material**, because the real ESO Voriplasmic Corpse diffuse, normal,
specular and tint-mask binding IDs have not been recovered.

- `res://tests/Godot/eso_original_body_walk_comparison_smoke.tscn`
- `res://tests/Godot/eso_original_body_walk_comparison_ui_smoke.gd`

## Next blocker

Identify the **original ESO mechanism that combines
`CustomizationSkeleton` deformations with runtime `mshBn_*`
animation skinning**. A complete original torso and character
material mapping must then be verified before deployment to Auridon.
