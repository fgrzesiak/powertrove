#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif

[Setup]
AppId=PowerTrove
AppName=PowerTrove
AppVersion={#AppVersion}
AppPublisher=PowerTrove
DefaultDirName={localappdata}\Programs\PowerTrove
DefaultGroupName=PowerTrove
UninstallDisplayIcon={app}\PowerTrove.exe
OutputBaseFilename=PowerTrove-v{#AppVersion}-win-x64-installer
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
WizardStyle=modern

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PowerTrove"; Filename: "{app}\PowerTrove.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PowerTrove.exe"; Description: "Launch PowerTrove"; Flags: nowait postinstall skipifsilent unchecked

[Code]
// Guard version downgrade in the user's existing installation; never reset settings.
function InstalledVersion(): String;
begin
  if not RegQueryStringValue(HKCU,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerTrove_is1', 'DisplayVersion', Result) then
    Result := '';
end;

function CompareNumericVersions(OldVersion, NewVersion: String): Integer;
var
  PosOld, PosNew, ValueOld, ValueNew: Integer;
  OldPart, NewPart: String;
begin
  Result := 0;
  while (OldVersion <> '') or (NewVersion <> '') do
  begin
    PosOld := Pos('.', OldVersion);
    if PosOld > 0 then
    begin
      OldPart := Copy(OldVersion, 1, PosOld - 1);
      Delete(OldVersion, 1, PosOld);
    end
    else
    begin
      OldPart := OldVersion;
      OldVersion := '';
    end;

    PosNew := Pos('.', NewVersion);
    if PosNew > 0 then
    begin
      NewPart := Copy(NewVersion, 1, PosNew - 1);
      Delete(NewVersion, 1, PosNew);
    end
    else
    begin
      NewPart := NewVersion;
      NewVersion := '';
    end;

    if (OldPart = '') or (NewPart = '') then
    begin
      Result := 2;
      Exit;
    end;
    ValueOld := StrToIntDef(OldPart, -1);
    ValueNew := StrToIntDef(NewPart, -1);
    if (ValueOld < 0) or (ValueNew < 0) then
    begin
      Result := 2;
      Exit;
    end;
    if ValueOld < ValueNew then
    begin
      Result := -1;
      Exit;
    end;
    if ValueOld > ValueNew then
    begin
      Result := 1;
      Exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Existing: String;
  Comparison: Integer;
begin
  Result := True;
  Existing := InstalledVersion();
  if Existing = '' then Exit;
  Comparison := CompareNumericVersions(Existing, '{#AppVersion}');
  if Comparison > 0 then
  begin
    MsgBox('A newer version of PowerTrove is installed, or the installed version cannot be verified. Downgrade is refused.',
      mbError, MB_OK);
    Result := False;
  end;
end;
