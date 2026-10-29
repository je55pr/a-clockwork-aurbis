# Local Granny2 model-name indexer

ESO's depot contains tens of thousands of original model records whose manifest filenames are missing or numeric. This tool recovers **internal original GR2 model, mesh and animation names** into a small, editable derived-metadata index. It never writes to game archives or exports models. The Godot **Model records** category automatically uses matching indexed names, so users can search by readable GR2 names.

## Requirements

- .NET 8 SDK and the installed ESO depot.
- A working Oodle-compatible decoder already configured for this project, such as `.research/ooz.dll`.
- **A separately provided, compatible 64-bit Granny2 DLL** exposing `GrannyReadEntireFileFromMemory`, `GrannyGetFileInfo` and `GrannyFreeFile`. The repository does **not** contain, distribute, or install the proprietary native Granny SDK. The previous research clone's compatible `granny2_x64.dll` was used only in a local experiment. Check your rights to use any third-party DLL before running it.
- Read-only access to `depot/eso.mnf` and DAT files. During the large game update, late DLC archives may be incomplete. **The index is optional**; the browser works without it.

From `C:\Repos\eso-test`, for example:

```powershell
dotnet run --project tools/ModelNameIndexer -- `
    --manifest "C:\ESO\The Elder Scrolls Online\depot\eso.mnf" `
    --oodle-dll ".research\ooz.dll" `
    --granny-dll "C:\path\to\a\compatible\granny2_x64.dll" `
    --output ".research\model-names.tsv" `
    --limit 500
```

Run the same command again to pick up the **next** uncached batch. Specify `--archive-min 105 --archive-max 117` to concentrate on the original model-heavy archives, or omit these filters to inspect all available archives. `--limit` caps the number of newly attempted source records per invocation, not the number of successful models. `--retry` forces rechecking `NOT_GR2` cache entries; transient `ERROR_*` failures are retried by default. Existing successful GR2 reads remain cached as long as their archive index, DAT offset and stored/decoded sizes match.

The default output path is `.research/model-names.tsv`, ignored by Git, which stores **only text-based derived identifiers and internal names**. Each row includes the original numeric ID, originating archive/offset, source sizes, status, embedded source name, first model, first mesh, up to 16 animation names and model/mesh/animation counts. Strings use base64 UTF-8 solely to keep tabs and newlines unambiguous. **No model binary, DDS, animation geometry or copyrighted file content is stored**.

Godot reads the cache on startup via `EsoModelNameIndex`, validates each row against the current manifest's record fingerprint and overlays recovered names onto the original **Model records** category. Where the GR2 has only a generic name such as `MainHand_001`, the browser retains its eight-digit resource ID to distinguish multiple records. In the details pane, the source metadata remains labelled as **internal model names**, not named player items or NPC identities.

## Verification and caveats

During the 27 October 2026 download, the following was independently validated:

- The first 250 archive-105+ model-related records contained **248 decodable, named GR2 files**, two non-GR2 records, and zero decode errors.
- A second batch decoded **1,500 further names** without errors, yielding 1,748 GR2 names in the Godot browser. Searching `ArcaneBMaster` returned the source record `0x0029A89F`, with internally recovered `ArcaneBMasterADR_Helmet_A`; the 1280×720 UI screenshot and provenance were checked.
- A further full batch of 8,000 model records yielded **7,999 GR2 names** and one additional decode error. The local model cache therefore holds **9,747 verified GR2 reads**, **two non-GR2 records**, and **301 retryable decode failures** in the current incomplete depot.
- A probe in late archives 240–257 found **300 transient decode failures** while the depot was actively updating. They were kept as `ERROR_*` cache rows, not mislabelled GR2 names. They can be retried later.
- The 54,055 currently linked model records are concentrated in archives **105–117**, with much smaller late-DLC subsets. Logs reside under ignored `.research/`.

The indexer is a **separate opt-in command-line tool**, never loaded into the Godot game process, because a native DLL's ABI mismatch can cause an access violation. In this machine's compatible Granny build, `granny_file_info` uses packed x64 field offsets: `FromFileName=16`; model count/pointer `96/100`; mesh count/pointer `84/88`; animation count/pointer `120/124`. The scanner rejects unreasonable counts but a corrupt native model/DLL still requires process isolation. On a crash, re-run; completed lines are flushed and remain usable.

**No inference of hostile/friendly NPC identity is made from prefix codes**. For instance, `CRE_` may refer to decorative objects, and many equipment models have generic mesh names. Finding actual weapon/gameplay definitions and NPC archetypes is a separate evidence-linking step.

Implementation: `tools/ModelNameIndexer/Program.cs`, `scripts/EsoModelNameIndex.cs`, and `scripts/EsoAssetCatalogue.cs`.
