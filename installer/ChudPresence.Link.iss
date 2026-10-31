#ifndef SourceDir
  #error Pass /DSourceDir with the self-contained publish directory.
#endif
#ifndef OutputDir
  #error Pass /DOutputDir with the release directory.
#endif
#ifndef AppVersion
  #error Pass /DAppVersion with the application version.
#endif
#ifndef Runtime
  #define Runtime "win-x64"
#endif

#define AppName "ChudPresence Link"
#define AppExe "ChudPresence.Link.exe"

[Setup]
; Keep this ID stable across versions and architectures so upgrades replace the app.
AppId={{EFD0F081-4EB4-4D49-AC6C-42CB813CA467}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=ChudPresence
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
DefaultDirName={localappdata}\Programs\ChudPresence Link
DefaultGroupName={#AppName}
DisableProgramGroupPage=no
PrivilegesRequired=lowest
MinVersion=10.0.17763
#if Runtime == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=ChudPresence-Link-{#AppVersion}-{#Runtime}-Setup
SetupIconFile=..\src\ChudPresence.Link.Windows\Assets\Link.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
; Link closes to the tray. Require Exit Link before replacing/removing files.
AppMutex=Local\ChudPresence.Link
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
SetupAppRunningError=ChudPresence Link is running. Choose Exit Link from its window or system tray menu, then click OK to continue.
UninstallAppRunningError=ChudPresence Link is running. Choose Exit Link from its window or system tray menu, then click OK to continue.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; AppUserModelID: "ChudPresence.Link"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon; AppUserModelID: "ChudPresence.Link"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command, InstalledExe: String;
begin
  if CurUninstallStep = usUninstall then begin
    InstalledExe := ExpandConstant('{app}\{#AppExe}');
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#AppName}', Command) then begin
      { Preserve startup entries owned by a different portable copy. }
      if (CompareText(Copy(Command, 1, Length(InstalledExe) + 2), '"' + InstalledExe + '"') = 0)
        or (CompareText(Command, InstalledExe) = 0) then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    end;
  end;
end;
