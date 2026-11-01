# ESO character-face construction: original source evidence

Status: **research prototype, NOT complete Auridon NPC rendering**.
Derived local geometry, animation files, DDS assets and native libraries stay
under ignored `.research/` and the user's installed ESO depot. Do not commit
or redistribute original ESO meshes, screenshots or proprietary game DLLs.

## Original face assemblies

The read-only face gallery uses original GR2 model source assets without
procedural features, guessed morphs or remapped face bones.

| ESO asset | Original mesh | Vertices | Skin bindings | Face rig |
| --- | --- | ---: | ---: | --- |
| `0x000E2CFF` | `Head_004` | 404 | 6 | 39 bones |
| `0x001076DD` | `Head_600` | 6,530 | 43 | 49 bones |
| `0x001230E1` | `Head_601` | 1,443 | 38 | 44 bones |
| `0x00129B5D` | `Head_008` | 1,721 | 38 | 44 bones |
| `0x000DEE57` | `Head_502` | 890 | 35 | 42 bones |
| `0x000DEE59` | `Head_503` | 866 | 43 | 50 bones |

The archive also contains another `Head_008` source (`0x0012A5A3`,
1,548 vertices/36 skin bindings), plus `Head_010`, `Head_011`
and `Head_012` assets.

These are real **facial structures**, not just blank skulls.
`Head_600` includes controls such as `H_eyeball_L/R`,
`H_eyelid_Hi/Low_*`, `H_teeth_*`, `H_mouth_*`,
`H_nose*`, `H_eyebrow_*`, `H_ear_*`, jaw and cheek controls.
The more compact `Head_004` lacks most of these. The head labelled
`Head_008` is visibly pointed-eared and a promising elven geometry
candidate, but we have **not yet established its race and sex mapping
from ESO's own appearance records**. `A_H_*` and `K_H_*` facial rig
prefixes suggest distinct Argonian and Khajiit systems; those species
assignments need independent source mapping before being used as data.

The local name index contains **40,369 surveyed unlinked source IDs**.
It is a partial archive catalogue, not proof that other head models
don't exist.

## Verified original head + hair

Two *different* original heads have a complete source-exact compatible
binding for the same original hairstyle:

- `Head_600`, resource `0x001076DD`
- `Head_008`, resource `0x00129B5D`
- `HAR_M_HU_0343`, resource `0x0029B2AF`, **2,841 vertices**,
  **12 bone bindings**, authored within an original 17-bone subset rig

The installed native Granny library independently matches **12/12**
hair skin bindings to both target head skeletons. For all twelve
bindings, the original hair and target-head inverse-world rest
matrices agree within **0.00001** (observed max difference zero at
the available precision). We attach the *unchanged original hair
vertices, weights and inverse matrices* to the existing source
`CustomizationSkeleton` without any hand offsets or bone aliases.

**Crucial negative test:** the same hair binding names occur in
`Head_601`, but its original bind matrices differ by up to
**0.258830 m** at `H_forehead`, so that pairing is **rejected**.
A separate `HAR_F_HU_0343` hair/other `Head_008` pairing showed
a similarly incompatible source-rest difference. Same names are
not enough: only the fully matching rest pose is safe.

The gallery's actual mesh screenshots have been inspected, and the
hair is visible attached to the two compatible faces. No invented
facial features or animation corrections are applied.

## Where the colour and detail come from

`--inspect-face-colors` examines the original Granny
`DiffuseColor0` member of each head's packed source vertex format,
not the Godot diagnostic material. The test inspected the original
404, 6,530, 1,721, 1,548, 890 and 866 vertex heads.

**Every tested source vertex has white `RGBA=(255,255,255,255)`.**
The original source records also report **zero embedded textures**.
Some contain an embedded material record, but it does not provide
the full game texture/skin material selection.
Thus the correct skin, eyes, eyebrows and lip colour cannot
be recovered from the vertex colours. The live game assigns appearance
through **external material/texture and customization records which we
have not decoded**. Do not fill these gaps with guessed skin tones,
cartoon face spheres or generic stock textures and call it authentic.

## Try the original-face research viewer

`./Launch-ESOTest.ps1 -OriginalFaceGallery`

Six original source-skinned faces. The 6,530-vertex `Head_600`
and 1,721-vertex `Head_008` both have a fully source-compatible
hairstyle. Press **1–6** to focus an individual head, **0** to
compare all, drag to orbit, wheel to zoom. Neutral materials are
diagnostic and intentionally lack ESO's original skin textures.

Tests:

- `res://tests/Godot/eso_original_face_gallery_smoke.tscn`
- `res://tests/Godot/eso_original_face_gallery_ui_smoke.gd`
- `tests/check_original_face_colour_streams.ps1`
- Existing `tests/check_native_character_binding.ps1`

## Requirements before promoting this into Auridon NPCs

1. Recover original **head material / skin and eye texture assignment**
   resources. Source GR2 is not enough; `DiffuseColor0` provides no
   missing colour.
2. Recover actual **appearance configuration** (race/sex, face shape
   sliders, hair, complexion, eye colour, facial markings) and the
   mapping from those IDs to specific named NPCs. The existing UESP
   population JSON carries race/gender and lore, not ESO's complete
   per-NPC appearance configuration.
3. Reproduce the game's **CustomizationSkeleton -> runtime animation
   rig** assembly using source records, without guessed bone names or
   proportional offsets. The hybrid `Head_004` test proves only
   5/9 head skin bindings animate without this missing stage.
4. Only after one complete character has **correct source face,
   materials, body geometry and gait** should we replace the
   provisional Auridon NPC renderer. Preserve the original static
   instance batching for distant NPCs.

The currently shipped Auridon visual layer remains untouched by
this isolated research prototype.
