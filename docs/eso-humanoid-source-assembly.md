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

## Next blocker

Identify the **original ESO mechanism that combines
`CustomizationSkeleton` deformations with runtime `mshBn_*`
animation skinning**. A complete original torso and character
material mapping must then be verified before deployment to Auridon.
