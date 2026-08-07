# Local Linux AOT publish + smoke test via Podman (Windows has no cross-OS
# AOT, and this machine lacks the Windows SDK for local win-x64 linking).
$repo = Split-Path $PSScriptRoot -Parent
podman run --rm -v "${repo}:/repo" mcr.microsoft.com/dotnet/sdk:10.0 bash /repo/eng/publish-linux.sh
