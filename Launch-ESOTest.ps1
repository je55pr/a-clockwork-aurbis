param(
    [switch]$BuildOnly,
    [switch]$ForceBuild,
    [switch]$FirstPersonLab,
    [switch]$ModelLab,
    [switch]$RigLab,
    [switch]$WalkComparisonLab,
    [switch]$LegacyExperimentalRigLab
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = Join-Path $projectRoot 'EsoTest.csproj'
$geometryProject = Join-Path $projectRoot 'tools\ModelGeometry\ModelGeometry.csproj'
$geometryAssembly = Join-Path $projectRoot 'tools\ModelGeometry\bin\Debug\net8.0\ModelGeometry.dll'
$projectMetadata = Join-Path $projectRoot '.godot'
$builtAssembly = Join-Path $projectRoot '.godot\mono\temp\bin\Debug\EsoTest.dll'
$stampFile = Join-Path $projectMetadata 'eso-test-build-fingerprint.txt'
$logFile = Join-Path $projectMetadata 'eso-test-launch.log'
# Select a local Godot .NET editor without hardcoding another repository's
# paths into the published source. Ignored configuration preserves shortcuts.
$godotExe = $env:ESO_TEST_GODOT_EXE
$localEditorFile = Join-Path $projectRoot '.research\godot-exe.txt'
if (-not $godotExe -and (Test-Path -LiteralPath $localEditorFile -PathType Leaf)) {
    $godotExe = (Get-Content -LiteralPath $localEditorFile -TotalCount 1).Trim()
}
if (-not $godotExe) {
    foreach ($name in @('godot-mono', 'godot', 'Godot')) {
        $candidate = Get-Command $name -ErrorAction SilentlyContinue
        if ($candidate) { $godotExe = $candidate.Source; break }
    }
}
if (-not $godotExe) {
    throw 'Godot .NET editor not found. Set ESO_TEST_GODOT_EXE or put its full path in the ignored .research\godot-exe.txt file.'
}

function Show-LaunchError([string]$message) {
    Write-Error $message -ErrorAction Continue
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            $message, 'ESO Test launcher',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    } catch {
        # Can still inspect the launcher log if desktop dialogs are unavailable.
    }
}

function Get-SourceFingerprint([string]$root) {
    # Track additions/deletions as well as edits; timestamp-only checks miss deletions.
    # All ignored directories are generated output or local third-party research.
    $skipDirectories = @('.git', '.godot', '.research', 'bin', 'obj', '.vs', '.idea')
    $sourceExtensions = @('.cs', '.csproj', '.gd', '.tscn', '.tres', '.gdshader',
                          '.shader', '.sln', '.props', '.targets', '.json', '.godot')
    $entries = New-Object 'System.Collections.Generic.List[string]'
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($root)

    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($childDirectory in [System.IO.Directory]::GetDirectories($directory)) {
            if ($skipDirectories -notcontains [System.IO.Path]::GetFileName($childDirectory)) {
                $pending.Push($childDirectory)
            }
        }
        foreach ($source in [System.IO.Directory]::GetFiles($directory)) {
            if ($sourceExtensions -notcontains [System.IO.Path]::GetExtension($source).ToLowerInvariant()) {
                continue
            }
            $file = [System.IO.FileInfo]::new($source)
            $relativePath = $source.Substring($root.Length).TrimStart([char]'\')
            $entries.Add(('{0}|{1}|{2}' -f $relativePath, $file.Length, $file.LastWriteTimeUtc.Ticks))
        }
    }

    $lines = $entries.ToArray()
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $manifest = [String]::Join([Environment]::NewLine, $lines)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest))
        return [Convert]::ToBase64String($hash)
    } finally {
        $sha.Dispose()
    }
}

