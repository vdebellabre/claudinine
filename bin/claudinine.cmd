@echo off
rem Platform-routing shim (Windows half of the dual-shim pattern).
setlocal
set "arch=x64"
if /i "%PROCESSOR_ARCHITECTURE%"=="ARM64" set "arch=arm64"
"%~dp0win-%arch%\claudinine.exe" %*
endlocal & exit /b %ERRORLEVEL%
