# Original ESO native head/hair relationship and installed DDS provenance.
# No proprietary game bytes are committed. Source DDS IDs are still
# provisional associations, not authoritative runtime material records.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$oodle=Join-Path $root '.research\ooz.dll'
$index=Join-Path $root 'data\eso_humanoid_hair_texture_candidates.tsv'
foreach($p in @($tool,$manifest,$granny,$oodle,$index)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Original ESO source missing: $p"}
}
Push-Location -LiteralPath $root
try{
    $rows=@([IO.File]::ReadAllLines($index)|Where-Object {$_ -match '^[0-9A-F]{8}\t'})
    if($rows.Count -ne 1){throw 'Expected exactly one original humanoid hair source candidate'}
    $f=$rows[0].Split([char]9)
    if($f.Length -ne 5 -or
       ($f -join '|') -cne '0029A87F|0029A877|0029A879|0029A87B|archive-neighbor-provisional'){
        throw 'Original ESO hair DDS candidate evidence changed'
    }
    $native=(& dotnet $tool --inspect-native-binding $granny 0029A87F 001230E1 $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0 -or
       !$native.Contains('total=10, exact=10, name-differing=0, index-unmapped=0') -or
       !$native.Contains('COMPATIBLE_SOURCE_RIG:')){
        throw 'Native Granny hair/head relationship no longer exact'
    }
    $source=(& dotnet $tool --audit-character-material $granny 0029A87F $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw 'Original GR2 hair material unavailable'}
    foreach($name in @('HAR_F_HU_0343_D.tga','har_f_hu_0343_n.tga',
                        'har_f_hu_0343_s.tga','har_f_hu_0343_t.tga')){
        if(!$source.Contains($name)){throw "Missing original artist hair source path $name"}
    }
    $dir=Join-Path $root '.research\npc-rigs'
    [IO.Directory]::CreateDirectory($dir)|Out-Null
    foreach($id in @('0029A877','0029A879','0029A87B','0029A87D')){
        $out=Join-Path $dir ('hair-source-'+$id+'.dds')
        $message=(& dotnet $tool --extract-original-dds $manifest $oodle $id $out 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Original hair DDS source $id invalid: $message"}
        $header=[IO.File]::ReadAllBytes($out)
        if($header.Length -lt 128 -or
           [Text.Encoding]::ASCII.GetString($header,0,4) -ne 'DDS ' -or
           [BitConverter]::ToInt32($header,16) -ne 576 -or
           [BitConverter]::ToInt32($header,12) -ne 256){
            throw "Original nearby hair DDS dimensions/header changed for $id"
        }
        $codec=[Text.Encoding]::ASCII.GetString($header,84,4)
        if($id -ne '0029A87D' -and $codec -cne 'DXT5'){
            throw "Expected three original DXT5 source hair channels for $id, got $codec"
        }
    }
    Write-Output 'PASS original ESO Head_601 hair HAR_F_HU_0343: 10/10 native Granny skin bindings, original D/N/S/tint authoring paths, three 576x256 DXT5 archive resources and one 576x256 candidate tint-map resource.'
    Write-Output 'All proposed DDS mesh-ID associations remain archive-neighbor PROVISIONAL.'
}
finally{Pop-Location}
