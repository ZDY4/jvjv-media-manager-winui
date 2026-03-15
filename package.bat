@echo off
setlocal

set ROOT=%~dp0
set PROJECT=%ROOT%src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj
set OUTDIR=%ROOT%artifacts\publish
set TOOL=%ROOT%tools\Invoke-WindowsAppSdkDotNet.ps1

set RID=win-x64
if not "%~1"=="" set RID=%~1
set PLATFORM=x64
if /I "%RID%"=="win-x86" set PLATFORM=x86
if /I "%RID%"=="win-arm64" set PLATFORM=ARM64
set SELF_CONTAINED=true
set WINDOWS_APP_SDK_SELF_CONTAINED=true
if /I "%~2"=="framework-dependent" (
    set SELF_CONTAINED=false
    set WINDOWS_APP_SDK_SELF_CONTAINED=false
)

if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"

echo Publishing %PROJECT%
echo Output: %OUTDIR%
echo Runtime: %RID%
echo Platform: %PLATFORM%
echo Self-contained: %SELF_CONTAINED%
if /I "%SELF_CONTAINED%"=="false" echo Requires Windows App Runtime on target machine.

pwsh -NoLogo -NoProfile -Command "& '%TOOL%' -Action Publish -Target '%PROJECT%' -Configuration Release -RuntimeIdentifier '%RID%' -Output '%OUTDIR%' -AdditionalArgs @('--self-contained','%SELF_CONTAINED%','-p:WindowsAppSDKSelfContained=%WINDOWS_APP_SDK_SELF_CONTAINED%','-p:Platform=%PLATFORM%','-p:PublishSingleFile=false','-p:PublishTrimmed=false')"
if errorlevel 1 exit /b 1

echo Done.
