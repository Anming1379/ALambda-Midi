@echo off
rem ALambda Midi - .NET 10 Desktop Runtime helper
rem This launcher is ASCII-only; the real work is done by install-dotnet.ps1
setlocal
cd /d "%~dp0"
set "ARGS=%*"
if /i "%ARGS%"=="/quiet" set "ARGS=-Quiet"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-dotnet.ps1" %ARGS%
set "RC=%ERRORLEVEL%"
if "%RC%"=="" set "RC=0"
exit /b %RC%
