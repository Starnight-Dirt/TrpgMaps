# TrpgMaps - Win7 / 32-bit compatible edition - build script
# Uses the MSBuild shipped with Visual Studio 2022, or the one bundled with
# .NET Framework 4.x.
#
# A NuGet restore IS performed: the project references the build-time-only
# package Microsoft.NETFramework.ReferenceAssemblies. It adds no runtime
# dependency and copies nothing to the output folder - it only lets MSBuild
# compile for the chosen framework without that framework's Targeting Pack
# being installed locally.
#
# IMPORTANT: this file must stay ASCII-only. Windows PowerShell 5.1 reads a
# .ps1 without a BOM as ANSI, so non-ASCII comments get mangled and can break
# parsing.
#
# There is now a SINGLE target framework: v3.5. The same exe runs from
# Windows 7 SP1 through Windows 11 with nothing to install, because the
# <exe>.config falls back from the CLR 2.0 that Win7 ships to the CLR 4.x that
# every newer Windows ships. See TrpgMaps.csproj for the full reasoning.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Configuration Debug
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SelfCheck
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SelfTest
#
# Output: bin\Release\  (or bin\Debug\ for -Configuration Debug)
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Headless self check: renders the map canvas offscreen (basemap vs no
    # basemap), prints the measured cell size and the sidebar geometry.
    # Creates NO window and binds NO socket, so it is safe to run unattended -
    # nothing can pop up a "do you want to run this program" prompt.
    [switch]$SelfCheck,

    # Everything: the headless self check plus the HTTP tests and the UI
    # screenshots.
    # WARNING: the HTTP tests and the screenshots DO open a real window, which
    # on an unattended machine can trip a security prompt that nobody can answer.
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot | Split-Path -Parent
$csproj = Join-Path $projectDir 'TrpgMaps.csproj'
if (-not (Test-Path $csproj)) { throw "project not found: $csproj" }

function Assert-AsciiScripts {
    # Windows PowerShell 5.1 reads a .ps1 WITHOUT a BOM as ANSI (GBK on a Chinese
    # system). A Chinese character whose second GBK byte is 0x60 (a backtick)
    # then silently escapes the following newline and swallows the next line --
    # which shows up as a bizarre "unexpected token }" somewhere further down.
    # So: every .ps1 in this project must be pure ASCII. Fail loudly instead.
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

function Find-MSBuild {
    # 1) locate Visual Studio 2022 through vswhere
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($path) {
            $candidate = Join-Path $path.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path $candidate) { return $candidate }
        }
    }

    # 2) well-known install roots on every drive
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem).Name) {
        foreach ($edition in @('Enterprise', 'Professional', 'Community', 'BuildTools')) {
            $candidate = "${drive}:\Program Files\Microsoft Visual Studio\2022\$edition\MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }

    # 3) last resort: the MSBuild bundled with .NET Framework 4.x
    foreach ($fw in @('Framework64', 'Framework')) {
        $candidate = Join-Path $env:WINDIR "Microsoft.NET\$fw\v4.0.30319\MSBuild.exe"
        if (Test-Path $candidate) { return $candidate }
    }

    throw 'MSBuild not found. Install Visual Studio 2022 (with MSBuild) or .NET Framework 4.x.'
}

$msbuild = Find-MSBuild
Write-Host "MSBuild : $msbuild"

# Any running instance holds a lock on bin\Release\TrpgMaps.exe, and MSBuild
# responds with a wall of MSB3026 "Beginning retry N in 1000ms" lines that ends in
# MSB3027/MSB3021 -- which reads like a code error but is not. The test scripts
# already stop the app themselves; this is the safety net for a hand-launched copy
# or one left behind by an interrupted run.
$stale = @(Get-Process -Name 'TrpgMaps' -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    Write-Host ("Stopping {0} running instance(s) holding the output exe..." -f $stale.Count)
    $stale | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

$outSub = "bin\$Configuration\"

$logDir = Join-Path $projectDir 'build_logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir "build-$Configuration-x86.log"

# Force English MSBuild/VBCSCompiler messages so the log stays ASCII and is
# readable regardless of the machine's display language.
$env:VSLANG = '1033'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'

$msbuildArgs = @(
    $csproj,
    '/restore',
    '/t:Build',
    "/p:Configuration=$Configuration",
    '/p:Platform=x86',
    '/v:minimal',
    '/nologo'
)

Write-Host "Target  : net35  ->  $outSub"

& $msbuild $msbuildArgs 2>&1 | Tee-Object -FilePath $log
$exit = $LASTEXITCODE

if ($exit -ne 0) {
    Write-Host ""
    Write-Host "BUILD FAILED (exit $exit). Log: $log"
    exit $exit
}

$outDir = Join-Path $projectDir $outSub
$exe = Join-Path $outDir 'TrpgMaps.exe'
Write-Host ""
Write-Host "BUILD SUCCEEDED"
Write-Host "  framework : v3.5 (Win7 ~ Win11, nothing to install)"
Write-Host "  output    : $outDir"
Write-Host "  exe       : $exe"
Write-Host "  log       : $log"

if (-not ($SelfCheck -or $SelfTest)) { exit 0 }

# ------------------------------------------------- headless self check
# Offscreen rendering only: no window, no socket, no dialog. Safe unattended.
Write-Host ""
Write-Host "=== SELF CHECK (headless, no window) ==="
$checkDir = Join-Path $logDir 'selfcheck'
New-Item -ItemType Directory -Force -Path $checkDir | Out-Null

# Piping into Out-Null makes PowerShell wait for the (GUI-subsystem) exe to exit.
& $exe --selfcheck $checkDir 2>&1 | Out-Null
$checkCode = $LASTEXITCODE
$fail = 0

if ($checkCode -eq 0) {
    Write-Host "  [PASS] selfcheck        all grid/geometry expectations met"
} else {
    Write-Host ("  [FAIL] selfcheck        exit=$checkCode  report: " + (Join-Path $checkDir 'selfcheck.txt'))
    $fail++
}

$report = Join-Path $checkDir 'selfcheck.txt'
if (Test-Path $report) {
    Write-Host "  report   : $report"
    Write-Host "  images   : $checkDir"
    Write-Host "  Measure a rendered cell with: scripts\measure_grid.py <png> --expect <cm*37.8>"
}

if (-not $SelfTest) {
    Write-Host ""
    if ($fail -eq 0) { Write-Host "SELF CHECK PASSED"; exit 0 }
    Write-Host ("SELF CHECK FAILED ({0} item(s))" -f $fail)
    exit 1
}

# ------------------------------------------------- full test suite
# NOTE: the two scripts below start the app AND launch it as a real window;
# on an unattended machine that may raise a security prompt.
Write-Host ""
Write-Host "=== SELF TEST (starts the app, needs a window) ==="

foreach ($name in @('smoke_test.ps1', 'auth_test.ps1')) {
    $script = Join-Path $PSScriptRoot $name
    if (-not (Test-Path $script)) { continue }

    $testLog = Join-Path $logDir (($name -replace '\.ps1$', '') + '.log')
    & powershell -NoProfile -ExecutionPolicy Bypass -File $script *> $testLog
    $code = $LASTEXITCODE

    $summary = ''
    if (Test-Path $testLog) {
        # '>' redirection in Windows PowerShell 5.1 writes UTF-16LE, hence Unicode.
        $summary = (Get-Content $testLog -Encoding Unicode |
                    Where-Object { $_ -match 'ALL .* ?PASSED|FAILED' } |
                    Select-Object -Last 1)
    }

    if ($code -eq 0) {
        Write-Host ("  [PASS] {0,-15} {1}" -f $name, $summary)
    } else {
        Write-Host ("  [FAIL] {0,-15} {1}" -f $name, $summary)
        Write-Host ("         log: $testLog")
        $fail++
    }
}

# One process renders every screen: each extra GUI launch is another chance of
# tripping a prompt.
$shotScript = Join-Path $PSScriptRoot 'ui_shot.ps1'
if (Test-Path $shotScript) {
    $shotAll = Join-Path $logDir 'shot_all.txt'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $shotScript -Exe $exe *> $shotAll
    $shotCode = $LASTEXITCODE

    if ($shotCode -eq 0) {
        # Report the real count instead of a hardcoded one: the "all" step list
        # grows whenever a new screen is added (e.g. the drawing panel).
        $shotCount = 0
        if (Test-Path $shotAll) {
            $summary = Get-Content $shotAll -Encoding Unicode -ErrorAction SilentlyContinue |
                       Where-Object { $_ -match 'shots=\d+' } | Select-Object -Last 1
            if ($summary -match 'shots=(\d+)') { $shotCount = [int]$Matches[1] }
        }
        if ($shotCount -gt 0) {
            Write-Host "  [PASS] ui screenshots   build_logs\shots\ ($shotCount screens, one process)"
        } else {
            Write-Host "  [PASS] ui screenshots   build_logs\shots\ (one process)"
        }
    } else {
        Write-Host ("  [FAIL] ui screenshots   exit=$shotCode  log: $shotAll")
        $fail++
    }
}

Write-Host ""
if ($fail -eq 0) { Write-Host "SELF TEST PASSED"; exit 0 }
Write-Host ("SELF TEST FAILED ({0} item(s))" -f $fail)
exit 1
