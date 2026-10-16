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
Filename: "{app}\Dingus.exe"; Flags: nowait; Check: ReopenAfterSilentInstall

[Code]
// Dingus needs the .NET 8 Desktop Runtime. Most PCs already have it; if not, it's downloaded from
// Microsoft (about 55 MB) and installed before Dingus. Keeps the Dingus download itself small.
const
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function HasDesktopRuntime8: Boolean;
var
  Dir: String;
  Rec: TFindRec;
begin
  Result := False;
  Dir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\');
  if FindFirst(Dir + '8.*', Rec) then
  begin
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then Result := True;
      until Result or not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

// After a silent in-app update, open Dingus again (/NOLAUNCH turns that off, for automated tests).
function ReopenAfterSilentInstall: Boolean;
begin
  Result := WizardSilent and (Pos('/NOLAUNCH', UpperCase(GetCmdTail)) = 0);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Getting .NET', 'Dingus needs Microsoft''s .NET 8 Desktop Runtime. Downloading it now…', nil);
end;

function InstallRuntime: String;
var
  Code: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  // The runtime installer asks Windows for permission itself (it installs for all users).
  if not ShellExec('', Exe, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
    Result := 'The .NET Desktop Runtime couldn''t be started. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this setup again.'
  else if (Code <> 0) and (Code <> 1638) and (Code <> 3010) then
    Result := 'The .NET Desktop Runtime didn''t install (code ' + IntToStr(Code) + '). Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run this setup again.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if HasDesktopRuntime8 then Exit;
  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      Result := 'Couldn''t download the .NET Desktop Runtime: ' + GetExceptionMessage + '. Check your internet connection and try again.';
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;
  Result := InstallRuntime;
end;

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
