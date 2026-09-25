; Vexis Installer
; Build: run build-installer.bat (it publishes the app and passes the version)
; Manual: makensis /DVERSION=2.1.0 installer.nsi  |  Requires .\publish\ folder

!ifndef VERSION
  !define VERSION "0.0.0"
!endif

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

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
VIProductVersion  "${VERSION}.0"
VIAddVersionKey   "ProductName"      "Vexis Hardware Monitoring"
VIAddVersionKey   "ProductVersion"   "${VERSION}"
VIAddVersionKey   "FileVersion"      "${VERSION}"
VIAddVersionKey   "FileDescription"  "Vexis Hardware Monitoring Installer"
VIAddVersionKey   "LegalCopyright"   "MIT License"

; ── MUI Settings ──────────────────────────────────────────────────────────────
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE       "Vexis Hardware Monitoring ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT        "A real-time hardware monitor with fan control and RGB support.$\r$\n$\r$\nRequires Windows 10/11 (64-bit) and Administrator privileges.$\r$\nThe PawnIO sensor driver will be installed if it is missing."
!define MUI_FINISHPAGE_RUN          "$INSTDIR\Vexis.exe"
!define MUI_FINISHPAGE_RUN_TEXT     "Launch Vexis"
!define MUI_FINISHPAGE_LINK         "View on GitHub"
!define MUI_FINISHPAGE_LINK_LOCATION "https://github.com/Vlottiz/Vexis-PC-monitor-and-controll"

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

  ; Close a running copy so files can be replaced when updating
  nsExec::ExecToLog 'taskkill /IM Vexis.exe /F'

  ; Copy all published files
  File /r "publish\*.*"

  ; WebView2 runtime — the UI needs it. Always on Windows 11; may be missing on
  ; Windows 10. The bootstrapper (downloaded by build-installer.bat) fetches the
  ; current runtime from Microsoft, so this step needs internet on such PCs.
  ; Registered under WOW6432Node (NSIS default 32-bit view) or per-user.
  ReadRegStr $3 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $3 == ""
  ${OrIf} $3 == "0.0.0.0"
    ReadRegStr $3 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $3 == ""
  ${OrIf} $3 == "0.0.0.0"
!if /FileExists "MicrosoftEdgeWebview2Setup.exe"
    DetailPrint "Installing Microsoft Edge WebView2 Runtime..."
    InitPluginsDir
    File "/oname=$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" "MicrosoftEdgeWebview2Setup.exe"
    ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $1
    DetailPrint "WebView2 setup exit code: $1"
!else
    DetailPrint "WebView2 Runtime missing - Vexis will show a download link on first launch."
!endif
  ${Else}
    DetailPrint "WebView2 Runtime $3 already installed."
  ${EndIf}

  ; Sensor driver — PawnIO (signed, works with Memory Integrity ON).
  ; $0 = PawnIO registered (uninstall key), $2 = driver service present.
  ; Older Vexis builds deleted the service but left the registration, so
  ; "registered but no service" is repaired with uninstall + install.
  SetRegView 64
  EnumRegValue $0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" 0
  EnumRegValue $2 HKLM "SYSTEM\CurrentControlSet\Services\PawnIO" 0
  SetRegView default
  ${If} $0 != ""
  ${AndIf} $2 != ""
    DetailPrint "PawnIO already installed."
  ${ElseIf} ${FileExists} "$INSTDIR\PawnIO_setup.exe"
    ${If} $0 != ""
      DetailPrint "Repairing PawnIO sensor driver..."
      ExecWait '"$INSTDIR\PawnIO_setup.exe" -uninstall -silent'
    ${EndIf}
    DetailPrint "Installing PawnIO sensor driver..."
    ExecWait '"$INSTDIR\PawnIO_setup.exe" -install -silent' $1
    DetailPrint "PawnIO setup exit code: $1"
  ${Else}
    DetailPrint "PawnIO_setup.exe not bundled - install PawnIO from https://pawnio.eu"
  ${EndIf}

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Registry — install info
  WriteRegStr HKLM "Software\Vexis" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\Vexis" "Version"    "${VERSION}"

  ; Add/Remove Programs entry
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "DisplayName"     "Vexis Hardware Monitoring"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis" \
    "Publisher"       "Vlottiz"
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

; In-app updates run this installer with /S /UPDATE (silent, so there is no
; finish page) — start the new version once the files are in place.
Function .onInstSuccess
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/UPDATE" $R1
  ${IfNot} ${Errors}
    Exec '"$INSTDIR\Vexis.exe"'
  ${EndIf}
FunctionEnd

; ── Uninstall ─────────────────────────────────────────────────────────────────
Section "Uninstall"
  ; Stop running instance
  nsExec::ExecToLog 'taskkill /IM Vexis.exe /F'

  ; Remove the "Start with Windows" logon task, if the user turned it on
  nsExec::ExecToLog 'schtasks /Delete /TN "Vexis" /F'

  ; Remove files
  RMDir /r "$INSTDIR"

  ; Remove shortcuts
  RMDir /r "$SMPROGRAMS\Vexis"
  Delete    "$DESKTOP\Vexis.lnk"

  ; Remove registry
  DeleteRegKey HKLM "Software\Vexis"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Vexis"

SectionEnd
