# AI Development Context — Windows Utility

**Base release:** 2.0.23
**Framework:** .NET 10 / WPF / Windows  
**Primary source archive:** `WindowsUtilityBySajith-2.0.23-AI-Handoff.zip`
**Archive hash:** record the delivered ZIP's SHA-256 alongside the file; it is not embedded here because changing this file would change the archive hash.

> **Purpose of this file**  
> Give this file to another AI together with the latest full source ZIP. It is a handoff document for continuing development without reconstructing the project history from scratch.

---

## 1. Instructions for the next AI

Treat the uploaded source ZIP as the authoritative current implementation. Read this file first, then inspect these files before editing anything:

1. `README.md`
2. `CHANGELOG.md`
3. `VALIDATION.md`
4. `UI-REFACTOR-NOTES.md`
5. `Directory.Build.props`
6. `CentricDeviceMonitor.sln`
7. `src/CentricDeviceMonitor/MainWindow.xaml`
8. `src/CentricDeviceMonitor/MainWindow.xaml.cs`

Do **not** rebuild existing features from scratch unless explicitly requested. Preserve working handlers, models, services, installer behavior, data migration, safety checks and existing user data.

Every source-code revision must increment the release version. The next normal development revision after this base should therefore be **2.0.24**.

Before returning a changed source package:

- update the version consistently;
- run or request a real Windows `.NET 10` build;
- parse/check XAML;
- verify XAML event handlers resolve;
- scan for WPF/WinForms namespace collisions;
- preserve disk/network safety guards;
- update `README.md`, `CHANGELOG.md`, `VALIDATION.md` and this context file when architecture/features materially change.

---

## 2. Product overview

**Windows Utility** is a Windows technician/support utility intended for IT technicians and administrators. It is a `.NET 10` WPF desktop application supported by a LocalSystem monitoring service and a separate interactive tray companion.

The current UI is an Apple/macOS-inspired Windows interface using a navigation sidebar and four main sections:

- **System Stats**
- **Network Diagnostics**
- **Utility Tools**
- **Power & Battery**

The application is intentionally technician-oriented and contains elevated/system-level capabilities. It is not a consumer-only dashboard.

The visible branding should remain **Windows Utility**. Company name and website branding were intentionally removed from the visible application. The About text includes:

> **Made for IT technicians by an IT technician.**

---

## 3. Solution architecture

### Main application

Project:

`src/CentricDeviceMonitor/CentricDeviceMonitor.csproj`

Key configuration:

- `TargetFramework`: `net10.0-windows`
- `UseWPF`: `true`
- `UseWindowsForms`: `true`
- `Nullable`: enabled
- `LangVersion`: 14
- executable: `CentricDeviceMonitor.exe`
- administrator manifest is used

Important dependencies:

- `LibreHardwareMonitorLib 0.9.7-pre708`
- `System.Management 10.0.9`
- `Hwinfo.SharedMemory.Net 2.1.0`

Main shell:

- `MainWindow.xaml`
- `MainWindow.xaml.cs`

`MainWindow.xaml.cs` is large and acts as the primary coordinator. Do not blindly rewrite it. Prefer adding or extending dedicated services when possible.

### LocalSystem background service

Project:

`src/CentricDeviceMonitor.Service/CentricDeviceMonitor.Service.csproj`

Executable:

`CentricDeviceMonitorService.exe`

Service name:

`CentricDeviceMonitorService`

The service runs as **LocalSystem** and handles privileged/background monitoring. Important files:

- `MonitoringEngine.cs`
- `MonitoringWindowsService.cs`
- `PrivilegedCommandProcessor.cs`
- `ServiceLog.cs`

The service shares selected models/services from the WPF project through linked compile items.

### Notification-area companion

Project:

`src/CentricDeviceMonitor.Tray/CentricDeviceMonitor.Tray.csproj`

Executable:

`CentricDeviceMonitorTray.exe`

Important file:

- `ServiceStatusTrayContext.cs`

This companion exists because a Windows service runs in Session 0 and cannot reliably display UI in the signed-in user's notification area. Do **not** try to move the tray icon directly into the Windows service.

---

## 4. Current source tree — important files

### Core UI

- `src/CentricDeviceMonitor/App.xaml` — global design tokens/styles and shared resources.
- `src/CentricDeviceMonitor/App.xaml.cs` — application startup and global compatibility setup.
- `src/CentricDeviceMonitor/MainWindow.xaml` — sidebar + four main pages.
- `src/CentricDeviceMonitor/MainWindow.xaml.cs` — main orchestration, handlers and dashboard refresh behavior.

### Important dialogs

- `Dialogs/IpScannerWindow.*` — subnet scan + managed devices.
- `Dialogs/DeviceEditorWindow.*` — add/edit managed device.
- `Dialogs/DeviceLogWindow.*` — connection incidents/history.
- `Dialogs/NetworkSpeedTestWindow.*` — local-link information, packet-loss/link-quality testing, FAST.com launch and visual two-PC LAN throughput.
- `Dialogs/ThermalLoadTestWindow.*` — supervised CPU/GPU cooler-response test with a persistent action footer and live temperature safety stops.
- `Dialogs/StorageDiagnosticsWindow.*` — drive health, sequential benchmark and real-capacity verification.
- `Dialogs/DiskPreparationWindow.*` — guarded disk/partition operations.
- `Dialogs/WindowsRepairWindow.*` — online/offline Windows repair tools.
- `Dialogs/BatteryDiagnosticsWindow.*`
- `Dialogs/CpuTemperatureLogWindow.*`
- `Dialogs/SystemHealthLogWindow.*`
- `Dialogs/AboutWindow.*`
- `Dialogs/TermsWindow.*`

### Important services

- `HardwareMonitorService.cs` — CPU/GPU hardware sensors, temperature, power and related telemetry.
- `CpuLoadGenerator.cs` — cancellable per-logical-processor CPU workload with controlled duty cycles.
- `GpuRenderLoadController.cs` — dependency-free WPF 3D/Direct3D render workload; blocked when Windows reports software-only rendering.
- `SystemHardwareInventoryService.cs` — PC identity, RAM, GPU, displays, storage, Windows activation.
- `SystemHealthDetailsService.cs` — extended OS/network details and public-IP lookup.
- `NetworkDeviceService.cs` — managed-device reachability checks.
- `LocalIpScannerService.cs` — local subnet scanning.
- `TcpPortScannerService.cs` — common TCP open-port checking.
- `LanThroughputService.cs` — actual two-PC LAN throughput measurement.
- `NetworkQualityTestService.cs` — controlled ICMP packet-loss, latency and jitter measurement.
- `NetworkSpeedTestService.cs` — local network/link information. Public internet testing is intentionally delegated to FAST.com in the browser.
- `StorageDiagnosticsService.cs` — storage health, benchmarks and capacity verification.
- `DiskPreparationService.cs` — disk/partition operations and safety validation.
- `PowerPlanService.cs` — current/available Windows power plans and activation.
- `SystemPowerService.cs` — shutdown/restart scheduling/actions.
- `ExternalWindowsToolService.cs` — technician shortcuts and external Windows tools.
- `BackgroundMonitoringServiceManager.cs` — Windows service status plus elevated enable/start, disable/stop and restart control.
- `BackgroundServiceRuntimeStore.cs` — service snapshot/heartbeat access.
- `PrivilegedCommandClient.cs` — privileged request exchange with LocalSystem service.
- `ThemeManager.cs` — System/Light/Dark theme handling.
- `SafeWpfInputLanguageSource.cs` — important WPF input-language compatibility workaround.
- `SharedDataPaths.cs` — common application/service data paths.

---

## 5. UI design rules

The current UI was deliberately refactored away from the old multi-coloured dashboard-card design.

### Theme palette

Light:

- main background: `#F5F5F7`
- card: `#FFFFFF`
- text: `#1D1D1F`

Dark:

- main background: `#1E1E1E`
- card: `#2C2C2E`
- text: `#F5F5F7`

Primary accent:

- system blue: `#007AFF`

