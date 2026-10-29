# Original ESO Granny2 model naming research (27 October 2026)

## Status and why it matters

ESO's depot contains roughly **54,055 source records linked through FAFAEBEB global metadata category 49** but most are identified by numeric archive IDs rather than readable paths. This limits the asset browser's ability to distinguish buildings, weapons, vegetation, creatures and actors.

**Verified breakthrough:** Existing UESP extractor source code has a proven method for recovering meaningful original Granny model names from within compressed GR2 resource data. We reproduced the logic in a **read-only disposable .NET probe**, operating on the current local ESO installation. The recovery works independently of the ESO client process, including during the partial depot update where the selected DAT archives are already available.

### Verified archive samples

| Original archive ID | First model / mesh name recovered | Additional details |
|---|---|---|
| `0x0028F157` | `ROK_BAD_DeadlandsLGDoor001` | Two original animation names: `HoldClose`, `Open` |
| `0x002938BB` | `SYS_STR_GuildMages001` | 41 texture references, 78 materials, 1 mesh and 1 model |
| `0x002BAC6F` | `CHL_STR_SKGBuilding012` | 48 texture references, 100 materials, 1 mesh and 1 model |
| `0x0001D21D` | `VEG_BSH_RiftBush001` | A recognizable bush model |
| `0x0015C9C1` | `ROK_ELS_Cliff010` | A recognizable cliff model |
| `0x0011BAD1` | `TRE_SUM_RedMangrove002` | Original animation name `Idle` |
| `0x0001EE39` | `NOR_EXC_LightBoots001` | Equipment-related mesh candidate; not yet linked to actual item definition |

All these were recovered from the **internal GR2 model/mesh/animation data**, not inferred from icon filenames or nearby DDS file IDs.

A deterministic read-only probe selected 101 available metadata-49-linked asset records with decoded sizes 20 KB–1.8 MB from depot archive indices 95–125 (distributed across the source manifest). **100 records decoded as Granny2 models, and all 100 had nonempty recovered model, mesh or animation names**. All 100 had numeric `FromFileName` strings, showing why the original file names are lost in the manifest-derived name index. One additional record did not have the expected GR2 magic; no decoder/model parsing failures occurred in this particular selection. **This is not an unbiased estimate for the full 54,055 records**; other archives, compressed variants or missing/partial DATs may have lower recovery rates.

Logs (untracked, ignored): `.research/model-name-sample.log`, and the disposable script `.research/Probe/Program.cs`. No game assets were exported or modified.

## Recovery method and read-only implementation notes

In the preexisting UESP research clone, `.research/uesp-esoapps/common/EsoMnffile.cpp`, `CMnfFile::SaveSubFileGR2` uses `GrannyReadEntireFileFromMemory`, `GrannyGetFileInfo` and `GrannyFreeFile`. It first examines `granny_file_info::FromFileName`; if it is empty or all numeric, it falls back to the first `Model->Name`, first `Mesh->Name`, and first `Animation->Name`, prepending type labels to differentiate files. UESP's source credits Kriskras99 for the name-recovery technique. Supporting ABI definitions are in `.research/uesp-esoapps/common/granny/granny211.h`.

Our isolated C# research code can call the locally available x64 `granny2_x64.dll` through `NativeLibrary.Load` / `GetExport` without writing the source file to disk: pass a pinned buffer to `GrannyReadEntireFileFromMemory`, then read `GrannyGetFileInfo` and release it via `GrannyFreeFile`. This is **research tooling only**, not production code or a commitment to ship the Granny DLL. The first probe encountered an access violation because the foreign `granny_file_info` struct is **packed**, not default-aligned. The corrected x64 offsets, validated by coherent model/mesh names in independent source records, are:

| Field | Count offset | Array pointer offset |
|---|---:|---:|
| `FromFileName` | — | 16 |
| Textures | 24 | 28 |
| Materials | 36 | 40 |
| Skeletons | 48 | 52 |
| VertexDatas | 60 | 64 |
| TriTopologies | 72 | 76 |
| Meshes | 84 | 88 |
| Models | 96 | 100 |
| Animations | 120 | 124 |

For model/mesh/animation names, dereference the first array element pointer and read its first pointer member (the name string). **Do not use the default C# struct field packing for this binary layout.** When integrating, use isolated tests, bounds/sanity checks and a clearly compatible decoder. Preserve provenance: model name recovered from file internals is different evidence from a game-defined weapon, NPC, friendly/hostile classification or spawn record.

## Practical next work after this test

1. Add a **derived model-name index** to the asset browser, with `resourceId`, `original internal model name`, `mesh names`, `animation names`, and file provenance. Lazy or incremental decoding is important because opening tens of thousands of models at application startup would hurt responsiveness. **Do not bundle or redistribute external SDK DLLs** without separate rights/licence review; consider a pure managed GR2 parser or separate user-provided conversion backend.
2. Sample across **all available source archives** when the depot finishes downloading. Detect and handle variant GR2 signatures, non-model files, truncated/failed Oodle streams and multiple mesh/animation names, and measure real coverage before assigning categories.
3. Use internal naming prefixes (`VEG_`, `TRE_`, `ROK_`, etc.) as **search/filter hints** only. Do not label `CRE_` a game creature automatically: one sample (`CRE_EXC_MINLyre001`) was a decorative lyre object. Verify actual creature rig/archetype metadata before assigning `Enemy` or `Friendly NPC`.
4. Use Granny model metadata to associate materials, textures, skeletons and animations, then find a mapping to named game items/actors and actual world placements. This yields a **model-centric asset explorer**, not a fabricated gameplay entity database.
5. Separately, investigate offline addon metadata and public ESO item-name catalogues for **label enrichment**, making clear that public item IDs/icons and private archive model IDs are not automatically a shared key.

**Source:** UESP `uesp/uesp-esoapps`, `common/EsoMnffile.cpp` (local clone). Readme documents GR2 original name reconstruction via the Granny DLL: https://github.com/uesp/uesp-esoapps/blob/master/EsoExtractData/EsoExtractData/ReadMe.txt

No project viewer code was modified during this research pass, and the depot remained read-only.
