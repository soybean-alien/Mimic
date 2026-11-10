; Mimic by soybean_alien
; Small per-user installer for the WinUI 3 build. It carries only Mimic itself.
; When the .NET 10 Runtime or Windows App Runtime is missing, setup opens
; Microsoft's download in the browser and waits. Tools and the Whisper model are downloaded inside Mimic.
;
; Build with build.ps1, which passes /DVERSION, /DSOURCE and /DOUTFILE.

Unicode true
SetCompressor /SOLID lzma
RequestExecutionLevel user
ManifestDPIAware true

!include "MUI2.nsh"
!include "FileFunc.nsh"

!define PRODUCT "Mimic"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT}"
!define POWERSHELL '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass'

Name "${PRODUCT}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\${PRODUCT}"
InstallDirRegKey HKCU "Software\soybean_alien\${PRODUCT}" "InstallDir"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileDescription" "${PRODUCT} setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "soybean_alien"

!define MUI_ICON "..\Mimic\Assets\icon.ico"
!define MUI_UNICON "..\Mimic\Assets\icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Mimic.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Open Mimic"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\START HERE.txt"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Read the step-by-step guide"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Section "Mimic" SecMain
  SectionIn RO

  ; Microsoft runtimes: check, and if one is missing open its download and wait.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File "prereqs.ps1"
  check_runtimes:
  nsExec::Exec '${POWERSHELL} -File "$PLUGINSDIR\prereqs.ps1"'
  Pop $0
  StrCmp $0 "0" runtimes_ready
    StrCpy $1 "the .NET 10 Runtime and the Windows App Runtime"
    StrCmp $0 "10" 0 +2
      StrCpy $1 "the .NET 10 Runtime (about 30 MB)"
    StrCmp $0 "20" 0 +2
      StrCpy $1 "the Windows App Runtime (about 125 MB)"
    IfSilent 0 +2
      Abort "A Microsoft runtime Mimic needs is missing."
    MessageBox MB_YESNO|MB_ICONINFORMATION "Mimic needs $1 from Microsoft, which this PC does not have yet.$\r$\n$\r$\nOpen Microsoft's download in your browser now? Setup will wait while you install it." IDYES +2
      Abort "Setup was cancelled before the Microsoft runtime was installed."
    StrCmp $0 "20" +2
      ExecShell "open" "https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-10.0.14-windows-x64-installer"
    StrCmp $0 "10" +2
      ExecShell "open" "https://aka.ms/windowsappsdk/2.6/latest/windowsappruntimeinstall-x64.exe"
    MessageBox MB_OKCANCEL|MB_ICONINFORMATION "Setup is paused.$\r$\n$\r$\nMicrosoft's download for $1 is open in your browser and starts by itself. Run the downloaded file, and when it has finished click OK to continue. Setup waits as long as you need.$\r$\n$\r$\nIf nothing opened, the downloads are:$\r$\nhttps://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-10.0.14-windows-x64-installer$\r$\nhttps://aka.ms/windowsappsdk/2.6/latest/windowsappruntimeinstall-x64.exe" IDOK check_runtimes
      Abort "The Microsoft runtime is missing."
  runtimes_ready:

  ; Remove the files of an earlier Mimic install. Saved playback files
  ; (resources\downloads) and everything in the user's Mimic folder are left alone.
  Delete "$INSTDIR\mimic.exe"
  Delete "$INSTDIR\MimicIconSource.exe"
  Delete "$INSTDIR\install-runtime.ps1"
  Delete "$INSTDIR\uninstall-user-shortcut.ps1"
  Delete "$INSTDIR\WebView2Loader.dll"
  RMDir /r "$INSTDIR\runtime"

  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"
  File /oname=README.txt "..\README.md"
  File "/oname=START HERE.txt" "..\START HERE.md"

  CreateShortcut "$DESKTOP\Mimic.lnk" "$INSTDIR\Mimic.exe" "" "$INSTDIR\Assets\icon.ico"
  CreateShortcut "$SMPROGRAMS\Mimic.lnk" "$INSTDIR\Mimic.exe" "" "$INSTDIR\Assets\icon.ico"

  WriteUninstaller "$INSTDIR\uninstall.exe"
  WriteRegStr HKCU "Software\soybean_alien\${PRODUCT}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "soybean_alien"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Assets\icon.ico"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\Mimic.lnk"
  Delete "$SMPROGRAMS\Mimic.lnk"
  ; Only the program files go. The library, tools and models in the user's Mimic
  ; folder, and saved playback files, stay.
  !include "${FILELIST}"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\START HERE.txt"
  Delete "$INSTDIR\uninstall.exe"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\soybean_alien\${PRODUCT}"
SectionEnd
