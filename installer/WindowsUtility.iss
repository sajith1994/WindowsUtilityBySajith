#define MyAppName "Windows Utility"
#ifndef MyAppVersion
#define MyAppVersion "2.0.85"
#endif
#define MyAppExeName "WindowsUtilityBySajith.exe"
#define MyServiceName "WindowsUtilityService"
#define MyServiceExeName "WindowsUtilityService.exe"
#define MyTrayExeName "WindowsUtilityTray.exe"
#define MyDataFolder "WindowsUtility"
; Names from before 2.0.85, kept only to stop, move and remove what older versions left behind.
#define MyLegacyAppExeName "CentricDeviceMonitor.exe"
#define MyLegacyServiceName "CentricDeviceMonitorService"
#define MyLegacyTrayExeName "CentricDeviceMonitorTray.exe"
#define MyLegacyDataFolder "CentricDeviceMonitor"

[Setup]
AppId={{BDE939BA-32D8-45E4-A080-6BD62E4D24B5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Windows Utility
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=WindowsUtilityBySajith-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=4
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\WindowsUtility\Assets\app.ico
LicenseFile=..\TERMS-AND-CONDITIONS.txt
CloseApplications=force
; Default filter is *.exe,*.dll,*.chm. Now that the payload is a shared-runtime folder rather
; than three single-file executables, that default matches every .NET runtime DLL and drags in
; unrelated holders such as the Inventory and Compatibility Appraisal service, which Setup
; cannot close. Only our own executables are ever worth closing.
CloseApplicationsFilter=WindowsUtility*.exe,CentricDeviceMonitor*.exe
RestartApplications=no
UsePreviousAppDir=yes
DisableDirPage=auto

[Dirs]
Name: "{commonappdata}\{#MyDataFolder}"; Permissions: users-modify
Name: "{commonappdata}\{#MyDataFolder}\logs"; Permissions: users-modify
Name: "{commonappdata}\{#MyDataFolder}\service"; Permissions: users-modify

[InstallDelete]
; Shortcuts from releases before the product was renamed from "Windows Utility by Sajith".
Type: files; Name: "{autoprograms}\Windows Utility by Sajith.lnk"
Type: files; Name: "{autoprograms}\Uninstall Windows Utility by Sajith.lnk"
Type: files; Name: "{autodesktop}\Windows Utility by Sajith.lnk"
; The background service, status icon and scripts under their names from before 2.0.85.
Type: files; Name: "{app}\CentricDeviceMonitor*"
Type: files; Name: "{app}\Manage-Centric*.ps1"

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Manage-WindowsUtilityService.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Manage-WindowsUtilityDashboardStartup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Install-PawnIO.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "dependencies\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{commonstartup}\Windows Utility Service Status"; Filename: "{app}\{#MyTrayExeName}"; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "standardusermaintenance"; Description: "Let standard users run maintenance tools without an administrator password (Print Spooler, printers, Windows Temp, network repair)"; GroupDescription: "People without administrator rights:"

[Registry]
; Read by the background service before it does maintenance for a standard user. HKLM, so only administrators can
; change it (also later in the dashboard). Silent updates leave the current choice alone.
Root: HKLM; Subkey: "SOFTWARE\Windows Utility"; ValueType: dword; ValueName: "StandardUserMaintenance"; ValueData: "1"; Tasks: standardusermaintenance; Check: not WizardSilent; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Windows Utility"; ValueType: dword; ValueName: "StandardUserMaintenance"; ValueData: "0"; Tasks: not standardusermaintenance; Check: not WizardSilent; Flags: uninsdeletekey

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-PawnIO.ps1"" -InstallerPath ""{tmp}\PawnIO_setup.exe"" -RebootMarkerPath ""{tmp}\WindowsUtility-PawnIO-RebootRequired.flag"""; StatusMsg: "Installing the CPU hardware sensor driver..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-WindowsUtilityService.ps1"" -Action Install -ServiceExe ""{app}\{#MyServiceExeName}"""; StatusMsg: "Installing Windows Utility background service..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-WindowsUtilityDashboardStartup.ps1"" -Action Install -ApplicationExe ""{app}\{#MyAppExeName}"""; StatusMsg: "Configuring elevated dashboard startup..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyTrayExeName}"; Description: "Start background service status icon"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-WindowsUtilityDashboardStartup.ps1"" -Action Run"; Description: "Launch {#MyAppName}"; Flags: runhidden nowait postinstall skipifsilent
; Self-update started from the dashboard without the background service (/SILENT /RELAUNCH plus a marker file):
; reopen the dashboard as the user who started Setup, not with Setup's administrator rights. Updates installed by the
; background service (/REMOTEUPDATE) are reopened by the new service in every session that had the dashboard open.
Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Check: IsRelaunchRequested; Flags: nowait runasoriginaluser

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#MyTrayExeName} /F >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-WindowsUtilityDashboardStartup.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-WindowsUtilityService.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Windows Utility remote monitoring"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRemoteMonitoringFirewallRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Windows Utility remote monitoring (VPN)"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRemoteMonitoringVpnFirewallRule"

[Code]

function IsRemoteUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/REMOTEUPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function IsRelaunchRequested(): Boolean;
var
  I: Integer;
begin
  { Never start the dashboard directly when the background service (SYSTEM) runs the update: it would open in
    the invisible SYSTEM session. The new service reopens it for the signed-in users instead. }
  Result := False;
  if IsRemoteUpdate() then
    Exit;

  { The app also writes this marker before starting a self-update, in case the switch is lost. }
  { The old folder too: a dashboard from before 2.0.85 wrote it there (it is moved before this is read, if possible). }
  Result := FileExists(ExpandConstant('{commonappdata}\{#MyDataFolder}\update-relaunch.flag'))
    or FileExists(ExpandConstant('{commonappdata}\{#MyLegacyDataFolder}\update-relaunch.flag'));
  if Result then
    Exit;

  { Checked by hand because CmdLineParamExists is not available in every Inno Setup 6 release. }
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

var
  InstallModePage: TInputOptionWizardPage;

function IsCleanInstallationSelected(): Boolean;
begin
  Result := (InstallModePage <> nil) and (InstallModePage.SelectedValueIndex = 1);
end;

procedure InitializeWizard();
begin
  InstallModePage := CreateInputOptionPage(
    wpSelectDir,
    'Installation mode',
    'Choose how Windows Utility should be installed',
    'Upgrade preserves devices, settings and logs. Clean installation removes all previous Windows Utility program files and legacy compatibility data, service data, settings, logs and per-user cache before installing a fresh copy.',
    True,
    False);

  InstallModePage.Add('Upgrade existing installation (recommended)');
  InstallModePage.Add('Clean installation (delete all previous application files and data)');
  InstallModePage.SelectedValueIndex := 0;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (InstallModePage <> nil) and
     (CurPageID = InstallModePage.ID) and
     IsCleanInstallationSelected() then
  begin
    Result := MsgBox(
      'Clean installation will permanently remove the previous Windows Utility installation folder and legacy compatibility data, saved devices, settings, connection incidents, CPU temperature history, health history, service logs, application logs and temporary/cache files.' + #13#10 + #13#10 +
      'Continue with a clean installation?',
      mbConfirmation,
      MB_YESNO) = IDYES;
  end;
end;

procedure StopService(const Name: String);
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command ""$s=Get-Service -Name ''' + Name + ''' -ErrorAction SilentlyContinue; if ($null -ne $s -and $s.Status -ne ''Stopped'') { Stop-Service -Name ''' + Name + ''' -Force; (Get-Service -Name ''' + Name + ''').WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(20)) }""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure RemoveService(const Name: String);
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command ""$s=Get-Service -Name ''' + Name + ''' -ErrorAction SilentlyContinue; if ($null -ne $s) { if ($s.Status -ne ''Stopped'') { Stop-Service -Name ''' + Name + ''' -Force -ErrorAction SilentlyContinue; $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(20)) }; sc.exe delete ' + Name + ' | Out-Null; $deadline=(Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 300; $s=Get-Service -Name ''' + Name + ''' -ErrorAction SilentlyContinue } while ($null -ne $s -and (Get-Date) -lt $deadline) }""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

{ Before 2.0.85 the shared data folder had another name. Renamed in one step when possible; otherwise what is
  missing is copied (with its permissions) and the old folder removed. The new service copies anything left. }
procedure MoveLegacyData();
var
  Legacy, Current: String;
  ResultCode: Integer;
begin
  Legacy := ExpandConstant('{commonappdata}\{#MyLegacyDataFolder}');
  Current := ExpandConstant('{commonappdata}\{#MyDataFolder}');
  if not DirExists(Legacy) then
    Exit;

  if not DirExists(Current) then
    if RenameFile(Legacy, Current) then
      Exit;

  Exec(ExpandConstant('{sys}\robocopy.exe'), '"' + Legacy + '" "' + Current + '" /E /COPY:DATS /XC /XN /XO /R:2 /W:1 /NFL /NDL /NJH /NJS /NP',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode < 8 then
    DelTree(Legacy, True, True, True);
end;

procedure DeletePreviousApplicationData();
var
  ResultCode: Integer;
begin
  { Remove the old binaries first so stale DLLs/native files cannot survive a clean install. }
  DelTree(ExpandConstant('{app}'), True, True, True);

  { Remove machine-wide service state, devices, settings and all monitoring logs. }
  DelTree(ExpandConstant('{commonappdata}\{#MyDataFolder}'), True, True, True);
  DelTree(ExpandConstant('{commonappdata}\{#MyLegacyDataFolder}'), True, True, True);

  { Remove the current user's own folder (application log, data of pre-service versions). }
  DelTree(ExpandConstant('{localappdata}\{#MyDataFolder}'), True, True, True);
  DelTree(ExpandConstant('{localappdata}\{#MyLegacyDataFolder}'), True, True, True);

  { Remove any scheduled dashboard task plus the legacy Run entry. }
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Centric Device Monitor Dashboard" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Windows Utility by Sajith Dashboard" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CentricDeviceMonitor');
end;

procedure StopRunningComponents();
var
  ResultCode: Integer;
begin
  { The tray icon holds the installed binaries open. It has no top-level window, so Inno's
    normal "close applications" request cannot reach it and Setup falls back to prompting the
    user. Terminating it here keeps the in-use page from appearing at all. }
  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM "{#MyTrayExeName}" /F >nul 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM "{#MyAppExeName}" /F >nul 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  { Installs made before the executables were renamed still have the old processes running. }
  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM "{#MyLegacyAppExeName}" /F >nul 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM "{#MyLegacyTrayExeName}" /F >nul 2>&1',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  { Runs before Setup scans for files in use. }
  if CurPageID = wpReady then
    StopRunningComponents();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { The dashboard deliberately minimizes to the tray on normal close. During an
    upgrade/clean install we must terminate it before replacing the executable. }
  StopRunningComponents();

  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command ""Get-Process -Name ''CentricDeviceMonitor'' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  StopService('{#MyServiceName}');
  StopService('{#MyLegacyServiceName}');

  if IsCleanInstallationSelected() then
  begin
    RemoveService('{#MyServiceName}');
    RemoveService('{#MyLegacyServiceName}');
    DeletePreviousApplicationData();
  end
  else
  begin
    { Upgrading from before 2.0.85: the service is installed again under its new name. }
    RemoveService('{#MyLegacyServiceName}');
    MoveLegacyData();
  end;

  Result := '';
end;

function NeedRestart(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\WindowsUtility-PawnIO-RebootRequired.flag'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Centric Device Monitor Dashboard" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Windows Utility by Sajith Dashboard" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CentricDeviceMonitor');

    if MsgBox(
      'Do you also want to remove saved devices, settings, connection incident history, CPU temperature history, system health history, and service logs?',
      mbConfirmation,
      MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{commonappdata}\{#MyDataFolder}'), True, True, True);
      DelTree(ExpandConstant('{commonappdata}\{#MyLegacyDataFolder}'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#MyDataFolder}'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\{#MyLegacyDataFolder}'), True, True, True);
    end;
  end;
end;
