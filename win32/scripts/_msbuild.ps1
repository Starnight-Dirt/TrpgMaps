# Shared helper: locate MSBuild. Dot-source this file:
#   . (Join-Path $PSScriptRoot '_msbuild.ps1')
#   $msbuild = Get-MSBuildPath
#
# Kept in its own file so build.ps1 and make_release.ps1 agree on where MSBuild is.
# IMPORTANT: this file must stay ASCII-only (see build.ps1 for why).

function Get-MSBuildPath {
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
