; Vexis Installer
; Build: makensis installer.nsi  |  Requires .\publish\ folder

!include "MUI2.nsh"
!include "LogicLib.nsh"

; ── Metadata ──────────────────────────────────────────────────────────────────
Name              "Vexis Hardware Monitoring"
OutFile           "VexisHM-Setup.exe"
!define MUI_ICON                     "vexis.ico"
!define MUI_UNICON                   "vexis.ico"
InstallDir        "$PROGRAMFILES64\Vexis"
InstallDirRegKey  HKLM "Software\Vexis" "InstallDir"
RequestExecutionLevel admin
SetCompressor     /SOLID lzma
Unicode           True

; ── Version info ──────────────────────────────────────────────────────────────
VIProductVersion  "2.0.0.0"
VIAddVersionKey   "ProductName"      "Vexis Hardware Monitoring"
VIAddVersionKey   "ProductVersion"   "2.0.0"
VIAddVersionKey   "FileDescription"  "Vexis Hardware Monitoring Installer"
VIAddVersionKey   "LegalCopyright"   "MIT License"

; ── MUI Settings ──────────────────────────────────────────────────────────────
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE       "Vexis Hardware Monitoring 2.0"
!define MUI_WELCOMEPAGE_TEXT        "A real-time hardware monitor with fan control and RGB support.$\r$\n$\r$\nRequires Windows 10/11 (64-bit) and Administrator privileges.$\r$\nInternet connection required on first launch to download hardware sensor driver."
!define MUI_FINISHPAGE_RUN          "$INSTDIR\Vexis.exe"
!define MUI_FINISHPAGE_RUN_TEXT     "Launch Vexis"
!define MUI_FINISHPAGE_LINK         "View on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "https://github.com/Vlottiz/vexis"

; ── Pages ─────────────────────────────────────────────────────────────────────
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE       "LICENSE.txt"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ── Install ───────────────────────────────────────────────────────────────────
Section "Vexis Hardware Monitoring" SecMain
  SectionIn RO  ; required, can't deselect

  SetOutPath "$INSTDIR"

  ; Copy all published files
  File /r "publish\*.*"

  ; Add Defender exclusion for the app folder and exe (needs admin — we have it)
  nsExec::ExecToLog 'powershell.exe -NoProfile -WindowStyle Hidden -Command "Add-MpPreference -ExclusionPath \"$INSTDIR\" -ExclusionProcess \"Vexis.exe\" -ErrorAction SilentlyContinue"'

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Registry — install info
  WriteRegStr HKLM "Software\Vexis" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Vexis" "Version"    "2.0.0"

  ; Add/Remove Programs entry
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "DisplayName"     "Vexis Hardware Monitoring"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "DisplayVersion"  "2.0.0"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "Publisher"       "your-handle"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "DisplayIcon"     "$INSTDIR\Vexis.exe"
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "NoRepair"  1

  ; Start Menu shortcut
  CreateDirectory "$SMPROGRAMS\Vexis"
  CreateShortcut  "$SMPROGRAMS\Vexis\Vexis.lnk" \
    "$INSTDIR\Vexis.exe" "" "$INSTDIR\Vexis.exe" 0
  CreateShortcut  "$SMPROGRAMS\Vexis\Uninstall.lnk" \
    "$INSTDIR\Uninstall.exe"

  ; Desktop shortcut
  CreateShortcut "$DESKTOP\Vexis.lnk" \
    "$INSTDIR\Vexis.exe" "" "$INSTDIR\Vexis.exe" 0

SectionEnd

; ── Uninstall ─────────────────────────────────────────────────────────────────
Section "Uninstall"
  ; Stop running instance
  nsExec::ExecToLog 'taskkill /IM Vexis.exe /F'

  ; Remove Defender exclusions
  nsExec::ExecToLog 'powershell.exe -NoProfile -WindowStyle Hidden -Command "Remove-MpPreference -ExclusionPath \"$INSTDIR\" -ExclusionProcess \"Vexis.exe\" -ErrorAction SilentlyContinue"'

  ; Remove files
  RMDir /r "$INSTDIR"

  ; Remove shortcuts
  RMDir /r "$SMPROGRAMS\Vexis"
  Delete    "$DESKTOP\Vexis.lnk"

  ; Remove registry
  DeleteRegKey HKLM "Software\Vexis"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis"

SectionEnd
