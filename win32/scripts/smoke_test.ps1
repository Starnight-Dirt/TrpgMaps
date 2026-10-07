# TrpgMaps Win7 / 32-bit edition - end-to-end smoke test
# Usage: powershell -File scripts\smoke_test.ps1
# (ASCII only: Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$dst = $PSScriptRoot | Split-Path -Parent
if (-not (Test-Path (Join-Path $dst 'TrpgMaps.csproj'))) { $dst = $PSScriptRoot }
$out = Join-Path $dst 'bin\Release'
$exe = Join-Path $out 'TrpgMaps.exe'
$base = 'http://127.0.0.1:5000'

if (-not (Test-Path $exe)) { Write-Host "exe not found: $exe"; exit 1 }

Get-Process TrpgMaps -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $out 'app.log') -Force -ErrorAction SilentlyContinue

$fail = 0
function Check($name, $ok, $extra = '') {
    if ($ok) { Write-Host ("  [PASS] {0} {1}" -f $name, $extra) }
    else { Write-Host ("  [FAIL] {0} {1}" -f $name, $extra); $script:fail++ }
}

# Post a JSON body as UTF-8 bytes.
# Windows PowerShell 5.1 encodes a *string* body with the ANSI code page, so
# Chinese names (maps/ and terrain/ are full of them) reach the server mangled
# and it answers "image not found" (seen as name=??P1 in the login reply).
# The server always decodes the body as UTF-8, so hand it UTF-8 bytes.
function Post-Json($url, $obj) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($obj | ConvertTo-Json))
    return Invoke-WebRequest $url -Method POST -Body $bytes `
        -ContentType 'application/json; charset=utf-8' -UseBasicParsing
}

Write-Host "=== start app ==="
# --windowed keeps the test from taking over the whole screen in a borderless
# TopMost fullscreen window.
# --no-prompts: the app offers to fix a blocking firewall rule with a modal
# dialog at startup; unattended runs must never wait for a human.
# Deliberately NOT Start-Process: that goes through the shell (ShellExecute), and
# when the caller's stdout is redirected to a file or pipe the launched GUI app
# inherits that handle. An unattended runner then keeps waiting for the handle to
# close and looks like it hangs forever (observed: frozen after "=== start app ===",
# no child process alive). Process.Start with UseShellExecute=false never touches
# the shell and avoids the handle inheritance entirely.
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--windowed --no-prompts'
$psi.WorkingDirectory = $out
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$proc = [System.Diagnostics.Process]::Start($psi)
Write-Host "PID=$($proc.Id)"

$ready = $false
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 750
    if ($proc.HasExited) { Write-Host "process exited early, ExitCode=$($proc.ExitCode)"; break }
    try { if ((Invoke-WebRequest "$base/api/network" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $ready = $true; break } } catch { }
}
Check 'embedded web server started' $ready "($([math]::Round(($i+1)*0.75,1))s)"
if (-not $ready) {
    Get-Content (Join-Path $out 'app.log') -ErrorAction SilentlyContinue | Select-Object -First 20
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    exit 1
}

Write-Host ""
Write-Host "=== network info ==="
$net = (Invoke-WebRequest "$base/api/network" -UseBasicParsing).Content | ConvertFrom-Json
Check 'local ip' ($net.ip -match '^\d+\.\d+\.\d+\.\d+$') "ip=$($net.ip) port=$($net.port)"
Check 'player url' ($net.playerUrl -eq "http://$($net.ip):5000/player") $net.playerUrl
Check 'qr code png' ($net.qrCode.StartsWith('data:image/png;base64,') -and $net.qrCode.Length -gt 500) "len=$($net.qrCode.Length)"

Write-Host ""
Write-Host "=== firewall: can a phone actually reach us? ==="
# Every HTTP check in this script goes through 127.0.0.1, and Windows Firewall
# does NOT filter loopback traffic. So they all pass even when every phone on
# the LAN is blocked -- which is exactly how a real bug hid for a while:
# an inbound Block rule for TrpgMaps.exe (written when the firewall
# prompt was dismissed behind the fullscreen TopMost window) left the DM side
# looking perfectly healthy while no phone could ever connect.
# Read the rule table directly (no admin needed) and fail loudly.
$fwState = 'unknown'
$fwDetail = ''
try {
    $rules = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules' -ErrorAction Stop
    $exeLower = $exe.ToLowerInvariant()
    $blocked = 0
    $allowed = 0
    foreach ($prop in $rules.PSObject.Properties) {
        $v = [string]$prop.Value
        if ($v -notmatch 'App=') { continue }
        $appPart = ($v -split '\|' | Where-Object { $_ -like 'App=*' } | Select-Object -First 1)
        if (-not $appPart) { continue }
        if ($appPart.Substring(4).ToLowerInvariant() -ne $exeLower) { continue }
        if ($v -notmatch 'Active=TRUE') { continue }
        if ($v -notmatch 'Dir=In') { continue }
        if ($v -match 'Action=Block') { $blocked++ }
        elseif ($v -match 'Action=Allow') { $allowed++ }
    }
    if ($blocked -gt 0) { $fwState = 'blocked' }
    elseif ($allowed -gt 0) { $fwState = 'allowed' }
    else { $fwState = 'norule' }
    $fwDetail = "allow=$allowed block=$blocked"
} catch {
    $fwDetail = $_.Exception.Message
}

Check 'firewall does not block LAN' ($fwState -ne 'blocked') "state=$fwState $fwDetail"
if ($fwState -ne 'allowed') {
    Write-Host "  [HINT] fix from the network panel, or run: TrpgMaps.exe --fix-firewall"
}
if ($fwState -eq 'norule') {
    Write-Host "         (no rule yet: Windows asks once on first launch - and if that"
    Write-Host "          prompt is dismissed it writes a permanent Block rule instead)"
}

Write-Host ""
Write-Host "=== player page and static assets ==="
# The DM side is a native WinForms UI, so only the player page and the
# player-side scripts/styles/icons are still served over HTTP.
foreach ($p in @('/player', '/js/realtime.js', '/js/player.js',
        '/css/player.css', '/images/map.png', '/images/expand.png',
        '/images/fullscreen.png', '/images/draw.png')) {
    try {
        $r = Invoke-WebRequest "$base$p" -UseBasicParsing -TimeoutSec 5
        Check $p ($r.StatusCode -eq 200 -and $r.RawContentLength -gt 0) "$($r.RawContentLength)B"
    } catch { Check $p $false $_.Exception.Message }
}

# The phone paints the DM's terrain on a dedicated canvas that sits between the
# basemap and the grid, so the element must actually ship in the page.
$page = (Invoke-WebRequest "$base/player" -UseBasicParsing).Content
Check 'player page has the drawing canvas' ($page -match 'player-draw-canvas')
Check 'player page has the realtime socket script' ($page -match 'realtime\.js')

Write-Host ""
Write-Host "=== map endpoints ==="
$maps = (Invoke-WebRequest "$base/api/maps" -UseBasicParsing).Content | ConvertFrom-Json
Check 'map list' ($maps.images.Count -ge 2) "count=$($maps.images.Count)"
Check 'map list carries pixel size' (@($maps.images | Where-Object { $_.width -gt 0 -and $_.height -gt 0 }).Count -eq $maps.images.Count) `
    (($maps.images | ForEach-Object { "$($_.name)=$($_.width)x$($_.height)" }) -join ', ')
