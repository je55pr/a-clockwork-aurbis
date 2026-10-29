# Contributing to A Clockwork Aurbis

Use the `dev` branch as the integration target. Create focused feature branches
and submit pull requests. CI and automated review should pass before squash
merging. GitHub automatically deletes merged branches.

Run `dotnet build EsoTest.csproj`, `dotnet build
tools/ModelGeometry/ModelGeometry.csproj` and `dotnet build
tools/ModelNameIndexer/ModelNameIndexer.csproj` for source compilation.
Actual previews require a legally installed ESO depot and separately provided
optional native decode libraries; CI deliberately never downloads game assets.

Do not commit extracted ESO assets, shader dumps, native Granny/Oodle DLLs,
user credentials, generated caches or local path configuration. Retain original
third-party licensing and document new references in `THIRD_PARTY.md`.
