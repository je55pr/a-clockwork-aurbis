# Verifies source archive identities and supported DDS files for the
# experimental original ESO creature material candidate index.
# Important: co-indexed source bytes do NOT prove an in-game material link.
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $project
$helper=Join-Path $project 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if([string]::IsNullOrEmpty($manifest)){
    $manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'
}
$oodle=Join-Path $project '.research\ooz.dll'
$index=Join-Path $project 'data\eso_creature_texture_candidates.tsv'
foreach($file in @($helper,$manifest,$oodle,$index)){
    if(!(Test-Path -LiteralPath $file -PathType Leaf)){
        throw "Missing installed source dependency: $file"
    }
}
$output=Join-Path $project '.research\creature-texture-source-audit'
[IO.Directory]::CreateDirectory($output)|Out-Null
$mappingCount=0
$normalCount=0
$specularCount=0
$textureIDs=New-Object 'System.Collections.Generic.HashSet[string]'
$meshes=New-Object 'System.Collections.Generic.HashSet[string]'
foreach($row in [IO.File]::ReadLines($index)){
    if([string]::IsNullOrWhiteSpace($row) -or $row.StartsWith('#')){continue}
    $f=$row.Split([char]9)
    if($f.Count -ne 5){throw "Expected source candidate schema: mesh diffuse normal specular evidence"}
    foreach($item in $f[0..3]){
        if($item -cnotmatch '^[0-9A-F]{8}$'){
            throw "Invalid hexadecimal original source asset ID: $item"
        }
    }
    $mesh=$f[0]
    if(!$meshes.Add($mesh)){throw "Duplicate creature source model $mesh"}
    $evidence=$f[4]
    if($evidence -cnotin @('archive-neighbor-provisional','archive-triplet-provisional')){
        throw "Attempt to assert unsupported runtime material evidence: $evidence"
    }
    $m=[Convert]::ToUInt32($mesh,16)
    $diffuse=[Convert]::ToUInt32($f[1],16)
    $normal=[Convert]::ToUInt32($f[2],16)
    $specular=[Convert]::ToUInt32($f[3],16)
    if($diffuse -eq 0){throw "Diffuse texture candidate is missing for mesh $mesh"}
    if([Math]::Abs([long]$m-[long]$diffuse) -gt 64){
        throw "Diffuse candidate lacks local archive-ID provenance: $mesh"
    }
    if($evidence -eq 'archive-triplet-provisional'){
        if($normal -ne $diffuse+2 -or $specular -ne $diffuse+4){
            throw "Expected source archive DDS triplet not present for $mesh"
        }
    }
    foreach($id in $f[1..3]){
        if($id -eq '00000000'){continue}
        [void]$textureIDs.Add($id)
    }
    if($normal -ne 0){$normalCount++}
    if($specular -ne 0){$specularCount++}
    $mappingCount++
}
if($mappingCount -ne 9 -or $normalCount -ne 7 -or $specularCount -ne 8){
    throw "Original candidate inventory changed: mapped=$mappingCount normals=$normalCount specular=$specularCount"
}
$checked=0
foreach($id in $textureIDs){
    $filename=Join-Path $output ($id+'.dds')
    if(!(Test-Path -LiteralPath $filename)){
        $result=(& dotnet $helper --extract-original-dds $manifest $oodle $id $filename 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Original archive DDS decode failed for ${id}: $result"}
    }
    $bytes=[IO.File]::ReadAllBytes($filename)
    if($bytes.Length -lt 128 -or
       [Text.Encoding]::ASCII.GetString($bytes,0,4) -ne 'DDS '){
        throw "Original source texture candidate is not a DDS: $id"
    }
    $w=[BitConverter]::ToInt32($bytes,16)
    $h=[BitConverter]::ToInt32($bytes,12)
    $format=[Text.Encoding]::ASCII.GetString($bytes,84,4)
    if($format -cnotin @('DXT1','DXT3','DXT5') -or
       $w -lt 4 -or $w -gt 4096 -or
       $h -lt 4 -or $h -gt 4096){
        throw "Unsupported source DDS metadata for candidate $id $($w)x$h $format"
    }
    $checked++
}
Write-Output "PASS $mappingCount archived original ESO creature DDS colour candidates; $normalCount packed-normal IDs, $specularCount source specular IDs, $checked real DDS archive resources"
Write-Output 'PASS every source-ID association explicitly marked provisional; no extracted game bytes entered the tracked candidate index'