foreach ($m in $maps.images) {
    $esc = [uri]::EscapeDataString($m.name)
    $r = Invoke-WebRequest "$base/api/maps/image/$esc" -UseBasicParsing
    $disk = (Get-Item (Join-Path $out "maps\$($m.name)")).Length
    Check "read map $($m.name)" ($r.StatusCode -eq 200 -and $r.RawContentLength -eq $disk) "$($r.RawContentLength)B"

    # The native grid renderer needs the pixel size on its own endpoint too.
    $size = (Invoke-WebRequest "$base/api/maps/size/$esc" -UseBasicParsing).Content | ConvertFrom-Json
    Check "size of $($m.name)" ($size.width -eq $m.width -and $size.height -eq $m.height) "$($size.width)x$($size.height)"
}
# Pick the basemaps to drive the tests from the live list instead of hardcoding a
# name: maps/ is user data and its contents change (the original canned test
# images were deleted when the real battle maps were dropped in).
$mapA = $maps.images[0].name
$mapB = $maps.images[$maps.images.Count - 1].name
Check 'picked two distinct basemaps for the tests' ($mapA -ne $mapB) "A=$mapA B=$mapB"

$set = (Post-Json "$base/api/maps/set" @{ image = $mapA; mode = 'contain' }).Content | ConvertFrom-Json
Check 'set current map' ($set.success -and $set.current -eq $mapA) "current=$($set.current)"

