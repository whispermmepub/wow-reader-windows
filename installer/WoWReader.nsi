Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define APP_NAME "WoW Reader"
!define APP_VERSION "1.0.0"
!define APP_EXE "WowReader.Windows.exe"
!define WEBVIEW2_GUID "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define WEBVIEW2_EXE "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
!define PROJECT_ROOT "${__FILEDIR__}/.."

Name "${APP_NAME}"
OutFile "${PROJECT_ROOT}/artifacts/offline-installer/WoWReader-Windows-Offline-Setup-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\WoW Reader"
InstallDirRegKey HKCU "Software\WoWReader" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
ShowInstDetails show
ShowUnInstDetails show

VIProductVersion "1.0.0.0"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} 64-bit offline installer"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "PolyForm Noncommercial 1.0.0; see bundled LICENSE.txt"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME}"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${PROJECT_ROOT}/LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "This installer requires 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Function DetectWebView2Runtime
  StrCpy $0 "0"
  SetRegView 32
  ClearErrors
  ReadRegStr $1 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
  ${If} $1 != ""
  ${AndIf} $1 != "0.0.0.0"
    StrCpy $0 "1"
  ${EndIf}
  ClearErrors
  ReadRegStr $1 HKCU "Software\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
  ${If} $1 != ""
  ${AndIf} $1 != "0.0.0.0"
    StrCpy $0 "1"
  ${EndIf}

  SetRegView 64
  ClearErrors
  ReadRegStr $1 HKLM "SOFTWARE\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
  ${If} $1 != ""
  ${AndIf} $1 != "0.0.0.0"
    StrCpy $0 "1"
  ${EndIf}
  ClearErrors
  ReadRegStr $1 HKCU "Software\Microsoft\EdgeUpdate\Clients\${WEBVIEW2_GUID}" "pv"
  ${If} $1 != ""
  ${AndIf} $1 != "0.0.0.0"
    StrCpy $0 "1"
  ${EndIf}
  SetRegView 64
FunctionEnd

Section "Install ${APP_NAME}" SEC_APP
  Call DetectWebView2Runtime
  ${If} $0 != "1"
    DetailPrint "Installing the bundled Microsoft WebView2 Evergreen Runtime (x64)..."
    SetOutPath "$PLUGINSDIR"
    File /oname=${WEBVIEW2_EXE} "${PROJECT_ROOT}/artifacts/offline-installer/input/${WEBVIEW2_EXE}"
    ExecWait '"$PLUGINSDIR\${WEBVIEW2_EXE}" /silent /install' $1
    StrCmp $1 "0" webview2_ok
    StrCmp $1 "3010" webview2_ok
    MessageBox MB_ICONSTOP "The Microsoft WebView2 Runtime installer failed (exit code $1). WoW Reader was not installed."
    SetErrorLevel $1
    Abort
  webview2_ok:
  ${EndIf}

  SetOutPath "$INSTDIR"
  File /r "${PROJECT_ROOT}/artifacts/offline-installer/publish/*"
  File /oname=LICENSE.txt "${PROJECT_ROOT}/LICENSE"
  File "${PROJECT_ROOT}/THIRD-PARTY-NOTICES.md"
  File "${PROJECT_ROOT}/README.md"
  CreateDirectory "$INSTDIR\docs"
  SetOutPath "$INSTDIR\docs"
  File "${PROJECT_ROOT}/docs/ANDROID-PARITY.md"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  SetRegView 32
  WriteRegStr HKCU "Software\WoWReader" "InstallDir" "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\WoW Reader"
  CreateShortcut "$SMPROGRAMS\WoW Reader\WoW Reader.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\WoW Reader\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\WoW Reader.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

Section "Uninstall"
  Delete "$SMPROGRAMS\WoW Reader\WoW Reader.lnk"
  Delete "$SMPROGRAMS\WoW Reader\Uninstall.lnk"
  RMDir "$SMPROGRAMS\WoW Reader"
  Delete "$DESKTOP\WoW Reader.lnk"
  DeleteRegKey HKCU "Software\WoWReader"
  RMDir /r "$INSTDIR"
  ; User library, settings, notes, fonts, and other local data under %LOCALAPPDATA%\WoWReader are intentionally preserved.
SectionEnd
