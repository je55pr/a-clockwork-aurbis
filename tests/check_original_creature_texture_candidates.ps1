# Validate every original ESO creature DDS candidate directly against the
# locally installed archive without copying proprietary image bytes.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$oodle=Join-Path $root '.research\ooz.dll'
$index=Join-Path $root 'data\eso_creature_texture_candidates.tsv'
foreach($p in @($tool,$manifest,$oodle,$index)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){
        throw "Original ESO source dependency missing: $p"
    }
}
Push-Location -LiteralPath $root
try{
    $result=(& dotnet $tool --audit-original-creature-texture-index $manifest $oodle $index 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Original ESO DDS batch audit failed: $result"}
    $expected='60 source mesh links \(51 authoring\+structural provisional\), 58 source normal maps, 58 specular candidates, 146 distinct genuine archived DDS files, 0 flagged weak-neighbour mismatches'
    if($result -notmatch $expected){
        throw "Original DDS index provenance or source resources changed: $result"
    }
    Write-Output $result.Trim()
}
finally{Pop-Location}