Write-Host ""
Write-Host "=== basemap rotation ==="
# The DM panel turns the basemap clockwise by 90 (repeatable) or 180.
# The player page adds another 90, so the value must reach the phones.
$rot90 = (Post-Json "$base/api/maps/rotate" @{ delta = 90 }).Content | ConvertFrom-Json
Check 'rotate 90 clockwise' ($rot90.success -and $rot90.rotation -eq 90) "rotation=$($rot90.rotation)"
$rot180 = (Post-Json "$base/api/maps/rotate" @{ delta = 90 }).Content | ConvertFrom-Json
Check 'rotate again wraps to 180' ($rot180.rotation -eq 180) "rotation=$($rot180.rotation)"
$rotPlus180 = (Post-Json "$base/api/maps/rotate" @{ delta = 180 }).Content | ConvertFrom-Json
Check 'rotate 180 lands back on 0' ($rotPlus180.rotation -eq 0) "rotation=$($rotPlus180.rotation)"

Write-Host ""
Write-Host "=== grid line colour ==="
# Grid lines are pure white or pure black; "auto" is decided server-side from the
# basemap's average brightness, so the returned color is always white or black.
# NOTE: this file must stay pure ASCII - Windows PowerShell 5.1 reads a .ps1
# without a BOM as ANSI, and a Chinese character whose second byte is 0x60
# (a backtick) silently swallows the following newline.
function Set-GridColor($mode) {
    (Post-Json "$base/api/maps/gridcolor" @{ mode = $mode }).Content | ConvertFrom-Json
}
$gcWhite = Set-GridColor 'white'
Check 'grid colour forced white' ($gcWhite.success -and $gcWhite.mode -eq 'white' -and $gcWhite.color -eq 'white') `
    "mode=$($gcWhite.mode) color=$($gcWhite.color)"
$gcBlack = Set-GridColor 'black'
Check 'grid colour forced black' ($gcBlack.success -and $gcBlack.mode -eq 'black' -and $gcBlack.color -eq 'black') `
    "mode=$($gcBlack.mode) color=$($gcBlack.color)"
$gcAuto = Set-GridColor 'auto'
Check 'grid colour auto resolves to a concrete colour' `
    ($gcAuto.success -and $gcAuto.mode -eq 'auto' -and ($gcAuto.color -eq 'white' -or $gcAuto.color -eq 'black')) `
    "mode=$($gcAuto.mode) color=$($gcAuto.color)"
$gcBad = Set-GridColor 'rdbu'
Check 'unknown grid colour falls back to auto' ($gcBad.mode -eq 'auto') "mode=$($gcBad.mode)"