Design characteristics:

- neutral cards
- approximately 12–16px radius
- subtle theme-aware borders
- soft shadows
- rounded buttons
- segmented-control style tabs
- consistent icon/glyph treatment
- clean whitespace
- concise visible help text/tooltips

Theme choices:

- **System**
- **Light**
- **Dark**

System mode follows Windows app-theme changes.

Shared dialog styles that multiple windows need must live in **`App.xaml`**. Do not put a style only in `MainWindow.Resources` if another window references it, or a `StaticResourceExtension` runtime failure can occur.

---

## 6. Current feature inventory

### System Stats

Current functionality includes:

- CPU temperature
- CPU usage
- CPU Package Power
  - current watts
  - session average
  - session maximum
- GPU Power
  - current watts
  - session average
  - session maximum
  - actual sensor only; never estimate from utilization
- RAM live usage
- RAM module count
- RAM slot information where available
- individual RAM module size/speed/manufacturer/part number
- Print Spooler status and start/stop/restart controls
- Background Monitoring service status/startup/account/last-started
- CPU alert threshold and sound behavior
- PC name
- manufacturer/brand
- model
- system SKU
- BIOS/system serial
- Windows edition/version/build
- Windows activation state
  - activated
  - not activated
  - grace/trial where Windows reports it
- background LocalSystem service enable/start, disable/stop and restart controls
- display inventory
  - multiple displays
  - resolution
  - current refresh rate
  - physical diagonal where EDID reports dimensions
- graphics adapters
  - multiple GPUs
  - model
  - VRAM when Windows reports it
  - driver/video processor/status
- physical disks and mounted volume inventory
- capacity/used/free information

### Network Diagnostics

Current functionality includes:

- connected/disconnected state
- active adapter summary
- Wi-Fi SSID/signal when available
- local IPv4
- gateway
- DNS
- public IP
- city/region/country
- ISP/ASN/organization
- managed-device monitoring
- automatic ping monitoring
- managed devices displayed on the main Network Diagnostics page
- hostname/MAC/response time/last checked
- manual status refresh
- managed-device connection history
- inferred shutdown/restart outage events
- local subnet IP scanner
- common TCP port scan for discovered IPs
- network troubleshooting shortcuts
- network repair shortcuts
- packet-loss/link-quality testing against the gateway, another local PC, IP address or hostname

Public-IP/location data is retrieved from:

`https://ipwho.is/`

It is cached and the dashboard must remain functional if the service is unavailable.

### Network troubleshooting shortcuts

Examples include:

- `ipconfig /all`
- `netsh interface ip show config`
- `route print`
- `arp -a`
- `netstat -ano`
- `getmac /v`
- `ping`
- `tracert`
- `nslookup`
- `net use`

Network repair shortcuts include guarded Administrator actions for:

- `ipconfig /release` + `ipconfig /renew`
- `ipconfig /flushdns`
- `netsh winsock reset`
- `netsh int ip reset`
- `arp -d *`
- Full Repair sequence

Connectivity-changing actions must continue to warn users, especially in RDP/remote sessions.

### Internet and LAN speed testing

The app intentionally does **not** call an undocumented FAST.com API.

For public internet testing:

- Windows Utility opens the official `https://fast.com/` website in the default browser.
- FAST.com handles the public internet speed test.

For real local-network throughput:

- `LanThroughputService` provides a built-in PC-to-PC TCP test.
- Internet access is not required.
- PC A starts a temporary LAN test server.
- PC B connects to PC A's IP.
- default TCP port: **5201**
- user-visible durations: 5/10/20/30 seconds
- user-visible parallel streams: 1/2/4/8
- results include LAN latency, upload Mbps and download Mbps
- live current/running-average transfer progress drives graphical upload/download meters
- meter scale follows the active link and expands when measured throughput exceeds it
- data is generated in memory; no local user files are read/transferred
- optional firewall helper should be restricted to Private/Domain profiles and require confirmation

For packet loss and link quality:

- `NetworkQualityTestService` sends controlled ICMP samples without invoking a shell command;
- the default gateway detected from the active adapter is offered as the normal local-path target;
- UI presets provide 10/25/50/100 packets at 250/500/1000 ms intervals;
- results include sent/received/lost, loss percentage, average/minimum/maximum latency and mean consecutive-sample jitter;
- the UI grades the completed result Excellent/Good/Fair/Poor/No response and supports cancellation;
- packet loss is an indicator of cable/link quality, not proof of a physical cable fault. Preserve the recommendation to use a physical cable tester for certification.

Do not confuse **negotiated NIC/Wi-Fi link rate** with actual LAN throughput. Both values are useful but must be labelled differently.

### Managed-device monitoring

Managed devices are stored separately from runtime status. Runtime status includes:

- online/offline/checking/error
- ping response time
- hostname
- MAC address
- last checked

Connection incidents include outage/recovery/downtime.

When a device goes offline and later recovers, the history can show an inferred shutdown/restart event. Ping cannot prove the exact reason for an outage, so preserve wording such as **inferred from loss of reachability** rather than claiming a confirmed reboot.

### Utility Tools

Includes technician shortcuts such as:

- Devices & Printers
- Task Manager
- CMD / CMD Admin
- PowerShell / PowerShell Admin
- Disk Cleanup
- DiskPart Admin
- Disk Tools
- Disk Management
- temporary-file cleanup
- user-application termination/reporting
- Windows activation information
- Storage Health & Speed
- Storage Sense
- CPU/GPU thermal load test
  - CPU-only, GPU-render-only or combined mode
  - 50% / 75% / 100% load
  - finite 30-second / 1-minute / 2-minute / 5-minute durations
  - separate CPU/GPU stop temperatures; CPU supports and defaults to 105 °C
  - live current/peak temperature, time, progress and load state
  - fixed-footer start acknowledgement plus persistent Start/Stop actions
  - automatic stop on temperature limit, missing telemetry, duration or window closure
- external utilities supported by the existing project

### Essential Windows Repair

Online quick actions:

- `sfc /scannow`
- DISM Online CheckHealth
- DISM Online ScanHealth
- DISM Online RestoreHealth
- CHKDSK `/f /r`

Windows Repair & Recovery window:

- BCDEdit OS-drive detection
- offline DISM CheckHealth/ScanHealth/RestoreHealth
- offline SFC
- selected-drive CHKDSK
- BootRec `/fixmbr`
- BootRec `/fixboot`
- BootRec `/rebuildbcd`

Offline drive input must remain restricted to a valid drive-letter form such as `C:` or `D:` before interpolation into commands.

BootRec is normally a WinRE tool. If unavailable on the desktop, preserve the existing behavior that provides/copies the fixed command and WinRE guidance rather than pretending it executed successfully.

### Storage Health & Speed

Includes:

- physical disk health
- SMART/lifetime information where exposed
- temperatures where exposed
- sequential storage benchmark
- pass selector: 1 / 2 / 3 / 4 / 5 / Continuous
- current/average/peak/min write/read statistics where implemented
- capacity verification using generated temporary data

Capacity verification is a two-step workflow:

**Step 1 — write/fill**

- current write speed
- average write speed
- fastest write speed
- data written
- progress
- elapsed
- ETA

**Step 2 — read/verify**

- current read speed
- average read speed
- fastest read speed
- data verified
- progress
- elapsed
- ETA

Temporary test data is deleted after the test/cancellation cleanup path.

### DiskPart & Disk Management

The disk module supports:

- physical disk enumeration
- partition enumeration
- Clean disk
- Initialize GPT/MBR
- Create partition
- custom-size partitions / multiple partitions
- Format selected partition
- Delete selected partition
- Erase + prepare full disk

**Critical rule:** Windows boot/system disk and boot/system partitions must remain blocked from destructive actions.

Before a destructive operation, disk/partition state should be re-read, not trusted only from stale UI state.

Do not remove the explicit safety checks and confirmations.

### Power & Battery

Includes:

- scheduled shutdown/restart
- daily scheduling
- force-close option
- current Windows power plan
- installed power-plan selector
- activate selected plan
- native Power Options shortcut
- lid/power-button settings shortcut
- battery health/diagnostics
- an explicit scheduler master switch and enable/disable action button
- a **Paused** state when the schedule is saved as enabled but the LocalSystem service is not running

Storage diagnostics were intentionally moved out of this page and into **Utility Tools**.

---

## 7. Data locations and compatibility

Shared data root:

`%PROGRAMDATA%\CentricDeviceMonitor`

Important paths include:

- `%PROGRAMDATA%\CentricDeviceMonitor\devices.json`
- `%PROGRAMDATA%\CentricDeviceMonitor\settings.json`
- `%PROGRAMDATA%\CentricDeviceMonitor\logs\...`
- `%PROGRAMDATA%\CentricDeviceMonitor\service\status.json`
- `%PROGRAMDATA%\CentricDeviceMonitor\service\service.log`
- privileged request/response directories under the service folder

There is legacy migration logic from:

`%LOCALAPPDATA%\CentricDeviceMonitor`

### Do not casually rename internal identifiers

Although visible company branding was removed, internal names such as:

- `CentricDeviceMonitor`
- `CentricDeviceMonitorService`
- `%PROGRAMDATA%\CentricDeviceMonitor`
- installer AppId

remain intentionally for upgrade/data/service compatibility.

Changing these names without a migration plan could break:

- upgrades
- service management
- saved devices
- settings
- logs
- startup entries
- uninstall detection

If a future request explicitly requires renaming internal identifiers, create a migration plan first.

---

## 8. Versioning rule — mandatory

Single build-version source:

`Directory.Build.props`

Current:

```xml
<AppVersion>2.0.23</AppVersion>
```

Every source-code revision requested by the project owner must increment the version.

For the next revision, use:

```xml
<AppVersion>2.0.24</AppVersion>
```

Also keep release-bearing documentation/fallback values synchronized:

- `Directory.Build.props`
- `src/CentricDeviceMonitor/app.manifest` (`x.x.x.0`)
- `installer/CentricDeviceMonitor.iss` fallback `#define MyAppVersion`
- `README.md`
- `CHANGELOG.md`
- `VALIDATION.md`
- `UI-REFACTOR-NOTES.md` when relevant

The release script passes `AppVersion` into the application, service, tray companion and Inno Setup.

Do not reintroduce hard-coded inconsistent versions inside project files.

---

## 9. Build and installer

SDK selection:

`global.json` requests .NET SDK `10.0.100` with `latestFeature` roll-forward.

Main solution:

`CentricDeviceMonitor.sln`

Common commands:

```cmd
BUILD-INSTALLER.bat
```

or:

```powershell
.\build-release.ps1 -Runtime win-x64 -CreateInstaller
```

Portable build:

```cmd
BUILD-PORTABLE.bat
```

The release pipeline:

1. validates .NET 10 SDK availability;
2. restores all three projects;
3. publishes the WPF app self-contained/single-file;
4. publishes the LocalSystem service;
5. publishes the tray companion;
6. copies service/tray executables into the application distribution;
7. downloads/validates the PawnIO dependency for installer builds;
8. invokes Inno Setup 6.

Installer source:

`installer/CentricDeviceMonitor.iss`

Stable installer AppId:

`{BDE939BA-32D8-45E4-A080-6BD62E4D24B5}`

Do not change the AppId for normal upgrades.

Installer properties include:

- administrator privileges required
- Terms & Conditions licence page
- background LocalSystem service registration
- tray companion/startup configuration
- PawnIO hardware-access dependency

Silent install arguments currently used/recommended:

```text
/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS
```

Silent uninstall arguments:

