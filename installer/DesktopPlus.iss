#define MyAppName "DesktopPlus"
#define MyAppExeName "DesktopPlus.exe"

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#ifndef MyPublishDir
  #define MyPublishDir "..\\artifacts\\publish\\win-x64"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\\artifacts\\installer"
#endif

[Setup]
AppId={{9797F778-02C2-472B-AED5-9480B6DC93AE}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  StartupRunRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  StartupRunRegistryValue = 'DesktopPlus';
  StartupLaunchArgument = '--startup';

var
  StartupEntryExistsBeforeInstall: Boolean;

function BuildStartupCommand(): string;
begin
  Result := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" ' + StartupLaunchArgument;
end;

function InitializeSetup(): Boolean;
var
  ExistingStartupValue: string;
begin
  StartupEntryExistsBeforeInstall := RegQueryStringValue(
    HKEY_CURRENT_USER,
    StartupRunRegistryKey,
    StartupRunRegistryValue,
    ExistingStartupValue
  ) and (Trim(ExistingStartupValue) <> '');
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and StartupEntryExistsBeforeInstall then
  begin
    RegWriteStringValue(
      HKEY_CURRENT_USER,
      StartupRunRegistryKey,
      StartupRunRegistryValue,
      BuildStartupCommand()
    );
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, StartupRunRegistryKey, StartupRunRegistryValue);
  end;
end;
