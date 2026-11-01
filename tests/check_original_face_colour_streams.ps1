# Read-only validation of original ESO facial colour streams. Runs using
# the installed game/depot and its native Granny library, never a synthetic
# material or guessed raster texture.
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
$cases=@(
 @{Id='000E2CFF';Mesh='Head_004';Verts=404},
 @{Id='001076DD';Mesh='Head_600';Verts=6530},
 @{Id='00129B5D';Mesh='Head_008';Verts=1721},
 @{Id='000DEE57';Mesh='Head_502';Verts=890},
 @{Id='000DEE59';Mesh='Head_503';Verts=866}
)
foreach($case in $cases){
 $id=$case.Id
 $output=(& $dotnet $helper --inspect-face-colors $granny $id $manifest $oodle 2>&1|Out-String)
 if($LASTEXITCODE -ne 0 -or
    !$output.Contains('embeddedTextures=0') -or
    !$output.Contains($case.Mesh) -or
    !$output.Contains("originalVerts=$($case.Verts)") -or
    !$output.Contains('uniqueRGBA=1') -or
    !$output.Contains("mostCommon=255,255,255,255:$($case.Verts)")){
  throw "Unexpected original ESO facial colour/source texture record $id : $output"
 }
 Write-Output "PASS source $id $($case.Mesh): $($case.Verts) real vertices, only white RGBA, no embedded textures"
}
Write-Output 'PASS face source materials must be recovered externally, not invented from white vertex colours'
