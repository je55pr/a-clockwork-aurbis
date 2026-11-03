# ESO character-selection A/B/A/C/D live-memory comparison

Captured 2026-11-03 with the user's explicit permission for **read-only**
inspection of a running ESO client. The research targets original character
material and customization relationships, not account/session metadata.

## Captured controls

- The same `eso64.exe` process (PID `19612`) persisted through all
  four character states. Pet and mount appearance could also change when a
  character was selected, so a difference cannot be attributed to only the
  humanoid.
- Game-only screenshots were taken for the first character (A), second (B),
  A on return, third (C), and fourth (D). These images stay under
  ignored `.research/live-eso-selection/startup/`.
- The source-material path watcher started before the game executable
  appeared. The game's working-set size grew from about 200 MiB to 4 GiB;
  original character material paths first appeared once it reached roughly
  3.9 GiB. The 35 bounded inventories were stable at **76 art paths** from
  the initial fully loaded selection onward. Their first observed addresses
  did not move during the controlled switches.
- Original asset-name strings in these scans described hair, equipment,
  weapons, pets, mounts and saddles. **No original skin, face, eye, or head
  texture source names** passed the bounded character-art path filter.
- Across **30 consecutive non-empty inventories**, all 76 source-art paths and their recorded addresses were unchanged. Five earlier startup inventories were empty.
- Repeat fingerprints covered **562 readable 4 KiB pages** near known
  `CustomizationSkeleton`, `ZosMaterial`, `GrannyRootBone`,
  and other archived material/skeleton string addresses. The actual bytes
  were neither saved nor committed. The archived TSV files contain **only
  page addresses and SHA-256 digests**, not raw memory.

## Four-state fingerprint analysis

`A`, `C`, and `D` each had a second same-character repeat sample
to estimate unrelated memory activity. The `B` sample lacked a repeat.

| Measurement | 4 KiB pages |
| --- | ---: |
| Readable pages compared in all states | 562 |
| Stable in both A samples | 508 |
| Stable in both C samples | 506 |
| Stable in both D samples | 500 |
| Stable across every A/C/D *within-character* control | **495** |
| Identical across A/B/C/D | **482** |
| Different in all A/B/C/D states | 58 |
| Different in all four states **and** stable within A/C/D controls | **0** |

Of 495 pages stable within their A/C/D controls, 482 were identical
in all four states. The remaining 13 had these **non-unique**
state patterns:

- **7** pages: A=C=D, B different (one-time B load/temporal event possible)
- **4** pages: A=B=C, D different (D-related or later temporal event possible)
- **2** pages: A=B, C=D (later temporal event or shared character data possible)

**Conclusion: no page in the original metadata-string neighbourhood
is demonstrated to identify four individual characters.**
The earlier 17 "stable candidates" from the A/B comparison were not
proof of actor state. The proper within-state control tests falsified
that interpretation. The source-art paths remain excellent for
original material discovery but poor for tracing selected-character
material assignments.

## Planned next experiment

The existing local read-only scanner
`.research/live-eso-selection/EsoReadOnlyPrivateMemoryFingerprint.cs`
compiles and is prepared for a broader comparison of committed,
non-executable, privately writable regions. It writes **only SHA-256
hash prefixes for each 16 KiB block**. Its source was compiled but
**not run against ESO** in this session: the game process had exited
after the D screenshots and targeted page snapshots were captured.
Do not treat its untested scanning performance or proposed method as
validated results.

For another controlled session, establish two same-character full
private-memory baselines first, then take a B sample, then return to
A and repeat. Look specifically for pages whose state reverses with
A/B/A, while discarding pages that change during within-character
controls. If viable and computationally reasonable, expand to C/D
only after the A/B/A control succeeds. The pet and mount load events
remain confounders. Follow any surviving pages with more narrowly
scoped structural analysis, preferably anchored in known original
model, material, and skeleton memory relationships.

The original ESO process was never modified. No network/session
data, original game resources, screenshots or raw memory are
included in this repository.
