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
                            [int]$expectedCode, [int]$expectedCount,
                            [bool]$externalRig = $false) {
    $mode = if ($externalRig) { '--inspect-native-external-binding' } else { '--inspect-native-binding' }
    $result = & $dotnet $helper $mode $GrannyDll $partId $targetId $manifest $OodleDll 2>&1
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
# ESO also ships skeleton-agnostic source meshes. The original Granny
# binding API can attach these only when EVERY named skin binding is present
# on the external original source skeleton. No aliasing/fallback is accepted.
Test-GrannyBinding '0015D6CD' '000020A2' 0 81 $true
Test-GrannyBinding '0015D6CF' '000020A2' 0 59 $true
Test-GrannyBinding '0015D6CD' '000DEF03' 3 81 $true
Test-GrannyBinding '0015D6CD' '000E2CFF' 3 81 $true
# The genuine original hybrid Head_004 includes five animation bindings
# AND four customization controls. Native Granny resolves only five of the
# nine against the animation model. That partial match is NOT acceptable
# for a complete skin, even though most of the head follows the walk.
$hybridResult = & $dotnet $helper --inspect-native-external-binding $GrannyDll '000E2D77' '000020A2' $manifest $OodleDll 2>&1
$hybridExit = $LASTEXITCODE
$hybridText = ($hybridResult | Out-String)
if ($hybridExit -ne 3 -or
    -not $hybridText.Contains('total=9, exact=5, name-differing=4, index-unmapped=0') -or
    -not $hybridText.Contains('INCOMPATIBLE_SOURCE_RIG:')) {
    throw "Hybrid Head_004 must not be treated as a fully compatible skin. $hybridText"
}
Write-Output 'PASS original hybrid Head_004 remains partially bound (5/9), not falsely declared complete'
Write-Output 'PASS exact native external skeletal binding and no approximate skin aliases'
