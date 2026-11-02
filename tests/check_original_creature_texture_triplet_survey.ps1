# Source-only regression of structural ESO creature DDS triplet survey.
# Never accepts archive neighbours as proven runtime material bindings.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$helper=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$oodle=Join-Path $root '.research\ooz.dll'
$names=Join-Path $root '.research\unlinked-model-names.tsv'
$out=Join-Path $root '.research\creature-dds-triplet-survey-smoke.tsv'
foreach($p in @($helper,$manifest,$oodle,$names)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){throw "Missing original source: $p"}
}
Push-Location -LiteralPath $root
try{
    $run=(& dotnet $helper --survey-original-creature-texture-triplets $manifest $oodle $names $out 24 20000 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Original ESO triplet audit failed: $run"}
    $rows=@(Import-Csv -Path $out -Delimiter ([char]9)|Where-Object {$_.'mesh-id' -match '^[0-9A-F]{8}$'})
    if($rows.Count -lt 200){throw "Too few validated original ESO structural DDS candidates: $($rows.Count)"}
    $withCandidate=@($rows|Group-Object 'mesh-id')
    $unique=@($withCandidate|Where-Object {$_.Count -eq 1})
    if($withCandidate.Count -lt 150 -or $unique.Count -lt 100){
        throw "Insufficient original source creature mesh coverage"
    }
    $cases=@(
        @('00023B09','00023B03','00023B05','00023B07'),
        @('00023B3D','00023B37','00023B39','00023B3B'),
        @('00023C73','00023C6B','00023C6D','00023C6F'),
        @('00123257','00123251','00123253','00123255')
    )
    foreach($case in $cases){
        $found=@($rows | Where-Object {
            $_.'mesh-id' -eq $case[0] -and $_.'diffuse-id' -eq $case[1] -and
            $_.'normal-id' -eq $case[2] -and $_.'specular-id' -eq $case[3]})
        if($found.Count -ne 1){
            throw "Original creature source DDS triplet identity changed: $($case -join '/')"
        }
    }
    foreach($row in $rows){
        if($row.evidence -cne 'archive-triplet-structural-provisional'){
            throw "Source triplet scan falsely declared runtime material identity"
        }
        $mean=$row.'packed-normal-means'.Split(',')
        if($mean.Count -ne 2 -or
           [double]::Parse($mean[0],[Globalization.CultureInfo]::InvariantCulture) -lt 0.28 -or
           [double]::Parse($mean[0],[Globalization.CultureInfo]::InvariantCulture) -gt 0.73 -or
           [double]::Parse($mean[1],[Globalization.CultureInfo]::InvariantCulture) -lt 0.28 -or
           [double]::Parse($mean[1],[Globalization.CultureInfo]::InvariantCulture) -gt 0.73){
            throw "Original ESO packed-normal evidence invalid for $($row.'normal-id')"
        }
    }
    # Independent input known: two ESO DDS files were previously mistaken
    # for normals, but real DXT5 channel inspection rejects them.
    $normalTest=(& dotnet $helper --inspect-original-packed-normal $manifest $oodle 0029B151 00023B39 000A0A67 000AA37B 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Original DDS AG channel test failed: $normalTest"}
    foreach($id in @('0029B151','00023B39')){
        if($normalTest -notmatch "(?m)^$id .+packed-normal-candidate=True"){
            throw "Known original source packed normal rejected: $id"
        }
    }
    foreach($id in @('000A0A67','000AA37B')){
        if($normalTest -notmatch "(?m)^$id .+packed-normal-candidate=False"){
            throw "Known original ESO non-normal DDS incorrectly accepted: $id"
        }
    }
    $near=@($unique|Where-Object {[int]$_.Group[0].'id-distance' -le 6})
    Write-Output "PASS native ESO source-only survey: $($rows.Count) structurally plausible DDS triplets, $($withCandidate.Count) meshes, $($unique.Count) unambiguous, $($near.Count) close unambiguous."
    Write-Output 'PASS four known creature archive triplets, packed AG normals, and two non-normal negatives; all candidates remain explicitly PROVISIONAL.'
}
finally{Pop-Location}
