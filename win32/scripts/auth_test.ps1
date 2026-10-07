# Token / room-lock / server-settings behaviour test (ASCII only).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\auth_test.ps1
# NOTE: keep this file ASCII-only; Windows PowerShell 5.1 reads .ps1 as ANSI.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$dst = $PSScriptRoot | Split-Path -Parent
if (-not (Test-Path (Join-Path $dst 'TrpgMaps.csproj'))) { $dst = $PSScriptRoot }
$out = Join-Path $dst 'bin\Release'
$exe = Join-Path $out 'TrpgMaps.exe'
if (-not (Test-Path $exe)) { Write-Host "exe not found: $exe"; exit 1 }

Get-Process TrpgMaps -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item (Join-Path $out 'app.log') -Force -ErrorAction SilentlyContinue

$fail = 0
function Check($name, $ok, $extra = '') {
    if ($ok) { Write-Host ("  [PASS] {0} {1}" -f $name, $extra) }
    else { Write-Host ("  [FAIL] {0} {1}" -f $name, $extra); $script:fail++ }
}

# Port may not be 5000 (changed during the test); start on 5000 first
$base = 'http://127.0.0.1:5000'

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
$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { Write-Host "process exited early: $($proc.ExitCode)"; break }
    try { if ((Invoke-WebRequest "$base/api/network" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ready = $true; break } } catch { }
}
Check 'server started' $ready
if (-not $ready) {
    Get-Content (Join-Path $out 'app.log') -ErrorAction SilentlyContinue | Select-Object -First 20
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    exit 1
}

