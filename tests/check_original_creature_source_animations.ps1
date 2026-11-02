# Read-only original ESO creature mesh/animation provenance test.
# Uses installed native Granny and Oodle; all source assets stay out of Git.
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
        throw "Original source dependency unavailable: $dependency"
    }
}
$cases=@(
    @{Name='Clannfear';Mesh='000815D7';Rig='00082309';Clip='00000E14';Bindings=54;BoneCount=104;Tracks=104},
    @{Name='Daedroth';Mesh='000A0A61';Rig='00046563';Clip='0000121C';Bindings=52;BoneCount=102;Tracks=57},
    @{Name='Guar';Mesh='0029B14F';Rig='00024A33';Clip='00024A69';Bindings=52;BoneCount=113;Tracks=67}
)
foreach($c in $cases){
    $binding=(& $dotnet $helper --inspect-native-external-binding $granny $c.Mesh $c.Rig $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0 -or
       !$binding.Contains("total=$($c.Bindings), exact=$($c.Bindings), name-differing=0, index-unmapped=0") -or
       !$binding.Contains('COMPATIBLE_SOURCE_RIG:')){
        throw "Native Granny did not establish exact source mesh bindings: $($c.Name) : $binding"
    }
    $rigInfo=(& $dotnet $helper --inspect-animation $granny $c.Rig $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Cannot inspect original rig for $($c.Name)"}
    $rig=$rigInfo|ConvertFrom-Json
    if($rig.SkeletonCount -ne 1 -or $rig.Skeletons[0].BoneCount -ne $c.BoneCount -or
       $rig.Skeletons[0].Name -ne 'GrannyRootBone'){
        throw "Original creature skeleton identity mismatch: $($c.Name)"
    }
    $clipInfo=(& $dotnet $helper --inspect-animation $granny $c.Clip $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Cannot inspect original walk clip for $($c.Name)"}
    $clip=$clipInfo|ConvertFrom-Json
    if($clip.AnimationCount -ne 1 -or $clip.Animations[0].Name -ne 'Walk' -or
       [Math]::Abs($clip.Animations[0].DurationSeconds-1.6666667) -gt 0.001 -or
       $clip.Animations[0].Groups[0].TransformTracks -ne $c.Tracks){
        throw "Original creature Walk clip identity/tracks mismatch: $($c.Name)"
    }
    Write-Output "PASS $($c.Name): $($c.Bindings)/$($c.Bindings) native mesh bindings, $($c.BoneCount) source rig bones, original Walk $($c.Clip) ($($c.Tracks) tracks)"
}
$catalog=Join-Path $project 'data\eso_creature_variants.tsv'
if(!(Test-Path -LiteralPath $catalog -PathType Leaf)){
    throw "Original creature variant catalogue missing"
}
$variantCount=0
foreach($row in [IO.File]::ReadLines($catalog)){
    if([string]::IsNullOrWhiteSpace($row) -or $row.StartsWith('#')){continue}
    $c=$row.Split([char]9)
    if($c.Length -ne 8){throw "Invalid original creature source catalogue row"}
    $sourceInfo=(& $dotnet $helper --inspect-animation $granny $c[1] $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Cannot inspect original variant source $($c[0])"}
    $source=$sourceInfo|ConvertFrom-Json
    if($source.MeshCount -ne 1 -or $source.SkeletonCount -ne 0 -or
       $source.Meshes[0].Name -ne $c[4] -or
       $source.Meshes[0].VertexCount -ne [int]$c[5] -or
       $source.Meshes[0].BoneBindings -ne [int]$c[6]){
        throw "Original creature catalogue source geometry/skin mismatch: $($c[0])"
    }
    $binding=(& $dotnet $helper --inspect-native-external-binding $granny $c[1] $c[2] $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0 -or
       !$binding.Contains("total=$($c[6]), exact=$($c[6]), name-differing=0, index-unmapped=0") -or
       !$binding.Contains('COMPATIBLE_SOURCE_RIG:')){
        throw "Original creature variant source native binding mismatch: $($c[0])"
    }
    Write-Output "PASS original ESO variant $($c[0]): $($c[1]), $($c[5]) vertices, $($c[6])/$($c[6]) exact bindings"
    $variantCount++
}
if($variantCount -ne 10){throw "Expected ten independently verified original creature variants, got $variantCount"}
Write-Output 'PASS original source animations and 10 curated native-mesh variants, no bone aliases'
