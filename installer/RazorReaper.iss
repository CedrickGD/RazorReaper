#define MyAppName "RazorReaper"
#define MyAppVersion "1.5.1"
#define MyAppPublisher ".itssomeguy on discord - telemetry data is being collected for statistics"
#define MyAppURL "rr.sellhub.cx"
#define MyAppExeName "RazorReaper.exe"

[Setup]
AppId={{7C0B8C10-169C-4157-A812-25C3F60197F6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=innosetuplicense.txt
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=C:\Users\cedri\Desktop
OutputBaseFilename=RazorReaper-Setup
SetupIconFile=rr-logo.ico
SolidCompression=yes
WizardStyle=modern dark windows11
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\RazorReaper\bin\Release\net10.0-windows10.0.19041.0\win-x64\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; win-x64\* is now the SELF-CONTAINED build output (bundled .NET 10 runtime + Windows App SDK).
; Excludes "publish\*" so a stray `dotnet publish` folder can never get double-packaged into the installer.
Source: "..\RazorReaper\bin\Release\net10.0-windows10.0.19041.0\win-x64\*"; DestDir: "{app}"; Excludes: "publish\*"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; nowait + postinstall: launch the new exe and don't block setup on it; preselected
;   in the interactive wizard as the "Launch RazorReaper" checkbox.
; runasoriginaluser: install runs elevated (DefaultDirName=Program Files), so
;   without this the relaunched app would inherit the admin token. Drops back
;   to the user who triggered the install.
; (no skipifsilent) is intentional: the auto-updater calls setup with
;   /VERYSILENT — this line is what makes the app come back up after the
;   silent install replaces files.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall runasoriginaluser

[Code]
procedure KillRunningApp;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  KillRunningApp;
  Result := '';
end;
