# ESO character-selection capture forensics

Analysed 2026-11-03 from two read-only captures made on 2026-11-02.
This is **offline source-asset research**, not evidence of game-runtime
draw-call-to-material bindings.

## Scope and retained evidence

The source evidence lives exclusively in ignored
`.research/live-eso-selection/`:

- Two ESO-only `PrintWindow` images of different selected characters,
  plus local reduced-size previews.
- `selection-baseline-assetpaths.tsv` and
  `selection-steady-assetpaths.tsv` from the first ESO process.
- `selection-new-process-assetpaths.tsv` and
  `selection-new-steady-assetpaths.tsv` from the restarted process,
  with a different selected character.
- Each TSV contains **only original character-art texture source paths
  and their transient memory addresses**. Arbitrary process memory,
  authentication values, account identifiers and game-file bytes were
  not saved in the source repository.
- The user closed ESO after the second capture. No same-process
  character switch was captured; process restarts confound comparisons.

No screenshots, process memory, generated DDS or extracted game data
are committed.

## Path grouping result

Both independently repeated static baselines were exactly stable:

| Measure | First selection | Second selection |
| --- | ---: | ---: |
| Original source-art paths | 92 | 92 |
| Distinct texture families | 24 | 24 |
| Complete diffuse/normal/specular/tint (D/N/S/T) families | 21 | 21 |
| Other, incomplete creature/equipment families | 3 | 3 |
| Changes between consecutive scans of *same* selection | 0 | 0 |

Across the two different game processes, **64 paths are common**:
**14 complete four-map families** (56 paths) and eight other paths
in three families (three bear-mount, three domestic-cat, two horse-saddle
textures). The other **28 paths per process belong to exactly seven
complete four-map families per capture**.

The source-art filenames are grouped into conventional
`*_D.tga`, `*_N.tga`, `*_S.tga`, and `*_T.tga` sets. Case
variations in the path and suffix are immaterial.

### Changed texture families

| Category | First process only | Second process only |
| --- | --- | --- |
| Hair | `HAR_M_HU_3210` | `HAR_F_HR_7710` |
| Adornment | None | `ADR_F_HU_1112` |
| Armour | `BlackreachExHeavy_A_U`, `BlackreachExMedium_A_U`, `BosmerMedium_A_U`, `TelvanniLight_A_U`, `WaywardGuardMedium_A_U` | `AltmerMedium-HB_D_U`, `BretonHeavy_D_U`, `BretonLight_A_U`, `NorthlandsAlchHeavy_A_U` |
| Weapon | `Nord_Bow_004` | `Breton_Sword_004` |

The screenshots show staff-bearing characters while a bow-material
family appears in one process and a sword family in the other.
There are also multiple unrelated armour families per selected
character. **Presence of a source-art string in the game's cache
does not prove that the visible character uses that material.**
The changed families might include asset preloading and restart
effects, not just selected-character differences.

None of the 184 path observations describes source art under a
skin, eye, or face texture directory. **This is a limit of the
bounded character-art path scan**, not proof that ESO has no such
textures or that their resources are not loaded elsewhere.

## Cross-check with the real archived GR2 materials

The original ESO asset-name index covers 40,367 successfully named
GR2 sources. The following capture filenames match original model
names **exactly**; each corresponding Granny material independently
names its own D/N/S/T source artwork paths:

| Source material basename | Original GR2 mesh ID | Source provenance |
| --- | --- | --- |
| `HAR_M_HU_3210` | `0002E5E3` | First-only hair set |
| `HAR_F_HN_3143` | `0003F837` | Present in both processes |
| `HAR_F_HO_3281` | `0004070F` | Present in both processes |
| `HAR_F_HU_7700` | `0004AAF5` | Present in both processes |

`HAR_F_HR_7710` and `ADR_F_HU_1112` were **not**
found under those exact names in the locally indexed 40k GR2 models;
that does not prove the game has no corresponding mesh.

Two independently archived `TelvanniLight` pieces,
`001086E3` (helmet) and `001086B5` (belt), both have native
Granny `diffusePath`, `normalPath`, `specularPath` and
`tintmaskPath` entries exactly matching the first capture's
`TelvanniLight_A_U_D/N/S/T` paths. This confirms that **one
authoring texture family may be shared by several different models**.
There are several plausible DDS quartets nearby in archive order;
that proximity alone does not identify the authoritative runtime
texture resource IDs.

## Four new original hairstyle DDS candidates

The actual GR2 `HAR_M_HU_3210` mesh `0002E5E3` independently
names the same original D/N/S/T artwork paths recovered in the
first capture. Four immediately preceding genuine ESO DDS resources
are an internally coherent set:

| Suspected role | Archive resource | Actual DDS format and dimensions |
| --- | --- | --- |
| Diffuse with hair cutout alpha | `0002E5DB` | DXT5, 576 × 256 |
| Native packed normal | `0002E5DD` | DXT5, 576 × 256 |
| Specular | `0002E5DF` | DXT5, 576 × 256 |
| Hair tint mask | `0002E5E1` | Uncompressed DDS, 576 × 256 |

