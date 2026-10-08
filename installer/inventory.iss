; Setup für den Inventory-Server. Dasselbe Setup installiert neu und aktualisiert eine vorhandene Installation.
; Gebaut von .github/workflows/release.yml:  iscc /DAppVersion=1.2.3 /DSourceDir=..\publish installer\inventory.iss
;
; Unbeaufsichtigt:  Inventory-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/PORT=5100] [/GROUPS="FIRMA\IT-Admins;S-1-5-32-544"]
;                   [/SERVICEACCOUNT="FIRMA\svc-inventory" /SERVICEPASSWORD=...] [/NOFIREWALL]

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define ServiceName "Inventory"
#define FirewallRule "Inventory"

[Setup]
; Die AppId bleibt in jeder Version gleich: daran erkennt das Setup eine vorhandene Installation.
AppId={{8C1F4B0E-6A4E-4B1B-9D6B-0C7E5E2A9F31}
AppName=Inventory
AppVersion={#AppVersion}
AppVerName=Inventory {#AppVersion}
AppPublisher=Inventory
DefaultDirName={autopf}\Inventory
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Inventory-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Inventory
; Der Dienst wird im Code gestoppt; der Neustart-Manager würde ihn sonst selbst beenden wollen.
CloseApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
german.SettingsCaption=Einstellungen
german.SettingsDescription=Wie soll Inventory erreichbar sein und wer darf es bedienen?
german.PortLabel=Port der Weboberfläche:
german.GroupsLabel=Erlaubte Windows-Gruppen (Name oder SID, mehrere mit Semikolon; leer = lokale Administratoren):
german.AccountCaption=Dienstkonto
german.AccountDescription=Unter welchem Konto soll der Dienst laufen?
german.AccountSystem=Lokales System (empfohlen)
german.AccountSystemHint=Zielrechner werden mit den in Inventory hinterlegten Zugangsdaten angesprochen.
german.AccountCustom=Bestimmtes Konto
german.AccountCustomHint=Braucht das Recht "Anmelden als Dienst" und für Updates lokale Administratorrechte.
german.AccountUser=Konto (DOMÄNE\benutzer):
german.AccountPassword=Passwort:
german.FirewallTask=Port in der Windows-Firewall freigeben
german.OpenBrowser=Inventory im Browser öffnen
german.ServiceFailed=Der Dienst konnte nicht gestartet werden. Details stehen im Windows-Ereignisprotokoll. Bei einem eigenen Dienstkonto fehlt meist das Recht "Anmelden als Dienst".
german.DeleteData=Sollen auch die Daten (Datenbank, Schlüssel, Paketdateien, Sicherungen) unter %1 gelöscht werden?
english.SettingsCaption=Settings
english.SettingsDescription=How should Inventory be reachable and who may use it?
english.PortLabel=Port of the web interface:
english.GroupsLabel=Allowed Windows groups (name or SID, separate several with semicolons; empty = local administrators):
english.AccountCaption=Service account
english.AccountDescription=Which account should the service run as?
english.AccountSystem=Local System (recommended)
english.AccountSystemHint=Target computers are accessed with the credentials stored in Inventory.
english.AccountCustom=Specific account
english.AccountCustomHint=Needs the "Log on as a service" right and local administrator rights for updates.
english.AccountUser=Account (DOMAIN\user):
english.AccountPassword=Password:
english.FirewallTask=Open the port in Windows Firewall
english.OpenBrowser=Open Inventory in the browser
english.ServiceFailed=The service could not be started. See the Windows event log for details. With a custom service account the "Log on as a service" right is usually missing.
english.DeleteData=Also delete the data (database, keys, package files, backups) in %1?

[Tasks]
Name: "firewall"; Description: "{cm:FirewallTask}"; Check: IsFreshInstall

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Run]
Filename: "{code:GetLocalUrl}"; Description: "{cm:OpenBrowser}"; Flags: postinstall shellexec skipifsilent nowait

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\cmd.exe"; Parameters: "/c timeout /t 5 /nobreak >nul"; Flags: runhidden; RunOnceId: "WaitService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#FirewallRule}"""; Flags: runhidden; RunOnceId: "DeleteFirewallRule"

[Code]
var
  SettingsPage: TInputQueryWizardPage;
  AccountPage: TWizardPage;
  SystemAccountRadio, CustomAccountRadio: TRadioButton;
  AccountUserEdit, AccountPasswordEdit: TEdit;
  FreshInstall: Boolean;

function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    ResultCode := -1;
  Log(Format('%s %s -> %d', [FileName, Params, ResultCode]));
  Result := ResultCode;
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function ServiceExists(): Boolean;
begin
  Result := Sc('query {#ServiceName}') = 0;
end;

function ServiceStopped(): Boolean;
begin
  // sc.exe liefert den Zustand nur als Text; find setzt den Rückgabewert.
  Result := RunHidden(ExpandConstant('{sys}\cmd.exe'), '/c sc query {#ServiceName} | find "STOPPED"') = 0;
end;

function IsFreshInstall(): Boolean;
begin
  Result := FreshInstall;
end;

function PortValue(): String;
begin
  if WizardSilent() or (SettingsPage = nil) then
    Result := ExpandConstant('{param:PORT|5100}')
  else
    Result := Trim(SettingsPage.Values[0]);
end;

function Groups(): String;
begin
  if WizardSilent() or (SettingsPage = nil) then
    Result := ExpandConstant('{param:GROUPS|}')
  else
    Result := Trim(SettingsPage.Values[1]);
end;

function ServiceAccount(): String;
begin
  if WizardSilent() or (AccountPage = nil) then
    Result := ExpandConstant('{param:SERVICEACCOUNT|}')
  else if CustomAccountRadio.Checked then
    Result := Trim(AccountUserEdit.Text)
  else
    Result := '';
end;

function ServicePassword(): String;
begin
  if WizardSilent() or (AccountPage = nil) then
    Result := ExpandConstant('{param:SERVICEPASSWORD|}')
  else
    Result := AccountPasswordEdit.Text;
end;

function WantFirewallRule(): Boolean;
begin
  if WizardSilent() then
    Result := Pos('/NOFIREWALL', Uppercase(GetCmdTail())) = 0
  else
    Result := WizardIsTaskSelected('firewall');
end;

function GetLocalUrl(Param: String): String;
begin
  Result := 'http://localhost:' + PortValue();
end;

function InitializeSetup(): Boolean;
begin
  // Bei einem Update bleiben Port, Gruppen und Dienstkonto unangetastet; die Seiten dazu entfallen.
  FreshInstall := not ServiceExists();
  Result := True;
end;

function AddHint(Page: TWizardPage; const Text: String; Top: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(Page);
  Result.Parent := Page.Surface;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Caption := Text;
  Result.Left := ScaleX(18);
  Result.Top := Top;
  Result.Width := Page.SurfaceWidth - ScaleX(18);
  Result.Height := ScaleY(30);
end;

function AddEdit(Page: TWizardPage; const LabelText: String; Top: Integer): TEdit;
var
  Caption: TNewStaticText;
begin
  Caption := TNewStaticText.Create(Page);
  Caption.Parent := Page.Surface;
  Caption.Caption := LabelText;
  Caption.Left := ScaleX(18);
  Caption.Top := Top;
  Result := TEdit.Create(Page);
  Result.Parent := Page.Surface;
  Result.Left := ScaleX(18);
  Result.Top := Top + ScaleY(18);
  Result.Width := ScaleX(280);
end;

procedure InitializeWizard();
begin
  SettingsPage := CreateInputQueryPage(wpSelectDir, CustomMessage('SettingsCaption'), CustomMessage('SettingsDescription'), '');
  SettingsPage.Add(CustomMessage('PortLabel'), False);
  SettingsPage.Add(CustomMessage('GroupsLabel'), False);
  SettingsPage.Values[0] := ExpandConstant('{param:PORT|5100}');
  SettingsPage.Values[1] := ExpandConstant('{param:GROUPS|}');

  AccountPage := CreateCustomPage(SettingsPage.ID, CustomMessage('AccountCaption'), CustomMessage('AccountDescription'));
  SystemAccountRadio := TRadioButton.Create(AccountPage);
  SystemAccountRadio.Parent := AccountPage.Surface;
  SystemAccountRadio.Caption := CustomMessage('AccountSystem');
  SystemAccountRadio.Left := 0;
  SystemAccountRadio.Top := 0;
  SystemAccountRadio.Width := AccountPage.SurfaceWidth;
  SystemAccountRadio.Checked := True;
  AddHint(AccountPage, CustomMessage('AccountSystemHint'), ScaleY(20));

  CustomAccountRadio := TRadioButton.Create(AccountPage);
  CustomAccountRadio.Parent := AccountPage.Surface;
  CustomAccountRadio.Caption := CustomMessage('AccountCustom');
  CustomAccountRadio.Left := 0;
  CustomAccountRadio.Top := ScaleY(58);
  CustomAccountRadio.Width := AccountPage.SurfaceWidth;
  AddHint(AccountPage, CustomMessage('AccountCustomHint'), ScaleY(78));

  AccountUserEdit := AddEdit(AccountPage, CustomMessage('AccountUser'), ScaleY(114));
  AccountPasswordEdit := AddEdit(AccountPage, CustomMessage('AccountPassword'), ScaleY(162));
  AccountPasswordEdit.PasswordChar := '*';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (not FreshInstall) and ((PageID = SettingsPage.ID) or (PageID = AccountPage.ID));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Number: Integer;
begin
  Result := True;
  if CurPageID = SettingsPage.ID then
  begin
    Number := StrToIntDef(PortValue(), 0);
    Result := (Number > 0) and (Number < 65536);
    if not Result then
      MsgBox(CustomMessage('PortLabel') + ' 1-65535', mbError, MB_OK);
  end
  else if (CurPageID = AccountPage.ID) and CustomAccountRadio.Checked then
  begin
    Result := Trim(AccountUserEdit.Text) <> '';
    if not Result then
      MsgBox(CustomMessage('AccountUser'), mbError, MB_OK);
  end;
end;

// Vor dem Kopieren: laufenden Dienst beenden, sonst sind die Programmdateien gesperrt.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Attempt: Integer;
begin
  Result := '';
  if not ServiceExists() then
    Exit;
  Sc('stop {#ServiceName}');
  for Attempt := 1 to 60 do
  begin
    if ServiceStopped() then
      Exit;
    Sleep(1000);
  end;
  // Beendet sich der Dienst nicht von selbst (z. B. hängendes Deployment), den Prozess beenden.
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Inventory.Server.exe');
  Sleep(2000);
end;

function JsonString(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  Result := '"' + Result + '"';
end;

function GroupsJson(): String;
var
  Rest, Item: String;
  Separator: Integer;
begin
  Result := '';
  Rest := Groups();
  while Rest <> '' do
  begin
    Separator := Pos(';', Rest);
    if Separator = 0 then
    begin
      Item := Trim(Rest);
      Rest := '';
    end
    else
    begin
      Item := Trim(Copy(Rest, 1, Separator - 1));
      Rest := Copy(Rest, Separator + 1, Length(Rest));
    end;
    if Item <> '' then
    begin
      if Result <> '' then
        Result := Result + ', ';
      Result := Result + JsonString(Item);
    end;
  end;
end;

// Die Einstellungen des Betreibers liegen unter ProgramData und werden nur bei der Erstinstallation geschrieben.
procedure WriteConfiguration();
var
  DataDir, ConfigFile, Json, Access: String;
begin
  DataDir := ExpandConstant('{commonappdata}\Inventory');
  ForceDirectories(DataDir);

  // Im Datenverzeichnis liegen verschlüsselte Zugangsdaten und Schlüssel: nur SYSTEM, Administratoren und das Dienstkonto.
  Access := '/inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F"';
  if ServiceAccount() <> '' then
    Access := Access + ' "' + ServiceAccount() + ':(OI)(CI)M"';
  RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + DataDir + '" ' + Access);

  ConfigFile := DataDir + '\appsettings.json';
  if FileExists(ConfigFile) then
    Exit;
  Json := '{' + #13#10 + '  "Urls": "http://*:' + PortValue() + '",' + #13#10 + '  "Inventory": {' + #13#10 + '    "Authentication": "Windows"';
  if GroupsJson() <> '' then
    Json := Json + ',' + #13#10 + '    "AllowedGroups": [ ' + GroupsJson() + ' ]';
  Json := Json + #13#10 + '  }' + #13#10 + '}' + #13#10;
  // UTF-8 ohne BOM, damit Umlaute in Gruppennamen (z. B. Domänen-Admins) erhalten bleiben.
  SaveStringsToUTF8FileWithoutBOM(ConfigFile, [Json], False);
end;

procedure CreateService();
var
  Params: String;
begin
  Params := 'create {#ServiceName} binPath= "\"' + ExpandConstant('{app}') + '\Inventory.Server.exe\"" start= auto DisplayName= "Inventory"';
  if ServiceAccount() <> '' then
    Params := Params + ' obj= "' + ServiceAccount() + '" password= "' + ServicePassword() + '"';
  Sc(Params);
  Sc('description {#ServiceName} "Inventarisierung und Softwareverteilung"');
  // Der schnelle erste Neustart fängt auch ein fehlgeschlagenes Update ab.
  Sc('failure {#ServiceName} reset= 86400 actions= restart/10000/restart/60000/restart/300000');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartResult: Integer;
begin
  if CurStep <> ssPostInstall then
    Exit;
  if FreshInstall then
  begin
    WriteConfiguration();
    CreateService();
    if WantFirewallRule() then
    begin
      RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="{#FirewallRule}"');
      RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="{#FirewallRule}" dir=in action=allow protocol=TCP localport=' + PortValue());
    end;
  end;
  StartResult := Sc('start {#ServiceName}');
  // 1056 = läuft bereits.
  if (StartResult <> 0) and (StartResult <> 1056) and (not WizardSilent()) then
    MsgBox(CustomMessage('ServiceFailed'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  DataDir := ExpandConstant('{commonappdata}\Inventory');
  // Unbeaufsichtigt nie löschen: die Daten sind nicht wiederherstellbar.
  if DirExists(DataDir) and (not UninstallSilent()) then
    if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataDir, True, True, True);
end;
