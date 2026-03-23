@echo off
setlocal

set ROOT=%~dp0
set PROJECT=%ROOT%src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj
set TOOL=%ROOT%tools\Invoke-WindowsAppSdkDotNet.ps1
set RID=win-x64
if not "%~1"=="" set RID=%~1

set PLATFORM=x64
if /I "%RID%"=="win-x86" set PLATFORM=x86
if /I "%RID%"=="win-arm64" set PLATFORM=ARM64

for /f %%i in ('pwsh -NoLogo -NoProfile -Command "Get-Date -Format yyyyMMdd-HHmmss"') do set RUNSTAMP=%%i
set OUTDIR=%ROOT%artifacts\run\self-contained\%RID%\%RUNSTAMP%
set EXE=%OUTDIR%\JvJvMediaManager.WinUI.exe

echo Publishing self-contained JvJv Media Manager (%RID%)...
pwsh -NoLogo -NoProfile -Command "& '%TOOL%' -Action Publish -Target '%PROJECT%' -Configuration Release -RuntimeIdentifier '%RID%' -Output '%OUTDIR%' -AdditionalArgs @('--self-contained','true','-p:WindowsAppSDKSelfContained=true','-p:Platform=%PLATFORM%','-p:PublishSingleFile=false','-p:PublishTrimmed=false')"
if errorlevel 1 (
    echo Publish failed.
    exit /b 1
)

if not exist "%EXE%" (
    echo Executable not found:
    echo %EXE%
    exit /b 1
)

echo Launching...
start "" "%EXE%"
