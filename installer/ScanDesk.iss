; Inno Setup script for ScanDesk.
; Build: dotnet publish (see build-installer.ps1), then compile this file with ISCC.exe.

#define AppName "ScanDesk"
; The version is passed in by build-installer.ps1 (/DAppVersion=x.y.z); this is only a fallback.
#ifndef AppVersion
  #define AppVersion "0.0.0-local"
#endif
#define AppExe "ScanDesk.exe"

[Setup]
AppId={{6F1C2B7A-3E4D-4C1B-9A55-5C0D2E8B7A10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Jack Mutsers
AppPublisherURL=https://github.com/Jack-Mutsers/ScanDesk
AppCopyright=Copyright (C) 2026 Jack Mutsers. Licensed under GPL-3.0.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\ScanDesk.ico
OutputDir=..\dist
OutputBaseFilename=ScanDesk-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
; Dutch first: it is used when Windows' own language isn't one of these.
; InfoBeforeFile shows the privacy summary during installation (skipped for silent in-app updates).
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"; InfoBeforeFile: "privacy-nl.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "privacy-en.txt"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; In-app updates run the installer with /SILENT: reopen ScanDesk afterwards as the normal (non-admin) user.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent
