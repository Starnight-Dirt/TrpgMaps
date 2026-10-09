# TrpgMaps - release packaging script (win7 / 32-bit edition)
#
# Produces, into  <workspace>\releases\ :
#   TrpgMaps_v<ver>_win32_installer.exe    single-file installer / uninstaller
#   TrpgMaps_v<ver>_win32_portable.zip     extract anywhere and run
#   TrpgMaps_v<ver>_win32_update.zip       in-app / manual upgrade package
#   version.json                           the manifest the app polls (gitee/github)
#
# And into <workspace>\ :
#   TrpgMaps_v<ver>_win32_src.zip          source tree for github / gitee
#   version.json                           same manifest, ready to commit
#   open\                                  that source zip, unpacked - the working
#                                          tree tools\upload_git.py pushes from
#
# The source zip is laid out exactly the way the PUBLIC repository has to look -
# extract it, push it, done:
#
#   Readme.md      end-user readme (outer layer; the only file users read)
#   .gitignore     outer-layer ignore rules
#   win32\         the project itself   <- NOTE: the local folder is named
#                                          differently, see below
#
# open\ is rebuilt from the zip on every release, so it is never hand-edited and
# can never drift from the package (its .git folder survives the unpack).
#
# The workspace-root Readme.md is a TEMPLATE, not the shipped file: it carries the
# {{VERSION}} placeholder and a TEMPLATE-ONLY note that this script strips while
# rendering. Edit the template, never open\Readme.md - that one is an output.
#
# The local development folder keeps its old (Chinese) name; only this published
# copy is called "win32", which is what AppInfo.RepoDir and the update URLs point
# at. The workspace root also carries a version.json for convenience, but the
# copy that actually matters is the one inside the project folder, because that
# is the one that lands in the repo at  win32\version.json .
#
# IMPORTANT: this file must stay ASCII-only. Windows PowerShell 5.1 reads a .ps1
# without a BOM as ANSI, and a Chinese character whose second GBK byte is 0x60
# (a backtick) then eats the following newline - which shows up much later as a
# bizarre "unexpected token }".
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1 -SkipBuild
#   powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1 -FullUpdate
#   powershell -ExecutionPolicy Bypass -File scripts\make_release.ps1 -LeanSource
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Reuse whatever is already in bin\Release instead of rebuilding.
    [switch]$SkipBuild,

    # Put maps\ and terrain\ into the update package too. Off by default:
    # those are ~34 MB of art that almost never changes, and the whole point of
    # an update package is that it is small.
    [switch]$FullUpdate,

    # Leave the map / terrain artwork out of the source zip as well.
    [switch]$LeanSource,

    # Sign the produced executables with tools\sign-release.ps1.
    [switch]$Sign
)

$ErrorActionPreference = 'Stop'

# Safety net, no longer load-bearing: PowerShell's own archive cmdlets write a
# progress bar per file, and for a 200-file payload that floods the log with
# thousands of lines of "oooo". We build the zips ourselves now (see New-Zip),
# but any future call to one of those cmdlets would bring the noise back.
$ProgressPreference = 'SilentlyContinue'

$projectDir = $PSScriptRoot | Split-Path -Parent          # the win7 project folder
$workspace  = $projectDir | Split-Path -Parent            # the workspace root
$outputDir  = Join-Path $projectDir "bin\$Configuration"
$releaseDir = Join-Path $workspace 'releases'
$stagingRoot = Join-Path $projectDir 'build_logs\release_staging'

function Assert-Ascii([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    foreach ($b in $bytes) { if ($b -gt 127) { throw "not ASCII: $path" } }
}
Assert-Ascii $PSCommandPath

# ---------------------------------------------------------------- version

$appInfoPath = Join-Path $projectDir 'Core\AppInfo.cs'
$appInfoText = Get-Content $appInfoPath -Raw
if ($appInfoText -notmatch 'public const string Version\s*=\s*"([0-9\.]+)"') {
    throw "cannot read AppInfo.Version from $appInfoPath"
}
$version = $Matches[1]
$prefix  = "TrpgMaps_v${version}_win32_"

Write-Host "TrpgMaps release packaging"
Write-Host "  version : $version"
Write-Host "  project : $projectDir"
Write-Host "  output  : $outputDir"

# ---------------------------------------------------------------- msbuild

# NOTE: dot-source (leading dot), not call (&): a child scope would swallow
# Get-MSBuildPath.
. (Join-Path $PSScriptRoot '_msbuild.ps1')
$msbuild = Get-MSBuildPath

function Invoke-MSBuild([string]$csproj) {
    $env:VSLANG = '1033'
    $env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
    & $msbuild $csproj /restore /t:Build "/p:Configuration=$Configuration" /p:Platform=x86 /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed: $csproj" }
}

function Reset-Directory([string]$path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

# Deleting an archive that was written seconds ago can fail with "file in use"
# when an antivirus or the search indexer has just opened it. Retrying a few
# times beats dying halfway through a release - that also skips the staging
# cleanup and leaves a few hundred MB behind.
function Remove-Leaf([string]$path) {
    if (-not (Test-Path $path)) { return }
    for ($i = 0; $i -lt 10; $i++) {
        try { [System.IO.File]::Delete($path); return }
        catch { Start-Sleep -Milliseconds 400 }
    }
    throw ("cannot delete, still locked: " + $path)
}

# One file per invocation on purpose: passing an array through "powershell -File"
# does not survive the child's argument parsing reliably.
function Invoke-Signer([string]$file) {
    $signer = Join-Path $workspace 'tools\sign-release.ps1'
    if (-not (Test-Path $signer)) { Write-Host "  sign-release.ps1 not found, skipped"; return }
    # The signer picks the digest from the output path: anything that is not
    # *net9.0-windows* is the net35 build, which gets SHA-1 because a bare
    # Windows 7 can only verify SHA-256 with KB3033929 installed.
    & powershell -NoProfile -ExecutionPolicy Bypass -File $signer -Path $file
}

# Zip writer, used for all four archives.
#
# Deliberately NOT Compress-Archive: on Windows PowerShell 5.1 that cmdlet stores
# the separator inside entry names as a BACKSLASH (verified at byte level:
# "win32\\.gitignore" instead of "win32/.gitignore"). Our own extractor copes with
# it and so does Explorer, so it went unnoticed - but the source zip exists to be
# unpacked into a Git repository, and any non-Windows tool (unzip, macOS Archive
# Utility, a CI runner) turns "win32\App.net35.config" into a FILE whose name
# contains a backslash instead of a folder. The ZIP spec wants forward slashes.
function New-Zip([string]$stage, [string]$zipPath) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    Remove-Leaf $zipPath

    $root = (Get-Item $stage).FullName.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem $stage -Recurse -File)) {
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally {
        $archive.Dispose()
    }
}

