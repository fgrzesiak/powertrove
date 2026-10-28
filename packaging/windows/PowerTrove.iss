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
SetupIconFile=..\..\src\PowerTrove.Desktop\Assets\PowerTrove.ico
InfoAfterFile={#PayloadDir}\THIRD-PARTY-NOTICES.txt
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

[InstallDelete]
; Core-only releases compile in no pack. Remove only the pack files earlier PowerTrove versions installed here;
; downloaded packs live in the per-user pack store and are never touched.
Type: files; Name: "{app}\PowerTrove.Pack.*.dll"
Type: files; Name: "{app}\PowerTrove.Pack.*.pdb"
Type: filesandordirs; Name: "{app}\pack-manifests"
; The worker now shares the app directory; remove the separate copy earlier versions installed.
Type: filesandordirs; Name: "{app}\packhost"

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PowerTrove"; Filename: "{app}\PowerTrove.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\PowerTrove.exe"; Description: "Launch PowerTrove"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsInAppUpdate
Filename: "{app}\PowerTrove.exe"; Flags: nowait; Check: IsInAppUpdate

[Code]
var
  PackPage: TInputOptionWizardPage;
  FreshInstall: Boolean;

// Official pack IDs in the order of the selection page. Keep in sync with pack-releases.json.
function PackId(Index: Integer): String;
begin
  case Index of
    0: Result := 'powertrove.files';
    1: Result := 'powertrove.pdf';
    2: Result := 'powertrove.images';
    3: Result := 'powertrove.media';
    4: Result := 'powertrove.office';
    5: Result := 'powertrove.data';
    6: Result := 'powertrove.tables';
    7: Result := 'powertrove.einvoice';
  else
    Result := '';
  end;
end;

procedure InitializeWizard();
begin
  PackPage := CreateInputOptionPage(wpWelcome, 'Choose your tools', 'Which tools should PowerTrove download?',
    'PowerTrove installs only its core. Setup then downloads the selected tools from the official, signed channel and verifies each one. ' +
    'If that is not possible now, for example offline, PowerTrove installs them at its first start. You can add or remove tools in Settings at any time.', False, False);
  PackPage.Add('File Tools: rename, zip, compare and sort files');
  PackPage.Add('PDF Tools: merge, split, convert and organize PDFs');
  PackPage.Add('Image Tools: convert, resize and compress images');
  PackPage.Add('Audio and Video Tools: convert, trim and compress audio and video');
  PackPage.Add('Office tools: convert Office documents to PDF and PDFs to Word');
  PackPage.Add('Data tools: convert CSV, JSON, Excel and contact files');
  PackPage.Add('Table Export: export Excel selections as CSV, JSON, Markdown or SQL');
  PackPage.Add('E-Invoice Tools: view and convert XRechnung and ZUGFeRD invoices');
  PackPage.Values[0] := True;
  PackPage.Values[1] := True;
  PackPage.Values[2] := True;
end;

// Updates keep their packs: the app takes over formerly compiled-in packs, and Settings manages the rest.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PackPage <> nil) and (PageID = PackPage.ID) and not FreshInstall;
end;

// The app's headless setup command installs these through its verified package manager and removes this file.
// Anything it cannot install now stays listed, and the app installs it at its next start.
procedure CurStepChanged(CurStep: TSetupStep);
var
  I, ResultCode: Integer;
  Selection: String;
begin
  if (CurStep <> ssPostInstall) or not FreshInstall then Exit;
  Selection := '';
  for I := 0 to 7 do
    if PackPage.Values[I] then
    begin
      if Selection <> '' then Selection := Selection + ',';
      Selection := Selection + '"' + PackId(I) + '"';
    end;
  if Selection = '' then Exit;
  SaveStringToFile(ExpandConstant('{app}\setup-packs.json'), '{"packs":[' + Selection + ']}', False);
  WizardForm.StatusLabel.Caption := 'Downloading and verifying the selected tools...';
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not Exec(ExpandConstant('{app}\PowerTrove.exe'), '--install-setup-packs', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
      or (ResultCode <> 0) then
      SuppressibleMsgBox('Some tools could not be downloaded now. PowerTrove installs them at its next start.', mbInformation, MB_OK, IDOK);
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

// Only the explicit in-app update handoff relaunches automatically. Normal installs retain opt-in launch.
function IsInAppUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/POWERTROVEUPDATE') = 0 then
      Result := True;
end;

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
  FreshInstall := Existing = '';
  if FreshInstall then Exit;
  Comparison := CompareNumericVersions(Existing, '{#AppVersion}');
  if Comparison > 0 then
  begin
    MsgBox('A newer version of PowerTrove is installed, or the installed version cannot be verified. Downgrade is refused.',
      mbError, MB_OK);
    Result := False;
  end;
end;
