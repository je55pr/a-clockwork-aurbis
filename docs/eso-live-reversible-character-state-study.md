# Read-only ESO character-selection memory: controlled state reversals

Captured on 2026-11-03 in an authorized, same-process ESO character-selection session. Research aims to link original humanoid meshes, skeletons and runtime material/texture assignments. This report records observations only; **nothing here establishes a material pointer, race field, hair choice or character identity**.

## Controlled character descriptions

The four characters were all female.

| Label | Race | Outfit | Pet | Mount | Additional |
| --- | --- | --- | --- | --- | --- |
| A | Breton | Witchy/vampy custom outfit with dyes | Black hare | Black horse | Custom outfit |
| B | Orc | Smithing/Dwemer custom outfit with different dyes | Dwemer spider | The same black horse as A | Flower hair adornment |
| C | High Elf | Default/unmodified riding corset outfit | Orange cat | Gloomspore bear | Same default outfit, pet and mount as D |
| D | Orc | Same default/unmodified riding corset outfit as C | Same orange cat as C | Same Gloomspore bear as C | Same flower hair adornment as B |

These descriptions are user-provided, not inferred from bytes. A/B share a mount; B/D share Orc race and flower adornment; C/D share outfit, pet and mount. Those joint changes are confounders.

## Collection and privacy

In-process executable: `eso64.exe`, PID `33672` during this session (ephemeral). All captures used read-only Windows process access. The large-region scanner hashed 16 KiB chunks from private, committed, writable, non-executable regions (roughly 3.5 GiB per scan). Follow-up snapshots hashed 4 KiB pages. Hashes and memory addresses are in ignored `.research/live-eso-selection/startup/second-run/`; **no raw process bytes or screenshots are committed**.

Each selected state received two snapshots to exclude pages changing while the selected character remained unchanged.

## A -> B -> A

Six 16 KiB wide snapshots were compared. A total of 221,645 blocks occurred in all six, of which 69 had stable hashes within all three A/B/A states, changed for B, and exactly recovered their original A hash. A targeted follow-up over 69 candidate 16 KiB blocks read 276 4 KiB pages (272 accessible in all six follow-up samples). **56 individual 4 KiB pages** passed within-state stability and exact A -> B -> A reversal. Their addresses are in `reversible-4k-confirmed.txt`.

## Four-character comparison

In the first fine scan, among those 56 A/B/A-selected pages, 54 had stable repeats for C and D. Of the 54, 14 had four distinct hashes, 15 had three and 25 had two. These pages were **preselected to differ between A and B** and thus cannot reveal a common A/B mount through identical hashes.

Later complete 16 KiB wide scans of C and D both succeeded. In a comparison of broad hashes from A, B, A-return, C and D, 207,071 blocks occurred and stayed stable within all five selections. Among blocks whose A hash recovered on A return, 6,634 had the pattern A=B and C=D. This **does not establish mount/outfit linkage**, since C/D were captured later and cache or loading history can also produce the grouping.

## D -> C -> D

Six 16 KiB snapshots were available across initial D, C and returned D:

| Criterion | 16 KiB blocks |
| --- | ---: |
| Readable across all six | 215,173 |
| Stable within all three selected states | 208,514 |
| Exactly the same throughout | 204,858 |
| C differs, returned D equals original D | **17** |
| C/D comparison did not recover original D | 3,639 |

The 17 reversible 16 KiB chunk addresses are in `D-C-D-reversible-16k-addresses.txt`. They **do not overlap** the 56 earlier A/B/A-selected 4 KiB pages.

A subsequent repeated **4 KiB** D -> C -> D capture examined all four pages within each of those 17 regions (68 pages total). In all six follow-up captures, all 68 pages were readable and stable within the selected state.

| Criterion | 4 KiB pages |
| --- | ---: |
| Identical in D, C and returned D | 52 |
| Reversed exactly from D to C and back to D | **10** |
| Stable but did not recover original D contents | 6 |

These ten page addresses are in `D-C-D-confirmed-4k-addresses.txt`. They total 40 KiB of page ranges, not necessarily 40 KiB of actual changing data.

Their **parent 16 KiB chunks**, cross-compared with earlier wide captures of A, B, C and D, had the following hash patterns, with label digits indicating equal or distinct hashes within the four characters:

- 3 parents: `0010` (A=B=D; C different). All three were stable within A/B and recovered original A on A-return.
- 1 parent: `0101` (A=C; B=D). It was stable within A/B but did not recover original A.
- 1 parent: `0121` (B=D; A and C separate). It did not meet earlier within-A stability or A-return.
- 5 parents: `0123` (all different). These did not recover initial A in the earlier wide samples.

**Important:** Parent 16 KiB hash patterns do not establish identical values in the corresponding 4 KiB subpage for A/B, as 4 KiB A/B snapshots were taken over different candidate regions. Some chunks are not 16 KiB-aligned to the absolute address grid; parent matching uses the actual recorded chunk start, not rounding down.

## Interpretation and next experiment

Exact reversible hashes are substantially stronger than changes inferred from two snapshots or file-name strings, yet they prove only that **some state reversibly tracks a character switch**. Neither structural pointers nor material bindings have been decoded. Character render state, shared pet/mount data, selection UI state, loaded data caches and other dependencies remain possible.

Best next controlled experiment: while the same ESO process is available, capture B twice then return to D and capture D twice for a **D -> B -> D** study, potentially distinguishing a shared Orc/flower correlation from the different outfits, pets and mounts. A more decisive follow-up would change exactly one pet, mount, outfit, or adornment while holding the same character selected, if the client allows it.

All research fingerprint TSVs, local image captures and internal process addresses are kept outside version control. No game assets are distributed by this report.
