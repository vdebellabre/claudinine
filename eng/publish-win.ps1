# Local win-x64 AOT publish + smoke test.
#
# This machine's VS 2026 has the MSVC toolchain binaries and onecore libs on
# disk, but the component registration is broken: vswhere does not report
# VC.Tools.x86.x64, so ILCompiler's findvcvarsall.bat (and even VsDevCmd)
# cannot set up the environment. We bypass detection entirely:
# IlcUseEnvironmentalTools=true + manual LIB/PATH. CI uses stock runners and
# needs none of this.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

$vc = Get-ChildItem "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Tools\MSVC" |
    Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\Lib"
$sdk = Get-ChildItem $sdkRoot | Sort-Object Name -Descending | Select-Object -First 1

$env:LIB = "$($vc.FullName)\lib\onecore\x64;$($sdk.FullName)\um\x64;$($sdk.FullName)\ucrt\x64"
$env:PATH = "$($vc.FullName)\bin\Hostx64\x64;$env:PATH"

dotnet publish "$repo/src/Claudinine" -c Release -r win-x64 -o "$repo/publish/win-x64" --nologo -p:IlcUseEnvironmentalTools=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& "$repo/publish/win-x64/claudinine.exe" version
'{"hook_event_name":"SessionStart","transcript_path":"C:\\nonexistent"}' | & "$repo/publish/win-x64/claudinine.exe" hook
if ($LASTEXITCODE -ne 0) { throw "hook smoke test failed: $LASTEXITCODE" }
Write-Host "OK: $([math]::Round((Get-Item "$repo/publish/win-x64/claudinine.exe").Length/1MB,2)) MB"
