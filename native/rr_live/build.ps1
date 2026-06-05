# Builds rr_live.dll (x64). Requires the VS C++ build tools (VC.Tools.x86.x64).
$ErrorActionPreference = "Stop"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw "VC++ build tools not found. Install the 'Desktop development with C++' workload." }
$vcvars = Join-Path $vsRoot "VC\Auxiliary\Build\vcvars64.bat"
$dir = $PSScriptRoot
cmd /c "`"$vcvars`" && cd /d `"$dir`" && cl /nologo /LD /EHsc /O2 /MT rr_live.cpp /Fe:rr_live.dll"
if (Test-Path (Join-Path $dir "rr_live.dll")) { Write-Output "OK: rr_live.dll built." } else { throw "Build produced no DLL." }
