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

## D -> B -> D, with independent long-term verification

B and D are both female Orcs and share a flower hair adornment, but have
different outfits, pets and mounts. The same ESO process was kept running.

The user captured the first B and final D snapshots locally using the
existing scanners when remote execution was blocked. All four final D files
were verified to exist before analysis. Captures are stored only in the
ignored local research directory.

The 16 KiB comparison uses the prior returned-D broad fingerprints, two
B snapshots, and two new returned-D snapshots:

| Criterion | 16 KiB blocks |
| --- | ---: |
| Readable in all six snapshots | 212,471 |
| Stable within each of the three states | 205,652 |
| Identical throughout | 201,336 |
| Exact D -> B -> D reversals | **280** |
| Stable in each state but not restored on D return | 4,036 |

Of these 280, **10** also belong to the earlier 69 A -> B -> A
reversible 16 KiB blocks; **none** overlap the 17 D -> C -> D
reversible 16 KiB blocks. The different experiments produce partially
disjoint candidate sets, not a unique actor record.

A deliberately restricted 4 KiB comparison of the 68 pages inside the
earlier 17 D/C candidate blocks found **2 exact D -> B -> D reversible
pages**, **56 unchanged pages** and **10 pages that drifted**. All 68
were stable within each selected state. The two reversible pages are
`0x18E272AD000` and `0x18E9058B000`. This limited page subset is
not representative of the entire broad 280-block candidate set.
A 4 KiB page may return to its original hash even if other pages in
the parent 16 KiB chunk do not, explaining why the fine and wide
reversal sets need not overlap.

We then matched the 280 D/B/D reversible 16 KiB chunks to widely
spaced earlier B and D captures (roughly one hour apart). **Only two
chunks** retained exact, distinct B and D values across all available
same-character observations, including the original D, intermediate
returned D, latest returned D, and two separated B captures:

- `0x18E4ACCC000`
- `0x18E91E90000`

This stronger temporal filter rejects many transient caches, changing
allocations and other confounding data, but the remaining two chunks
still have **no proven render-material or character-identity meaning**.
Their hashes differ between B and D, so the difference is not
explained by *only* the Orc race or shared flower adornment.

The 280 chunk addresses are preserved at
`.research/live-eso-selection/startup/second-run/D-B-D-reversible-16k-addresses.txt`,
outside version control.

## Interpretation and next experiment

Exact reversible hashes are substantially stronger than changes inferred from two snapshots or file-name strings, yet they prove only that **some state reversibly tracks a character switch**. Neither structural pointers nor material bindings have been decoded. Character render state, shared pet/mount data, selection UI state, loaded data caches and other dependencies remain possible.

The D -> B -> D controlled experiment is complete. The next step is a **narrowly scoped, read-only structural inspection** of the two long-term-stable 16 KiB candidate regions. Look for plausible pointers, object layout, and relationships to original skeleton/material resources without collecting credentials, chat, account data or unrelated process memory. Confirm any interpretation with a repeated switch before reporting it as fact. If the client allows a single trait (pet, mount, outfit or adornment) to change independently, that would be an even more discriminating test than switching entire characters.

All research fingerprint TSVs, local image captures and internal process addresses are kept outside version control. No game assets are distributed by this report.
