# Local-source reproducible verification of the live-capture-linked original
# HAR_M_HU_3210 material D/N/S/T quartet. No game capture is required.
# Never infer an authoritative runtime material ID or skeletal compatibility.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$oodle=Join-Path $root '.research\ooz.dll'
$index=Join-Path $root 'data\eso_captured_hair_material_candidates.tsv'
foreach($p in @($tool,$manifest,$granny,$oodle,$index)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){
        throw "Missing original ESO source prerequisite: $p"
    }
}
Push-Location -LiteralPath $root
try {
    $rows=@([IO.File]::ReadAllLines($index)|Where-Object {$_ -match '^[0-9A-F]{8}\t'})
    if($rows.Count -ne 1 -or
       ($rows[0].Split([char]9) -join '|') -cne
       '0002E5E3|0002E5DB|0002E5DD|0002E5DF|0002E5E1|archive-triplet-authoring-provisional'){
        throw 'Expected exact unverified original hair quartet, not an inferred runtime link'
    }
    $source=(& dotnet $tool --audit-character-material $granny 0002E5E3 $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0 -or !$source.Contains("original-material name='HAR_M_HU_3210'")){
        throw 'Source mesh original authoring material identity changed'
    }
    foreach($name in @('HAR_M_HU_3210_D.tga','HAR_M_HU_3210_N.tga',
                        'HAR_M_HU_3210_S.tga','HAR_M_HU_3210_T.tga')){
        if(!$source.Contains($name)){
            throw "Original 0002E5E3 artist path not present: $name"
        }
    }
    $local=Join-Path $root '.research\live-eso-selection'
    [IO.Directory]::CreateDirectory($local)|Out-Null
    foreach($id in @('0002E5DB','0002E5DD','0002E5DF','0002E5E1')){
        $path=Join-Path $local ('source-audit-'+$id+'.dds')
        $sourceOutput=(& dotnet $tool --extract-original-dds $manifest $oodle $id $path 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Original DDS source $id failed: $sourceOutput"}
        $bytes=[IO.File]::ReadAllBytes($path)
        if($bytes.Length -lt 128 -or
           [Text.Encoding]::ASCII.GetString($bytes,0,4) -cne 'DDS ' -or
           [BitConverter]::ToInt32($bytes,16) -ne 576 -or
           [BitConverter]::ToInt32($bytes,12) -ne 256){
            throw "Original DDS resource $id dimensions/header changed"
        }
        if($id -ne '0002E5E1'){
            if([Text.Encoding]::ASCII.GetString($bytes,84,4) -cne 'DXT5' -or
               $bytes.Length -ne 147584){
                throw "Original compressed hair DDS $id is not 576x256 DXT5"
            }
        }else{
            if([BitConverter]::ToUInt32($bytes,84) -ne 0 -or
               [BitConverter]::ToUInt32($bytes,80) -ne 0x41 -or
               [BitConverter]::ToUInt32($bytes,88) -ne 32 -or
               [BitConverter]::ToUInt32($bytes,92) -ne 0x00FF0000 -or
               [BitConverter]::ToUInt32($bytes,96) -ne 0x0000FF00 -or
               [BitConverter]::ToUInt32($bytes,100) -ne 0x000000FF -or
               [BitConverter]::ToUInt32($bytes,104) -ne [uint32]4278190080){
                throw 'Original 3210 hair tint mask is not uncompressed BGRA8'
            }
        }
    }
    $binding=(& dotnet $tool --inspect-native-external-binding $granny 0002E5E3 001076DD $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 3 -or
       !$binding.Contains('total=13, exact=12, name-differing=1, index-unmapped=0') -or
       !$binding.Contains('INCOMPATIBLE_SOURCE_RIG:')){
        throw 'Original 3210 hairstyle was unexpectedly treated as valid for Head_600'
    }
    Write-Output 'PASS original HAR_M_HU_3210 GR2 names D/N/S/T art paths: 0002E5DB/DD/DF three 576x256 DXT5 maps, 0002E5E1 uncompressed 576x256 BGRA8 mask.'
    Write-Output 'PASS incompatible Head_600 native skin explicitly rejected (12 of 13 names); mask visualization is diagnostic, DDS pairing remains provisional.'
}
finally { Pop-Location }
