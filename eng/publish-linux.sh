#!/bin/sh
# Runs inside mcr.microsoft.com/dotnet/sdk:10.0 (see eng/publish-linux.ps1).
# Native AOT prerequisites on Debian: clang + zlib1g-dev.
set -e
apt-get update -qq
apt-get install -y -qq clang zlib1g-dev >/dev/null
# Run from src/: global.json lives there and is found by walking up from the
# working directory. Target the csproj, not the directory — the directory
# resolves to Claudinine.slnx, which also carries the (exe-type) test project,
# and a non-self-contained exe cannot reference this self-contained one
# (NETSDK1151). -o is absolute, so it is unaffected by the cwd.
cd /repo/src
dotnet publish Claudinine/Claudinine.csproj -c Release -r linux-x64 -o /out -v q --nologo
ls -l /out
/out/claudinine version
echo '{"hook_event_name":"SessionStart","transcript_path":"/nonexistent"}' | /out/claudinine hook
echo HOOK_OK
