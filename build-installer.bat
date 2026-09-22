@echo off
title PC Monitor — Build Installer
echo.
echo  ============================================
echo   PC Monitor — Installer Builder
echo  ============================================
echo.

:: Check for admin
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo  [!] Run this as Administrator
    pause
    exit /b 1
)

:: Step 1 — Publish
echo  [1/3] Publishing app...
cd /d "%~dp0"
taskkill /IM Pcmonitor2.0.exe /F >nul 2>&1
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish >nul 2>&1
if %errorlevel% neq 0 (
    echo  [!] Publish failed. Run "dotnet publish" manually to see errors.
    pause
    exit /b 1
)
echo      Done.

:: Step 2 — Copy web assets + bundled DLLs into publish folder
echo  [2/4] Copying web assets...
copy /y *.html publish\ >nul 2>&1
copy /y *.js   publish\ >nul 2>&1
copy /y *.css  publish\ >nul 2>&1
copy /y LICENSE.txt publish\ >nul 2>&1
echo      Done.

:: Step 2b — Copy hidapi.dll (HIDAPI library for MSI ARGB header control)
echo  [3/4] Checking for hidapi.dll...
if exist "hidapi.dll" (
    copy /y hidapi.dll publish\ >nul 2>&1
    echo      hidapi.dll copied.
) else (
    echo      [!] hidapi.dll not found in project root.
    echo          MSI motherboard RGB will not work without it.
    echo          Download from: https://github.com/libusb/hidapi/releases
    echo          Extract hidapi.dll ^(x64^) and place it here: %~dp0hidapi.dll
)

:: Step 4 — Build installer
echo  [4/4] Building installer...

:: Try common NSIS locations
set NSIS=""
if exist "C:\Program Files (x86)\NSIS\makensis.exe" set NSIS="C:\Program Files (x86)\NSIS\makensis.exe"
if exist "C:\Program Files\NSIS\makensis.exe"       set NSIS="C:\Program Files\NSIS\makensis.exe"

if %NSIS%=="" (
    echo.
    echo  [!] NSIS not found. To build an installer:
    echo      1. Download NSIS from https://nsis.sourceforge.io/Download
    echo      2. Install it
    echo      3. Run this script again
    echo.
    echo  For now, your publish folder is ready to zip and share:
    echo  %~dp0publish\
    echo.
    explorer "%~dp0publish"
    pause
    exit /b 0
)

%NSIS% installer.nsi
if %errorlevel% neq 0 (
    echo  [!] NSIS build failed.
    pause
    exit /b 1
)

echo.
echo  ============================================
echo   SUCCESS — PCMonitor-Setup.exe is ready!
echo  ============================================
echo.
echo  Share PCMonitor-Setup.exe with anyone.
echo  They just double-click and follow the wizard.
echo.
explorer "%~dp0"
pause