The source-only local Godot inspection decoded all four files.
The first texture visibly contains hair-strand artwork and an
alpha range of 0–255. The second's decoded mean RGB is approximately
`(0, 0.503, 1)` with mean alpha `0.498`, consistent with the
original ESO AG packed-normal convention. The third is predominantly
greyscale and the fourth is predominantly a red-channel mask.
The original 32-bit BGRA tint candidate `0002E5E1` has almost
exclusively black-or-white red values, near-zero green/blue,
and **constant zero alpha**, unlike the related original
`0029A87D` tint candidate, whose alpha is **constant 255**.
Neither alpha value can safely be assumed to represent actual dye
intensity. This reproduces the same source layout previously seen
near other original ESO hairstyles.

**Evidence classification: strong structural + original-art-path
association, still provisional.** No captured draw call or native
actor material record proved a mapping between this GR2 and these
four DDS resource IDs.

The original hairstyle is not directly reusable on our current
high-detail head gallery: native `GrannyNewMeshBinding` accepts
only **12/13** original skin names against each of
`Head_600`, `Head_008`, `Head_601` and `Head_004`.
The other two tested heads `Head_502` and `Head_503`
match only **6/13**. No missing customization bones have been
remapped or fabricated. The three other captured and indexed original
hairstyles also fail full native compatibility with the tested
`Head_600` and `Head_601` skeletons. Their source artwork
paths are real, but they are **not validated plug-in replacements**.

## Controlled A/B/A/C/D selection study (2026-11-03)

A later [four-character same-process memory experiment](eso-live-four-character-state-analysis.md)
recorded startup-to-selection asset paths, five ESO-only screenshots,
and 562-page read-only metadata-region fingerprints. The 76 original
source-art paths and their memory addresses stayed unchanged through
30 consecutive non-empty scans. After filtering natural within-character
changes, **no tested page uniquely identified all four selected
characters**. Material-string neighborhoods must not be mistaken for
active runtime actor/material references.

## Follow-up: source Telvanni Light armour atlases (2026-11-03)

The captured `TelvanniLight_A_U_D/N/S/T` paths now lead to a
[source-backed four-channel Telvanni material lab](eso-original-telvanni-materials.md).
Granny verifies these original artwork paths and actual primary, secondary,
and accent dye colours on four genuine equipment meshes. Two 576×256
D/N/S/T archive quartets produce coherent original UV material placement
on helmets, belt and shoulders, but **runtime material-ID links remain
provisional**. The `TelvanniLight_Tassets_A` mapping was explicitly
**rejected** after both candidate atlases sampled transparent/empty
source UV areas. No equipment has been attached to the existing
incompatible humanoid body skeletons.

## Practical consequences

1. **Model a four-channel material bundle.** Character hair, body
   equipment and adornments expose D/N/S/T artwork references.
   The existing source hair shader now accepts all four actual
   candidate DDS textures, including strict decoding of original
   **uncompressed BGRA8** tint masks. **`Y` toggles a red-channel
   grayscale diagnostic** on genuinely skin-compatible source hair,
   but does not guess the original game dye or substitute alpha
   for the red mask. `T` still compares source and neutral hair.
2. **Resolve material filenames to shipped DDS IDs separately.**
   Granny authoring paths, model file IDs, neighbouring DDS
   quartets and compressed-channel inspection give strong
   candidates but not authoritative runtime assignments.
3. **Do not infer the currently drawn character from a memory-path
   scan.** The two snapshots were taken in different processes, and
   the screenshot/material discrepancies independently show cached
   or otherwise inactive resources.
4. **Keep skin/eye discovery separate.** No face/skin/eye art paths
   appeared in this bounded path filter, so it does not solve the
   grey-face problem.
5. For a future live session, the missing experiment is a stable
   same-process **A → B → A character selection** with successive
   read-only path inventories and, if supported and permitted, an
   explicitly scoped GPU draw-call capture. Verify the PID remains
   constant, the window identity is ESO, and no session information
   is recorded. The earlier Windows desktop capture that targeted
   another foreground application was discarded.

## Reproduction on the locally installed game archives

Run the existing source-only helper under
`C:\Repos\a-clockwork-aurbis-godot-lab`:

```powershell
$granny = 'C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'
$manifest = 'C:\ESO\The Elder Scrolls Online\depot\eso.mnf'
$tool = 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
dotnet $tool --audit-character-material $granny 0002E5E3 $manifest .research\ooz.dll
dotnet $tool --audit-resource-neighbors $manifest .research\ooz.dll 0002E5E3 30
dotnet $tool --inspect-native-external-binding $granny 0002E5E3 001076DD $manifest .research\ooz.dll
```

The new source-only `data/eso_captured_hair_material_candidates.tsv`
tracks the provisional `0002E5E3` to four-DDS relationship.
`tests/check_original_captured_hair_quartet.ps1` checks all
four of the original GR2 authoring file paths, the three DXT5 images,
the original BGRA8 tint image and the deliberately incompatible
12/13 Granny binding to `Head_600`. The Godot headless test
`res://tests/Godot/eso_original_captured_hair_tint_smoke.tscn`
checks all three mapped original hair models, mask-channel ranges,
source image dimensions and shader diagnostic mode. The captured
`3210` hairstyle is **not** silently attached to a head.

The original candidate DDS bytes, debug contact sheet, derived
local GPU tint-mode captures and original selection screenshots
remain under ignored `.research/live-eso-selection/` only.
