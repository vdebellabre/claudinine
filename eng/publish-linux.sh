#!/bin/sh
# Runs inside mcr.microsoft.com/dotnet/sdk:10.0 (see eng/publish-linux.ps1).
# Native AOT prerequisites on Debian: clang + zlib1g-dev.
set -e
apt-get update -qq
apt-get install -y -qq clang zlib1g-dev >/dev/null
cd /repo
dotnet publish src/Claudinine -c Release -r linux-x64 -o /out -v q --nologo
ls -l /out
/out/claudinine version
echo '{"hook_event_name":"SessionStart","transcript_path":"/nonexistent"}' | /out/claudinine hook
echo HOOK_OK
