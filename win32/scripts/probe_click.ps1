# Build + run the click double-fire probe (Windows PowerShell 5.1, ASCII only).
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot | Split-Path -Parent   # the win7 project root
$proj = Join-Path $root 'installer\probe_click\probe_click.csproj'
if (-not (Test-Path $proj)) { throw "probe project not found: $proj" }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = $null
if (Test-Path $vswhere) {
    $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($p) {
        $c = Join-Path $p.Trim() 'MSBuild\Current\Bin\MSBuild.exe'
        if (Test-Path $c) { $msbuild = $c }
    }
}
if (-not $msbuild) {
    foreach ($d in (Get-PSDrive -PSProvider FileSystem).Name) {
        foreach ($e in @('Enterprise', 'Professional', 'Community', 'BuildTools')) {
            $c = "${d}:\Program Files\Microsoft Visual Studio\2022\$e\MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path $c) { $msbuild = $c; break }
        }
        if ($msbuild) { break }
    }
}
if (-not $msbuild) { throw 'MSBuild not found' }

Write-Host "MSBuild: $msbuild"

$log = Join-Path $root 'build_logs\probe-click.log'
New-Item -ItemType Directory -Force -Path (Split-Path $log -Parent) | Out-Null

$buildArgs = @(
    $proj, '/restore', '/t:Build', '/p:Configuration=Release', '/p:Platform=x86',
    '/v:minimal', '/nologo'
)
$saved = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $msbuild @buildArgs 2>&1 | Tee-Object -FilePath $log | Out-Null
$exit = $LASTEXITCODE
$ErrorActionPreference = $saved

Write-Host "build exit = $exit"
if ($exit -ne 0) {
    Get-Content $log -Tail 40 | ForEach-Object { Write-Host ("  " + $_) }
    exit 1
}

$exe = Join-Path $root 'installer\probe_click\bin\x86\Release\net35\probe_click.exe'
if (-not (Test-Path $exe)) { throw "probe exe not found: $exe" }

Write-Host ""
Write-Host "=== running probe (one synthetic click) ==="
$outFile = Join-Path $root 'build_logs\probe-click.out.txt'
$ErrorActionPreference = 'Continue'
& $exe 2>&1 | Out-File -FilePath $outFile -Encoding ascii
$ErrorActionPreference = $saved

Get-Content $outFile | ForEach-Object { Write-Host ("  " + $_) }
exit 0
