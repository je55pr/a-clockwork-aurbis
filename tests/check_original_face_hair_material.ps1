# Original ESO male high-detail hair source audit.
# Provisional DDS candidates come from a matching female hairstyle's
# archive-neighbor triplet; the two original GR2 artist paths match.
# No runtime material-ID link is asserted.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$oodle=Join-Path $root '.research\ooz.dll'
$mapping=Join-Path $root 'data\eso_original_face_hair_texture_candidates.tsv'
foreach($file in @($tool,$granny,$manifest,$oodle,$mapping)){
    if(!(Test-Path -LiteralPath $file -PathType Leaf)){
        throw "Original ESO male hair source unavailable: $file"
    }
}
Push-Location -LiteralPath $root
try{
    $rows=@([IO.File]::ReadAllLines($mapping)|Where-Object {$_ -match '^[0-9A-F]{8}\t'})
    if($rows.Count -ne 1 -or ($rows[0].Split([char]9) -join '|') -cne
        '0029B2AF|0029A877|0029A879|0029A87B|0029A87D|archive-triplet-authoring-provisional'){
        throw 'Original male hairstyle candidate source identity changed'
    }
    $materials=@{}
    foreach($id in @('0029B2AF','0029A87F')){
        $output=(& dotnet $tool --audit-character-material $granny $id $manifest $oodle)
        if($LASTEXITCODE -ne 0){throw "Original ESO hair material audit failed for $id"}
        $materials[$id]=($output|Out-String)
        foreach($file in @('HAR_F_HU_0343_D.tga','har_f_hu_0343_n.tga',
                           'har_f_hu_0343_s.tga','har_f_hu_0343_t.tga')){
            if(!$materials[$id].Contains($file)){
                throw "Original shared male/female hair source art missing: $id $file"
            }
        }
    }
    foreach($head in @('001076DD','00129B5D')){
        $native=(& dotnet $tool --inspect-native-external-binding $granny 0029B2AF $head $manifest $oodle 2>&1 |Out-String)
        if($LASTEXITCODE -ne 0 -or
           !$native.Contains('total=12, exact=12, name-differing=0, index-unmapped=0') -or
           !$native.Contains('COMPATIBLE_SOURCE_RIG:')){
            throw "ESO Granny head $head rejected exact male hair bind"
        }
    }
    $dir=Join-Path $root '.research\npc-rigs'
    [IO.Directory]::CreateDirectory($dir)|Out-Null
    foreach($id in @('0029A877','0029A879','0029A87B')){
        $path=Join-Path $dir ('face-hair-candidate-'+$id+'.dds')
        $source=& dotnet $tool --extract-original-dds $manifest $oodle $id $path
        if($LASTEXITCODE -ne 0){throw "Original source hair DDS resource unavailable: $id"}
        $bytes=[IO.File]::ReadAllBytes($path)
        if($bytes.Length -lt 128 -or
           [Text.Encoding]::ASCII.GetString($bytes,0,4) -ne 'DDS ' -or
           [BitConverter]::ToInt32($bytes,16) -ne 576 -or
           [BitConverter]::ToInt32($bytes,12) -ne 256 -or
           [Text.Encoding]::ASCII.GetString($bytes,84,4) -ne 'DXT5'){
            throw "Original hair $id archive DDS dimensions/type changed"
        }
    }
    Write-Output 'PASS original ESO male HAR_M_HU_0343 artist material paths reuse female HAR_F_HU_0343 D/N/S/tint artwork; original heads Head_600 and Head_008 each bind exact 12/12; source candidate DDS D/N/S all genuine 576x256 DXT5 files.'
    Write-Output 'Original ESO runtime male-hair DDS assignment remains PROVISIONAL.'
}
finally{Pop-Location}
