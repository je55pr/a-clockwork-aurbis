# Read-only regression for original ESO GR2 skeleton and character-mesh
# binding. Requires the user's locally installed ESO game files. No source
# data, proprietary DLLs or extracted assets are committed to the repository.
[CmdletBinding()]
param(
    [string]$EsoRoot = 'C:\ESO\The Elder Scrolls Online',
    [string]$OodleDll = '',
    [string]$GrannyDll = ''
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OodleDll) { $OodleDll = Join-Path $root '.research\ooz.dll' }
if (-not $GrannyDll) { $GrannyDll = Join-Path $EsoRoot 'game\client\granny2_x64.dll' }
$manifest = Join-Path $EsoRoot 'depot\eso.mnf'
$helper = Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
foreach ($needed in @($manifest, $OodleDll, $GrannyDll, $helper)) {
    if (-not (Test-Path -LiteralPath $needed -PathType Leaf)) {
        throw "Required local source dependency missing: $needed"
    }
}
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

function Test-GrannyBinding([string]$partId, [string]$targetId,
                            [int]$expectedCode, [int]$expectedCount) {
    $result = & $dotnet $helper --inspect-native-binding $GrannyDll $partId $targetId $manifest $OodleDll 2>&1
    $exitCode = $LASTEXITCODE
    $summary = ($result | Out-String)
    if ($exitCode -ne $expectedCode) {
        throw "Native binding $partId => $targetId returned $exitCode instead of $expectedCode. $summary"
    }
    $isCompatible = $expectedCode -eq 0
    $status = if ($isCompatible) { 'COMPATIBLE_SOURCE_RIG:' }
              else { 'INCOMPATIBLE_SOURCE_RIG:' }
    if (-not $summary.Contains($status)) {
        throw "Missing required native binding classification $status. $summary"
    }
    $countMatch = [regex]::Match($summary, 'total=(\d+), exact=(\d+)')
    if (-not $countMatch.Success -or
        [int]$countMatch.Groups[1].Value -ne $expectedCount) {
        throw "Native source binding count wrong for $partId => $targetId. $summary"
    }
    $exact = [int]$countMatch.Groups[2].Value
    if (($isCompatible -and $exact -ne $expectedCount) -or
        (-not $isCompatible -and $exact -ne 0)) {
        throw "Unexpected native source binding names $partId => $targetId. $summary"
    }
    Write-Output "PASS original ESO native binding: $partId => $targetId; exact $exact/$expectedCount; $status"
}

# Positive controls: the original source GR2 mesh/rig are compatible.
Test-GrannyBinding '000DEF03' '000DEF03' 0 22
Test-GrannyBinding '000E2CFF' '000E2CFF' 0 6
# Negative controls: the original source animation skeleton has different
# bindings. A native fallback to root index zero is NOT a valid skin.
Test-GrannyBinding '000DEF03' '0000209F' 3 22
Test-GrannyBinding '000E2CFF' '0000209F' 3 6
Write-Output 'PASS no approximate outfit-to-animation mapping accepted'
