@echo off
setlocal

set ROOT=%~dp0
set JVJVMM_STORAGE_ROOT=%ROOT%data
set CONFIG=Debug
if not "%~1"=="" set CONFIG=%~1

set EXE=%ROOT%src\JvJvMediaManager.WinUI\bin\%CONFIG%\net10.0-windows10.0.19041.0\JvJvMediaManager.WinUI.exe
set RUNTIME_CHECK=pwsh -NoLogo -NoProfile -Command "if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8*' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"

echo Building JvJv Media Manager (%CONFIG%)...
pwsh -NoLogo -NoProfile -File "%ROOT%build.ps1" -Configuration %CONFIG%
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

if not exist "%EXE%" (
    echo Executable not found:
    echo %EXE%
    exit /b 1
)

%RUNTIME_CHECK%
if errorlevel 1 (
    echo.
    echo Windows App Runtime 1.8 was not detected.
    echo.
    echo Options:
    echo 1. Install it once when Windows prompts you.
    echo 2. Or run run-portable.bat to publish and launch a self-contained build.
    echo.
)

echo Launching...
start "" "%EXE%"
