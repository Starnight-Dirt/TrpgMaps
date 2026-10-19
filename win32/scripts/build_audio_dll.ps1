# Build the native audio DLL for the 32-bit win7 build.
#
#   powershell -ExecutionPolicy Bypass -File scripts\build_audio_dll.ps1 [-Rebuild]
#
# Produces native\bin\x86\TrpgAudio.dll (x86, statically linked CRT so it runs
# on a bare Windows 7 with no VC++ redistributable).
#
# Source: native\audio_bridge.c + miniaudio.h (public domain / MIT-0).
#
# NOTE: this does NOT call vcvars32.bat. That batch file shells out to reg.exe,
# which is on this machine's command blacklist, and the whole toolchain lookup
# then dies with "PROGRAM BLOCKED BY SECURITY POLICY". We only need cl.exe plus
# INCLUDE/LIB, so we locate them ourselves and set the two variables by hand.
#
# ASCII only: Windows PowerShell 5.1 reads .ps1 as ANSI without a BOM.

param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $PSScriptRoot          # ...\win7 version folder
$native = Join-Path $root 'native'
$src    = Join-Path $native 'audio_bridge.c'
$srcCpp = Join-Path $native 'mf_aac.cpp'
$header = Join-Path $native 'miniaudio.h'
$outDir = Join-Path $native 'bin\x86'
$dll    = Join-Path $outDir 'TrpgAudio.dll'
$log    = Join-Path $root 'build_logs\build_audio_dll.txt'

foreach ($f in @($src, $srcCpp, $header)) {
    if (-not (Test-Path $f)) { throw ("missing " + $f) }
}

if ((Test-Path $dll) -and (-not $Rebuild)) {
    Write-Host ("TrpgAudio.dll already built (" + (Get-Item $dll).Length + " bytes); use -Rebuild to force.")
    exit 0
}

# ------------------------------------------------------------- locate cl.exe
$searchRoots = @()
foreach ($r in @('D:\Program Files\Microsoft Visual Studio', 'C:\Program Files\Microsoft Visual Studio',
                 'D:\Program Files (x86)\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio')) {
    if (Test-Path $r) { $searchRoots += $r }
}

$clExe = $null
foreach ($r in $searchRoots) {
    $hit = Get-ChildItem -Path $r -Recurse -Filter 'cl.exe' -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '\\Tools\\MSVC\\[^\\]+\\bin\\Hostx64\\x86\\cl\.exe$' } |
           Sort-Object FullName -Descending | Select-Object -First 1
    if ($hit) { $clExe = $hit.FullName; break }
}
if (-not $clExe) { throw 'cl.exe (x86 cross compiler) not found - install the MSVC C++ toolset' }

# ...\VC\Tools\MSVC\<version>\bin\Hostx64\x86\cl.exe  ->  ...\VC\Tools\MSVC\<version>
$msvcTools = Split-Path (Split-Path (Split-Path (Split-Path $clExe)))
$msvcInclude = Join-Path $msvcTools 'include'
$msvcLib = Join-Path $msvcTools 'lib\x86'
Write-Host ('cl.exe : ' + $clExe)
if (-not (Test-Path $msvcInclude)) { throw ('missing MSVC include: ' + $msvcInclude) }

# ------------------------------------------------------------ locate the SDK
$sdkRoot = $null
foreach ($r in @('D:\Windows Kits\10', 'C:\Program Files (x86)\Windows Kits\10', 'C:\Program Files\Windows Kits\10')) {
    if (Test-Path (Join-Path $r 'Include')) { $sdkRoot = $r; break }
}
if (-not $sdkRoot) { throw 'Windows SDK 10 not found' }

$sdkVer = Get-ChildItem -Path (Join-Path $sdkRoot 'Include') -Directory |
          Where-Object { Test-Path (Join-Path $_.FullName 'um\windows.h') } |
          Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdkVer) { throw ('no usable SDK version under ' + (Join-Path $sdkRoot 'Include')) }
$verName = $sdkVer.Name
Write-Host ('SDK    : ' + $sdkRoot + '  ' + $verName)

$incUcrt   = Join-Path $sdkRoot ('Include\' + $verName + '\ucrt')
$incShared = Join-Path $sdkRoot ('Include\' + $verName + '\shared')
$incUm     = Join-Path $sdkRoot ('Include\' + $verName + '\um')
$libUcrt   = Join-Path $sdkRoot ('Lib\' + $verName + '\ucrt\x86')
$libUm     = Join-Path $sdkRoot ('Lib\' + $verName + '\um\x86')

# ------------------------------------------------------------------ compile
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
$env:INCLUDE = ($msvcInclude, $incUcrt, $incShared, $incUm) -join ';'
$env:LIB     = ($msvcLib, $libUcrt, $libUm) -join ';'

# /MT   : static CRT -> no vcruntime140.dll needed on the target machine
# /O2   : optimise (miniaudio is decoder-heavy)
# /LD   : build a DLL
# 0x0601: target Windows 7
Push-Location $native
try {
    # audio_bridge.c  -> C   (miniaudio + stb_vorbis glue)
    # mf_aac.cpp      -> C++ (Media Foundation: the OS AAC/MP3/WMA decoder)
    # cl picks the language per extension; /EHsc is for the C++ side only.
    $args = @('/nologo', '/LD', '/O2', '/MT', '/GS-', '/utf-8', '/EHsc',
              '/D_WIN32_WINNT=0x0601', '/DWINVER=0x0601',
              $src, $srcCpp, ('/Fe:' + $dll), ('/Fo:' + $outDir + '\'),
              '/link', 'ole32.lib', 'winmm.lib', 'user32.lib', 'advapi32.lib',
              'mfplat.lib', 'mfreadwrite.lib', 'mfuuid.lib')
    & $clExe @args *> $log
    $code = $LASTEXITCODE
} finally {
    Pop-Location
    Remove-Item Env:\INCLUDE -ErrorAction SilentlyContinue
    Remove-Item Env:\LIB -ErrorAction SilentlyContinue
}

$text = ''
if (Test-Path $log) { $text = Get-Content $log -Raw -ErrorAction SilentlyContinue }

if ($code -ne 0 -or -not (Test-Path $dll)) {
    Write-Host $text
    throw ('native build FAILED (exit ' + $code + '), see ' + $log)
}

$size = (Get-Item $dll).Length
Write-Host ('BUILD SUCCEEDED  ' + $dll + '  ' + $size + ' bytes')
$warn = @($text -split "`r?`n" | Where-Object { $_ -match 'warning C' })
if ($warn.Count) { Write-Host ('compiler warnings: ' + $warn.Count) }

exit 0
