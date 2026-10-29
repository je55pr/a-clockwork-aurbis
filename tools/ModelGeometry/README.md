# ESO original GR2 geometry preview

The **Asset Library → Model records → Preview source 3D geometry** action renders a selected original ESO mesh in a separate, navigable 3D inspector. Drag to orbit, use the mouse wheel to zoom, and click **Close** or press **Esc** to return to the browser. The viewer also works for model records whose original internal name has not yet been indexed.

## Original source, derived preview

The Godot application does **not** load a Granny SDK DLL. Instead, `tools/ModelGeometry/Program.cs` runs in an isolated .NET subprocess and decodes the selected original depot GR2. It obtains source model meshes through the compatible Granny2 API and converts their vertex positions to a canonical P3 XYZ layout, using `GrannyCopyMeshVertices` and `GrannyCopyMeshIndices`. The data is checked for finite positions and valid triangle indices.

The helper writes an **ESM5** binary `.esm` cache into ignored `.research/model-previews/`. It contains derived geometry metadata: mesh names, source positions, original UV0/UV1 coordinates, reconstructed source normals/tangents and validated-frame flags, triangle indices, original material groups, and source **diffuse, normal, detail, diffuse2 and specular** resource IDs, plus typed original authoring flags, material/alpha type, sidedness, and secondary gloss/Fresnel/glow constants. The `-v5` cache suffix prevents older, incompatible ESM1/2/3/4 previews from being reused. The cache filename includes the source resource ID, DAT archive, offset, stored byte size, hash and decoded byte size to avoid stale results when ESO updates. Godot additionally validates buffer lengths, finite positions and index ranges before constructing an `ArrayMesh`.

The inspector automatically centres the geometry. It renders the **original diffuse DDS and matching packed normal DDS** for each recoverable source material group. Normals and tangent frames are decoded from the original GR2 and follow the client shader's signed-1.15 packing and alpha/green normal-channel rules. Unavailable DDS or unverified source frames fall back without fabricated mappings. The model remains static. **Specular, tint, transparent, and secondary material variant rules are incomplete.** See [model shader investigation](../../docs/MODEL_SHADER_RESEARCH.md). The material assignment is original; Godot's lighting and neutral fallback are not ESO shader parity. An equipment model may be only one equipment component rather than the complete in-game item, and a recognizable prefix or mesh name is **not proof of hostile/friendly NPC identity**.

### Optional local native dependency

The local CLI requires a **compatible, separately supplied x64 Granny2 DLL** exporting these symbols:

- `GrannyReadEntireFileFromMemory`, `GrannyGetFileInfo`, `GrannyFreeFile`
- `GrannyGetMeshVertexCount`, `GrannyGetMeshIndexCount`
- `GrannyCopyMeshVertices`, `GrannyCopyMeshIndices`
- `GrannyP3VertexType` (exported pointer to canonical P3 vertex layout)
- `GrannyGetMeshVertexType`, `GrannyGetMeshVertices`, `GrannyGetTotalObjectSize` (inspect original packed UV0 source format)

No Granny DLL is bundled or redistributed by this repository. Set `ESO_GRANNY_DLL` to the compatible DLL you are authorised to use. On the developer machine, the helper can also use the compatible DLL from the existing *ignored* `.research/uesp-esoapps/EsoExtractData/EsoExtractData/granny2_x64.dll` research checkout, if present.

The desktop shortcut compiles the separate helper when its source changes. Building directly:

```powershell
dotnet build tools/ModelGeometry/ModelGeometry.csproj

# Optional isolated diagnostic (writes only to ignored .research)
dotnet run --project tools/ModelGeometry -- `
    "C:\path\to\granny2_x64.dll" 0028F157 `
    ".research\model-preview-0028F157.esm" `
    "C:\ESO\The Elder Scrolls Online\depot\eso.mnf" `
    ".research\ooz.dll"
```

For a supported model, selecting **Preview source 3D geometry** performs this operation automatically through the helper process, asynchronously. If the model cannot be read because the depot download is incomplete, the DLL is absent or decoding fails, the user sees an error in the preview instead of an empty or fabricated model. Each run has a 30-second timeout. Crashes in the foreign native DLL are contained in the child process, and the rest of Godot remains running.

See [original model material bindings, recovered fixed-point UV encoding and source DDS mapping](../../docs/MODEL_TEXTURE_RESEARCH.md) for the complete reverse-engineering evidence, in-memory DDS header correction and all material ID examples.

## Validation (27 October 2026)

| Original source model | ID | Original geometry |
|---|---|---|
| `ROK_BAD_DeadlandsLGDoor001` | `0x0028F157` | 173 vertices, 186 triangles; source XYZ bounds [-1.33, 0.01, 0.05] to [1.34, 4.02, 0.51] |
| `ArcaneBMasterADR_Helmet_A` | `0x0029A89F` | 458 vertices, 558 triangles; rendered as a small ornamental mesh in the Godot preview |
| `ROK_GRY_MRKCliff001` | `0x0009F7B7` | 3,316 vertices, 4,460 triangles; displayed as a clearly recognisable cliff in the Godot 1280×720 test |

Godot 4.7.2 Mono and the separate CLI built with zero warnings/errors. Original diffuse/normal-map previews were verified for the cliff (**2 diffuse + 2 normal groups**) and Guild Mages building (**13 diffuse + 13 normal groups**), with zero neutral fallbacks. The secondary/detail shader experiment changed the cliff brightness noticeably and is therefore **disabled by default** until the original shader-variant flags are recovered. Screenshots and all converted previews remain under ignored `.research/`, while source game files are read-only. Model preview screenshots and all converted previews remain under ignored `.research/`, while source game files are read-only.

## Next accurate links

Original normal-map and compressed frame formats are decoded and rendered. The typed GR2 **authoring flags**, secondary gloss/Fresnel/glow parameters, sidedness and alpha-mode values are also recovered. The next investigation is the **mapping between exporter enums and runtime shader variants**, together with per-instance constants, specular, tint, transparency and vertex colour. Original detail UV1 and all five named texture channel IDs are extracted but only their authenticated diffuse/normal subset is enabled by default. For an explicitly experimental comparison only, set `ESO_EXPERIMENTAL_MODEL_DETAIL=1` (not exact ESO shader parity). Skeletons, rigging and animations remain separate work. For character assets, recover model mesh bindings, skeletons and animations separately. Actual game item→3D model and actor archetype→model references are still unknown and require a verified mapping rather than matching names by intuition.
