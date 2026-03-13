@echo off
setlocal

set ROOT=%~dp0
set PROJECT=%ROOT%src\JvJvMediaManager.WinUI\JvJvMediaManager.WinUI.csproj
set OUTDIR=%ROOT%artifacts\publish

set RID=win-x64
if not "%~1"=="" set RID=%~1

if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"

echo Publishing %PROJECT%
echo Output: %OUTDIR%
echo Runtime: %RID%

dotnet publish "%PROJECT%" -c Release -r %RID% -o "%OUTDIR%" --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false
if errorlevel 1 exit /b 1

echo Done.
