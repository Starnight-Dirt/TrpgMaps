# TrpgMaps - Win7 / 32-bit edition - headless UI screenshot helper
#
# Renders the DM window to PNG files so that layout problems (is the sidebar
# visible, how big is the gap to the floating panel, how many pixels is one
# grid cell, does the slider remember its value ...) can be checked without a
# human staring at the screen.
#
# Mode "all" (the default) captures every screen in ONE process launch.
# That matters: each launch of the GUI can trigger a security prompt
# ("do you want to run this program") which nobody can answer while the
# operator is away, and --ui implies --no-server so the Windows Firewall
# prompt never appears either.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM as
# ANSI, so non-ASCII characters here would get mangled and break parsing.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\ui_shot.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\ui_shot.ps1 -Out build_logs\shots
#   powershell -ExecutionPolicy Bypass -File scripts\ui_shot.ps1 -Mode map
#   powershell -ExecutionPolicy Bypass -File scripts\ui_shot.ps1 -Height 480 -Mode net
#   powershell -ExecutionPolicy Bypass -File scripts\ui_shot.ps1 -Exe bin\Release\TrpgMaps.exe
#
# Modes (single-shot modes write <Out> as a file; "all" treats it as a directory):
#   all          - every screen below, in one run
#   base         - nothing open, grid only (no basemap)
#   sidebar      - sidebar expanded, no panel
#   map          - sidebar expanded + map panel + basemap
#   mapcollapsed - sidebar collapsed + map panel (compare the gaps)
#   grid35       - map panel open, slider dragged to 3.5 cm/cell
#   closeall     - everything closed
#   gridreopen   - map panel reopened (the slider must still read 3.5)
#   net          - sidebar expanded + network panel
#   rotate90     - map panel open with the basemap turned 90 degrees clockwise
#   gridcolor    - map panel open with a basemap selected, grid colour left on "auto"
#   draw         - drawing panel open (top-right float window), first terrain asset picked
#   drawbrush    - drawing panel in brush mode with an origin placed and a live selection
#   drawcone     - same, but the brush shape is the fan/cone (blue sector instead of a circle)
#   drawbrushloose - same as drawbrush, but the selection mode is Loose: every cell the shape
#                  really covers is selected (compare it against drawbrush to see the extra cells)
#   drawtangent  - Loose mode with the radius fixed at 1.5 cells (7.5 ft): the circle is exactly
#                  tangent to the grid lines, so the four axial cells outside it must NOT be picked
[CmdletBinding()]
param(
    [string]$Exe = '',

    [ValidateSet('all', 'base', 'sidebar', 'map', 'mapcollapsed', 'grid35', 'closeall', 'gridreopen', 'net', 'about', 'rotate90', 'gridcolor', 'draw', 'drawbrush', 'drawcone', 'drawbrushloose', 'drawtangent')]
    [string]$Mode = 'all',

    [string]$Out = '',

    [int]$Port = 5100,

    # Window height (the app defaults to 800). Use a small value - e.g. 480 - to
    # check what the panel does on a 1366x768 laptop: does it overflow, and does
    # the thin scroll thumb show up.
    [int]$Height = 0,

    [int]$TimeoutSec = 120
)

$ErrorActionPreference = 'Stop'

$projectDir = $PSScriptRoot | Split-Path -Parent
if (-not $Exe) { $Exe = Join-Path $projectDir 'bin\Release\TrpgMaps.exe' }
if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }

# Absolute path required: the process starts in the exe folder.
if (-not [System.IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $projectDir $Exe }
$Exe = (Get-Item $Exe).FullName

$isAll = ($Mode -eq 'all')
if (-not $Out) {
    if ($isAll) { $Out = Join-Path $projectDir 'build_logs\shots' }
    else { $Out = Join-Path $projectDir ("build_logs\shots\{0}.png" -f $Mode) }
}
if (-not [System.IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $projectDir $Out }

if ($isAll) {
    $outDir = $Out
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    Get-ChildItem $outDir -Filter '*.png' -ErrorAction SilentlyContinue | Remove-Item -Force
} else {
    $outDir = Split-Path $Out -Parent
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    if (Test-Path $Out) { Remove-Item $Out -Force }
}

$workDir = Split-Path $Exe -Parent
$appLog = Join-Path $workDir 'app.log'
if (Test-Path $appLog) { Remove-Item $appLog -Force }

# Quoted individually: the arg list is joined with spaces below.
# --ui already implies --no-server (no socket is bound, so no firewall dialog).
$appArgs = @('--ui', $Mode, ('"' + $Out + '"'), '--port', "$Port")
if ($Height -gt 0) { $appArgs += @('--height', "$Height") }

# Deliberately NOT Start-Process: that goes through the shell (ShellExecute), and
# when the caller's stdout is redirected to a file or pipe the launched GUI app
# inherits that handle -- an unattended runner then waits for it to close and
# looks like it hangs forever. Process.Start with UseShellExecute=false does not
# touch the shell and never passes the handle on.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = ($appArgs -join ' ')
$psi.WorkingDirectory = $workDir
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)

$exited = $proc.WaitForExit($TimeoutSec * 1000)
if (-not $exited) {
    try { $proc.Kill() } catch { }
    Write-Host "TIMEOUT after ${TimeoutSec}s (killed) - a modal dialog or a hang is likely"
    exit 2
}

Start-Sleep -Milliseconds 250

function Save-Log {
    if (-not (Test-Path $appLog)) { return }
    if (-not $isAll) { Copy-Item $appLog -Destination ($Out + '.log') -Force }
    # Keep a UTF-8 copy: the console may not render the app's Chinese log
    # lines, but the file is fine for later inspection.
    Copy-Item $appLog -Destination (Join-Path $outDir 'app.log.txt') -Force
}

if ($isAll) {
    Save-Log
    $shots = @(Get-ChildItem $outDir -Filter '*.png' -ErrorAction SilentlyContinue | Sort-Object Name)
    foreach ($s in $shots) { Write-Host ("  {0,-22} {1,9} bytes" -f $s.Name, $s.Length) }
    Write-Host ("mode=all  shots={0}  exit={1}" -f $shots.Count, $proc.ExitCode)
    Write-Host ("  dir -> {0}" -f $outDir)
    if ($shots.Count -ge 7) { exit 0 }
    Write-Host "FAIL: fewer screenshots than expected"
    exit 1
}

# Single-shot mode: still keep the log next to the image. Without this a failed
# layout (or the "firewall blocked" line) leaves no evidence at all, and the
# README promises <png>.log exists.
Save-Log

if (Test-Path $Out) {
    Write-Host ("OK    mode={0}  {1} bytes  exit={2}" -f $Mode, (Get-Item $Out).Length, $proc.ExitCode)
    Write-Host ("      -> {0}" -f $Out)
    Write-Host ("      log -> {0}.log" -f $Out)
    exit 0
}

Write-Host ("FAIL  mode={0}  exit={1}  no screenshot produced" -f $Mode, $proc.ExitCode)
if (Test-Path $appLog) {
    Write-Host "--- app.log (tail) ---"
    Get-Content $appLog -Tail 30 | ForEach-Object { Write-Host ("  " + $_) }
}
exit 1
