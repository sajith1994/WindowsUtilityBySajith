#define MyAppName "Windows Utility by Sajith"
#ifndef MyAppVersion
#define MyAppVersion "2.0.48"
#endif
#define MyAppExeName "WindowsUtilityBySajith.exe"
#define MyLegacyAppExeName "CentricDeviceMonitor.exe"
#define MyServiceExeName "CentricDeviceMonitorService.exe"
#define MyTrayExeName "CentricDeviceMonitorTray.exe"

[Setup]
AppId={{BDE939BA-32D8-45E4-A080-6BD62E4D24B5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\Windows Utility by Sajith
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
SetupIconFile=..\src\CentricDeviceMonitor\Assets\app.ico
LicenseFile=..\TERMS-AND-CONDITIONS.txt
CloseApplications=force
; Default filter is *.exe,*.dll,*.chm. Now that the payload is a shared-runtime folder rather
; than three single-file executables, that default matches every .NET runtime DLL and drags in
; unrelated holders such as the Inventory and Compatibility Appraisal service, which Setup
; cannot close. Only our own executables are ever worth closing.
CloseApplicationsFilter=CentricDeviceMonitor*.exe
RestartApplications=no
UsePreviousAppDir=yes
DisableDirPage=auto

[Dirs]
Name: "{commonappdata}\CentricDeviceMonitor"; Permissions: users-modify
Name: "{commonappdata}\CentricDeviceMonitor\logs"; Permissions: users-modify
Name: "{commonappdata}\CentricDeviceMonitor\service"; Permissions: users-modify

[Files]
Source: "..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Manage-CentricService.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Manage-CentricDashboardStartup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Install-PawnIO.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "dependencies\PawnIO_setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autoprograms}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{commonstartup}\Windows Utility Service Status"; Filename: "{app}\{#MyTrayExeName}"; WorkingDir: "{app}"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{tmp}\Install-PawnIO.ps1"" -InstallerPath ""{tmp}\PawnIO_setup.exe"" -RebootMarkerPath ""{tmp}\CentricDeviceMonitor-PawnIO-RebootRequired.flag"""; StatusMsg: "Installing the CPU hardware sensor driver..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricService.ps1"" -Action Install -ServiceExe ""{app}\{#MyServiceExeName}"""; StatusMsg: "Installing Windows Utility background service..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricDashboardStartup.ps1"" -Action Install -ApplicationExe ""{app}\{#MyAppExeName}"""; StatusMsg: "Configuring elevated dashboard startup..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyTrayExeName}"; Description: "Start background service status icon"; Flags: nowait postinstall skipifsilent
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricDashboardStartup.ps1"" -Action Run"; Description: "Launch {#MyAppName}"; Flags: runhidden nowait postinstall skipifsilent
; Self-update: the app starts Setup with /SILENT /RELAUNCH, so reopen the dashboard when the update finishes.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricDashboardStartup.ps1"" -Action Run"; Check: IsRelaunchRequested; Flags: runhidden nowait

[UninstallRun]
Filename: "{cmd}"; Parameters: "/c taskkill /IM {#MyTrayExeName} /F >nul 2>&1"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricDashboardStartup.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Manage-CentricService.ps1"" -Action Uninstall"; Flags: runhidden waituntilterminated

[Code]

function IsRelaunchRequested(): Boolean;
var
  I: Integer;
begin
  { Checked by hand because CmdLineParamExists is not available in every Inno Setup 6 release. }
  Result := False;
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
    'Choose how Windows Utility by Sajith should be installed',
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

procedure RemovePreviousServiceForCleanInstall();
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command ""$s=Get-Service -Name ''CentricDeviceMonitorService'' -ErrorAction SilentlyContinue; if ($null -ne $s) { if ($s.Status -ne ''Stopped'') { Stop-Service -Name ''CentricDeviceMonitorService'' -Force -ErrorAction SilentlyContinue; $s.WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(20)) }; sc.exe delete CentricDeviceMonitorService | Out-Null; $deadline=(Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 300; $s=Get-Service -Name ''CentricDeviceMonitorService'' -ErrorAction SilentlyContinue } while ($null -ne $s -and (Get-Date) -lt $deadline) }""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure DeletePreviousApplicationData();
var
  ResultCode: Integer;
begin
  { Remove the old binaries first so stale DLLs/native files cannot survive a clean install. }
  DelTree(ExpandConstant('{app}'), True, True, True);

  { Remove machine-wide service state, devices, settings and all monitoring logs. }
  DelTree(ExpandConstant('{commonappdata}\CentricDeviceMonitor'), True, True, True);

  { Remove data/cache left by older pre-service versions for the current user. }
  DelTree(ExpandConstant('{localappdata}\CentricDeviceMonitor'), True, True, True);

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

  { Installs made before the executable was renamed still have the old process running. }
  Exec(
    ExpandConstant('{cmd}'),
    '/c taskkill /IM "{#MyLegacyAppExeName}" /F >nul 2>&1',
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

  Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command ""$s=Get-Service -Name ''CentricDeviceMonitorService'' -ErrorAction SilentlyContinue; if ($null -ne $s -and $s.Status -ne ''Stopped'') { Stop-Service -Name ''CentricDeviceMonitorService'' -Force; (Get-Service -Name ''CentricDeviceMonitorService'').WaitForStatus(''Stopped'', [TimeSpan]::FromSeconds(20)) }""',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  if IsCleanInstallationSelected() then
  begin
    RemovePreviousServiceForCleanInstall();
    DeletePreviousApplicationData();
  end;

  Result := '';
end;

function NeedRestart(): Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\CentricDeviceMonitor-PawnIO-RebootRequired.flag'));
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
      DelTree(ExpandConstant('{commonappdata}\CentricDeviceMonitor'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\CentricDeviceMonitor'), True, True, True);
    end;
  end;
end;