# Zip reader - the mirror image of New-Zip, used to unpack the source zip into the
# publish tree ( <workspace>\open ).
#
# It deliberately does NOT use [IO.Compression.ZipFile]::ExtractToDirectory:
#   * that one refuses to run when the target directory already exists and is not
#     empty, and we need "overwrite in place" because open\ holds a .git folder;
#   * it cannot express "delete whatever the zip no longer carries", which is the
#     whole point here - see the call site.
#
# Entry-name decoding was checked against Python's zipfile on the real package:
# 118 of 118 names identical, Chinese ones included, because New-Zip sets the
# UTF-8 language-encoding flag (0x800) on every entry and the BCL honours it. If
# that ever stops being true, the file-count assertion at the call site fires
# instead of letting a half-renamed tree reach the repo.
function Expand-ZipInto([string]$zipPath, [string]$target, [string[]]$keep) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

    if (-not (Test-Path $target)) { New-Item -ItemType Directory -Force -Path $target | Out-Null }
    $targetFull = (Get-Item $target).FullName.TrimEnd('\')

    $wanted = @{}
    $files = 0
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) { continue }   # directory entry
            $relative = $entry.FullName.Replace('/', '\')
            $dest = Join-Path $targetFull $relative
            $parent = Split-Path $dest -Parent
            if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
            $input = $entry.Open()
            $output = [System.IO.File]::Create($dest)
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
            $wanted[$relative] = $true
            $files++
        }
    } finally {
        $archive.Dispose()
    }

    # Sweep: anything under the target that the zip does not carry goes away, except
    # the names in $keep (open\.git). Without this an asset that was dropped from
    # the project would keep sitting in open\, get committed again, and reappear in
    # the repo for ever.
    $removed = 0
    foreach ($item in (Get-ChildItem $targetFull -Recurse -File -Force)) {
        $relative = $item.FullName.Substring($targetFull.Length + 1)
        $skip = $false
        foreach ($name in $keep) {
            if ($relative -eq $name -or $relative.StartsWith($name + '\')) { $skip = $true; break }
        }
        if ($skip) { continue }
        if (-not $wanted.ContainsKey($relative)) { Remove-Item $item.FullName -Force; $removed++ }
    }
    # Bottom-up, so folders emptied by the sweep above can go too.
    foreach ($dir in (Get-ChildItem $targetFull -Recurse -Directory -Force | Sort-Object FullName -Descending)) {
        $relative = $dir.FullName.Substring($targetFull.Length + 1)
        $skip = $false
        foreach ($name in $keep) {
            if ($relative -eq $name -or $relative.StartsWith($name + '\')) { $skip = $true; break }
        }
        if ($skip) { continue }
        if (@(Get-ChildItem $dir.FullName -Force).Count -eq 0) { Remove-Item $dir.FullName -Force }
    }

    return @{ Files = $files; Removed = $removed }
}

# Running instances hold a lock on the exe and make MSBuild report a wall of
# MSB3026/MSB3027 retry errors that look like code errors but are not.
$stale = @(Get-Process -Name 'TrpgMaps' -ErrorAction SilentlyContinue)
if ($stale.Count -gt 0) {
    Write-Host "Stopping $($stale.Count) running TrpgMaps instance(s)..."
    $stale | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

# ---------------------------------------------------------------- 1. build

if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "=== 1/6 build app ==="
    Invoke-MSBuild (Join-Path $projectDir 'TrpgMaps.csproj')

    Write-Host "=== 2/6 build updater ==="
    Invoke-MSBuild (Join-Path $projectDir 'updater\TrpgMapsUpdater.csproj')
} else {
    Write-Host ""
    Write-Host "=== 1-2/6 build skipped (-SkipBuild) ==="
}

$appExe     = Join-Path $outputDir 'TrpgMaps.exe'
$updaterExe = Join-Path $projectDir 'updater\bin\Release\TrpgMapsUpdater.exe'
foreach ($required in @($appExe, $updaterExe)) {
    if (-not (Test-Path $required)) { throw "missing build output: $required" }
}

# ---------------------------------------------------------------- sign (early!)
#
# Signing MUST happen here, before step 3, not at the end of the script.
#
# payload.zip, portable.zip and the payload embedded in the installer are all
# copied out of bin\Release by steps 3-5. Signing at the end (where this used to
# live) therefore only ever signed the installer shell: the TrpgMaps.exe that
# users actually receive - the one inside portable.zip and inside the installer -
# stayed unsigned, which is exactly the bug this block exists to prevent.

if ($Sign) {
    Write-Host ""
    Write-Host "=== 2.5/6 sign app + updater (before packaging) ==="
    Invoke-Signer $appExe
    Invoke-Signer $updaterExe
}

# ---------------------------------------------------------------- 3. payload

Write-Host ""
Write-Host "=== 3/6 assemble payload ==="

Reset-Directory $stagingRoot
$payloadStage = Join-Path $stagingRoot 'payload'
Reset-Directory $payloadStage

# What ships and what does not. Deliberately an allow-list: anything new that a
# developer drops into the output folder by accident must not leak into a release.
$payloadFiles = @('TrpgMaps.exe', 'TrpgMaps.exe.config')
$payloadDirs  = @('Resources', 'wwwroot', 'maps', 'terrain', 'entity', 'item')

# The "fix the firewall" helper is a source file rather than a build output, so it
# is copied from the project root. Its name is Chinese and this script has to stay
# pure ASCII (see Assert-AsciiScripts), so the name is assembled from code points:
#   0x653E 0x884C 0x9632 0x706B 0x5899 = "fang xing fang huo qiang"
# Without it the end-user Readme would tell people to run a file that is not there.
$firewallBatName = [string]::Concat(
    [char]0x653E, [char]0x884C, [char]0x9632, [char]0x706B, [char]0x5899, '.bat')

foreach ($file in $payloadFiles) {
    $source = Join-Path $outputDir $file
    if (Test-Path $source) { Copy-Item $source (Join-Path $payloadStage $file) -Force }
}
foreach ($dir in $payloadDirs) {
    $source = Join-Path $outputDir $dir
    if (-not (Test-Path $source)) { continue }
    $target = Join-Path $payloadStage $dir
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item (Join-Path $source '*') $target -Recurse -Force
}

$firewallBatSource = Join-Path $projectDir $firewallBatName
if (Test-Path $firewallBatSource) {
    Copy-Item $firewallBatSource (Join-Path $payloadStage $firewallBatName) -Force
    Write-Host ("  + {0}" -f $firewallBatName)
} else {
    Write-Host "  WARNING: firewall helper batch file is missing from the project root"
}

# Never ship these: they are per-machine runtime state, not program data.
foreach ($junk in @('app.log', 'drawing.json', 'TrpgMaps.pdb', 'TrpgMapsUpdater.log')) {
    $path = Join-Path $payloadStage $junk
    if (Test-Path $path) { Remove-Item $path -Force }
}

$payloadCount = (Get-ChildItem $payloadStage -Recurse -File).Count
$payloadBytes = (Get-ChildItem $payloadStage -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("  payload: {0} files, {1:N1} MB" -f $payloadCount, ($payloadBytes / 1MB))

$payloadZip = Join-Path $projectDir 'installer\payload.zip'
New-Zip $payloadStage $payloadZip
Write-Host ("  payload.zip: {0:N1} MB" -f ((Get-Item $payloadZip).Length / 1MB))

# ---------------------------------------------------------------- 4. installer

Write-Host ""
Write-Host "=== 4/6 build installer ==="
Invoke-MSBuild (Join-Path $projectDir 'installer\TrpgMapsSetup.csproj')

$setupExe = Join-Path $projectDir 'installer\bin\Release\TrpgMapsSetup.exe'
if (-not (Test-Path $setupExe)) { throw "missing installer: $setupExe" }

# ---------------------------------------------------------------- 5. releases

Write-Host ""
Write-Host "=== 5/6 assemble releases ==="
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

$installerOut = Join-Path $releaseDir ($prefix + 'installer.exe')
Copy-Item $setupExe $installerOut -Force
Write-Host ("  installer : {0}  ({1:N1} MB)" -f (Split-Path $installerOut -Leaf), ((Get-Item $installerOut).Length / 1MB))

# --- portable ---
$portableStage = Join-Path $stagingRoot 'portable'
Reset-Directory $portableStage
Copy-Item (Join-Path $payloadStage '*') $portableStage -Recurse -Force

$portableZip = Join-Path $releaseDir ($prefix + 'portable.zip')
New-Zip $portableStage $portableZip
Write-Host ("  portable  : {0}  ({1:N1} MB)" -f (Split-Path $portableZip -Leaf), ((Get-Item $portableZip).Length / 1MB))

# --- update ---
# Layout (all paths inside the zip are relative to its root):
#   payload\...               files to overwrite in the install dir
#   TrpgMapsUpdater.exe       does the actual work in a separate process
#   update.json               version + list of files to delete
#   delete.bat                manual "delete obsolete files" script
#   fix-registry.bat          manual "repair installer bookkeeping" script
#   update-readme.txt         what this package is
$updateStage = Join-Path $stagingRoot 'update'
Reset-Directory $updateStage

# ---- obsolete list ----------------------------------------------------------
#
# Artwork that older builds shipped as PNG and that is now much smaller .webp.
# Two generations of this:
#   * maps\    - 19 PNG basemaps, superseded in round 12
#   * terrain\ - 14 PNG drawing assets (1254x1254, ~31 MB in total), superseded
#                in round 17. Leaving those behind is not just wasted disk: the
#                asset picker scans terrain\ and lists every allowed extension,
#                so an upgrade would show each terrain twice (the stale .png and
#                the new .webp) under the same display name.
#
# The list is DERIVED from the files actually present (X.webp -> <dir>/X.png)
# rather than hard-coded, for two reasons:
#   * it stays correct when artwork is added or renamed;
#   * those names are Chinese, and this .ps1 must remain pure ASCII.
# Only these exact names are ever deleted - user-uploaded art is never touched.
#
# This has to be computed BEFORE the update payload is assembled, because the
# payload depends on it (see the "never delete without replacing" block below).
$obsolete = @()
$artworkDirs = @('maps', 'terrain', 'entity', 'item')
foreach ($artDir in $artworkDirs) {
    $artPath = Join-Path $projectDir $artDir
    if (-not (Test-Path $artPath)) { continue }
    foreach ($file in (Get-ChildItem $artPath -File -Filter '*.webp')) {
        $obsolete += ($artDir + '/' + [System.IO.Path]::GetFileNameWithoutExtension($file.Name) + '.png')
    }
}
Write-Host ("  obsolete list: {0} file(s)" -f $obsolete.Count)

# ---- update payload ---------------------------------------------------------
$updatePayload = Join-Path $updateStage 'payload'
Reset-Directory $updatePayload

Copy-Item (Join-Path $payloadStage 'TrpgMaps.exe') $updatePayload -Force
Copy-Item (Join-Path $payloadStage 'TrpgMaps.exe.config') $updatePayload -Force
Copy-Item (Join-Path $payloadStage 'Resources') (Join-Path $updatePayload 'Resources') -Recurse -Force
Copy-Item (Join-Path $payloadStage 'wwwroot')  (Join-Path $updatePayload 'wwwroot')  -Recurse -Force

# Old installs do not have the firewall helper yet; the update is how they get it.
if (Test-Path (Join-Path $payloadStage $firewallBatName)) {
    Copy-Item (Join-Path $payloadStage $firewallBatName) $updatePayload -Force
}

# NEVER DELETE WITHOUT REPLACING.
#
# This is the invariant that keeps an upgrade from turning into data loss. Every
# folder that contributes an entry to deleteFiles must also ship its replacement
# in the payload - otherwise the updater deletes the old PNGs and puts nothing
# back, leaving the user with an EMPTY maps\ or terrain\ folder.
#
# That is exactly what round 17 would have done had this not been caught: the
# default update package carries only the program files (artwork is ~4 MB of
# "almost never changes" data, deliberately excluded), while the delete list
# still pointed at terrain\*.png.
#
# -FullUpdate ships all of it anyway; without it we force in just the folders
# that this particular upgrade is about to delete from.
$forcedArtDirs = @()
foreach ($relative in $obsolete) {
    $slash = $relative.IndexOf('/')
    if ($slash -le 0) { continue }
    $dir = $relative.Substring(0, $slash)
    if ($forcedArtDirs -notcontains $dir) { $forcedArtDirs += $dir }
}

if ($FullUpdate) {
    foreach ($dir in $artworkDirs) {
        $source = Join-Path $payloadStage $dir
        if (Test-Path $source) { Copy-Item $source (Join-Path $updatePayload $dir) -Recurse -Force }
    }
    Write-Host "  -FullUpdate: artwork folders copied into the update payload"
} else {
    foreach ($dir in $forcedArtDirs) {
        $source = Join-Path $payloadStage $dir
        if (-not (Test-Path $source)) { continue }
        Copy-Item $source (Join-Path $updatePayload $dir) -Recurse -Force
        Write-Host ("  + {0}\ forced into the payload (this update deletes its old PNGs)" -f $dir)
    }
    if ($forcedArtDirs.Count -eq 0) {
        Write-Host "  artwork: no obsolete files, program files only"
    }
}

Copy-Item $updaterExe (Join-Path $updateStage 'TrpgMapsUpdater.exe') -Force

$updateJson = @{
    version     = $version
    payload     = 'payload'
    deleteFiles = $obsolete
    notes       = 'TrpgMaps update package: replaces program files and removes the legacy PNG artwork (maps\ basemaps and terrain\ drawing assets) that was superseded by much smaller webp files.'
}
$updateJsonPath = Join-Path $updateStage 'update.json'
$updateJsonText = $updateJson | ConvertTo-Json -Depth 4
[System.IO.File]::WriteAllText($updateJsonPath, $updateJsonText, (New-Object System.Text.UTF8Encoding($false)))

# Manual scripts. Pure ASCII on purpose: cmd.exe interprets a .bat using the
# current code page, so any non-ASCII literal inside would be at the mercy of
# the machine's locale. They take the install dir as an argument instead, and
# an empty argument makes the updater look it up in the registry.
$deleteBat = @'
@echo off
rem TrpgMaps - delete obsolete files (needs the extracted update package).
rem usage: delete.bat ["C:\Program Files (x86)\TrpgMaps"]
setlocal
if "%~1"=="" (
  "%~dp0TrpgMapsUpdater.exe" --delete-only "%~dp0" "-"
) else (
  "%~dp0TrpgMapsUpdater.exe" --delete-only "%~dp0" "%~1"
)
endlocal
'@
[System.IO.File]::WriteAllText((Join-Path $updateStage 'delete.bat'), $deleteBat, [System.Text.Encoding]::ASCII)

$fixBat = @'
@echo off
rem TrpgMaps - repair install bookkeeping (installer edition only).
rem Writes the uninstall entry and recreates the shortcuts. Needs admin.
rem usage: fix-registry.bat ["C:\Program Files (x86)\TrpgMaps"]
setlocal
if "%~1"=="" (
  "%~dp0TrpgMapsUpdater.exe" --register-only "%~dp0" "-"
) else (
  "%~dp0TrpgMapsUpdater.exe" --register-only "%~dp0" "%~1"
)
endlocal
'@
[System.IO.File]::WriteAllText((Join-Path $updateStage 'fix-registry.bat'), $fixBat, [System.Text.Encoding]::ASCII)

$updateReadme = @"
TrpgMaps $version - win32 update package
=========================================

Contents
  payload\                 program files to overwrite, plus any artwork this
                           update replaces (never left half-done: a folder is
                           only emptied if its replacement ships here too)
  TrpgMapsUpdater.exe      the updater
  update.json              version + list of files to delete
  delete.bat               remove obsolete files (manual)
  fix-registry.bat         repair install bookkeeping (installer edition only)

Automatic update
  Open TrpgMaps, click the "About" button at the bottom of the sidebar and
  press "Check for updates". The program downloads this package, then hands
  control to TrpgMapsUpdater.exe, which waits for TrpgMaps to exit, overwrites
  the files, deletes the obsolete ones, repairs the registry (installer
  edition only) and restarts the program.

Manual update
  1. Extract this package anywhere. TrpgMaps itself must be closed.
  2. Copy everything inside payload\ over your TrpgMaps folder.
  3. Run delete.bat  (optional, removes obsolete files).
  4. Installer edition only: run fix-registry.bat as administrator.
     This is what makes the Control Panel entry show the new version number.

Notes
  * The portable edition is detected automatically: if the program folder is
    not the one recorded in the registry, only files are replaced and the
    registry is left alone.
  * Your maps and drawings (maps\ and drawing.json) are never touched.
"@
[System.IO.File]::WriteAllText((Join-Path $updateStage 'update-readme.txt'), $updateReadme, (New-Object System.Text.UTF8Encoding($false)))

$updateZip = Join-Path $releaseDir ($prefix + 'update.zip')
New-Zip $updateStage $updateZip
Write-Host ("  update    : {0}  ({1:N1} MB)" -f (Split-Path $updateZip -Leaf), ((Get-Item $updateZip).Length / 1MB))

# ---------------------------------------------------------------- functional verification
#
# Everything below runs WITHOUT administrator rights and without a GUI, so it is
# safe to run unattended. That matters: the real install path needs UAC, so the
# only way to keep it from rotting is to test the parts underneath it directly.

Write-Host ""
Write-Host "=== functional verification ==="
$verifyFailed = 0

function Assert-True([bool]$condition, [string]$what) {
    if ($condition) {
        Write-Host ("  [PASS] {0}" -f $what)
    } else {
        Write-Host ("  [FAIL] {0}" -f $what)
        $script:verifyFailed++
    }
}

# Byte search, used to look for file names inside zip archives and for the
# embedded resource name inside the installer exe - both store names as UTF-8.
#
# Built on top of String.IndexOf rather than a hand-rolled two-loop scan: going
# over a 35 MB archive one byte at a time in PowerShell takes minutes, while the
# same search through a Latin-1 string is a native memchr. Code page 28591 maps
# bytes 0..255 onto chars 1:1, so the returned index is still a byte offset.
function Find-Bytes([byte[]]$haystack, [byte[]]$needle) {
    if ($needle.Length -eq 0) { return -1 }
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    return $latin1.GetString($haystack).IndexOf(
        $latin1.GetString($needle), [System.StringComparison]::Ordinal)
}

# --- 1. every package must open with our own zip reader ---
foreach ($zip in @($payloadZip, $portableZip, $updateZip)) {
    # The updater is a GUI-subsystem exe: piping into Out-Null is what makes
    # PowerShell wait for it to exit.
    & $updaterExe --test-zip $zip | Out-Null
    Assert-True ($LASTEXITCODE -eq 0) ("zip readable: " + (Split-Path $zip -Leaf))
}

$updaterLog = Join-Path $projectDir 'updater\bin\Release\TrpgMapsUpdater.log'
if (Test-Path $updaterLog) {
    Write-Host "  --- updater log ---"
    Get-Content $updaterLog | ForEach-Object { Write-Host "  $_" }
}

# --- 2. the installer must carry the payload as an embedded resource ---
#
# We can NOT simply run "TrpgMapsSetup.exe /selftest" here: the manifest declares
# requireAdministrator (it has to - it writes to Program Files and HKLM), so
# launching it would raise a UAC prompt, which is exactly what an unattended
# script must never do. Instead we check the two things that could actually go
# wrong with the embedding:
#   * the resource name must be there at all (a typo in LogicalName would leave
#     the installer with no payload, and that only shows up when a user runs it);
#   * the file must be big enough to contain the payload.
# .NET stores embedded resource names as UTF-8 in the metadata strings heap, so a
# raw byte search for the name is a valid existence check.
$setupBytes = [System.IO.File]::ReadAllBytes($setupExe)
$setupText = [System.Text.Encoding]::UTF8.GetString($setupBytes)
Assert-True ($setupText.Contains('TrpgMaps.payload.zip')) "installer embeds resource TrpgMaps.payload.zip"

$payloadZipSize = (Get-Item $payloadZip).Length
$setupSize = (Get-Item $setupExe).Length
Assert-True ($setupSize -ge $payloadZipSize) "installer is at least as large as the payload ($setupSize vs $payloadZipSize)"
Assert-True (($setupSize - $payloadZipSize) -lt 4MB) "installer overhead is small ($([int](($setupSize - $payloadZipSize) / 1KB)) KB)"

Write-Host "  (installer /selftest needs an elevated prompt, so it is not run here)"

# --- 2b. the firewall helper must really be inside the shipped packages ---
#
# The end-user Readme tells people to double-click it, so shipping without it
# would be a broken instruction. Zip entries store names as UTF-8 when the
# language-encoding flag is set (New-Zip sets it for non-ASCII names), so a byte
# search is a valid existence check - same trick as the embedded resource above.
$firewallNeedle = [System.Text.Encoding]::UTF8.GetBytes($firewallBatName)
foreach ($package in @(
        @{ Path = $portableZip; Label = 'portable' },
        @{ Path = $updateZip;   Label = 'update'   })) {
    $bytes = [System.IO.File]::ReadAllBytes($package.Path)
    Assert-True ((Find-Bytes $bytes $firewallNeedle) -ge 0) `
        ("firewall helper present in " + $package.Label + " package")
}

# --- 2c. a shipped .bat that is not pure ASCII must at least be CRLF --------
#
# cmd.exe re-reads a running batch file by BYTE offset. A multi-byte (Chinese)
# character combined with an LF-only line ending makes it resume in the middle
# of a character, so it starts executing a fragment of a line as a command.
# Measured on this machine with a 6-line probe:
#   * Chinese + LF   -> fails on almost every run
#   * Chinese + CRLF -> still fails roughly 1 run in 30
#   * pure ASCII     -> 0 failures in 30 runs, LF or CRLF
# Hence the rule: keep a .bat PURE ASCII whenever possible (that is what the
# upload helper does, and its Chinese wording lives in tools\upload_git.py where
# Python handles UTF-8 properly). If a .bat really must carry Chinese text,
# make it CRLF so it is merely flaky instead of always broken.
$batFiles = @(Get-ChildItem $projectDir -File -Filter '*.bat')
foreach ($bat in $batFiles) {
    $bytes = [System.IO.File]::ReadAllBytes($bat.FullName)

    $hasNonAscii = $false
    foreach ($b in $bytes) { if ($b -gt 127) { $hasNonAscii = $true; break } }
    if (-not $hasNonAscii) { continue }

    $bareLf = 0
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) { $bareLf++ }
    }
    Assert-True ($bareLf -eq 0) `
        ("non-ASCII .bat must use CRLF: " + $bat.Name + " has " + $bareLf + " bare LF")
}
Assert-True ($batFiles.Count -gt 0) "the project folder ships its .bat helpers"

# --- 3. update package: delete list must remove exactly what it lists ---
$fakeDir = Join-Path $stagingRoot 'fake_install'
Reset-Directory $fakeDir

$manifestJsonPath = Join-Path $updateStage 'update.json'
# -Encoding UTF8 is mandatory: PowerShell 5.1 defaults to ANSI and would turn
# the Chinese file names in the list into mojibake.
$deleteList = (Get-Content $manifestJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json).deleteFiles

foreach ($relative in $deleteList) {
    $path = Join-Path $fakeDir ($relative -replace '/', '\')
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    Set-Content -Path $path -Value 'obsolete' -Encoding ASCII
}
# A file the updater must never touch (stands in for a user-uploaded map).
$keepFile = Join-Path $fakeDir 'maps\my_own_map.png'
Set-Content -Path $keepFile -Value 'user data' -Encoding ASCII
# And a real webp that a newer version ships - must survive.
$keepWebp = Join-Path $fakeDir 'maps\keep-me.webp'
Set-Content -Path $keepWebp -Value 'shipped' -Encoding ASCII

& $updaterExe --delete-only $updateStage $fakeDir | Out-Null
$deleteCode = $LASTEXITCODE

$stillThere = 0
foreach ($relative in $deleteList) {
    $path = Join-Path $fakeDir ($relative -replace '/', '\')
    if (Test-Path $path) { $stillThere++ }
}

Assert-True ($deleteCode -eq 0) "update delete script exits 0"
Assert-True ($stillThere -eq 0) ("obsolete files removed ({0} left of {1})" -f $stillThere, $deleteList.Count)
Assert-True (Test-Path $keepFile) "user-uploaded file untouched"
Assert-True (Test-Path $keepWebp) "unlisted file untouched"

# --- 3b. the delete list must cover EVERY artwork folder that switched format ---
#
# Round 17 moved terrain\ from 14 PNGs (~31 MB) to webp. Had the list kept
# covering maps\ only, an upgrade would leave all 14 old terrain PNGs on disk:
# 31 MB of junk, and worse - the asset picker scans the folder and lists every
# allowed extension, so each terrain would show up twice under the same name
# (the stale .png and the new .webp). Assert it per directory, so dropping one
# from the derivation above fails here instead of quietly shipping.
foreach ($artDir in @('maps', 'terrain', 'entity', 'item')) {
    $artPath = Join-Path $projectDir $artDir
    if (-not (Test-Path $artPath)) { continue }
    $expected = @(Get-ChildItem $artPath -File -Filter '*.webp').Count
    if ($expected -eq 0) { continue }
    $listed = @($deleteList | Where-Object { $_ -like ($artDir + '/*') }).Count
    Assert-True ($listed -eq $expected) `
        ("delete list covers " + $artDir + " PNGs: " + $listed + " of " + $expected)
}

# --- 3c. never delete without replacing -------------------------------------
#
# The delete list must never point at a folder whose replacement files are not
# in the payload: the updater would wipe the old PNGs and leave nothing behind.
# Round 17 is exactly that scenario (terrain PNG -> webp, artwork normally kept
# out of the update package), so this is checked, not assumed.
foreach ($artDir in @('maps', 'terrain', 'entity', 'item')) {
    $inDelete = @($deleteList | Where-Object { $_ -like ($artDir + '/*') }).Count
    if ($inDelete -eq 0) { continue }
    $shippedPath = Join-Path $updatePayload $artDir
    $shipped = 0
    if (Test-Path $shippedPath) { $shipped = @(Get-ChildItem $shippedPath -File).Count }
    Assert-True ($shipped -gt 0) `
        ("update payload replaces " + $artDir + ": " + $shipped + " shipped, " + $inDelete + " old PNGs deleted")
}

# --- 4. update package: payload must land in the target folder ---
& $updaterExe --apply $updateStage $fakeDir --no-relaunch | Out-Null
$applyCode = $LASTEXITCODE
Assert-True ($applyCode -eq 0) "update apply exits 0"
Assert-True (Test-Path (Join-Path $fakeDir 'TrpgMaps.exe')) "payload: TrpgMaps.exe copied"
Assert-True (Test-Path (Join-Path $fakeDir 'TrpgMaps.exe.config')) "payload: .exe.config copied"
Assert-True (Test-Path (Join-Path $fakeDir 'wwwroot\player.html')) "payload: wwwroot copied"
Assert-True (Test-Path (Join-Path $fakeDir 'Resources\avatar.png')) "payload: Resources copied"
Assert-True (Test-Path $keepFile) "user data still untouched after apply"
Assert-True (-not (Test-Path (Join-Path $fakeDir 'TrpgMapsUpdater.exe'))) "updater itself not copied into target"

# ---------------------------------------------------------------- 6. manifest + source zip

Write-Host ""
Write-Host "=== 6/6 manifest + source zip ==="

$manifest = [ordered]@{
    version      = $version
    notes        = "TrpgMaps $version - win32 build"
    update       = ($prefix + 'update.zip')
    portable     = ($prefix + 'portable.zip')
    installer    = ($prefix + 'installer.exe')
    mandatory    = $false
    releaseNotes = "https://gitee.com/starnight-dirt/TrpgMaps/releases"
}
$manifestText = $manifest | ConvertTo-Json -Depth 4
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

[System.IO.File]::WriteAllText((Join-Path $releaseDir 'version.json'), $manifestText, $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $workspace 'version.json'), $manifestText, $utf8NoBom)
# The copy that actually matters: the one inside the project folder, because that
# is the one that lands in the repo as  win32\version.json . The app polls
# <repo>/raw/<branch>/win32/version.json - see AppInfo.RepoDir / Core\UpdateCheck.cs.
[System.IO.File]::WriteAllText((Join-Path $projectDir 'version.json'), $manifestText, $utf8NoBom)
Write-Host "  version.json written to releases\, workspace root and the project folder"

# --- source zip ---
#
# The published tree. The project goes into a subfolder called win32 even though
# the LOCAL folder name is different: AppInfo.RepoDir and the update URLs are
# spelled with "win32", so the folder name in the repo has to agree with them.
$sourceStage  = Join-Path $stagingRoot 'source'
$sourceAppDir = Join-Path $sourceStage 'win32'
Reset-Directory $sourceStage

$sourceExcludeDirs  = @('bin', 'obj', 'build_logs', '.vs')
$sourceExcludeFiles = @('app.log', 'drawing.json', 'payload.zip', 'TrpgMapsUpdater.log')

function Copy-SourceTree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force -Path $to | Out-Null
    foreach ($dir in (Get-ChildItem $from -Directory)) {
        if ($sourceExcludeDirs -contains $dir.Name) { continue }
        Copy-SourceTree $dir.FullName (Join-Path $to $dir.Name)
    }
    foreach ($file in (Get-ChildItem $from -File)) {
        if ($sourceExcludeFiles -contains $file.Name) { continue }
        if ($file.Extension -eq '.pdb') { continue }
        Copy-Item $file.FullName (Join-Path $to $file.Name) -Force
    }
}

Copy-SourceTree $projectDir $sourceAppDir

if ($LeanSource) {
    foreach ($dir in @('maps', 'terrain', 'entity', 'item')) {
        $path = Join-Path $sourceAppDir $dir
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }
    Write-Host "  -LeanSource: artwork removed from the source zip"
}

# Outer layer of the published tree: the end-user readme and the outer ignore
# file. Both live in the workspace root, so after this the zip root holds
# Readme.md + .gitignore + win32\ and nothing else.
#
# Readme.md is RENDERED, not copied (it is a template - see its TEMPLATE-ONLY note):
#   * {{VERSION}} -> AppInfo.Version. The readme quotes the current version in
#     three places, and AppInfo.cs is documented as the single source of truth for
#     it; hand-copying it into a fourth place is exactly the mistake that comment
#     warns about ("changed one copy and forgot the other").
#   * line endings forced to LF and written UTF-8 without BOM, so changing editors
#     cannot turn the whole file into one giant diff in the repo.
#   * the TEMPLATE-ONLY comment block is dropped - it is a note for whoever edits
#     the template, not for people reading the repository.
$readmeTemplate = Join-Path $workspace 'Readme.md'
if (-not (Test-Path $readmeTemplate)) { throw ("missing the end-user readme template: " + $readmeTemplate) }

$readmeText = [System.IO.File]::ReadAllText($readmeTemplate)
$readmeText = $readmeText.Replace("`r`n", "`n").Replace("`r", "`n")
$readmeText = [System.Text.RegularExpressions.Regex]::Replace(
    $readmeText, '^\s*<!--\s*TEMPLATE-ONLY.*?-->[ \t]*\n', '',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
$readmeText = $readmeText.Replace('{{VERSION}}', $version)

# A mistyped placeholder would make the substitution above silently do nothing and
# ship a readme advertising a literal {{VERSION}}, so the render is checked rather
# than assumed - same reasoning as the layout assertions further down.
#
# The "starts at the H1" check is here because the strip regex is anchored on the
# TEMPLATE-ONLY marker: if that marker is ever renamed, the regex silently stops
# matching and the note ships. Checking "does not contain TEMPLATE-ONLY" is NOT
# enough - that assertion also passes when the marker never existed (it did exactly
# that, which is how the leak was found).
Assert-True (-not ($readmeText -match '\{\{')) "rendered Readme has no {{ }} left"
Assert-True ($readmeText.Contains('v' + $version)) ("rendered Readme names v" + $version)
Assert-True (-not ($readmeText.Contains("`r"))) "rendered Readme is LF-only"
Assert-True (-not ($readmeText.Contains('TEMPLATE-ONLY'))) "rendered Readme drops the template note"
Assert-True ($readmeText.TrimStart().StartsWith('# TrpgMaps')) "rendered Readme starts at the H1"

[System.IO.File]::WriteAllText((Join-Path $sourceStage 'Readme.md'), $readmeText, $utf8NoBom)

$outerIgnore = Join-Path $workspace '.gitignore'
if (Test-Path $outerIgnore) {
    Copy-Item $outerIgnore $sourceStage -Force
} else {
    Write-Host "  WARNING: missing outer file in the workspace root: .gitignore"
}

$sourceZip = Join-Path $workspace ('TrpgMaps_v' + $version + '_win32_src.zip')
New-Zip $sourceStage $sourceZip
Write-Host ("  source    : {0}  ({1:N1} MB)" -f (Split-Path $sourceZip -Leaf), ((Get-Item $sourceZip).Length / 1MB))

# The published layout is a hard requirement - the update URL points straight
# into it - so assert it rather than trusting the copy loop. It also catches the
# trap that bit us before: the project's dev readme is README.md while the outer
# one is Readme.md, and Windows treats those as the same name, so if they ever
# end up in the same folder one silently overwrites the other.
$sourceBytes = [System.IO.File]::ReadAllBytes($sourceZip)
foreach ($needle in @('win32/version.json', 'win32/README.md', 'Readme.md', '.gitignore')) {
    Assert-True ((Find-Bytes $sourceBytes ([System.Text.Encoding]::UTF8.GetBytes($needle))) -ge 0) `
        ("source zip contains " + $needle)
}

# --- the publish tree:  <workspace>\open ---
#
# open\ is the working tree tools\upload_git.py pushes from, and it is unpacked
# here on every release so that "what is in open\" and "what is in the zip" cannot
# drift apart: the zip is the artifact, open\ is just that artifact on disk.
#
# open\.git survives the unpack - it is the local clone the upload script pushes
# from, and losing it would mean re-cloning both remotes from scratch.
# Everything else is a strict mirror, obsolete files included: if the zip no longer
# carries a file, open\ must not either, or the next commit would resurrect it.
$openDir = Join-Path $workspace 'open'
$unpacked = Expand-ZipInto $sourceZip $openDir @('.git')

$openGitDir = (Join-Path $openDir '.git') + '\'
$openFileCount = @(Get-ChildItem $openDir -Recurse -File -Force | Where-Object { -not $_.FullName.StartsWith($openGitDir) }).Count
Assert-True ($openFileCount -eq $unpacked.Files) `
    ("open\ mirrors the source zip: " + $openFileCount + " on disk, " + $unpacked.Files + " in the zip")
Assert-True (Test-Path (Join-Path $openDir 'Readme.md')) "open\Readme.md present"
Assert-True (Test-Path (Join-Path $openDir '.gitignore')) "open\.gitignore present"
Assert-True (Test-Path (Join-Path $openDir 'win32\version.json')) "open\win32\version.json present"
Assert-True (-not ([System.IO.File]::ReadAllText((Join-Path $openDir 'Readme.md')) -match '\{\{')) `
    "open\Readme.md is the rendered copy, not the template"
Write-Host ("  open\     : {0} files unpacked, {1} obsolete removed" -f $unpacked.Files, $unpacked.Removed)

# ---------------------------------------------------------------- sign (installer)

# The app and the updater were already signed above ("sign app + updater"), before
# the payload was assembled. The installer only comes into existence at step 4,
# so it is the one thing left to sign here.

if ($Sign) {
    Write-Host ""
    Write-Host "=== sign the installer ==="
    Invoke-Signer $installerOut
}

# ---------------------------------------------------------------- cleanup

# The staging tree is a few hundred MB of copies that only exist to be zipped.
# Leaving it behind is how the project folder silently grows to 200 MB of junk.
if (Test-Path $stagingRoot) {
    try {
        [System.IO.Directory]::Delete($stagingRoot, $true)
        Write-Host ""
        Write-Host "  cleaned: build_logs\release_staging"
    } catch {
        Write-Host ""
        Write-Host ("  WARNING: could not remove " + $stagingRoot + " - " + $_.Exception.Message)
    }
}

# ---------------------------------------------------------------- summary

Write-Host ""
Write-Host "=== RELEASE READY ==="
Write-Host "  releases\ directory:"
Get-ChildItem $releaseDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("    {0,-46} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host "  workspace root:"
foreach ($name in @((Split-Path $sourceZip -Leaf), 'Readme.md', '.gitignore', 'version.json')) {
    $path = Join-Path $workspace $name
    if (Test-Path $path) {
        Write-Host ("    {0,-46} {1,8:N1} MB" -f $name, ((Get-Item $path).Length / 1MB))
    }
}
Write-Host ("    {0,-46} {1,8} files  (publish tree; run tools\upload_git.py)" -f 'open\', $unpacked.Files)

if ($verifyFailed -gt 0) {
    Write-Host ""
    Write-Host "ZIP VERIFICATION FAILED ($verifyFailed package(s))"
    exit 1
}
exit 0