```text
/VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

Interactive installation presents `TERMS-AND-CONDITIONS.txt` and requires acceptance. Silent deployment bypasses interactive wizard pages, so enterprise deployment policy should handle acceptance separately.

---

## 10. Hardware-monitoring architecture

Hardware information may come from multiple sources/fallbacks. Current dependencies include LibreHardwareMonitor, HWiNFO shared-memory support and PawnIO support.

Rules:

- Report actual CPU/GPU power sensors only.
- Do not estimate watts from CPU/GPU utilization.
- If a sensor is unavailable, display **Not available**.
- Some laptops, GPUs, displays and storage devices do not expose every sensor/EDID/SMART field. Use graceful fallback text rather than fake data.
- Session average/max values should reset naturally with a new dashboard process/session unless specifically redesigned.
- Thermal tests must use real CPU/GPU temperature telemetry for every selected load target. Do not allow a selected burner to start or continue when its required temperature sensor is unavailable.
- The built-in GPU load is a WPF/Direct3D render workload. Keep its rendering-tier check and do not present it as a vendor-specific maximum-power benchmark.

The LocalSystem service can provide privileged hardware telemetry through `BackgroundServiceSnapshot`.

---

## 11. Important safety rules

### Disk operations

Never weaken these protections:

- refuse destructive operations on the Windows boot/system disk;
- refuse destructive operations on boot/system partitions;
- re-query state immediately before destructive actions;
- require clear confirmations for erase/clean operations;
- do not silently format arbitrary disks.

### Network repair

- Release/Renew can drop RDP/VPN connectivity.
- Winsock/TCP-IP reset can require restart.
- Show warnings before disruptive actions.
- Preserve extra warning behavior for RDP/terminal sessions.

### LAN throughput firewall helper

- default TCP port 5201;
- validate accepted port range;
- firewall rule should be optional;
- restrict helper rule to Private/Domain profiles where possible;
- ask for confirmation.

### Windows repair

- validate offline drive letters strictly;
- never interpolate arbitrary user command text into elevated repair commands;
- BootRec actions require warning/confirmation;
- do not claim recovery commands succeeded if the executable is unavailable.

### Storage tests

- use generated temporary test data;
- make clear that large tests can consume time/write endurance;
- clean temporary files after completion/cancellation where possible;
- preserve safety reserve in capacity verification.

### CPU/GPU thermal load tests

- require an explicit acknowledgement and confirmation before starting;
- keep all tests finite and supervised; do not add an unattended/continuous mode;
- keep the acknowledgement and Start/Stop actions in the fixed footer so constrained window height cannot hide them;
- preserve the manual Stop action and close-window cancellation;
- preserve the close-time guards around asynchronous sensor preflight so a closed dialog can never continue into load startup;
- require a real CPU-specific temperature sensor before CPU load;
- require real GPU temperature telemetry for dedicated, hybrid or unclassified graphics; only a conservatively detected integrated-only configuration may reuse the CPU package sensor as its shared safety temperature;
- retain the all-adapters-integrated classifier guard and block unknown/mixed adapters from the shared-sensor path;
- show CPU running speed from real core-clock sensors (label the Windows current-clock fallback) and show package power only from a real power sensor, never an estimate;
- stop on temperature limit, missing telemetry, elapsed duration or sensor-read failure;
- do not run the WPF GPU workload in software-only rendering mode;
- describe the result as a practical cooler-response diagnostic, not cooling certification or guaranteed stability.

---

## 12. Privacy and Terms

Authoritative terms file:

`TERMS-AND-CONDITIONS.txt`

It is:

- shown by Inno Setup during interactive install;
- linked into the published WPF application;
- viewable from About -> Terms & Conditions.

Do not maintain a second divergent copy of the terms.

The software is not intended to collect personal information for advertising/profiling/resale. However, it does store technical diagnostic data locally, which can include:

- IP addresses
- MAC addresses
- hostnames
- device names
- timestamps
- monitoring results
- connection incidents
- system/hardware information
- logs

Therefore do **not** make the inaccurate claim that the application stores "no personal data whatsoever". The correct position is that it does not intentionally transmit this data to the developer during normal operation, except where the user intentionally uses an external network service such as `ipwho.is` or opens FAST.com.

---

## 13. Known .NET/WPF pitfalls already encountered

These are important because several previous releases failed Windows compilation or runtime checks for avoidable reasons.

### WPF + Windows Forms namespace collisions

The main WPF project intentionally has both:

```xml
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
```

Therefore names can be ambiguous.

Previous collisions included:

- `Application`
- `RadioButton`
- `ComboBox`
- `Color`
- `ColorConverter`

Use explicit WPF aliases/qualification when there is any ambiguity, for example:

```csharp
using WpfComboBox = System.Windows.Controls.ComboBox;
```

Do a source scan for new ambiguous controls whenever adding a WPF window.

### Missing `System.IO`

Previous build regressions occurred because new services used:

- `DriveInfo`
- `DriveType`
- `Stream`

without `using System.IO;`.

When adding framework types, perform a real compile rather than relying only on structural checks.

### WPF input-language LCID 4096 / 0x1000

Some Windows configurations expose transient/custom LCID/LANGID `0x1000`, which can cause legacy WPF input-language conversion failures.

The project includes:

`Services/SafeWpfInputLanguageSource.cs`

and registers it globally.

Do not reintroduce forced per-control `InputLanguageManager.SetInputLanguage(...)` workarounds. Managed-device editor fields should leave the user's normal keyboard layout untouched.

### Shared styles

A previous Windows Repair dialog failed at runtime because it referenced a style available only inside `MainWindow`.

Any style required by multiple windows must live in `App.xaml`.

### Tray/service architecture

Do not attempt to show the user's tray icon directly from the LocalSystem service. Session 0 isolation prevents this architecture from being reliable. Keep the separate interactive tray companion.

---

## 14. Managed-device editor history

The managed-device editor has already had Windows culture/DPI regressions fixed.

Preserve:

- normal editable device name text;
- normal editable IPv4 text;
- culture-safe WPF input path;
- deferred initial focus where implemented;
- larger text viewport;
- scrollable editor body;
- non-scrolling Cancel/Save action row;
- IPv4 validation;
- duplicate-IP validation/persistence behavior.

Do not replace those text boxes with masked/password-style fields.

---

## 15. Network speed implementation — current design

Earlier versions attempted to call Cloudflare speed-test endpoints directly and repeatedly hit HTTP 403 behavior in real environments.

That approach was intentionally abandoned.

Current design:

- local adapter/link information is measured locally;
- internet speed opens **FAST.com** in the browser;
- actual LAN speed is measured by the built-in two-PC TCP test.
- the LAN test reports live current/running-average progress to graphical upload/download meters;
- packet-loss quality is measured locally with controlled ICMP samples and does not depend on public internet access.

Do not reintroduce unsupported/undocumented public speed-test APIs unless the user explicitly requests a new supported provider/API.

`NetworkSpeedTestWindow.xaml.cs` currently aliases WPF `ComboBox` because the project also uses WinForms. Preserve this 2.0.19 build fix when adding selectors.

---

## 16. Last known build state

Base source: **2.0.23**.

The immediately preceding Windows build of 2.0.18 failed with:

```text
CS0104: 'ComboBox' is an ambiguous reference between
System.Windows.Controls.ComboBox
and
System.Windows.Forms.ComboBox
```

2.0.19 fixes that by explicitly binding the LAN-test combo handling to WPF `ComboBox`.

The 2.0.23 packaging environment did not include `dotnet`, MSBuild or a Windows runtime, so this release received structural/static validation but not a real Windows compiler/publish pass. The previous 2.0.22 handoff also did not contain a reported successful Windows build. Do not assume 2.0.23 is compiler-confirmed; the first step after receiving this handoff should be a real Windows build with:

```cmd
BUILD-INSTALLER.bat
```

If a build error is reported, fix the exact compiler error in the latest source rather than reverting features.

---

## 17. Validation checklist for every future release

Use this as the minimum release checklist:

- [ ] Increment version (next: 2.0.24).
- [ ] Keep `Directory.Build.props`, manifest, installer fallback and docs synchronized.
- [ ] Build all three projects with .NET 10 on Windows.
- [ ] Run `dotnet publish`/`BUILD-INSTALLER.bat` successfully.
- [ ] Confirm Inno Setup installer creation.
- [ ] Parse all WPF XAML as valid XML.
- [ ] Check every XAML `Click`/`Checked`/`SelectionChanged`/etc. handler exists.
- [ ] Check duplicate `x:Name` values.
- [ ] Scan new WPF code for WinForms naming collisions.
- [ ] Check nullable warnings introduced by new code.
- [ ] Verify `System.IO`, `System.Management`, networking and other namespaces required by new framework types.
- [ ] Preserve disk boot/system protections.
- [ ] Preserve RDP/network-repair warnings.
- [ ] Preserve LocalSystem service + tray-companion separation.
- [ ] Preserve existing `%PROGRAMDATA%\CentricDeviceMonitor` data unless migration is intentional.
- [ ] Verify upgrades preserve managed devices/settings/logs.
- [ ] Verify Add/Edit managed device works on high DPI and custom Windows locale configurations.
- [ ] Verify System/Light/Dark themes on any new window.
- [ ] Exercise CPU-only, GPU-only and combined thermal tests on Windows; verify manual stop plus temperature/telemetry/time/window-close safety stops.
- [ ] Update `CHANGELOG.md` with what changed and why.
- [ ] Update `VALIDATION.md` with what was actually checked.
- [ ] Return the complete updated source ZIP, not only snippets.

---

## 18. Recommended prompt to use with another AI

Copy/paste the following together with this file and the latest source ZIP:

> This is the latest complete source of **Windows Utility**. Read `AI_CONTEXT.md` first, then inspect `README.md`, `CHANGELOG.md`, `VALIDATION.md`, `Directory.Build.props`, `MainWindow.xaml` and the relevant service/dialog files before changing anything. Continue from the existing architecture; do not rebuild working features from scratch. Preserve all disk/network/thermal safety protections, LocalSystem service behavior, user data compatibility, Terms & Conditions, Apple-inspired System/Light/Dark UI, and internal compatibility identifiers. The project uses both WPF and Windows Forms, so explicitly qualify ambiguous control/framework types. Every code revision must increment the application version; the next revision after this base is 2.0.24. Make the requested changes, update the documentation/versioning, run as much validation as possible, and return a complete updated source ZIP plus a concise list of changes and any build limitations.

---

## 19. Files another AI should normally modify instead of creating parallel replacements

If a request affects an existing feature, edit its existing implementation:

- dashboard page/layout -> `MainWindow.xaml` / `MainWindow.xaml.cs`
- global visual style -> `App.xaml` / `ThemeManager.cs`
- managed IP scanner -> `IpScannerWindow.*`, `LocalIpScannerService.cs`, `NetworkDeviceService.cs`
- LAN speed and packet quality -> `NetworkSpeedTestWindow.*`, `LanThroughputService.cs`, `NetworkSpeedTestService.cs`, `NetworkQualityTestService.cs`
- storage diagnostics -> `StorageDiagnosticsWindow.*`, `StorageDiagnosticsService.cs`
- disks/partitions -> `DiskPreparationWindow.*`, `DiskPreparationService.cs`
- system hardware -> `SystemHardwareInventoryService.cs`, related models, MainWindow UI
- CPU/GPU sensors -> `HardwareMonitorService.cs`, service snapshot/MonitoringEngine as needed
- CPU/GPU thermal test -> `ThermalLoadTestWindow.*`, `CpuLoadGenerator.cs`, `GpuRenderLoadController.cs`, `HardwareMonitorService.cs`
- background monitoring -> service `MonitoringEngine.cs`
- tray health -> `ServiceStatusTrayContext.cs` and dashboard tray logic
- Windows repair -> `WindowsRepairWindow.*`, `ExternalWindowsToolService.cs` where appropriate
- power plans -> `PowerPlanService.cs`
- terms -> edit only root `TERMS-AND-CONDITIONS.txt`

Avoid creating duplicate service classes for features that already have a dedicated service.

---

## 20. Final principle

This project has grown through many incremental technician-focused improvements. The priority for future work is:

**preserve functionality + preserve safety + improve usability + validate on real Windows builds.**

A visually successful change that breaks a build, upgrade path, LocalSystem service, disk protection, or user data is not acceptable.
