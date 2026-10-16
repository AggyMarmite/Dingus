; Dingus installer (Inno Setup 6). Built automatically by .github/workflows/release.yml:
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\publish installer\Dingus.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E2F3C1A-8B4D-4E7A-9C2B-D1A5F0E7B934}
AppName=Dingus
AppVersion={#AppVersion}
AppVerName=Dingus {#AppVersion}
AppPublisher=AggyMarmite
AppPublisherURL=https://github.com/AggyMarmite/Dingus
AppSupportURL=https://github.com/AggyMarmite/Dingus/issues
AppUpdatesURL=https://github.com/AggyMarmite/Dingus/releases
DefaultDirName={localappdata}\Programs\Dingus
DefaultGroupName=Dingus
DisableProgramGroupPage=yes
; Installs for the current user only – no admin prompt, and in-app updates can run silently.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=Dingus-Setup-{#AppVersion}
SetupIconFile=..\Assets\Dingus.ico
UninstallDisplayIcon={app}\Dingus.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy before updating it, and reopen it afterwards.
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start Dingus when Windows starts"; GroupDescription: "Other:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Dingus"; Filename: "{app}\Dingus.exe"
Name: "{autodesktop}\Dingus"; Filename: "{app}\Dingus.exe"; Tasks: desktopicon

[Registry]
; Same entry the app's own "Start with Windows" setting uses, so the two stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Dingus"; \
  ValueData: """{app}\Dingus.exe"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Dingus.exe"; Description: "Open Dingus"; Flags: nowait postinstall skipifsilent
; After a silent in-app update, open the app again.
Filename: "{app}\Dingus.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
