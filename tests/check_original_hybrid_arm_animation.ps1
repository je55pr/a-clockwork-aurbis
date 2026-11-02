# Source-only audit of original hybrid ESO lower-arm records.
# Their genuine motion branch is animation-compatible. Their separate
# four customization skin joints are intentionally NOT remapped.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$tool=Join-Path $root 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$manifest=$env:ESO_MNF
if(!$manifest){$manifest='C:\ESO\The Elder Scrolls Online\depot\eso.mnf'}
$granny=$env:ESO_GRANNY_DLL
if(!$granny){$granny='C:\ESO\The Elder Scrolls Online\game\client\granny2_x64.dll'}
$oodle=Join-Path $root '.research\ooz.dll'
foreach($p in @($tool,$manifest,$granny,$oodle)){
    if(!(Test-Path -LiteralPath $p -PathType Leaf)){
        throw "Original ESO hybrid-arm dependency missing: $p"
    }
}
Push-Location -LiteralPath $root
try{
    foreach($part in @(
        @{Id='000E35AF';Name='ArmsLower_062';Vertices=528},
        @{Id='000E3851';Name='ArmsLower_061';Vertices=740}
    )){
        $id=$part.Id
        $raw=(& dotnet $tool --inspect-rig $granny $id $manifest $oodle)
        if($LASTEXITCODE -ne 0){throw "Original ESO hybrid source $id missing"}
        $json=$raw | ConvertFrom-Json
        $mesh=$json.RigMeshes[0]
        $bones=$json.RigSkeletons[0].Bones
        if($mesh.Name -ne $part.Name -or
           $mesh.Vertices.Count -ne $part.Vertices -or
           $bones.Count -ne 30 -or $mesh.BindingNames.Count -ne 12){
            throw "Original source hybrid mesh identity changed: $id"
        }
        $motion=@($mesh.BindingNames|Where-Object {$_ -like 'mshBn_*'})
        $custom=@($mesh.BindingNames|Where-Object {$_ -like 'arm_*'})
        if($motion.Count -ne 8 -or $custom.Count -ne 4){
            throw "Original hybrid skin moved/customization ratio changed: $id"
        }
        $native=(& dotnet $tool --inspect-native-binding $granny $id 000020A2 $manifest $oodle 2>&1|Out-String)
        if($LASTEXITCODE -ne 3 -or
           !$native.Contains('total=12, exact=8, name-differing=4, index-unmapped=0') -or
           !$native.Contains('INCOMPATIBLE_SOURCE_RIG:')){
            throw "Original Granny unexpectedly accepted or changed hybrid customization skin $id"
        }
        Write-Output "PASS ESO original $($part.Name) ${id}: 30 original skeleton joints, $($part.Vertices) source vertices, native Granny resolves 8/12 animation skin bindings; 4 unmapped customization controls correctly rejected."
    }
    Write-Output 'All 4/12 missing native binds per mesh remain unconverted. Not a complete NPC.'
}
finally{Pop-Location}