try {
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Missing C# project: $projectFile"
    }
    if (-not (Test-Path -LiteralPath $godotExe -PathType Leaf)) {
        throw "Godot executable not found: $godotExe`nSet ESO_TEST_GODOT_EXE to another Godot .NET executable if it has moved."
    }
    if (-not (Test-Path -LiteralPath $projectMetadata)) {
        [void](New-Item -ItemType Directory -Path $projectMetadata -Force)
    }

    $fingerprint = Get-SourceFingerprint $projectRoot
    $previous = if (Test-Path -LiteralPath $stampFile) {
        (Get-Content -LiteralPath $stampFile -Raw).Trim()
    } else { '' }
    $needsBuild = $ForceBuild -or
        -not (Test-Path -LiteralPath $builtAssembly -PathType Leaf) -or
        $fingerprint -ne $previous

    $time = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    if ($needsBuild) {
        Set-Content -LiteralPath $logFile -Encoding UTF8 -Value "[$time] Changes detected; building ESO Test."
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $buildOutput = & $dotnet build $projectFile --nologo --verbosity quiet 2>&1
        $buildExitCode = $LASTEXITCODE
        $buildOutput | Out-File -LiteralPath $logFile -Append -Encoding UTF8
        if ($buildExitCode -ne 0) {
            $details = ($buildOutput | Select-Object -Last 12 | Out-String).Trim()
            throw "Build failed (exit code $buildExitCode).`n`n$details`n`nComplete log: $logFile"
        }
        if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) {
            throw "Build completed but the Godot C# assembly was not found:`n$builtAssembly"
        }
        Set-Content -LiteralPath $stampFile -Encoding ASCII -Value $fingerprint
        Add-Content -LiteralPath $logFile -Value '[Success] Build completed.'
    } else {
        Set-Content -LiteralPath $logFile -Encoding UTF8 -Value "[$time] C# build up to date; skipped."
    }

    # The opt-in GR2 helper is a separate process so native Granny DLLs never
    # load into Godot. It needs compilation, not a DLL or ESO install, at launch.
    if ($needsBuild -or -not (Test-Path -LiteralPath $geometryAssembly -PathType Leaf)) {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $geometryOutput = & $dotnet build $geometryProject --nologo --verbosity quiet 2>&1
        $geometryExitCode = $LASTEXITCODE
        $geometryOutput | Out-File -LiteralPath $logFile -Append -Encoding UTF8
        if ($geometryExitCode -ne 0) {
            Add-Content -LiteralPath $logFile -Value '[Warning] Optional model preview helper failed to compile. Library still works.'
        }
    }

    if ($BuildOnly) {
        if ($needsBuild) { Write-Output 'BUILT' } else { Write-Output 'SKIPPED' }
        exit 0
    }

    # Launch graphical Godot; no long-lived launcher console is required.
    $arguments = '--path "' + $projectRoot + '"'
    if ($FirstPersonLab) { $arguments += ' "res://scenes/first_person_lab.tscn"' }
    if ($ModelLab) { $arguments += ' "res://scenes/model_animation_lab.tscn"' }
    # The default rig lab contains only native ESO poses. Explicit legacy
    # mode preserves the earlier experimental prosthetic-face/gait tests.
    if ($RigLab) { $arguments += ' "res://scenes/eso_native_animation_lab.tscn"' }
    if ($WalkComparisonLab) { $arguments += ' "res://scenes/eso_walk_comparison_lab.tscn"' }
    if ($LegacyExperimentalRigLab) { $arguments += ' "res://scenes/source_rig_lab.tscn"' }
    $process = Start-Process -FilePath $godotExe -ArgumentList $arguments -WorkingDirectory $projectRoot -PassThru
    Add-Content -LiteralPath $logFile -Value "Started Godot PID $($process.Id): $godotExe"
    exit 0
} catch {
    $message = $_.Exception.Message
    if (Test-Path -LiteralPath $projectMetadata) {
        Add-Content -LiteralPath $logFile -Value "[ERROR] $message" -ErrorAction SilentlyContinue
    }
    Show-LaunchError "$message`n`nLauncher log: $logFile"
    exit 1
}
