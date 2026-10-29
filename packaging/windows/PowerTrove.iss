#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef InstallerMessagesFile
  #error InstallerMessagesFile is required (generated from Resources/Strings*.resx)
#endif

#include InstallerMessagesFile

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

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
UsePreviousTasks=yes

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

[Tasks]
Name: "startmenu"; Description: "{cm:Installer_StartMenu}"; GroupDescription: "{cm:Installer_WindowsIntegration}"
Name: "explorermenu"; Description: "{cm:Installer_ExplorerMenu}"; GroupDescription: "{cm:Installer_WindowsIntegration}"; Flags: unchecked

[Icons]
Name: "{autoprograms}\PowerTrove"; Filename: "{app}\PowerTrove.exe"; WorkingDir: "{app}"; Tasks: startmenu

[Run]
Filename: "{app}\PowerTrove.exe"; Description: "{cm:Installer_Launch}"; Flags: nowait postinstall skipifsilent unchecked; Check: not IsInAppUpdate
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

// Only the explicit in-app update handoff relaunches automatically and skips new selections.
function IsInAppUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/POWERTROVEUPDATE') = 0 then
      Result := True;
end;

procedure InitializeWizard();
var
  I: Integer;
  Description: String;
begin
  if FreshInstall then Description := CustomMessage('Installer_ToolsDescriptionFresh')
  else Description := CustomMessage('Installer_ToolsDescriptionExisting');
  PackPage := CreateInputOptionPage(wpWelcome, CustomMessage('Installer_ToolsTitle'),
    CustomMessage('Installer_ToolsSubtitle'), Description, False, False);
  PackPage.Add(CustomMessage('Installer_Files'));
  PackPage.Add(CustomMessage('Installer_Pdf'));
  PackPage.Add(CustomMessage('Installer_Images'));
  PackPage.Add(CustomMessage('Installer_Media'));
  PackPage.Add(CustomMessage('Installer_Office'));
  PackPage.Add(CustomMessage('Installer_Data'));
  PackPage.Add(CustomMessage('Installer_Tables'));
  PackPage.Add(CustomMessage('Installer_Invoice'));
  for I := 0 to 7 do
    PackPage.Values[I] := (FreshInstall and (I < 3)) or
      FileExists(ExpandConstant('{localappdata}\PowerTrove\packs\' + PackId(I) + '\active.json'));
end;

// Manual reinstalls can add tools. Automatic/silent updates retain existing selections without prompting.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := ((PackPage <> nil) and (PageID = PackPage.ID) and (IsInAppUpdate or WizardSilent)) or
    ((PageID = wpSelectTasks) and IsInAppUpdate);
end;

// The app's headless setup command installs these through its verified package manager and removes this file.
// Anything it cannot install now stays listed, and the app installs it at its next start.
procedure CurStepChanged(CurStep: TSetupStep);
var
  I, ResultCode: Integer;
  Selection: String;
begin
  if (CurStep <> ssPostInstall) or IsInAppUpdate then Exit;
  if WizardIsTaskSelected('explorermenu') then
    if not Exec(ExpandConstant('{app}\PowerTrove.exe'), '--setup-explorer-menu install ' + ActiveLanguage,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      SuppressibleMsgBox(CustomMessage('Installer_ExplorerFailed'), mbInformation, MB_OK, IDOK);
  if not FreshInstall and WizardSilent then Exit;
  Selection := '';
  for I := 0 to 7 do
    if PackPage.Values[I] then
    begin
      if Selection <> '' then Selection := Selection + ',';
      Selection := Selection + '"' + PackId(I) + '"';
    end;
  if Selection = '' then Exit;
  if not SaveStringToFile(ExpandConstant('{app}\setup-packs.json'), '{"packs":[' + Selection + ']}', False) then
  begin
    SuppressibleMsgBox(CustomMessage('Installer_SelectionFailed'), mbInformation, MB_OK, IDOK);
    Exit;
  end;
  WizardForm.StatusLabel.Caption := CustomMessage('Installer_Downloading');
  WizardForm.ProgressGauge.Style := npbstMarquee;
  try
    if not Exec(ExpandConstant('{app}\PowerTrove.exe'), '--install-setup-packs', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode)
      or (ResultCode <> 0) then
      SuppressibleMsgBox(CustomMessage('Installer_DownloadDeferred'), mbInformation, MB_OK, IDOK);
  finally
    WizardForm.ProgressGauge.Style := npbstNormal;
  end;
end;

// The same ownership checks as Settings preserve registrations belonging to another app copy.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{app}\PowerTrove.exe'), '--setup-explorer-menu remove ' + ActiveLanguage,
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
    MsgBox(CustomMessage('Installer_DowngradeRefused'),
      mbError, MB_OK);
    Result := False;
  end;
end;
