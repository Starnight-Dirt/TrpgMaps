# TrpgMaps - crop a region out of a screenshot and scale it up.
#
# Why this exists: some UI details are only a few pixels wide (the 4px scroll
# thumb, a 1px border). Looking at the whole 1200x800 screenshot says nothing
# about them, so crop + nearest-neighbour zoom is the way to actually see it.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads a .ps1 without a BOM as
# ANSI, so non-ASCII characters here would get mangled and break parsing.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\crop_zoom.ps1 -In shot.png `
#       -X 400 -Y 0 -W 60 -H 800 -Scale 4 -Out thumb.png
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$In,
    [Parameter(Mandatory = $true)][int]$X,
    [Parameter(Mandatory = $true)][int]$Y,
    [Parameter(Mandatory = $true)][int]$W,
    [Parameter(Mandatory = $true)][int]$H,
    [int]$Scale = 4,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not [System.IO.Path]::IsPathRooted($In)) {
    $In = Join-Path (Split-Path $PSScriptRoot -Parent) $In
}
if (-not [System.IO.Path]::IsPathRooted($Out)) {
    $Out = Join-Path (Split-Path $PSScriptRoot -Parent) $Out
}

$src = [System.Drawing.Image]::FromFile($In)
try {
    # clamp the requested rectangle to the image
    if ($X -lt 0) { $X = 0 }
    if ($Y -lt 0) { $Y = 0 }
    if ($X + $W -gt $src.Width) { $W = $src.Width - $X }
    if ($Y + $H -gt $src.Height) { $H = $src.Height - $Y }
    if ($W -le 0 -or $H -le 0) { throw "empty crop rectangle for $In" }

    $dst = New-Object System.Drawing.Bitmap ($W * $Scale), ($H * $Scale)
    try {
        $g = [System.Drawing.Graphics]::FromImage($dst)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
            $g.DrawImage($src,
                (New-Object System.Drawing.Rectangle 0, 0, ($W * $Scale), ($H * $Scale)),
                (New-Object System.Drawing.Rectangle $X, $Y, $W, $H),
                [System.Drawing.GraphicsUnit]::Pixel)
        } finally { $g.Dispose() }
        $dst.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $dst.Dispose() }
} finally { $src.Dispose() }

Write-Host ("cropped {0} ({1},{2} {3}x{4})  x{5}  -> {6}" -f $In, $X, $Y, $W, $H, $Scale, $Out)
