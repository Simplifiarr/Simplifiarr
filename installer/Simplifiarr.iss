; Simplifiarr installer (Inno Setup 6).
;
; Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.2.3 /DPublishDir=..\publish installer\Simplifiarr.iss
;
; New install: asks for the web port and data folder, installs the files,
; creates the Windows service (starts with Windows, restarts if it crashes)
; and starts it.
;
; Update (the service already exists): stops it, replaces the files and
; starts it again. Its port and data folder are left exactly as they were,
; and so is the data.
;
; Simplifiarr's own Update button runs this silently:
;   Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="..." /SERVICE="name"
;
; Other options for silent installs: /PORT=6481 /DATA="D:\SimplifiarrData"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

; Windows file versions are numbers only (1.2.3), so a test build's
; 1.2.3-test.1 passes its number part here.
#ifndef FileVersion
  #define FileVersion AppVersion
#endif

#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define Exe "Simplifiarr.Web.exe"
#define DefaultPort "6481"

[Setup]
AppId={{6F1B4C2E-6A1D-4E2B-9B4F-5A3C2D1E0F17}
AppName=Simplifiarr
AppVersion={#AppVersion}
AppVerName=Simplifiarr {#AppVersion}
AppPublisher=Simplifiarr
AppPublisherURL=https://github.com/Simplifiarr/Simplifiarr
AppSupportURL=https://github.com/Simplifiarr/Simplifiarr/issues
AppUpdatesURL=https://github.com/Simplifiarr/Simplifiarr/releases
VersionInfoVersion={#FileVersion}
DefaultDirName={autopf}\Simplifiarr
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Simplifiarr-Setup-{#AppVersion}
SetupIconFile=..\assets\simplifiarr.ico
UninstallDisplayIcon={app}\{#Exe}
UninstallDisplayName=Simplifiarr
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The service is stopped and started by the code below.
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Add a desktop shortcut"; Flags: unchecked
Name: "firewall"; Description: "Let phones and other computers on my network open Simplifiarr (adds a Windows Firewall rule)"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[INI]
Filename: "{app}\Simplifiarr.url"; Section: "InternetShortcut"; Key: "URL"; String: "http://localhost:{code:GetPort}/"
Filename: "{app}\Simplifiarr.url"; Section: "InternetShortcut"; Key: "IconFile"; String: "{app}\{#Exe}"
Filename: "{app}\Simplifiarr.url"; Section: "InternetShortcut"; Key: "IconIndex"; String: "0"

[Icons]
Name: "{autoprograms}\Simplifiarr"; Filename: "{app}\Simplifiarr.url"; IconFilename: "{app}\{#Exe}"
Name: "{autodesktop}\Simplifiarr"; Filename: "{app}\Simplifiarr.url"; IconFilename: "{app}\{#Exe}"; Tasks: desktopicon

[Run]
Filename: "{app}\Simplifiarr.url"; Description: "Open Simplifiarr"; Flags: postinstall shellexec nowait skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\Simplifiarr.url"
Type: files; Name: "{app}\service-name.txt"

[Code]
var
  PortPage: TInputQueryWizardPage;
  DataPage: TInputDirWizardPage;
  ServiceExisted: Boolean;

  { Set once an existing service has been stopped, and cleared when it has
    been started again. If Setup stops early for any reason, it is started
    again on the way out, so a failed update never leaves it off. }
  StoppedService: String;

{ ------------------------------------------------------------ The service }

function ServiceName(): String;
begin
  Result := ExpandConstant('{param:SERVICE|}');
  if Result = '' then
    Result := GetPreviousData('ServiceName', 'Simplifiarr');
end;

function ServiceImagePath(Name: String; var ImagePath: String): Boolean;
begin
  Result := RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name, 'ImagePath', ImagePath);
end;

{ "C:\X\app.exe" --a=b  ->  C:\X\app.exe }
function ProgramOf(ImagePath: String): String;
var
  Rest: String;
  P: Integer;
begin
  ImagePath := Trim(ImagePath);
  if Copy(ImagePath, 1, 1) = '"' then
  begin
    Rest := Copy(ImagePath, 2, Length(ImagePath));
    P := Pos('"', Rest);
    if P > 0 then
      Result := Copy(Rest, 1, P - 1)
    else
      Result := Rest;
  end
  else
  begin
    P := Pos('.exe', Lowercase(ImagePath));
    if P > 0 then
      Result := Copy(ImagePath, 1, P + 3)
    else
      Result := ImagePath;
  end;
end;

{ The port in --Kestrel:Endpoints:Http:Url=http://0.0.0.0:6481 }
function PortOf(ImagePath: String): String;
var
  P, I: Integer;
  Rest: String;
begin
  Result := '';
  P := Pos('Http:Url=', ImagePath);
  if P = 0 then
    Exit;

  Rest := Copy(ImagePath, P + 9, Length(ImagePath));
  P := Pos(' ', Rest);
  if P > 0 then
    Rest := Copy(Rest, 1, P - 1);

  { The digits after the last ':' }
  for I := Length(Rest) downto 1 do
    if Rest[I] = ':' then
    begin
      Result := Copy(Rest, I + 1, Length(Rest));
      Break;
    end;

  if StrToIntDef(Result, 0) = 0 then
    Result := '';
end;

function RunHidden(FileName, Params: String): Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

function Sc(Params: String): Integer;
begin
  Log('sc.exe ' + Params);
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

function PowerShell(Command: String): Integer;
begin
  Result := RunHidden(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Command + '"');
end;

function IsStopped(Name: String): Boolean;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/C sc.exe query "' + Name + '" | find "STOPPED" >nul') = 0;
end;

{ Stop the service and make sure its program has really exited, so its
  files can be replaced. Only this install's copy is ended, never another
  copy of Simplifiarr installed somewhere else. }
procedure StopService(Name, ExePath: String);
var
  I: Integer;
begin
  Sc('stop "' + Name + '"');

  for I := 1 to 60 do
  begin
    if IsStopped(Name) then
      Break;
    Sleep(1000);
  end;

  PowerShell(
    'Get-Process -Name Simplifiarr.Web -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ''' + ExePath + ''' } | Stop-Process -Force; Start-Sleep -Seconds 2');
end;

procedure WaitForPort(Port: String);
begin
  PowerShell(
    '$until = (Get-Date).AddMinutes(3); ' +
    'while ((Get-Date) -lt $until) { ' +
    'try { $c = New-Object Net.Sockets.TcpClient; $c.Connect(''127.0.0.1'', ' + Port + '); $c.Close(); exit 0 } ' +
    'catch { Start-Sleep -Seconds 1 } }; exit 1');
end;

{ ------------------------------------------------------------ Choices }

function DefaultDataFolder(): String;
begin
  Result := ExpandConstant('{commonappdata}\Simplifiarr');
end;

function GetPort(Param: String): String;
var
  ImagePath: String;
begin
  if ServiceExisted and ServiceImagePath(ServiceName(), ImagePath) and (PortOf(ImagePath) <> '') then
    Result := PortOf(ImagePath)
  else if PortPage <> nil then
    Result := Trim(PortPage.Values[0])
  else
    Result := '{#DefaultPort}';
end;

function GetDataFolder(): String;
begin
  Result := RemoveBackslashUnlessRoot(Trim(DataPage.Values[0]));

  { A drive root ends in "\", which would escape the closing quote on the
    service's command line. "D:\." is the same folder. }
  if Copy(Result, Length(Result), 1) = '\' then
    Result := Result + '.';
end;

procedure InitializeWizard();
var
  ImagePath: String;
begin
  ServiceExisted := ServiceImagePath(ServiceName(), ImagePath);

  { Updating a copy that was installed some other way: install over it. }
  if ServiceExisted and (ExpandConstant('{param:DIR|}') = '') then
    WizardForm.DirEdit.Text := ExtractFileDir(ProgramOf(ImagePath));

  PortPage := CreateInputQueryPage(
    wpSelectDir,
    'Web page',
    'Which port should Simplifiarr''s web page use?',
    'You''ll open Simplifiarr at http://localhost:PORT. Keep the default unless another app already uses it.');
  PortPage.Add('Port:', False);
  PortPage.Values[0] := ExpandConstant('{param:PORT|{#DefaultPort}}');

  DataPage := CreateInputDirPage(
    PortPage.ID,
    'Data folder',
    'Where should Simplifiarr keep its settings, history and logs?',
    'This is separate from the program, so uninstalling or updating never touches it.',
    False,
    '');
  DataPage.Add('');
  DataPage.Values[0] := ExpandConstant('{param:DATA|' + DefaultDataFolder() + '}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { An update keeps the existing folder, port and data folder. }
  Result := ServiceExisted and ((PageID = wpSelectDir) or (PageID = PortPage.ID) or (PageID = DataPage.ID));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Port: Integer;
begin
  Result := True;

  if CurPageID = PortPage.ID then
  begin
    Port := StrToIntDef(Trim(PortPage.Values[0]), 0);
    if (Port < 1) or (Port > 65535) then
    begin
      MsgBox('The port has to be a number from 1 to 65535.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ServiceName', ServiceName());
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  if ServiceExisted then
    Result := 'Simplifiarr will be updated to version {#AppVersion}.' + NewLine + NewLine +
              'It stops for a minute while the files are replaced, then starts again.' + NewLine +
              'Its port, data folder and your settings stay as they are.' + NewLine + NewLine +
              MemoDirInfo
  else
    Result := MemoDirInfo + NewLine + NewLine +
              'Web page:' + NewLine + Space + 'http://localhost:' + GetPort('') + NewLine + NewLine +
              'Data folder:' + NewLine + Space + GetDataFolder() + NewLine + NewLine +
              MemoTasksInfo;
end;

{ ------------------------------------------------------------ Install }

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ImagePath: String;
begin
  Result := '';

  if ServiceImagePath(ServiceName(), ImagePath) then
  begin
    WizardForm.StatusLabel.Caption := 'Stopping Simplifiarr...';
    StoppedService := ServiceName();
    StopService(ServiceName(), ProgramOf(ImagePath));
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExePath, BinPath, Port, Data, RuleName: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  ExePath := ExpandConstant('{app}\{#Exe}');
  Port := GetPort('');

  if not ServiceExisted then
  begin
    WizardForm.StatusLabel.Caption := 'Creating the Simplifiarr service...';

    { The port and data folder go on the service's own command line, so
      they belong to this install and survive every update. }
    BinPath := '\"' + ExePath + '\" --Kestrel:Endpoints:Http:Url=http://0.0.0.0:' + Port;

    Data := GetDataFolder();
    if (Data <> '') and (CompareText(Data, DefaultDataFolder()) <> 0) then
      BinPath := BinPath + ' --Simplifiarr:DataPath=\"' + Data + '\"';

    Sc('create "' + ServiceName() + '" binPath= "' + BinPath + '" start= auto DisplayName= "Simplifiarr"');
    Sc('description "' + ServiceName() + '" "Simplifiarr media library tools (http://localhost:' + Port + ')"');
  end;

  { If it ever crashes, restart it after a minute; forget past failures after a day. }
  Sc('failure "' + ServiceName() + '" reset= 86400 actions= restart/60000/restart/60000/restart/60000');

  RuleName := 'Simplifiarr (' + ServiceName() + ')';
  if WizardIsTaskSelected('firewall') then
  begin
    RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="' + RuleName + '"');
    RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall add rule name="' + RuleName + '" dir=in action=allow protocol=TCP localport=' + Port);
  end;

  SaveStringToFile(ExpandConstant('{app}\service-name.txt'), ServiceName(), False);

  WizardForm.StatusLabel.Caption := 'Starting Simplifiarr...';
  Sc('start "' + ServiceName() + '"');
  StoppedService := '';
  WaitForPort(Port);
end;

procedure DeinitializeSetup();
begin
  if StoppedService <> '' then
  begin
    Log('Setup ended before finishing; starting ' + StoppedService + ' again.');
    Sc('start "' + StoppedService + '"');
  end;
end;

{ ------------------------------------------------------------ Uninstall }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Name: AnsiString;
begin
  if CurUninstallStep <> usUninstall then
    Exit;

  if not LoadStringFromFile(ExpandConstant('{app}\service-name.txt'), Name) then
    Name := 'Simplifiarr';
  Name := Trim(Name);

  StopService(Name, ExpandConstant('{app}\{#Exe}'));
  Sc('delete "' + Name + '"');
  RunHidden(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="Simplifiarr (' + Name + ')"');

  if not UninstallSilent() then
    MsgBox('Simplifiarr has been removed. Your settings, history and logs are still in its data folder ' +
           '(normally ' + ExpandConstant('{commonappdata}\Simplifiarr') + '); delete that folder too if you don''t need them.',
           mbInformation, MB_OK);
end;
