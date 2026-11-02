# Cross-check the expanded creature DDS candidate set with the original
# Granny material authoring records AND an independently regenerated triplet
# survey. Source IDs remain provisional, never runtime-verified.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$oodle=Join-Path $root '.research\ooz.dll'
$catalog=Join-Path $root 'data\eso_creature_texture_candidates.tsv'
$names=Join-Path $root '.research\unlinked-model-names.tsv'
$report=Join-Path $root '.research\verified-authoring-triplet-survey.tsv'
foreach($file in @($tool,$manifest,$granny,$oodle,$catalog,$names)){
    if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing original ESO source: $file"}
}
Push-Location -LiteralPath $root
try{
    $entries=@()
    foreach($line in [IO.File]::ReadLines($catalog)){
        if($line.StartsWith('#') -or [string]::IsNullOrWhiteSpace($line)){continue}
        $f=$line.Split([char]9)
        if($f.Length -ne 5){throw 'Wrong source DDS manifest schema'}
        if($f[4] -eq 'archive-triplet-authoring-provisional'){
            $entries += [pscustomobject]@{
                meshId=$f[0];diffuseId=$f[1];normalId=$f[2];specularId=$f[3]
            }
        }
    }
    if($entries.Count -ne 50){throw "Unexpected native-authoring source count $($entries.Count)"}
    $run=(& dotnet $tool --survey-original-creature-texture-triplets $manifest $oodle $names $report 24 20000 2>&1|Out-String)
    if($LASTEXITCODE -ne 0){throw "Original source triplet survey failed: $run"}
    $survey=@(Import-Csv -LiteralPath $report -Delimiter ([char]9)|Where-Object {$_.'mesh-id' -match '^[0-9A-F]{8}$'})
    $passed=0
    $artistDiffuseForDds=@{}
    foreach($row in $entries){
        $matches=@($survey|Where-Object {$_.'mesh-id' -eq $row.meshId})
        if($matches.Count -ne 1){throw "Ambiguous original material candidate for mesh $($row.meshId)"}
        $m=$matches[0]
        if($m.'diffuse-id' -ne $row.diffuseId -or
           $m.'normal-id' -ne $row.normalId -or
           $m.'specular-id' -ne $row.specularId -or
           [int]$m.'id-distance' -gt 6 -or
           $m.evidence -ne 'archive-triplet-structural-provisional'){
            throw "Native source material association lacks triplet evidence: $($row.meshId)"
        }
        $details=(& dotnet $tool --audit-character-material $granny $row.meshId $manifest $oodle 2>&1|Out-String)
        if($LASTEXITCODE -ne 0){throw "Original Granny material cannot be inspected: $($row.meshId)"}
        $artistDiffuse=''
        foreach($channel in @('diffuse','normal','specular')){
            $pattern="(?m)^\s+$($channel)Path \[[^\r\n]+?\] value=(.+)$"
            $match=[regex]::Match($details,$pattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if(!$match.Success -or
               $match.Groups[1].Value.Trim() -notmatch '(?i)\.(tga|dds)$'){
                throw "Original Granny material authoring $channel not found for $($row.meshId)"
            }
            if($channel -eq 'diffuse')
            {
                $artistDiffuse=$match.Groups[1].Value.Trim().Replace('/','\').ToLowerInvariant()
            }
        }
        if($artistDiffuseForDds.ContainsKey($row.diffuseId) -and
           $artistDiffuseForDds[$row.diffuseId] -cne $artistDiffuse){
            throw "Conflicting native artwork paths share source DDS $($row.diffuseId): $($row.meshId)"
        }
        $artistDiffuseForDds[$row.diffuseId]=$artistDiffuse
        $passed++
    }
    Write-Output "PASS 50 new original creature DDS candidates: independent archive triplets, packed-normal channels, <=6 ID distance, unique source mesh association and three native Granny authored texture paths."
    Write-Output 'All 50 archive DDS ID assignments explicitly remain PROVISIONAL, with source-art collision checks.'
}
finally{Pop-Location}
