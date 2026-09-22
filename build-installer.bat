@echo off
setlocal
title Vexis - Build Installer
cd /d "%~dp0"
set "LOG=%~dp0build.log"
echo Vexis build log > "%LOG%"
echo.
echo  ============================================
echo   Vexis - Installer Builder
echo  ============================================
echo.

rem --- Version: single source is <Version> in Pcmonitor2_0.csproj ------------
set "VERSION="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'Pcmonitor2_0.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "VERSION=%%v"
if not defined VERSION goto :no_version
echo  Version: %VERSION%
echo Version: %VERSION% >> "%LOG%"
echo.

rem --- Step 1: PawnIO sensor driver installer (bundled into the setup) -------
echo  [1/4] Checking PawnIO_setup.exe...
if exist "PawnIO_setup.exe" goto :have_pawnio
echo      Downloading from github.com/namazso/PawnIO.Setup ...
curl.exe -L --fail -o PawnIO_setup.exe https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe >> "%LOG%" 2>&1
if not exist "PawnIO_setup.exe" echo      [!] Download failed - put PawnIO_setup.exe from https://pawnio.eu in this folder.
goto :after_pawnio
:have_pawnio
echo      Found.
:after_pawnio
if not exist "hidapi.dll" echo      [!] hidapi.dll missing - MSI motherboard RGB will not work.
echo.

rem --- Step 2: Publish ------------------------------------------------------
echo  [2/4] Publishing app (Release, win-x64, self-contained)...
echo      This takes a minute. Output goes to build.log
taskkill /IM Vexis.exe /F >nul 2>&1
if exist publish rmdir /s /q publish
dotnet publish Pcmonitor2_0.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish >> "%LOG%" 2>&1
if errorlevel 1 goto :publish_failed
if not exist "publish\Vexis.exe" goto :publish_failed
copy /y LICENSE.txt publish\ >nul
echo      Done.
echo.

rem --- Step 3: Find NSIS ----------------------------------------------------
echo  [3/4] Looking for NSIS...
set "NSIS="
if exist "%ProgramFiles(x86)%\NSIS\makensis.exe" set "NSIS=%ProgramFiles(x86)%\NSIS\makensis.exe"
if exist "%ProgramFiles%\NSIS\makensis.exe" set "NSIS=%ProgramFiles%\NSIS\makensis.exe"
if not defined NSIS goto :no_nsis
echo      %NSIS%
echo.

rem --- Step 4: Build installer ----------------------------------------------
echo  [4/4] Building installer...
if exist "VexisHM-Setup.exe" del /f /q "VexisHM-Setup.exe"
"%NSIS%" /V2 /DVERSION=%VERSION% installer.nsi >> "%LOG%" 2>&1
if errorlevel 1 goto :nsis_failed
if not exist "VexisHM-Setup.exe" goto :setup_missing

echo.
echo  ============================================
echo   SUCCESS - VexisHM-Setup.exe  v%VERSION%
echo  ============================================
echo.
echo  Next: create a GitHub release tagged v%VERSION% and attach
echo  VexisHM-Setup.exe so the in-app update check sees it.
echo.
explorer /select,"%~dp0VexisHM-Setup.exe"
pause
exit /b 0

rem --- Errors ---------------------------------------------------------------
:no_version
echo  [!] Could not read the Version from Pcmonitor2_0.csproj
goto :fail

:publish_failed
echo.
echo  [!] dotnet publish failed. Last lines of build.log:
echo.
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 30"
goto :fail

:no_nsis
echo.
echo  [!] NSIS not found. Install it from https://nsis.sourceforge.io/Download
echo      then run this script again.
echo      The app itself is built in: %~dp0publish\
goto :fail

:nsis_failed
echo.
echo  [!] NSIS failed. Last lines of build.log:
echo.
powershell -NoProfile -Command "Get-Content '%LOG%' -Tail 30"
goto :fail

:setup_missing
echo.
echo  [!] NSIS finished but VexisHM-Setup.exe is not here.
echo      Windows Defender may have removed it - check
echo      Windows Security - Virus and threat protection - Protection history.
goto :fail

:fail
echo.
echo  Full log: %LOG%
echo.
pause
exit /b 1
