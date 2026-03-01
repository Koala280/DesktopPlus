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
  StartupApprovedRunRegistryKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  StartupRunRegistryValue = 'DesktopPlus';
  StartupLaunchArgument = '--startup';
  SettingsFileName = 'DesktopPlus_Settings.json';

var
  StartupEntryExistsBeforeInstall: Boolean;
  StartupShouldBeEnabledAfterInstall: Boolean;

function BuildStartupCommand(): string;
begin
  Result := '"' + ExpandConstant('{app}\{#MyAppExeName}') + '" ' + StartupLaunchArgument;
end;

function NormalizeSettingsJson(Input: string): string;
begin
  Result := Lowercase(Input);
  StringChangeEx(Result, ' ', '', True);
  StringChangeEx(Result, #9, '', True);
  StringChangeEx(Result, #13, '', True);
  StringChangeEx(Result, #10, '', True);
end;

function TryReadStartWithWindowsFromSettings(var StartWithWindowsEnabled: Boolean): Boolean;
var
  SettingsPath: string;
  JsonText: AnsiString;
  Normalized: string;
begin
  Result := False;
  StartWithWindowsEnabled := False;
  SettingsPath := ExpandConstant('{userappdata}') + '\' + SettingsFileName;

  if not FileExists(SettingsPath) then
  begin
    Exit;
  end;

  if not LoadStringFromFile(SettingsPath, JsonText) then
  begin
    Exit;
  end;

  Normalized := NormalizeSettingsJson(string(JsonText));
  if Pos('"startwithwindows":true', Normalized) > 0 then
  begin
    StartWithWindowsEnabled := True;
    Result := True;
    Exit;
  end;

  if Pos('"startwithwindows":false', Normalized) > 0 then
  begin
    StartWithWindowsEnabled := False;
    Result := True;
    Exit;
  end;
end;

function InitializeSetup(): Boolean;
var
  ExistingStartupValue: string;
  SettingsStartupEnabled: Boolean;
  HasSettingsValue: Boolean;
begin
  StartupEntryExistsBeforeInstall := RegQueryStringValue(
    HKEY_CURRENT_USER,
    StartupRunRegistryKey,
    StartupRunRegistryValue,
    ExistingStartupValue
  ) and (Trim(ExistingStartupValue) <> '');

  HasSettingsValue := TryReadStartWithWindowsFromSettings(SettingsStartupEnabled);
  if HasSettingsValue then
  begin
    StartupShouldBeEnabledAfterInstall := SettingsStartupEnabled;
  end
  else
  begin
    StartupShouldBeEnabledAfterInstall := StartupEntryExistsBeforeInstall;
  end;

  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and StartupShouldBeEnabledAfterInstall then
  begin
    RegWriteStringValue(
      HKEY_CURRENT_USER,
      StartupRunRegistryKey,
      StartupRunRegistryValue,
      BuildStartupCommand()
    );
    RegDeleteValue(HKEY_CURRENT_USER, StartupApprovedRunRegistryKey, StartupRunRegistryValue);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, StartupRunRegistryKey, StartupRunRegistryValue);
    RegDeleteValue(HKEY_CURRENT_USER, StartupApprovedRunRegistryKey, StartupRunRegistryValue);
  end;
end;
