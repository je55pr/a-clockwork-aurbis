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

## Source-backed tests

- `res://tests/Godot/eso_hybrid_head_smoke.tscn`: 404 original
  vertices, nine skin binds, matching source hierarchies and rest
  matrices, native pose changes, stationary customization controls.
- `res://tests/Godot/eso_hybrid_head_ui_smoke.gd`: actual source mesh
  display and pause/step/zoom interactions.
- Native Granny binding checks and full Auridon regression remain
  separate. No change to the live population renderer.

## Next blocker

Identify the **original ESO mechanism that combines
`CustomizationSkeleton` deformations with runtime `mshBn_*`
animation skinning**. A complete original torso and character
material mapping must then be verified before deployment to Auridon.
