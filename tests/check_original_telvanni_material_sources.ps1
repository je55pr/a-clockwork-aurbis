# Original installed ESO Telvanni Light material provenance check.
# The material authoring paths and tint numbers are genuine Granny fields.
# The nearby A/B DDS pairings are research hypotheses, not runtime links.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$oodle=Join-Path $root '.research\ooz.dll'
$index=Join-Path $root 'data\eso_telvanni_light_material_candidates.tsv'
foreach($p in @($tool,$granny,$manifest,$oodle,$index)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){
        throw "Installed original source prerequisite missing: $p"
    }
}
Push-Location -LiteralPath $root
try{
    $expected=@{
        '001086BD'=@('TelvanniLight_Helmet_B','TelvanniLight_B_U','001086C5','001086C7','001086C9','001086CB')
        '001086D9'=@('TelvanniLight_Helmet_A','TelvanniLight_A_U','001086CD','001086CF','001086D1','001086D3')
        '001086B5'=@('TelvanniLight_Belt_A','TelvanniLight_A_U','001086CD','001086CF','001086D1','001086D3')
        '00108795'=@('TelvanniLight_Shoulders_A','TelvanniLight_A_U','001086CD','001086CF','001086D1','001086D3')
    }
    $lines=@([IO.File]::ReadAllLines($index)|Where-Object {$_ -match '^[0-9A-F]{8}\t'})
    if($lines.Count -ne $expected.Count){
        throw "Expected four source-authoring researched outfit mesh candidates"
    }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($row in $lines){
        $fields=$row.Split([char]9)
        if($fields.Count -ne 6){
            throw "Original cloth mapping requires six evidence fields"
        }
        $id=$fields[0]
        if(!$seen.Add($id) -or !$expected.ContainsKey($id) -or
           $fields[5] -cne 'archive-triplet-authoring-provisional'){
            throw "Unknown original source or falsely authoritative association"
        }
        $source=$expected[$id]
        for($i=0;$i -lt 4;$i++){
            if($fields[$i+1] -cne $source[$i+2]){
                throw "Original $id artwork channel source IDs changed"
            }
        }
        $audit=(& dotnet $tool --audit-character-material $granny $id $manifest $oodle 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Cannot read actual original Granny material $id"}
        foreach($channel in @('D','N','S','T')){
            $name=$source[1]+'_'+$channel+'.tga'
            if($audit.IndexOf($name,[StringComparison]::OrdinalIgnoreCase) -lt 0){
                throw "Granny model $id lacks original $name artwork reference"
            }
        }
        $tints=@(
            'f4DiffuseTintPrimary [kind=10,size=4,count=4] value=0.50196081,0.47058824,0.43921569,1',
            'f4DiffuseTintSecondary [kind=10,size=4,count=4] value=0.34901962,0.34901962,0.34901962,1',
            'f4DiffuseTintAccent [kind=10,size=4,count=4] value=0.23921569,0.27843139,0.29803923,1'
        )
        foreach($tint in $tints){
            if(!$audit.Contains($tint)){throw "Original ESO $id primary/secondary/accent dye changed"}
        }
        $rig=(& dotnet $tool --inspect-rig $granny $id $manifest $oodle|Out-String)
        if($LASTEXITCODE -ne 0){throw "Original ESO $id Granny rig unavailable"}
        $native=$rig|ConvertFrom-Json
        if($native.RigMeshes[0].Name -cne $source[0] -or
           $native.RigSkeletons[0].Name -cne 'CustomizationSkeleton'){
            throw "Original equipment part name/root rig changed: $id"
        }
        Write-Output "Verified original GR2 $id $($source[0]) uses original $($source[1]) D/N/S/T authoring paths; three genuine source tint colours"
    }
    $dds=@('001086C5','001086C7','001086C9','001086CB',
           '001086CD','001086CF','001086D1','001086D3')
    $local=Join-Path $root '.research\npc-rigs'
    [IO.Directory]::CreateDirectory($local)|Out-Null
    foreach($id in $dds){
        $file=Join-Path $local ('source-telvanni-'+$id+'.dds')
        $output=(& dotnet $tool --extract-original-dds $manifest $oodle $id $file 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){
            throw "Original source DDS extraction failed for $id $output"
        }
        $bytes=[IO.File]::ReadAllBytes($file)
        if($bytes.Length -lt 128 -or
           [Text.Encoding]::ASCII.GetString($bytes,0,4) -cne 'DDS ' -or
           [BitConverter]::ToUInt32($bytes,16) -ne 576 -or
           [BitConverter]::ToUInt32($bytes,12) -ne 256){
            throw "Original archived Telvanni DDS $id dimensions/header mismatch"
        }
        if($id -in @('001086CB','001086D3')){
            if([BitConverter]::ToUInt32($bytes,84) -ne 0 -or
               [BitConverter]::ToUInt32($bytes,80) -ne 0x41 -or
               [BitConverter]::ToUInt32($bytes,88) -ne 32 -or
               [BitConverter]::ToUInt32($bytes,92) -ne 0x00FF0000 -or
               [BitConverter]::ToUInt32($bytes,96) -ne 0x0000FF00 -or
               [BitConverter]::ToUInt32($bytes,100) -ne 0x000000FF -or
               [BitConverter]::ToUInt32($bytes,104) -ne [uint32]4278190080){
                throw "Original armour tint is not 576x256 uncompressed BGRA8"
            }
        }elseif([Text.Encoding]::ASCII.GetString($bytes,84,4) -cne 'DXT5'){
            throw "Original Telvanni DDS $id is not DXT5"
        }
    }
    if($seen.Contains('001086B1')){throw 'Rejected tassets UV/material assignment unexpectedly promoted'}
    $negative=(& dotnet $tool --audit-character-material $granny 001086B1 $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0 -or
       $negative.IndexOf('TelvanniLight_A_U_D.tga',[StringComparison]::OrdinalIgnoreCase) -lt 0){
        throw 'Rejected TelvanniLight_Tassets_A source artwork must remain traceable'
    }
    Write-Output 'REJECTED tassets 001086B1: Granny source art is genuine, but the examined candidate atlases have no usable opaque pixels at the source UVs, so no DDS assignment is made.'
    Write-Output 'PASS four original Telvanni GR2 material records and 8 genuine 576x256 D/N/S/T DDS resources, with three source dye colours. All A/B atlas associations remain PROVISIONAL.'
}
finally{Pop-Location}
