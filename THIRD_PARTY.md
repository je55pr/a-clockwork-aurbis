# Third-party attribution and scope

The MES2/PES2 record layout and ZOSFT format in this experiment were implemented by reference to **eso-tools** (MIT), copyright (c) 2022 Aleksandr Zelenin:
https://github.com/eso-tools/eso-tools

The land-format decoding was informed by UESP's open research in EsoLand:
https://github.com/uesp/uesp-esoapps/blob/master/EsoLand/EsoLand/EsoLand.cpp

The optional **ooz** decompressor is GPL-3.0-or-later, copyright its authors:
https://github.com/powzix/ooz

For local investigation, ooz was compiled in the ignored `.research` directory into `ooz.dll`. Its compiled binaries and sources must not be copied into the redistributable game project without meeting its license conditions.

Optional GR2 model-name research follows UESP's read-only Granny2 filename recovery method in `common/EsoMnffile.cpp`: https://github.com/uesp/uesp-esoapps . The standalone `tools/ModelNameIndexer` accepts a **separately supplied compatible native Granny2 SDK DLL** solely for local decoding. Granny2 native DLLs may have proprietary redistribution or use restrictions. **No Granny DLL is included, redistributed, or loaded by the Godot application**; do not add one to the repository without checking its licence and your rights to use it. The separate `tools/ModelGeometry` helper uses the same opt-in local SDK for source GR2 vertex/triangle decoding and writes only ignored temporary derived previews.

No ESO binaries, DAT archives or extracted assets are tracked in the project. Reading assets from an ESO installation may still be constrained by the game's terms.
