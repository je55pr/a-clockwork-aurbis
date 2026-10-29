param(
    [string]$SkyrimData = $env:SKYRIM_DATA,
    [string]$GodotExe = $env:GODOT_EXE
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($SkyrimData)) {
    $SkyrimData = 'C:\Program Files (x86)\Steam\steamapps\common\SkyrimVR\Data'
}
$env:SKYRIM_DATA = $SkyrimData
if (-not (Test-Path -LiteralPath (Join-Path $SkyrimData 'Skyrim.esm'))) {
    throw "Skyrim VR game data not found: $SkyrimData"
}
if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
    throw 'Node.js is required to read Skyrim game data.'
}
$generated = Join-Path $root 'generated'
$terrainFile = Join-Path $generated 'riverwood.json'
$extractor = Join-Path $root 'tools\extract-riverwood.js'
if (-not (Test-Path -LiteralPath $terrainFile) -or
    (Get-Item -LiteralPath $terrainFile).LastWriteTimeUtc -lt (Get-Item -LiteralPath $extractor).LastWriteTimeUtc) {
    & node (Join-Path $root 'tools\extract-riverwood.js')
    if ($LASTEXITCODE -ne 0) { throw 'Riverwood record extraction failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $generated 'model-index.json'))) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        throw 'FFmpeg is required to prepare the local Skyrim textures.'
    }
    & node (Join-Path $root 'tools\extract-riverwood-textures.js')
    if ($LASTEXITCODE -ne 0) { throw 'Skyrim texture conversion failed.' }
    & node (Join-Path $root 'tools\convert-riverwood-models.js')
    if ($LASTEXITCODE -ne 0) { throw 'NIF mesh conversion failed.' }
}
$landscapeManifest = Join-Path $generated 'landscape\manifest.json'
$landscapeSource = Join-Path $generated 'riverwood.json'
if (-not (Test-Path -LiteralPath $landscapeManifest) -or
    (Get-Item -LiteralPath $landscapeSource).LastWriteTimeUtc -gt (Get-Item -LiteralPath $landscapeManifest).LastWriteTimeUtc -or
    (Get-Item -LiteralPath (Join-Path $root 'tools\bake-landscape.js')).LastWriteTimeUtc -gt (Get-Item -LiteralPath $landscapeManifest).LastWriteTimeUtc) {
    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        throw 'FFmpeg is required to prepare the original Skyrim landscape textures.'
    }
    & node (Join-Path $root 'tools\bake-landscape.js')
    if ($LASTEXITCODE -ne 0) { throw 'Skyrim landscape material baking failed.' }
}
if ([string]::IsNullOrWhiteSpace($GodotExe)) {
    $esoGodotPath = 'C:\Repos\a-clockwork-aurbis\.research\godot-exe.txt'
    if (Test-Path -LiteralPath $esoGodotPath) {
        $GodotExe = (Get-Content -LiteralPath $esoGodotPath -TotalCount 1).Trim()
    }
}
if ([string]::IsNullOrWhiteSpace($GodotExe)) {
    $godotCommand = Get-Command godot, godot-mono -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($godotCommand) { $GodotExe = $godotCommand.Source }
}
if ([string]::IsNullOrWhiteSpace($GodotExe) -or -not (Test-Path -LiteralPath $GodotExe)) {
    throw 'Godot 4.7 not found. Set GODOT_EXE to your installed Godot executable.'
}
Remove-Item Env:SKYRIM_CAPTURE -ErrorAction SilentlyContinue
& $GodotExe --path $root
