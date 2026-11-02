# Read-only validation of original ESO character material metadata.
# No guessed texture mappings, extracted textures, or original game assets are committed.
[CmdletBinding()]
param([string]$EsoRoot='C:\ESO\The Elder Scrolls Online')
$ErrorActionPreference='Stop'
$project=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$helper=Join-Path $project 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$granny=Join-Path $EsoRoot 'game\client\granny2_x64.dll'
$manifest=Join-Path $EsoRoot 'depot\eso.mnf'
$oodle=Join-Path $project '.research\ooz.dll'
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
foreach($dependency in @($helper,$granny,$manifest,$oodle)){
    if(!(Test-Path -LiteralPath $dependency -PathType Leaf)){
        throw "Original ESO source dependency missing: $dependency"
    }
}
function Inspect([string]$id) {
    $output=(& $dotnet $helper --audit-character-material $granny $id $manifest $oodle 2>&1 | Out-String)
    if($LASTEXITCODE -ne 0){ throw "Native material audit failed for $id : $output" }
    return $output
}
function MustContain([string]$text,[string]$needle,[string]$id) {
    if(!$text.Contains($needle)){
        throw "Missing original source evidence for $id : $needle"
    }
}
$hair=Inspect '0029B2AF'
MustContain $hair "mesh[0]='HAR_M_HU_0343'" 'hair'
MustContain $hair 'sourceMaterialCount=1, embeddedGrannyTextures=0' 'hair'
MustContain $hair 'HAR_F_HU_0343_D.tga' 'hair diffuse'
MustContain $hair 'har_f_hu_0343_n.tga' 'hair normal'
MustContain $hair 'har_f_hu_0343_s.tga' 'hair specular'
MustContain $hair 'har_f_hu_0343_t.tga' 'hair tint'
MustContain $hair 'typeName [kind=8,size=8,count=1] value=ZosMaterial' 'hair shader'
Write-Output 'PASS original hair material has artist-authored maps and ZosMaterial metadata'

$elf=Inspect '00129B5D'
MustContain $elf "mesh[0]='Head_008' native-material-bindings=0" 'elf head'
MustContain $elf 'sourceMaterialCount=0, embeddedGrannyTextures=0' 'elf head'
Write-Output 'PASS original pointed-ear head has no embedded material mapping'

$mannequin=Inspect '000E2CFF'
MustContain $mannequin "mesh[0]='Head_004' native-material-bindings=1" 'mannequin'
MustContain $mannequin 'Characters\Skins\Mannequin\Textures\MannequinM_D.tga' 'mannequin provenance'
Write-Output 'PASS Head_004 material is explicitly from Mannequin source-art, not an Altmer skin'

$other=Inspect '001076DD'
MustContain $other "mesh[0]='Head_600' native-material-bindings=1" 'Head_600'
MustContain $other 'typeName [kind=8,size=8,count=1] value=Standard' 'Head_600'
Write-Output 'PASS Head_600 original material is generic Standard, not a resolved character skin'
