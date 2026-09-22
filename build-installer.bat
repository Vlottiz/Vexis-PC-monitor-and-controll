@echo off
setlocal
title Vexis - Build Installer
cd /d "%~dp0"
echo.
echo  ============================================
echo   Vexis - Installer Builder
echo  ============================================
echo.

:: ── Version (single source: <Version> in Pcmonitor2_0.csproj) ─────────────────
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'Pcmonitor2_0.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set VERSION=%%v
if "%VERSION%"=="" (
    echo  [!] Could not read ^<Version^> from Pcmonitor2_0.csproj
    goto :fail
)
echo  Version: %VERSION%
echo.

:: ── Step 1: PawnIO sensor driver installer (bundled into the setup) ───────────
echo  [1/4] Checking PawnIO_setup.exe...
if not exist "PawnIO_setup.exe" (
    echo      Downloading from github.com/namazso/PawnIO.Setup ...
    curl.exe -L --fail -o PawnIO_setup.exe https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe
    if not exist "PawnIO_setup.exe" (
        echo      [!] Download failed. Download it manually from https://pawnio.eu into this folder.
    )
) else (
    echo      Found.
)
if not exist "hidapi.dll" (
    echo      [!] hidapi.dll missing - MSI motherboard RGB will not work.
)
echo.

:: ── Step 2: Publish ────────────────────────────────────────────────────────────
echo  [2/4] Publishing app (Release, win-x64, self-contained)...
taskkill /IM Vexis.exe /F >nul 2>&1
if exist publish rmdir /s /q publish
dotnet publish Pcmonitor2_0.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
if errorlevel 1 (
    echo.
    echo  [!] Publish failed - see the errors above.
    goto :fail
)
copy /y LICENSE.txt publish\ >nul
echo.

:: ── Step 3: Find NSIS ─────────────────────────────────────────────────────────
echo  [3/4] Looking for NSIS...
set "NSIS="
if exist "%ProgramFiles(x86)%\NSIS\makensis.exe" set "NSIS=%ProgramFiles(x86)%\NSIS\makensis.exe"
if exist "%ProgramFiles%\NSIS\makensis.exe"      set "NSIS=%ProgramFiles%\NSIS\makensis.exe"
if not defined NSIS (
    echo.
    echo  [!] NSIS not found. Install it from https://nsis.sourceforge.io/Download
    echo      then run this script again.
    echo.
    echo  The app itself is built in: %~dp0publish\
    explorer "%~dp0publish"
    goto :fail
)

:: ── Step 4: Build installer ────────────────────────────────────────────────────
echo  [4/4] Building installer...
"%NSIS%" /V2 /DVERSION=%VERSION% installer.nsi
if errorlevel 1 (
    echo  [!] NSIS build failed - see the errors above.
    goto :fail
)

echo.
echo  ============================================
echo   SUCCESS - VexisHM-Setup.exe  (v%VERSION%)
echo  ============================================
echo.
echo  Next: create a GitHub release tagged v%VERSION% and attach
echo  VexisHM-Setup.exe so the in-app update check sees it.
echo.
explorer /select,"%~dp0VexisHM-Setup.exe"
pause
exit /b 0

:fail
echo.
pause
exit /b 1