Write-Host ""
Write-Host "=== upload dedup (MD5) and new file ==="
# Note: PowerShell 5.1 Invoke-WebRequest has no -Form support, so use curl.exe
# Copy a real basemap (keeping its extension) so the server sees identical bytes.
$dupExt = [System.IO.Path]::GetExtension($mapA)
$dupPath = Join-Path $env:TEMP ("dnd_dup_content" + $dupExt)
Copy-Item (Join-Path $out ("maps\" + $mapA)) $dupPath -Force
$code = & curl.exe -s -o NUL -w "%{http_code}" -F "file=@$dupPath" "$base/api/maps/upload"
Check 'duplicate content rejected (409)' ($code -eq '409') "HTTP=$code"
Remove-Item $dupPath -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Red)
$g.Dispose()
$freshPath = Join-Path $env:TEMP 'dnd_fresh_upload.png'
$bmp.Save($freshPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
$code = & curl.exe -s -o NUL -w "%{http_code}" -F "file=@$freshPath" "$base/api/maps/upload"
Check 'new image accepted (200)' ($code -eq '200') "HTTP=$code"
$mapsAfter = (Invoke-WebRequest "$base/api/maps" -UseBasicParsing).Content | ConvertFrom-Json
Check 'uploaded file listed' (@($mapsAfter.images | Where-Object { $_.name -eq 'dnd_fresh_upload.png' }).Count -eq 1)
# Cleanup: remove the uploaded test map so the maps folder stays clean
Remove-Item (Join-Path $out 'maps\dnd_fresh_upload.png') -Force -ErrorAction SilentlyContinue
Remove-Item $freshPath -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "=== realtime (SSE) and player login ==="
$sseJob = Start-Job -ScriptBlock {
    param($u)
    try {
        $req = [System.Net.HttpWebRequest]::Create($u)
        $req.Timeout = 9000
        $resp = $req.GetResponse()
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $sb = New-Object System.Text.StringBuilder
        $deadline = (Get-Date).AddSeconds(8)
        while ((Get-Date) -lt $deadline) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            [void]$sb.AppendLine($line)
            if ($sb.ToString() -match 'event: kicked') { break }
        }
        $reader.Close(); $resp.Close()
        $sb.ToString()
    } catch { "SSE_ERROR: $($_.Exception.Message)" }
} -ArgumentList "$base/api/events?id=smoke1"

Start-Sleep -Seconds 2
# A deliberately non-ASCII player name, to prove the round trip survives UTF-8.
$login = (Post-Json "$base/api/login" @{
    id = 'smoke1'; name = [char]0x6D4B + [char]0x8BD5 + 'P1'; cookieId = 'cookie_smoke_1'
}).Content | ConvertFrom-Json
Check 'player login' ($login.ok -eq $true) "name=$($login.name)"
Check 'chinese player name survives the round trip' `
    ($login.name -eq ([char]0x6D4B + [char]0x8BD5 + 'P1')) "name=$($login.name)"

Start-Sleep -Milliseconds 800
$players = (Invoke-WebRequest "$base/api/players" -UseBasicParsing).Content | ConvertFrom-Json
Check 'player roster' ($players.Count -eq 1 -and $players[0].online) "name=$($players[0].name) online=$($players[0].online) ip=$($players[0].ip)"

Write-Host ""
Write-Host "=== map broadcast ==="
# Switching the map here is what should push map_change (with rotation, grid
# colour and drawingVersion) to the phone, so the SSE asserts below depend on it.
$set2 = (Post-Json "$base/api/maps/set" @{ image = $mapB; mode = 'fill' }).Content | ConvertFrom-Json
Check 'switch map mode' ($set2.success -and $set2.current -eq $mapB) "current=$($set2.current)"

# Give the debounced grid-anchor re-broadcast time to land.
# The grid anchor is measured from the DM canvas, and every caller broadcasts the
# map_change BEFORE it repaints that canvas (panel handlers included), so the first
# payload necessarily describes the previous basemap. ApplyMapToCanvas() therefore
# schedules a corrected re-broadcast 600ms later, and an HTTP-only change needs the
# 200ms canvas/store sync tick in front of that. The SSE job below stops on the
# 'kicked' event, so without this wait it would close before the correction arrives
# and the asserts would be reading the stale payload.
Start-Sleep -Milliseconds 1400

Write-Host ""
Write-Host "=== kick player ==="
$kick = (Post-Json "$base/api/players/kick" @{ name = $players[0].name }).Content | ConvertFrom-Json
Check 'kick endpoint' ($kick.success -eq $true)
Start-Sleep -Milliseconds 900
$players2 = (Invoke-WebRequest "$base/api/players" -UseBasicParsing).Content | ConvertFrom-Json
# Kicked players keep their roster entry (marked offline) so their token stays valid for a later rejoin.
Check 'kicked player marked offline, entry kept' ($players2.Count -eq 1 -and $players2[0].online -eq $false) "count=$($players2.Count) online=$($players2[0].online)"

Write-Host ""
Write-Host "=== reconnect (the grey-dot bug) ==="
# "Online" is bound to the SSE subscription id. When the phone drops off (screen
# locked, browser backgrounded) and comes back, the browser reconnects on a NEW
# id -- so it must log in again on that new connection, otherwise the roster
# stays offline and the DM panel keeps showing a grey dot until the player
# manually reloads the page. player.js does exactly this on every reconnect.
$reId = 'smoke-reconnect'
$re = (Post-Json "$base/api/login" @{ id = $reId; name = $players[0].name; token = $login.token }).Content | ConvertFrom-Json
Check 'reconnect on a new connection id is accepted' ($re.ok -eq $true) "msg=$($re.msg)"
$players3 = (Invoke-WebRequest "$base/api/players" -UseBasicParsing).Content | ConvertFrom-Json
Check 'reconnected player is back online' ($players3.Count -eq 1 -and $players3[0].online -eq $true) "online=$($players3[0].online)"

$sseOut = Receive-Job $sseJob -Wait -AutoRemoveJob
$events = ($sseOut -split "`r?`n" | Where-Object { $_ -match '^event:' }) -replace '^event:\s*', ''
Write-Host "  SSE events: $($events -join ', ')"
Check 'SSE login_success' ($events -contains 'login_success')
Check 'SSE map_change' ($events -contains 'map_change')
# The phone needs the basemap angle to add its own 90 degrees on top of it.
Check 'map_change carries the rotation' ($sseOut -match '"rotation"')
# The player page draws its grid with exactly this colour, it does not inspect
# the basemap itself (see player.js).
Check 'map_change carries the grid colour' ($sseOut -match '"gridColor"')
# The phone only refetches /api/drawing when this version lags behind. A phone that
# joins later gets the same field through /api/request_map (both use MapPayload),
# which is the only chance it has to learn what the DM already drew.
Check 'map_change carries the drawing version' ($sseOut -match '"drawingVersion"')

# --- grid anchor -------------------------------------------------------------
# The 4 resolution-independent numbers that let the phone put the grid -- and
# with it every drawn tile -- exactly where the DM's canvas has it. Without them
# the phone invents its own top-left-anchored grid and every drawing lands on the
# wrong part of the basemap. See Core/GridAnchor.cs and computeGridFrame() in
# wwwroot/js/player.js.
# The stream can hold more than one map_change (the first one after a basemap
# switch still carries the old anchor); the LATEST one is the authoritative state.
function SseNum($name) {
    $ms = [regex]::Matches($sseOut, '"' + $name + '"\s*:\s*(-?[0-9]+(?:\.[0-9]+)?)')
    if ($ms.Count -gt 0) { return [double]$ms[$ms.Count - 1].Groups[1].Value }
    return [double]::NaN
}
$anchorX = SseNum 'gridCellsX'
$anchorY = SseNum 'gridCellsY'
$anchorHW = SseNum 'gridHalfW'
$anchorHH = SseNum 'gridHalfH'
Check 'map_change carries the grid anchor' `
    (($anchorX -gt 0) -and ($anchorY -gt 0) -and ($anchorHW -gt 0) -and ($anchorHH -gt 0)) `
    "cellsX=$anchorX cellsY=$anchorY halfW=$anchorHW halfH=$anchorHH"

$metaB = @($maps.images | Where-Object { $_.name -eq $mapB })[0]
if ($metaB) {
    # cellsX/cellsY are the basemap's displayed size divided by one cell, so their
    # ratio is the basemap's own aspect ratio, whatever the canvas size or zoom is.
    $ratio = $metaB.width / $metaB.height
    $gotRatio = $anchorX / $anchorY
    Check 'anchor follows the basemap aspect' `
        ([math]::Abs($gotRatio - $ratio) -lt (0.02 * $ratio)) `
        ("cells ratio {0:0.###} vs map {1:0.###}" -f $gotRatio, $ratio)

    # halfW/halfH are the canvas half-size in cells: A = canvasW/cell, B = canvasH/cell.
    # The window is not resized in this test, so A and B are whatever the --windowed
    # canvas happens to be -- unknown here and deliberately NOT assumed (it is not the
    # 1200x800 of WindowedWidth/Height: the frame eats ~16x39). Instead check the
    # relation the DM layout rule forces (Core/MapCanvas.ComputeImageRect):
    #   fill    -> cellsX = max(A, B*r),  cellsY = max(A/r, B)
    #   contain -> cellsX = min(A, B*r),  cellsY = min(A/r, B)      with r = mapW/mapH
    # This pins the anchor without assuming a window size, and it also catches a canvas
    # that is still showing a different basemap (or none at all): in that case
    # A = cellsX and B = cellsY, which the max() above cannot satisfy.
    $A = 2.0 * $anchorHW
    $B = 2.0 * $anchorHH
    $wantX = [math]::Max($A, $B * $ratio)      # the current mode is 'fill'
    $wantY = [math]::Max($A / $ratio, $B)
    Check 'anchor matches the canvas fit rule' `
        (([math]::Abs($anchorX - $wantX) -lt 0.02) -and ([math]::Abs($anchorY - $wantY) -lt 0.02)) `
        ("canvas {0:0.###}x{1:0.###} cells -> basemap {2:0.###}x{3:0.###}" -f $A, $B, $wantX, $wantY)
} else {
    Write-Host "  [SKIP] anchor geometry check (no metadata for $mapB)"
}

Check 'SSE player_list' ($events -contains 'player_list')
Check 'SSE kicked' ($events -contains 'kicked')

Write-Host ""
Write-Host "=== terrain catalog (the draw panel asset list) ==="
# The DM draw panel lists its assets from here, and the phone reads the same
# catalog to pull a one-cell tile instead of the 1.3-3.3 MB original.
$terrain = (Invoke-WebRequest "$base/api/terrain" -UseBasicParsing).Content | ConvertFrom-Json
$kindCount = @($terrain.kinds).Count
Check 'terrain catalog has three kinds' ($kindCount -eq 3) "kinds=$kindCount"
$kindNames = (@($terrain.kinds) | ForEach-Object { $_.kind }) -join ','
Check 'terrain kinds are terrain,entity,item' ($kindNames -eq 'terrain,entity,item') $kindNames
$assetCount = @($terrain.assets).Count
Check 'terrain catalog lists assets' ($assetCount -ge 1) "assets=$assetCount"
Check 'every asset reports opacity in (0,1] and the four flags' `
    (@($terrain.assets | Where-Object {
        $_.opacity -gt 0 -and $_.opacity -le 1 -and
        $null -ne $_.disadvantageous -and $null -ne $_.cover -and
        $null -ne $_.obstacle -and $null -ne $_.climb }).Count -eq $assetCount) `
    "assets=$assetCount"
# The url must be built from the asset's own kind, otherwise the panel would
# load a thumbnail from the wrong folder.
Check 'every asset url points at its own kind folder' `
    (@($terrain.assets | Where-Object { $_.url -notmatch [regex]::Escape("/api/terrain/image/$($_.kind)/") }).Count -eq 0)

$asset = @($terrain.assets)[0]
$ak = $asset.kind
$af = [uri]::EscapeDataString($asset.file)
$img = Invoke-WebRequest "$base/api/terrain/image/$ak/$af" -UseBasicParsing
$imgDisk = (Get-Item (Join-Path $out "terrain\$($asset.file)")).Length
Check "read terrain image $($asset.file)" ($img.StatusCode -eq 200 -and $img.RawContentLength -eq $imgDisk) `
    "$($img.RawContentLength)B"

# The phone asks for a small one-cell tile. It must be a real response that is
# tiny compared with the original -- that is the whole point of the endpoint.
$tile = Invoke-WebRequest "$base/api/terrain/tile/$ak/$af`?px=64" -UseBasicParsing
Check 'terrain tile 64px is much smaller than the original' `
    ($tile.StatusCode -eq 200 -and $tile.RawContentLength -gt 100 -and $tile.RawContentLength -lt 60000) `
    "tile=$($tile.RawContentLength)B vs original=$($imgDisk)B"
# px is clamped to 16..512, so an absurd value must still answer 200 (never 500).
$tileBig = Invoke-WebRequest "$base/api/terrain/tile/$ak/$af`?px=9999" -UseBasicParsing
Check 'tile px is clamped instead of erroring' ($tileBig.StatusCode -eq 200) "$($tileBig.RawContentLength)B"
# Path traversal must never leave the asset folders. The target is deliberately
# something that is NOT a valid route either way, so the check passes whether or
# not the server normalises the dot segments first.
$blocked = $false
try {
    $bad = Invoke-WebRequest "$base/api/terrain/image/$ak/..%2F..%2Fapp.log" -UseBasicParsing -ErrorAction Stop
} catch {
    $blocked = $true
}
Check 'terrain image blocks path traversal' $blocked

Write-Host ""
Write-Host "=== drawing state and phone sync ==="
$drawing = (Invoke-WebRequest "$base/api/drawing" -UseBasicParsing).Content | ConvertFrom-Json
Check 'drawing payload has version and cells' ($null -ne $drawing.version -and $null -ne $drawing.cells) `
    "version=$($drawing.version) cells=$(@($drawing.cells).Count)"
$clearDraw = (Invoke-WebRequest "$base/api/drawing/clear" -Method POST -UseBasicParsing).Content | ConvertFrom-Json
Check 'clear drawing is local-only and works' ($clearDraw.success -eq $true) "removed=$($clearDraw.removed)"
$drawing2 = (Invoke-WebRequest "$base/api/drawing" -UseBasicParsing).Content | ConvertFrom-Json
Check 'drawing is empty right after clear' (@($drawing2.cells).Count -eq 0) "cells=$(@($drawing2.cells).Count)"

Write-Host ""
Write-Host "=== QR code decodes back to the player url ==="
# Optional: needs Python 3. Independently decodes the QR (format info / RS / text).
$scriptDir = $PSScriptRoot
if (-not $scriptDir -and $PSCommandPath) { $scriptDir = Split-Path -Parent $PSCommandPath }
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$verifyPy = Join-Path $scriptDir 'qr_verify.py'
$python = $null
foreach ($cand in @('python', 'python3', 'py')) {
    $cmd = Get-Command $cand -ErrorAction SilentlyContinue
    if (-not $cmd) { continue }
    $p = $cmd.Path
    if (-not $p) { $p = $cmd.Source }
    if (-not $p) { $p = $cmd.Definition }
    if ($p) { $python = $p; break }
}
if (-not $python) {
    # Not on PATH: probe common install directories
    $roots = @(
        "$env:LOCALAPPDATA\Programs\Python",
        'D:\Program Files\Python311', 'C:\Program Files\Python311', 'C:\Python311',
        'D:\Python311', 'C:\Program Files\Python312', 'D:\Program Files\Python312'
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $exe2 = Get-ChildItem $root -Recurse -Filter 'python.exe' -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($exe2) { $python = $exe2.FullName; break }
    }
}

if ($python -and (Test-Path $verifyPy)) {
    # Reuse the running instance; reuse playerUrl fetched at the start
    $expectedUrl = $net.playerUrl
    if (-not $expectedUrl) {
        $net2 = (Invoke-WebRequest "$base/api/network" -UseBasicParsing).Content | ConvertFrom-Json
        $expectedUrl = $net2.playerUrl
    }
    $qrBase64 = $net.qrCode
    if (-not $qrBase64) {
        $net2 = (Invoke-WebRequest "$base/api/network" -UseBasicParsing).Content | ConvertFrom-Json
        $qrBase64 = $net2.qrCode
    }

    if ($expectedUrl -and $qrBase64) {
        $tmpDir = Join-Path $out '_qrtest'
        New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
        $qrPng = Join-Path $tmpDir 'qr.png'
        [IO.File]::WriteAllBytes($qrPng, [Convert]::FromBase64String(($qrBase64 -replace '^data:image/png;base64,', '')))
        $result = & $python $verifyPy $qrPng $expectedUrl 2>&1
        $text = ($result | Out-String)
        Check 'QR decodes to player url' ($text -match 'RESULT: PASS') "url=$expectedUrl"
        if ($text -notmatch 'RESULT: PASS') {
            Write-Host "     --- qr_verify output ---"
            $text -split "`r?`n" | ForEach-Object { "     $_" }
        }
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Check 'QR decodes to player url' $false "playerUrl=[$expectedUrl] qrLen=$($qrBase64.Length)"
    }
} else {
    Write-Host "  [SKIP] QR decode check (python3 or qr_verify.py not available)"
}

Write-Host ""
Write-Host "=== remove basemap (the map panel's remove button) ==="
# The DM panel's remove-map button ("che diao di tu") and POST /api/maps/remove are
# the same code path (Routes.RemoveCurrentMap). Removing must drop the current
# selection only: every file stays in maps\, and the DM screen plus every phone fall
# back to "no basemap, grid only" -- exactly like a freshly started app.
#
# Subscribe first: the phone only learns about it through the broadcast. The job
# waits specifically for the payload that says "there is no basemap" -- a plain
# event count would not do, because the map switch just above also produces a
# map_change plus a debounced correction.
$rmJob = Start-Job -ScriptBlock {
    param($u)
    try {
        $req = [System.Net.HttpWebRequest]::Create($u)
        $req.Timeout = 9000
        $resp = $req.GetResponse()
        $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
        $sb = New-Object System.Text.StringBuilder
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            [void]$sb.AppendLine($line)
            # The remove payload is the one whose url is empty.
            if ($line -match '"url"\s*:\s*""') { break }
        }
        $reader.Close(); $resp.Close()
        $sb.ToString()
    } catch { "SSE_ERROR: $($_.Exception.Message)" }
} -ArgumentList "$base/api/events?id=smoke_remove"

Start-Sleep -Milliseconds 900

# Make sure something is selected first, so there is something to remove.
$setA = (Post-Json "$base/api/maps/set" @{ image = $mapA; mode = 'fill' }).Content | ConvertFrom-Json
Check 'selected a basemap before removing' ($setA.success -eq $true -and $setA.current -eq $mapA) `
    "current=$($setA.current)"
Start-Sleep -Milliseconds 900

# Snapshot the list at this instant: the upload test above deletes its own file
# from disk afterwards, so an older list would not be comparable.
$mapsBeforeRemove = (Invoke-WebRequest "$base/api/maps" -UseBasicParsing).Content | ConvertFrom-Json

$rm = (Post-Json "$base/api/maps/remove" @{}).Content | ConvertFrom-Json
Check 'remove drops the current basemap' ($rm.success -eq $true -and $rm.removed -eq $mapA) `
    "removed=$($rm.removed)"

# "Removing" is not "deleting": maps\ is user data and must be untouched.
$mapsAfterRemove = (Invoke-WebRequest "$base/api/maps" -UseBasicParsing).Content | ConvertFrom-Json
Check 'removing keeps every file on disk' `
    (@($mapsAfterRemove.images).Count -eq @($mapsBeforeRemove.images).Count -and
     @($mapsAfterRemove.images | Where-Object { $_.name -eq $mapA }).Count -eq 1) `
    "count=$(@($mapsAfterRemove.images).Count) (was $(@($mapsBeforeRemove.images).Count))"

$rmAgain = (Post-Json "$base/api/maps/remove" @{}).Content | ConvertFrom-Json
Check 'removing again is a harmless no-op' ($rmAgain.success -eq $false) "message=$($rmAgain.message)"

$rmOut = Receive-Job $rmJob -Wait -AutoRemoveJob
# The phone clears the image when url is empty and keeps drawing its grid.
Check 'phone is told the basemap is gone' `
    ($rmOut -match '"image"\s*:\s*""' -and $rmOut -match '"url"\s*:\s*""') "empty image/url"
# With no basemap there is nothing to align against, so the anchor must be zeros
# rather than "the basemap fills the whole viewport".
Check 'no basemap publishes a zero grid anchor' `
    ($rmOut -match '"gridCellsX"\s*:\s*0' -and $rmOut -match '"gridHalfW"\s*:\s*0') `
    "gridCellsX/gridHalfW are 0"

# Leave the app usable.
$restore = (Post-Json "$base/api/maps/set" @{ image = $mapA; mode = 'contain' }).Content | ConvertFrom-Json
Check 'a basemap can be selected again after removing' `
    ($restore.success -eq $true -and $restore.current -eq $mapA) "current=$($restore.current)"

Write-Host ""
Write-Host "=== shutdown ==="
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 900 }
Check 'process exited' $proc.HasExited

Write-Host ""
Write-Host "=== run log ==="
$log = Join-Path $out 'app.log'
if (Test-Path $log) {
    Get-Content $log | Select-Object -Last 12 | ForEach-Object { "  $_" }
    $err = @(Get-Content $log | Select-String -Pattern 'Unhandled|unhandled exception|Exception').Count
    Check 'no unhandled exception' ($err -eq 0) "matched=$err"
} else { Write-Host "  (no app.log)" }

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL CHECKS PASSED" } else { Write-Host "$fail CHECK(S) FAILED" }
exit $fail
