# Proves two original Clannfear meshes share native UVs, topology,
# and artist-authored maps, without pretending the archive DDS IDs are known.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$oodle=Join-Path $root '.research\ooz.dll'
$cache=Join-Path $root '.research\npc-rigs'
foreach($item in @($helper,$granny,$manifest,$oodle)){
    if(!(Test-Path -LiteralPath $item -PathType Leaf)){throw "Original ESO dependency unavailable: $item"}
}
[IO.Directory]::CreateDirectory($cache)|Out-Null
function HashBytes([byte[]]$bytes){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')}
    finally{$sha.Dispose()}
}
function CheckOriginal([string]$id,[string]$expectedName){
    $path=Join-Path $cache ('material-family-'+$id+'.esm')
    $output=(& dotnet $helper $granny $id $path $manifest $oodle 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Original source mesh extraction failed for $id $output"}
    $reader=[IO.BinaryReader]::new([IO.File]::OpenRead($path))
    try{
        $magic=[Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if($magic -ne 'ESM5' -or $reader.ReadInt32() -ne 1){
            throw "Native source geometry header unexpected: $id"
        }
        $name=$reader.ReadString()
        $verts=$reader.ReadInt32()
        $indices=$reader.ReadInt32()
        $groups=$reader.ReadInt32()
        [void]$reader.ReadBoolean()
        [void]$reader.ReadBoolean()
        if($name -cne $expectedName -or $verts -ne 2424 -or
           $indices -ne 11124 -or $groups -ne 1){
            throw "Original native mesh geometry unexpected for $id"
        }
        [void]$reader.ReadBytes($verts*12)
        $uv=$reader.ReadBytes($verts*8)
        [void]$reader.ReadBytes($verts*8)
        [void]$reader.ReadBytes($verts*12)
        [void]$reader.ReadBytes($verts*16)
        $topo=$reader.ReadBytes($indices*4)
        if($uv.Length -ne $verts*8 -or $topo.Length -ne $indices*4){
            throw "Native source UV/topology truncated for $id"
        }
        $material=(& dotnet $helper --audit-character-material $granny $id $manifest $oodle 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Source material metadata unavailable for $id"}
        $channels=@{}
        foreach($channel in @('diffuse','normal','specular')){
            $pattern="(?m)^\s+$($channel)Path \[[^\r\n]+?\] value=(.+)$"
            $match=[regex]::Match($material,$pattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if(!$match.Success){throw "Source artist $channel path missing for $id"}
            $channels[$channel]=$match.Groups[1].Value.Trim()
        }
        return [PSCustomObject]@{
            Id=$id;Name=$name;UV=(HashBytes $uv);Indices=(HashBytes $topo);
            Diffuse=$channels.diffuse;Normal=$channels.normal;Specular=$channels.specular
        }
    }
    finally{$reader.Dispose()}
}
$a=CheckOriginal '000815D7' 'Clannfear_A_Basic'
$b=CheckOriginal '0008230B' 'Clannfear_C_Basic_CH'
if($a.UV -cne $b.UV -or $a.Indices -cne $b.Indices){
    throw 'Original source Clannfear UV and topology equivalence failed'
}
foreach($channel in @('Diffuse','Normal','Specular')){
    if($a.$channel -cne $b.$channel){
        throw "Original Clannfear authoring $channel paths differ"
    }
}
if(!$a.Diffuse.EndsWith('\Clannfear_D.tga',[StringComparison]::OrdinalIgnoreCase) -or
   !$a.Normal.EndsWith('\clannfear_ch_n.tga',[StringComparison]::OrdinalIgnoreCase) -or
   !$a.Specular.EndsWith('\clannfear_ch_s.tga',[StringComparison]::OrdinalIgnoreCase)){
    throw 'Original material authoring path unexpectedly changed'
}
Write-Output 'PASS two original ESO basic Clannfears: byte-identical UVs, triangle topology and source-authored diffuse/normal/specular paths.'
Write-Output "SOURCE_UV_SHA256=$($a.UV)"
Write-Output 'NO runtime DDS archive IDs inferred from source-art paths.'
