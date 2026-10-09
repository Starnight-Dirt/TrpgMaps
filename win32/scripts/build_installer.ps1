# TrpgMaps - build ONLY the installer shell (installer\TrpgMapsSetup.csproj)
#
# Why this exists as a separate script: the full release flow (make_release.ps1)
# rebuilds the app, regenerates payload.zip (34 MB), and re-runs 20 assertions.
# When the thing being changed is just the installer's UI, that whole loop takes
# minutes and hides the only signal we care about (C# errors in the installer).
#
# This script compiles nothing but the installer, against whatever payload.zip
# is already on disk. Payload is untouched by the UI code, so reusing it is safe.
#
# -Preview also runs the new /uipreview mode and writes one PNG per wizard state
# into build_logs\ui_preview\. That mode skips the elevation check, so it works
# unattended - the normal launch path declares requireAdministrator and would
# raise a UAC prompt nobody can answer.
#
# ASCII-only on purpose (see build.ps1 for the full explanation).
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build_installer.ps1 -Preview
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Run the /uipreview screenshot mode afterwards.
    [switch]$Preview
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot | Split-Path -Parent
$csproj = Join-Path $projectDir 'installer\TrpgMapsSetup.csproj'
if (-not (Test-Path $csproj)) { throw "installer project not found: $csproj" }

function Assert-AsciiScripts {
    $bad = @()
    foreach ($file in (Get-ChildItem $PSScriptRoot -Filter '*.ps1' -File)) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        foreach ($b in $bytes) {
            if ($b -gt 127) { $bad += $file.Name; break }
        }
    }
    if ($bad.Count -gt 0) {
        throw ("these .ps1 files contain non-ASCII bytes (must be pure ASCII): " + ($bad -join ', '))
    }
}
Assert-AsciiScripts

# dot-source, not call: a child scope would swallow Get-MSBuildPath
. (Join-Path $PSScriptRoot '_msbuild.ps1')
$msbuild = Get-MSBuildPath
Write-Host "MSBuild : $msbuild"

# A running installer (or the app) holds the output exe open.
foreach ($name in @('TrpgMapsSetup', 'TrpgMapsUninstall')) {
    $stale = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if ($stale.Count -gt 0) {
        Write-Host ("Stopping {0} running {1} instance(s)..." -f $stale.Count, $name)
        $stale | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
}

$logDir = Join-Path $projectDir 'build_logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "build-installer-$Configuration.log"

$env:VSLANG = '1033'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'

# When previewing we build into a SEPARATE output folder with the asInvoker
# manifest. Two reasons:
#   * the shipped exe declares requireAdministrator, and Windows reads that
#     before any of our code runs - so even /uipreview would raise a UAC prompt;
#   * it keeps the real build output (which the release script copies from)
#     untouched.
$previewDir = ''
if ($Preview) {
    $previewOut = 'bin\Preview\'
    $previewDir = Join-Path $logDir 'ui_preview'
    $args = @(
        $csproj, '/restore', '/t:Build',
        "/p:Configuration=$Configuration",
        '/p:Platform=x86',
        "/p:OutputPath=$previewOut",
        '/p:ApplicationManifest=app.preview.manifest',
        '/v:minimal', '/nologo'
    )
    Write-Host "Preview build: $previewOut (asInvoker manifest)"
} else {
    $args = @(
        $csproj, '/restore', '/t:Build',
        "/p:Configuration=$Configuration",
        '/p:Platform=x86',
        '/v:minimal', '/nologo'
    )
}

& $msbuild @args 2>&1 | Tee-Object -FilePath $log
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    Write-Host ""
    Write-Host "INSTALLER BUILD FAILED (exit $exit). Log: $log"
    exit $exit
}

$exe = Join-Path $projectDir "installer\bin\$Configuration\TrpgMapsSetup.exe"
if ($Preview) { $exe = Join-Path $projectDir "installer\bin\Preview\TrpgMapsSetup.exe" }

Write-Host ""
Write-Host "INSTALLER BUILD SUCCEEDED"
Write-Host ("  exe : {0}  ({1:N0} bytes)" -f $exe, (Get-Item $exe).Length)
Write-Host ("  log : {0}" -f $log)

if (-not $Preview) { exit 0 }

# ---------------------------------------------------------------- preview
Write-Host ""
Write-Host "=== UI preview (no UAC, no registry writes) ==="
if (Test-Path $previewDir) {
    [System.IO.Directory]::Delete($previewDir, $true)
}
New-Item -ItemType Directory -Force -Path $previewDir | Out-Null

# Piping into Out-Null is what makes PowerShell wait for a GUI-subsystem exe.
#
# stderr goes to a FILE, not to 2>&1: GDI+ pushes "libpng warning: iCCP" onto
# stderr while saving the PNGs, and with $ErrorActionPreference='Stop' a native
# command writing to a merged stream aborts the script even though the exit code
# is 0. The file also keeps the warning around for inspection.
$errFile = Join-Path $previewDir 'stderr.txt'

# $ErrorActionPreference is 'Stop' for the build (we WANT it to abort on a failed
# build). Around this one call it has to be 'Continue': a native command that
# writes anything to stderr gets turned into a terminating error under 'Stop',
# and GDI+ always emits "libpng warning: iCCP" while saving the PNGs.
$savedEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$oldEapNative = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false
& $exe /uipreview $previewDir 2>$errFile | Out-Null
$code = $LASTEXITCODE
$ErrorActionPreference = $savedEap

$shots = @(Get-ChildItem $previewDir -Filter '*.png' -ErrorAction SilentlyContinue | Sort-Object Name)
foreach ($s in $shots) { Write-Host ("  {0,-22} {1,9} bytes" -f $s.Name, $s.Length) }
Write-Host ("  shots={0}  exit={1}" -f $shots.Count, $code)
Write-Host ("  dir -> {0}" -f $previewDir)

if (Test-Path $errFile) {
    $noise = Get-Content $errFile -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch 'libpng warning' }
    if ($noise) {
        Write-Host "  --- stderr (libpng noise filtered) ---"
        $noise | ForEach-Object { Write-Host ("  " + $_) }
    }
}

if ($code -ne 0 -or $shots.Count -lt 5) {
    $appLog = Join-Path (Split-Path $exe -Parent) 'app.log'
    if (Test-Path $appLog) {
        Write-Host "--- app.log (tail) ---"
        Get-Content $appLog -Tail 30 | ForEach-Object { Write-Host ("  " + $_) }
    }
    exit 1
}
exit 0