function Api($path, $body) {
    $json = if ($body) { $body | ConvertTo-Json -Compress } else { '{}' }
    return (Invoke-WebRequest "$base$path" -Method POST -Body $json -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
}
function NewSseId($tag) { return "sse_${tag}_$([guid]::NewGuid().ToString('N').Substring(0,8))" }

Write-Host ""
Write-Host "=== 1) first join issues a token ==="
$idA = NewSseId 'a'
$loginA = Api '/api/login' @{ id = $idA; name = 'Alice'; token = '' }
Check 'first join allowed' ($loginA.ok -eq $true) "msg=$($loginA.msg)"
Check 'token issued' ($loginA.token -and $loginA.token.Length -ge 16) "len=$($loginA.token.Length)"
Check 'marked as new token' ($loginA.isNewToken -eq $true)
$tokenA = $loginA.token

Write-Host ""
Write-Host "=== 2) same name without token is rejected (name taken) ==="
$idB = NewSseId 'b'
$loginB = Api '/api/login' @{ id = $idB; name = 'Alice'; token = '' }
Check 'no-token rejoin rejected' ($loginB.ok -eq $false) "code=$($loginB.code) msg=$($loginB.msg)"
Check 'reject code is TOKEN_MISMATCH' ($loginB.code -eq 'TOKEN_MISMATCH')

Write-Host ""
Write-Host "=== 3) same name with wrong token is rejected ==="
$idC = NewSseId 'c'
$loginC = Api '/api/login' @{ id = $idC; name = 'Alice'; token = 'deadbeefdeadbeefdeadbeefdeadbeef' }
Check 'wrong-token rejoin rejected' ($loginC.ok -eq $false) "code=$($loginC.code)"

Write-Host ""
Write-Host "=== 4) same name with correct token reconnects ==="
$idD = NewSseId 'd'
$loginD = Api '/api/login' @{ id = $idD; name = 'Alice'; token = $tokenA }
Check 'valid-token rejoin allowed' ($loginD.ok -eq $true) "msg=$($loginD.msg)"
Check 'token returned unchanged' ($loginD.token -eq $tokenA)
Check 'not flagged as new' ($loginD.isNewToken -eq $false)

Write-Host ""
Write-Host "=== 5) a different player name is still allowed ==="
$idE = NewSseId 'e'
$loginE = Api '/api/login' @{ id = $idE; name = 'Bob'; token = '' }
Check 'second player allowed' ($loginE.ok -eq $true) "msg=$($loginE.msg)"
$tokenB = $loginE.token

Write-Host ""
Write-Host "=== 6) lock the room ==="
$lock = Api '/api/room/lock' @{ locked = $true }
Check 'room locked' ($lock.success -eq $true -and $lock.locked -eq $true)

Write-Host ""
Write-Host "=== 7) locked room: new name rejected, existing name+token allowed ==="
$idF = NewSseId 'f'
$loginF = Api '/api/login' @{ id = $idF; name = 'Charlie'; token = '' }
Check 'new name rejected while locked' ($loginF.ok -eq $false) "code=$($loginF.code)"
Check 'reject code is ROOM_LOCKED' ($loginF.code -eq 'ROOM_LOCKED')

$idG = NewSseId 'g'
$loginG = Api '/api/login' @{ id = $idG; name = 'Bob'; token = $tokenB }
Check 'existing name+token allowed while locked' ($loginG.ok -eq $true) "msg=$($loginG.msg)"

$idH = NewSseId 'h'
$loginH = Api '/api/login' @{ id = $idH; name = 'Bob'; token = 'wrongtokenwrongtokenwrongtoken' }
Check 'existing name+wrong token rejected while locked' ($loginH.ok -eq $false) "code=$($loginH.code)"

Write-Host ""
Write-Host "=== 8) unlock the room ==="
$unlock = Api '/api/room/lock' @{ locked = $false }
Check 'room unlocked' ($unlock.success -eq $true -and $unlock.locked -eq $false)
$idI = NewSseId 'i'
$loginI = Api '/api/login' @{ id = $idI; name = 'Charlie'; token = '' }
Check 'new name allowed after unlock' ($loginI.ok -eq $true) "msg=$($loginI.msg)"

Write-Host ""
Write-Host "=== 9) settings: address list and save ==="
$settings = (Invoke-WebRequest "$base/api/settings" -UseBasicParsing).Content | ConvertFrom-Json
Check 'settings returned ip' ([bool]$settings.listenIp) "ip=$($settings.listenIp) port=$($settings.port)"
Check 'address dropdown populated' ($settings.addresses.Count -ge 1) "count=$($settings.addresses.Count)"
$recommended = @($settings.addresses | Where-Object { $_.recommended })
Check 'at least one recommended address' ($recommended.Count -ge 1) "rec=$($recommended[0].address)"
Write-Host ("     addresses: " + (($settings.addresses | ForEach-Object { $_.address }) -join ', '))

# Record map count and player count before the port change
$mapsBefore = ((Invoke-WebRequest "$base/api/maps" -UseBasicParsing).Content | ConvertFrom-Json).images.Count
$playersBefore = ((Invoke-WebRequest "$base/api/players" -UseBasicParsing).Content | ConvertFrom-Json).Count
Write-Host "     before restart: maps=$mapsBefore players=$playersBefore"

$newPort = 5123
Write-Host ""
Write-Host "=== 10) save new ip/port: server reopens, state preserved ==="
$save = (Invoke-WebRequest "$base/api/settings" -Method POST -Body (@{ ip = $settings.listenIp; port = $newPort } | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
Check 'save reported success' ($save.success -eq $true) "msg=$($save.message)"
Check 'port updated in response' ($save.port -eq $newPort) "port=$($save.port)"
Check 'player count preserved' ($save.playerCount -eq $playersBefore) "count=$($save.playerCount)"
Check 'process still alive' (-not $proc.HasExited)

$base2 = "http://127.0.0.1:$newPort"
$ok2 = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 400
    try { if ((Invoke-WebRequest "$base2/api/network" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) { $ok2 = $true; break } } catch { }
}
Check 'service reachable on new port' $ok2

if ($ok2) {
    $net2 = (Invoke-WebRequest "$base2/api/network" -UseBasicParsing).Content | ConvertFrom-Json
    Check 'player url uses new port' ($net2.playerUrl -like "*:$newPort/player") $net2.playerUrl
    $mapsAfter = ((Invoke-WebRequest "$base2/api/maps" -UseBasicParsing).Content | ConvertFrom-Json).images.Count
    Check 'maps preserved after restart' ($mapsAfter -eq $mapsBefore) "maps=$mapsAfter"
    $playersAfter = ((Invoke-WebRequest "$base2/api/players" -UseBasicParsing).Content | ConvertFrom-Json).Count
    Check 'roster preserved after restart' ($playersAfter -eq $playersBefore) "players=$playersAfter"

    # Existing player with token can still reconnect on the new port
    $idJ = NewSseId 'j'
    $loginJ = (Invoke-WebRequest "$base2/api/login" -Method POST -Body (@{ id = $idJ; name = 'Alice'; token = $tokenA } | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
    Check 'token still valid after restart' ($loginJ.ok -eq $true) "msg=$($loginJ.msg)"

    # Invalid port rejected
    $bad = (Invoke-WebRequest "$base2/api/settings" -Method POST -Body (@{ ip = $settings.listenIp; port = 99999 } | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
    Check 'invalid port rejected' ($bad.success -eq $false) "msg=$($bad.message)"

    # Foreign / non-local IP rejected
    $badIp = (Invoke-WebRequest "$base2/api/settings" -Method POST -Body (@{ ip = '10.99.99.99'; port = $newPort } | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
    Check 'foreign ip rejected' ($badIp.success -eq $false) "msg=$($badIp.message)"

    # Restore port 5000 so later smoke tests are unaffected
    $restore = (Invoke-WebRequest "$base2/api/settings" -Method POST -Body (@{ ip = $settings.listenIp; port = 5000 } | ConvertTo-Json) -ContentType 'application/json' -UseBasicParsing).Content | ConvertFrom-Json
    Check 'port restored to 5000' ($restore.success -eq $true) "port=$($restore.port)"
}

Write-Host ""
Write-Host "=== 11) shutdown ==="
if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 800 }
Check 'process exited' $proc.HasExited

$log = Join-Path $out 'app.log'
if (Test-Path $log) {
    $err = @(Get-Content $log | Select-String -Pattern 'Unhandled|UnhandledException').Count
    Check 'no unhandled exception' ($err -eq 0) "matched=$err"
}

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL AUTH CHECKS PASSED" } else { Write-Host "$fail CHECK(S) FAILED" }
exit $fail
