# Windows Utility by Sajith 2.0.23

## 2.0.23 - Live CPU telemetry and integrated-GPU thermal safety

- Added live CPU running speed to the thermal-load window using real LibreHardwareMonitor core-clock sensors, with `Win32_Processor.CurrentClockSpeed` as a clearly identified fallback.
- Added live CPU package power to the thermal-load window. Wattage continues to come only from an exposed hardware power sensor and is never estimated from utilization.
- Expanded the thermal metric row to show CPU temperature, CPU speed, CPU package power, GPU/shared temperature, elapsed time and active load together.
- Enabled GPU and combined tests on conservatively detected integrated-only graphics when no separate GPU temperature sensor exists. The CPU package temperature is used as the shared thermal safety sensor because the integrated GPU shares that package.
- Kept dedicated, hybrid, unknown-adapter and software-rendering safeguards intact: those configurations still require real GPU temperature telemetry and hardware WPF rendering before a GPU load can start.
- Added explicit shared-sensor status, confirmation text, peak tracking and automatic stop behavior against the selected GPU temperature limit.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.23**.

## 2.0.22 - Visible thermal-test controls and 105 °C CPU limit

- Moved the thermal-test acknowledgement, **Start load test**, **Stop now** and **Close** controls into a fixed footer so the Start action is no longer clipped on height-constrained displays.
- Compacted the Test settings card and placed the CPU/GPU stop-temperature controls side by side so both remain visible in the available settings area.
- Increased the CPU stop slider from a 95 °C maximum to a 105 °C maximum and made 105 °C the default, as requested.
- Added an explicit warning that 105 °C may be at or above a processor's thermal-throttling point; acknowledgement, confirmation, real-sensor preflight and every existing automatic/manual stop path remain enforced.
- Preserved CPU-only, GPU-only and combined load modes, finite duration/load presets, close-time guards, telemetry-loss stops and all unrelated utility behavior.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.22**.

## 2.0.21 - Guarded CPU and GPU thermal load test

- Added a dedicated **CPU & GPU Thermal Load Test** window in Utility Tools with CPU-only, GPU-render-only and combined modes.
- Added selectable 50%/75%/100% load levels and finite 30-second/1-minute/2-minute/5-minute durations. CPU load uses cancellable long-running workers across the logical processors with a duty cycle below 100%.
- Added a dependency-free animated WPF 3D workload that uses Direct3D hardware rendering when Windows exposes rendering tier 1 or 2. Software-only rendering is blocked so the GPU option does not silently become another CPU burner.
- Added real GPU-temperature acquisition through LibreHardwareMonitor with Libre/OpenHardwareMonitor WMI fallback. Sensor values are never estimated.
- Added live CPU/GPU temperatures, peak values, sensor/source details, elapsed and remaining time, progress, worker/render-tier status and a visible GPU render preview.
- Added guarded start acknowledgement plus automatic stop on the selected CPU/GPU temperature limit, missing required telemetry, elapsed duration or window closure. Manual Stop remains available throughout.
- Updated About, README and Terms & Conditions to describe the supervised thermal test and its limitations. The GPU workload is explicitly presented as a practical cooler-response check, not a vendor-specific maximum-power benchmark or cooling certification.
- Preserved the existing packet-quality, LAN throughput, background-service, power-scheduler, disk, network and data-safety behavior.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.21**.

## 2.0.20 - Packet quality, live LAN meters and background controls

- Added **Packet loss & link quality** to Network Speed & Bandwidth. It can target the detected gateway, another local PC, an IP address or a hostname and reports received/sent packets, loss percentage, latency range/average, jitter and a clear Excellent/Good/Fair/Poor grade.
- Added selectable 10/25/50/100-packet runs, 250/500/1000 ms intervals, live progress and cancellation. Guidance explains that local packet loss can indicate cable, connector, NIC, switch-port, Wi-Fi or congestion problems but does not replace a physical cable tester.
- Added live graphical upload and download meters to the native two-PC LAN throughput test. Multi-stream byte counters now report current and running-average Mbps throughout each direction; the visual scale follows the active adapter and expands for faster results.
- Added **Enable & start**, **Disable & stop** and **Restart** controls for the LocalSystem background service. Enabling restores Automatic (Delayed Start); disabling requires confirmation, preserves settings and pauses unattended monitoring/sensor alerts/power schedules.
- Made the system power scheduler master control explicit with dynamic enabled/disabled labels and a matching action button. Enabled schedules now display **Paused** instead of a misleading countdown while the background service is stopped.
- Preserved FAST.com browser testing, generated-memory-only LAN traffic, Private/Domain-only firewall helper behavior, service/tray separation and existing data paths.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.20**.

## 2.0.19 - LAN speed-test WPF build fix

- Fixed the `CS0104` publish failure in `NetworkSpeedTestWindow.xaml.cs` caused by `ComboBox` being ambiguous between WPF and Windows Forms.
- The LAN duration/parallel-stream selector helper now explicitly uses `System.Windows.Controls.ComboBox` through the `WpfComboBox` alias.
- Reviewed the new Network Speed & Bandwidth code-behind for other WPF/Windows Forms control-name collisions.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.19**.

## 2.0.18 - FAST.com internet testing and real two-PC LAN throughput

- Replaced the unreliable undocumented Cloudflare HTTP speed-test calls with a direct **Open FAST.com** action that launches the official FAST.com/Netflix test in the default browser. This avoids the repeated HTTP 403 failures seen with third-party endpoint emulation.
- FAST.com remains clearly identified as the public internet/ISP test. Download starts on the website and **Show more info** provides upload speed plus unloaded/loaded latency.
- Added a native **Actual LAN throughput - PC to PC** test that works with no internet connection. One Windows Utility instance starts a temporary TCP server and a second PC runs upload then download measurements against it.
- LAN tests use generated in-memory data only; no local files are read or transferred.
- Added configurable test duration (5/10/20/30 seconds) and parallel TCP streams (1/2/4/8) so faster Gigabit/Wi-Fi links can be exercised more effectively.
- Added measured LAN latency, upload Mbps and download Mbps results plus progress/status guidance.
- Added an optional Windows Firewall helper that allows the selected test TCP port on **Private/Domain** profiles after explicit confirmation. Default test port is 5201.
- Updated Network Diagnostics guidance to distinguish **negotiated link rate**, **FAST.com public internet speed**, and **actual end-to-end LAN throughput**.
- Updated Terms & Conditions and third-party notices for FAST.com and local generated LAN test traffic.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.18**.

## 2.0.17 - Internet speed-test 403 compatibility fix

- Fixed the Cloudflare upload-speed phase returning HTTP `403 (Forbidden)` on some connections.
- Updated upload requests to include the current `bytes=<payload-size>` query parameter used by Cloudflare's speed-test client.
- Changed generated upload content from NUL-filled binary data to printable synthetic `0` bytes and sends a browser-compatible `text/plain; charset=UTF-8` content type. No local/user files are used.
- Forces the upload measurement requests to HTTP/1.1 and disables `Expect: 100-continue` to avoid compatibility issues with edge proxies and web filters.
- Added one guarded fallback attempt using the minimal `POST /__up?bytes=<n>` request shape when the first upload receives 403/415.
- Latency and download measurements now appear immediately as those phases finish, so valid partial results remain visible if a later public upload endpoint is blocked.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.17**.

## 2.0.16 - Network speed publish fix

- Fixed the Windows publish failure in `NetworkSpeedTestService.cs` by importing `System.IO`, resolving both `CS0246` errors for the `Stream` type used by the internet download/upload test.
- Preserved all 2.0.15 Network Speed & Bandwidth, Network Diagnostics, Utility Tools, repair/recovery, monitoring, hardware inventory, and installer functionality.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.16**.


## 2.0.15 - Network speed/bandwidth testing, shortcut guidance and repair-window fix

- Added **Speed & bandwidth test** to the Current Connection card on Network Diagnostics.
- Added a dedicated **Network Speed & Bandwidth** window that reports the active adapter, local IPv4/gateway, negotiated Ethernet/Wi-Fi link rate, Wi-Fi receive/transmit link rates, signal/radio/channel details, local gateway latency and live adapter traffic. Local link information works without internet access.
- Added an optional user-started internet test using Cloudflare public speed-test endpoints for HTTP latency plus multi-stream download/upload throughput. The UI clearly distinguishes internet throughput from negotiated LAN/Wi-Fi link capacity and discloses the synthetic test traffic.
- Added visible one-line **use cases** under each Network Troubleshooting, Network Repair, Windows Shortcut and Essential Windows Repair action, plus contextual tooltips on the remaining Utility Tools actions.
- Promoted shared card/text/icon styles to application scope, fixing the **Windows Repair & Recovery** window runtime `StaticResourceExtension` error seen when its MainWindow-only styles were unavailable.
- Updated Terms & Conditions and third-party notices to disclose the optional Cloudflare speed-test request and that the service necessarily sees the public IP/network metadata.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.15**.


## 2.0.14 - Essential Windows repair and recovery shortcuts

- Added an **Essential Windows Repair** card to Utility Tools with one-click Administrator shortcuts for `sfc /scannow`, DISM Online CheckHealth, ScanHealth, RestoreHealth, and CHKDSK `/f /r` on the current system drive.
- Added a dedicated **Windows Repair & Recovery** window with clearly separated Online Windows repair, Disk Check, Offline Windows image repair, and Boot Records (WinRE) sections.
- Added Windows-drive detection using BCDEdit `osdevice`, plus strict drive-letter validation before building offline DISM/SFC commands.
- Added offline DISM CheckHealth, ScanHealth, RestoreHealth, offline SFC, and an optional guarded DISM + SFC sequence against a technician-selected offline Windows volume.
- Added BootRec `/fixmbr`, `/fixboot`, `/rebuildbcd`, and combined sequence shortcuts. BootRec commands require explicit confirmation when available and are copied to the clipboard with WinRE guidance when `bootrec.exe` is unavailable in the current desktop session.
- Added warnings before CHKDSK `/f /r`, BootRec changes, and full offline-repair sequences. Offline repair warns when the selected volume does not contain a detectable `Windows` folder.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.14**.


## 2.0.13 - System hardware inventory build fix

- Fixed the 2.0.12 publish failure by importing `System.IO` in `SystemHardwareInventoryService`, resolving `DriveInfo` and `DriveType`.
- Removed the nullable warning in the multi-pass storage benchmark confirmation by using a non-null pass-count fallback.
- Removed the nullable warning in Windows activation grace-period formatting by using `GetValueOrDefault()`.
- Preserved all 2.0.12 hardware inventory, GPU power, RAM, storage, display and activation features.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.13**.

## 2.0.12 - Expanded System Stats hardware inventory and GPU power

- Expanded **System Stats** with PC name, manufacturer/brand, model, system SKU and BIOS serial number.
- Added connected-display inventory with native resolution, current refresh rate and physical diagonal size when the monitor firmware reports its dimensions.
- Added multi-GPU inventory with adapter name, reported VRAM, driver version and video processor details.
- Added live **GPU Power** using real LibreHardwareMonitor power sensors, including current, average-this-session and maximum-this-session wattage.
- Extended the LocalSystem monitoring snapshot with GPU power so privileged sensor access can be reused by the dashboard.
- Added physical-disk inventory and mounted-volume used/capacity information, including multiple installed disks/volumes.
- Expanded memory reporting with installed RAM module count, reported slot count, module capacity, speed, manufacturer and part number.
- Added Windows activation state directly to System Stats and improved grace/trial reporting with remaining time when Windows exposes `GracePeriodRemaining`.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.12**.


## 2.0.11 - Installer terms, privacy disclosure and in-app terms viewer

- Added the approved **End User Licence Agreement, Terms of Use and Disclaimer** as `TERMS-AND-CONDITIONS.txt`.
- Interactive Inno Setup installations now show the Terms & Conditions on the standard licence page and require acceptance before installation can continue.
- Added **Terms & Conditions** to the About window so installed users can review the same terms at any time.
- The terms disclose administrative/destructive capabilities, backup responsibility, storage-test load, network scanning/monitoring, local diagnostic data handling, third-party components, malware/security context, warranty limitations and liability limitations subject to applicable law.
- Clarified that the software is not designed to collect personal information for advertising/profiling/resale and does not intentionally transmit monitored-device or diagnostic information to the developer during normal operation, while accurately noting that local technical data such as IP/MAC/hostnames may be personal data in some contexts.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.11**.

## 2.0.10 - Managed-device editor visibility and editing

- Fixed the managed-device Add/Edit dialog fields appearing as clipped dots/dashes instead of readable device-name and IPv4 text.
- Increased the editor TextBox viewport and reduced internal vertical padding so the full existing values remain visible and editable at common Windows DPI/text-scaling levels.
- Reworked the editor into a scrollable content area with a dedicated non-scrolling action row so **Cancel** and **Save changes** remain visible even when available vertical space is limited.
- Increased the default editor size and enabled resizing as an additional accessibility fallback.
- Preserved the 2.0.9 culture-safe input-language handling, IPv4 validation and existing device persistence behavior.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.10**.

## 2.0.9 - Managed-device editor culture compatibility

- Fixed Add/Edit managed-device dialogs crashing with `CultureNotFoundException` on Windows language/region configurations that expose the custom/transient locale identifier `4096` (`0x1000`).
- Registered a culture-safe WPF input-language source at application startup so WPF never reconstructs a custom Windows locale through the obsolete numeric LCID path.
- Removed the per-control forced input-language override from the dashboard managed-device editor and kept both editors on the user's normal Windows keyboard layout.
- Deferred initial TextBox focus until each editor is rendered, preventing the input-language transition from occurring in the middle of the button event that opens the editor.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.9**.

## 2.0.8 - Multi-pass storage benchmark and CPU package power

- Replaced the storage speed-test single/continuous toggle with a pass selector supporting **1, 2, 3, 4, 5 or Continuous** passes.
- Finite benchmark runs now show **Pass X of Y**, while Continuous mode keeps incrementing until Stop / Cancel is pressed.
- Preserved per-pass write/read results and expanded the running summary with average, peak and minimum write/read throughput across completed passes.
- Added a **CPU Package Power** System Stats card showing the current real sensor reading in watts, the current-session average, and the maximum wattage recorded during the current dashboard session.
- Added CPU package-power acquisition through LibreHardwareMonitor with Libre/OpenHardwareMonitor WMI fallback. Wattage is reported only when a real CPU package/total power sensor is exposed; it is never inferred from CPU percentage.
- The LocalSystem monitoring service now publishes package-power readings and sample timestamps in its runtime snapshot so the dashboard can use the privileged background sensor path when available.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to **2.0.8**.


## 2.0.7 - Device lifecycle visibility, service tray health and network repair

- Added shutdown/restart reachability events to each managed device history. When a device recovers from an outage, the event records the time it became unreachable and clearly marks the shutdown/restart classification as inferred because ping monitoring alone cannot distinguish a power event from a network outage.
- Expanded the device-history window with Event, Event at, Event details and Ping result columns while preserving downtime and recovery information.
- Added a lightweight `CentricDeviceMonitorTray.exe` notification-area companion that starts at user logon, checks the LocalSystem monitoring service, reads the service heartbeat, and shows Running/Starting/Stopped plus monitoring state and managed-device count.
- The service-status tray icon uses a green/orange/red status dot, shows a status balloon on double-click, and exposes current service/monitoring/heartbeat information on right-click.
- The lightweight tray companion hides while the full dashboard is running to avoid duplicate icons; the dashboard tray menu now exposes the same live service/monitoring/heartbeat information.
- Added Administrator network-repair shortcuts for Release + Renew IP, Flush DNS, Reset Winsock + TCP/IP, Clear ARP Cache, and a guarded Full Repair sequence.
- Repair shortcuts warn before changing the network stack and explicitly warn when an RDP/terminal session is detected. Winsock/TCP-IP reset actions remind the technician that a Windows restart is required.
- Updated the release build and installer to publish/install the tray companion and start it automatically in the interactive user session.
- Bumped the synchronized application, service, tray companion, manifest and installer release version to 2.0.7.


## 2.0.6 - Storage verification metrics visibility

- Fixed the Capacity Verification layout that could clip Step 1/Step 2 speed and timing metrics below the visible tab area.
- Rebalanced the main Storage Health & Speed rows so the detailed test area receives more vertical space.
- Added vertical scrolling inside Capacity Verification for smaller/resized windows, while keeping all write/read metrics available.
- Bumped the synchronized application, service, manifest and installer release version to 2.0.6.

## 2.0.5 - Two-step storage verification, port scanning and network shortcuts

- Split real-capacity verification into two independently reported phases: Step 1 copies/fills test data and Step 2 reads/verifies it.
- Added per-step current speed, average speed, fastest speed, elapsed time, estimated time remaining and progress.
- Added common TCP open-port checks for devices discovered by the Local IP Scanner.
- Added Network Diagnostics shortcuts for ipconfig, netsh interface config, route, arp, netstat, getmac, ping, tracert, nslookup and net use.
- Target-based command shortcuts validate hostnames/IP addresses before launching CMD.
- Bumped the synchronized application, service, About and installer release version to 2.0.5.

## 2.0.4 - Capacity verification live speed metrics

- Added live average throughput to Storage Health & Speed capacity verification.
- Added fastest recorded throughput for the current verification run.
- Changed the ETA to estimate time remaining for the entire write + verify test instead of only the current phase.
- The new metrics reset cleanly at the start of every test and continue updating once per second.
- Bumped the synchronized application, service, About and installer release version to 2.0.4.

## 2.0.3 - Managed devices, storage tools and versioning

- Managed devices now perform an immediate live reachability check when **Network Diagnostics** is opened, rather than waiting for the next LocalSystem service ping interval.
- Closing the Network Scanner now preserves any fresher runtime status and immediately rechecks the managed list.
- The Network Scanner automatically checks saved managed devices on open so entries no longer remain at **Not checked** until Refresh is pressed.
- Moved **Storage Health & Speed** from **Power & Battery** to **Utility Tools**.
- Added a **Storage Sense** button that opens the native Windows Storage Sense settings page.
- Centralized the release version in `Directory.Build.props`; application, service and build output now share the same version source.
- Updated the installer build to receive the same centralized release version, and removed hard-coded 2.0.2 user-agent strings.

## Apple-inspired UI refactor

- Replaced the multi-colored dashboard tile grid with a macOS-inspired navigation sidebar and focused main-content pages.
- Added **System Stats**, **Network Diagnostics**, **Utility Tools**, and **Power & Battery** navigation groups while preserving the existing backend handlers and services.
- Added a centralized System/Light/Dark theme engine using the requested light (`#F5F5F7`, `#FFFFFF`, `#1D1D1F`) and dark (`#1E1E1E`, `#2C2C2E`, `#F5F5F7`) palettes.
- Standardized primary actions, selected states and switches on system blue (`#007AFF`).
- Added 14px neutral cards, subtle 6%/8% theme-aware borders, soft shadows, 8px controls, improved whitespace and simplified supporting copy.
- Restyled diagnostic-window tabs as Apple-style segmented controls and applied shared theme resources to cards, grids and dialogs.
- Added persistent UI theme preference with **System** mode following Windows app-theme changes.
- Fixed WPF/Windows Forms namespace collisions in the Apple UI refactor by explicitly qualifying WPF `RadioButton`, `Application`, `Color`, and `ColorConverter` types.

## Network and power workflow improvements

- Moved the managed IP-address status table directly into the lower area of **Network Diagnostics** so online/offline state, response time, hostname and last-check time are visible as soon as the page is opened.
- Added **Check all now** and **Manage addresses** actions above the live managed-device table.
- Moved **Storage Health & Speed** into the left column of **Power & Battery**.
- Added current Windows power-plan detection with a selector to activate any installed plan.
- Added direct shortcuts to Windows **Power Options** and **Lid & power button options**.
- Removed company and website branding from About, installer metadata and application/service file metadata.
- Added the About statement: **Made for IT technicians by an IT technician.**

## DiskPart and partition management

- Reworked disk preparation into independent state-aware operations instead of coupling initialization, partition creation and formatting.
- Fixed RAW-disk detection in the Windows Storage (`MSFT_Disk`) mapping so genuinely uninitialized disks correctly enable the Initialize action.
- **Clean disk** erases the complete partition table on a non-system disk and returns it to RAW.
- **Initialize disk** now performs only GPT/MBR initialization and is enabled only for RAW disks, matching Windows Storage behavior.
- Added **Erase + prepare full disk** for already-formatted SSDs: after two confirmations it cleans the selected disk, initializes it as GPT/MBR, creates one maximum-size partition and quick-formats it as NTFS/exFAT.
- Added **Create partition** with either maximum free space or a user-entered GB size. Repeating the action allows multiple partitions to be created from the application.
- Added **Format selected** to quick-format an existing non-system partition without deleting the rest of the disk.
- Retained **Delete selected** to remove only the chosen non-system partition.
- Added allocated/free-space and partition-count columns to the physical-disk list, with disk state and unallocated space shown in the action summary.
- Disk and partition state is re-read immediately before destructive operations; boot/system disks and boot/system partitions remain blocked.

## Expanded system health

- Added Windows edition/display version and complete build number.
- Added laptop battery percentage and power/charging state when a battery is present.
- Added Wi-Fi signal strength percentage and SSID where Windows reports them.
- Added preferred local IPv4 address.
- Added public IP address, IP-based city/region/country and ISP/ASN/organization information.
- Public IP/location data is retrieved over HTTPS from `ipwho.is`, cached for five minutes, and fails gracefully when the PC is offline.
- Added a manual **Refresh** button for the extended system-health details while keeping automatic refresh active.

## Existing technician tools retained

- Chris Titus Tech WinUtil, Raphire Win11Debloat and Winhance official-command refresh/launch tiles.
- Normal/Admin CMD and PowerShell launchers and command-entry runners.
- Disk Cleanup, DiskPart and Disk Management shortcuts.
- Network/IP scanning, managed-device monitoring, hardware health, storage/battery diagnostics, cleanup, power scheduling and LocalSystem background monitoring.

## Version

- Application, background service, tray companion, manifest and installer version: 2.0.8.

## [2.0.51] - 2026-10-09

### Changed
- **Renamed to "Windows Utility"** (was "Windows Utility by Sajith") everywhere the name is shown: window titles,
  sidebar and About header, messages, installer, Start menu and desktop shortcuts, Apps & features entry, the
  background service display name, release titles, Terms & Conditions, README and notices. The developer credit in
  About is unchanged.
- The installer removes the old "Windows Utility by Sajith" Start menu and desktop shortcuts. Existing installs
  stay in their current folder; new installs default to `Program Files\Windows Utility`.
- Unchanged on purpose, because installed copies and updates depend on them: the `WindowsUtilityBySajith.exe`
  file name, the GitHub repository and update address, the installer file name and the
  "Windows Utility by Sajith Dashboard" startup task name.

## [2.0.50] - 2026-10-09

### Added
- **Automatic updates** (on by default; "Install updates automatically" in the update window). When a check finds
  a new release, the dashboard banner counts down 60 seconds, then downloads the installer, verifies its SHA-256,
  installs it silently and reopens the dashboard. **Remind me later** during the countdown postpones it. Checks run
  at every start and every 6 hours while the dashboard runs.

### Fixed
- **Dashboard did not reopen after an in-app update.** The installer reopened it through the "Windows Utility by
  Sajith Dashboard" startup task, which does not exist when dashboard startup is turned off, so nothing started.
  Setup now starts the dashboard directly (it already runs elevated as the same user). The app also leaves an
  `update-relaunch.flag` marker before starting the installer as a second signal, and removes it on start.
  Takes effect from the update to 2.0.50 onwards, because the installer being run decides how to reopen.

## [2.0.49] - 2026-10-09

### Added
- **Customizable System Stats dashboard.** Every card is now a tile. **Customize** shows a handle on each tile:
  drag a tile onto another to move it before/after it, drag the blue corner to resize (width snaps to a quarter,
  half, three-quarters or full width; height is free, double-click the corner to fit the content), or use the
  tile's size menu. **Reset layout** restores the default. The layout is saved automatically. Tiles flow into
  4, 2 or 1 columns depending on the window width.
- **Desktop widgets.** Right-click any tile (or use its pin button in Customize) to pin it to the desktop as a
  borderless widget showing the live tile, including while the dashboard is in the notification area. Drag to
  move, resize from the corner, right-click for Keep on top / Open dashboard / Remove widget, double-click to open
  the dashboard. Widgets and their positions come back when the dashboard starts. A widget is a live picture of
  the tile, so its buttons are not clickable.

### Changed
- **Small screens (1024x600, 1366x600).** Every window is kept inside the Windows work area. When a dialog's
  designed minimum size is larger than the screen, the window is shrunk to fit and its content scrolls instead of
  being cut off. The dashboard's minimum size is now 760x460.

## [2.0.48] - 2026-10-09

### Added
- **Check for updates from the dashboard.** A sidebar button above About opens the update window. When an update is
  available it changes to "Update to x.y.z".
- **Update alert banner** across the top of the dashboard. It appears every time the app opens while an update is
  available, with **Update now** and **Remind me later** (tomorrow, in 3 days, in a week). A snooze applies to that
  version only, so a newer release shows the alert again. The dashboard re-checks every 6 hours while it runs in
  the tray. Replaces the one-time startup message box.
- **Printers in the Print Spooler tile.** Installed printers with status, default marker, local/network/virtual
  type and address (refreshed every 30 seconds and after spooler actions), and printers found on the local subnet
  that accept RAW 9100, IPP 631 or LPD 515 connections, marked installed or not installed. The network scan runs
  once shortly after startup and on **Scan network**; it only opens and closes TCP connections.

## [2.0.47] - 2026-10-09

### Changed
- **Redesigned update window.** A coloured status badge with a clear headline (checking, up to date, update
  available, downloading, failed), a details panel with installed version, published date, latest available
  version and last-checked time, and a separate "What's new" section for release notes.
- **"You're up to date" when nothing has been published.** A missing release (HTTP 404) is now reported as up to
  date instead of an error. Network failures show a short, plain message; details still go to the application log.
- **Published date next to the version** in the update window and About window. The build stamps the date via
  `AppReleaseDate` in `Directory.Build.props` (override with `-p:AppReleaseDate=yyyy-MM-dd`); when the installed
  build is the published release, the date from `update.json` is shown instead.

## [2.0.46] - 2026-10-07

### Changed
- Update source now points at the `sajith1994/WindowsUtilityBySajith` GitHub repository (was a placeholder owner).
  No other change from 2.0.45.

## [2.0.45] - 2026-10-07

### Added
- **In-app updates.** About -> **Check for updates** opens an update window that reads `update.json` from the
  latest GitHub release, shows the new version and its notes, downloads the installer, verifies its SHA-256 and
  runs it silently. The app closes, the installer upgrades it and the dashboard reopens.
- **Automatic check at startup** (on by default, switchable in the update window). It prompts once per new version
  and never blocks startup.
- `publish-update.ps1`: builds the installer, writes `update.json` with the checksum and optionally publishes both
  as a GitHub release (`-Upload`, needs the GitHub CLI).
- Installer: `/RELAUNCH` switch reopens the dashboard after a silent self-update.

### Security
- The update address is fixed in the app and limited to HTTPS on github.com / githubusercontent.com. It is not
  configurable, because the settings file is writable by standard users while the app runs elevated.
- A download that does not match the published SHA-256 is deleted and never run.

## [2.0.44] - 2026-10-02

### Fixed
- **Build errors CS1056 / CS1585 in `App.xaml.cs`.** The 2.0.43 edit that added the activation
  listener inserted a literal `\n` after the class's closing brace and placed the new members
  outside the class. The members are now inside the class and the stray characters are gone.
  No functional change from 2.0.43.

## [2.0.43] - 2026-10-02

### Fixed
- **Dashboard could not be recovered once minimised with the tray icon hidden.** Launching it
  again from the desktop icon hit the single-instance mutex and showed "already running", leaving
  no route back to the window. A second launch now signals the running instance through a named
  event (`Local\CentricDeviceMonitor.Activate`), and the running instance restores, un-minimises
  and brings the window to the foreground. The "already running" message only appears if the
  owning instance does not respond.

### Changed
- **Executable renamed** from `CentricDeviceMonitor.exe` to `WindowsUtilityBySajith.exe`, so UAC
  prompts, Task Manager and the taskbar show the product name rather than the old project name.
  Updated in the project file, installer, build script and tray companion. The installer also
  terminates the old executable name during upgrade so pre-rename installs do not stall on the
  files-in-use check. The background service and tray executables are unchanged.

### Notes
- "Publisher: Unknown" on the UAC prompt is a code-signing matter, not a build setting. It will
  read "Unknown" until the executables are signed with a certificate.

## [2.0.42] - 2026-10-02

### Fixed
- **Command Prompt shortcuts opened with garbage on the first line and "The batch file cannot be
  found" on the last.** The generated `.cmd` was written with `Encoding.UTF8`, which emits a
  byte-order mark; cmd.exe does not understand BOMs, so `@echo off` became `´╗┐@echo off`, was
  rejected as an unrecognised command, and the rest of the script ran with echo still on. The
  script is now written without a BOM and sets `chcp 65001` explicitly, so non-ASCII paths still
  work.
- The script deleted itself with a plain `del`, which makes cmd complain when it reads past the
  end. Replaced with the `(goto) 2>nul & del` idiom, which ends batch processing before the
  delete, so there is no next line for cmd to look for.

## [2.0.41] - 2026-10-02

### Changed
- **New application artwork.** `Assets/app.ico` rebuilt from the supplied gear-and-shield image at
  16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 px. The icon is cropped to the rounded tile rather
  than the full composition, so the gear and shield still read at taskbar size, and the tile
  corners are transparent so it does not sit as a dark square on light taskbars. The dashboard,
  tray companion and installer all reference this one file.
- Sidebar logo replaced with the new artwork (`Assets/app-logo.png`), replacing the Segoe glyph.
- Terms & Conditions window now shows the full artwork behind the licence text at 14% opacity
  with a subtle gradient, keeping the text readable.

## [2.0.40] - 2026-10-02

### Fixed
- **NullReferenceException when opening a live graph.** The range and interval combo boxes declare
  `IsSelected="True"` in XAML, so `SelectionChanged` fires while `InitializeComponent` is still
  parsing - before `GraphCanvas`, which is declared below them, has been created. `Render` now
  returns early until the window has loaded, and the two handlers read the combo from `sender`
  rather than the named field, which is also still null at that point.

## [2.0.39] - 2026-10-02

### Fixed
- **Build error CS0104: `ComboBox` is an ambiguous reference.** Third instance of the same class of
  error (after `Brush` in 2.0.35 and `Point` in 2.0.37). `ComponentGraphWindow` now aliases
  `WpfComboBox`, matching the convention `MainWindow.xaml.cs` already uses.
- Swept every file added in this series against the full list of type names shared between
  `System.Windows.Forms`/`System.Drawing` and WPF. `ComboBox` was the only remaining one.

## [2.0.38] - 2026-10-02

### Added
- **Adjustable time range** on every live graph: 30 seconds, 1, 2, 5 or 10 minutes, defaulting to
  1 minute instead of waiting five for a full chart.
- **Adjustable sample interval**: 250 ms, 500 ms, 1 s or 2 s.
- **Per-reading checkboxes** in the legend, plus Show all and Clear history.

### Changed
- **Labelled axes when few readings are selected.** Five series in different units on one chart
  could not be read, so the scaling now depends on how many are shown:
  - One series: a single axis labelled in its own units.
  - Two series: left axis for the first, right axis for the second, each in its own colour.
  - Three or more: per-series scaling as before, with a note explaining it and suggesting
    unticking some.
- A time axis is drawn along the bottom ("now", "-15s", "-30s"...), matching the selected range.
- Percentage series are pinned to 0-100 rather than scaled to their own peak, so a CPU idling
  between 2% and 4% no longer looks like a wildly fluctuating load.
- Axis ranges pad by 10% and handle a flat line, so a constant value sits mid-chart instead of
  along the frame.

## [2.0.37] - 2026-10-02

### Fixed
- **Build error CS0104: `Point` is an ambiguous reference.** Same cause as the `Brush` error in
  2.0.35 - `UseWPF` and `UseWindowsForms` are both enabled, so `System.Drawing` and
  `System.Windows` both define `Point`. Now qualified as `System.Windows.Point`.
- Warning CS8629 in `GetGpuMemoryReading`: the final branch is only reachable when `total` is
  non-null, but the compiler cannot prove it. Replaced with a null-coalescing read.

### Added
- **Notification area icon toggle** and **desktop notifications toggle** in Background Monitoring.
  - Hiding the tray icon only affects this dashboard process; the background service and its
    monitoring are unaffected. The hint text changes to warn that closing the window with the icon
    hidden leaves no visible trace of the dashboard.
  - Disabling notifications silences all three balloon sites, including the CPU temperature alert.
    The sound alert stays under CPU Sensor and Alerts, since muting one should not mute the other.

## [2.0.36] - 2026-10-02

### Changed
- **Dashboard cards are now one per component.** CPU Temperature, CPU Usage and CPU Package Power
  were three separate cards describing the same part; they are now a single Processor card.
  Graphics, Memory and Print Spooler follow the same shape. Every existing element name was kept,
  so the display code is unchanged - only the layout moved.

### Added
- **Live graph windows.** Clicking the Processor, Graphics or Memory card opens a chart with every
  reading for that component on one timeline, sampled once per second with five minutes of history.
  - Processor: load, temperature, clock, package power, CPU fan RPM
  - Graphics: utilisation, temperature, power, VRAM used, GPU fan RPM
  - Memory: usage
- Each series is normalised against its own peak, because the readings span percentages, degrees,
  watts, gigahertz and RPM - a single shared axis would flatten everything except fan RPM. The
  legend shows the real current value and unit for each series.

## [2.0.35] - 2026-10-02

### Fixed
- **Build error CS0104: `Brush` is an ambiguous reference.** The health strip added in 2.0.34
  declared `Brush` and `Color` unqualified. The project enables both `UseWPF` and
  `UseWindowsForms`, so `System.Drawing` and `System.Windows.Media` are both in scope and each
  defines those types. `MainWindow.xaml.cs` already aliases `WpfBrush` for this exact reason -
  the new code now uses it, and qualifies `System.Windows.Media.Color`.

## [2.0.34] - 2026-09-19

### Added
- **PC health verdict strip** on System Stats, the first stage of the PC Assist-style redesign.
  The dashboard previously led with measurements only - "63.0 C", "0% used", "Connected" - which
  are correct but leave interpretation to whoever is reading the screen. The strip now leads with
  a verdict (No problems found / Worth a look / Action needed) and four domain checks: Cooling,
  Storage, Memory and Network.
- `PcHealthService` holds every threshold in one place so rules are not reimplemented per card.
  Rules include a fan reporting 0 RPM while the system is warm, which is the clearest cooling
  fault there is, and free space below 15% / 10%.

### Notes
- The strip interprets readings the dashboard has already taken; it adds no extra sensor polling.

## [2.0.33] - 2026-09-19

### Added
- **Direct3D 11 compute GPU load**, replacing the WPF render load as the primary GPU workload.
  The render load is driven by `CompositionTarget.Rendering`, which presents once per monitor
  refresh - a 10 ms budget on a 100 Hz panel. A few hundred low-poly spheres used about a third
  of it, so an RTX 2080 Ti stalled near 32% and 29 W no matter how much geometry was added or how
  large the window was made. A compute dispatch has no swap chain and no present, so nothing
  throttles it to the refresh rate.
- **GPU adapter selection.** The load runs on an explicitly chosen `IDXGIAdapter1`, so dedicated
  and integrated graphics can be tested separately. The WPF load could not do this - it runs on
  whichever adapter Windows assigns to WPF.
- Load level is applied as a duty cycle (busy share of each 100 ms window) rather than by changing
  what the shader does, so partial loads stay representative.

### Changed
- The WPF render load is retained as an automatic fallback when a volume or adapter refuses a
  D3D11 device. When that happens the window says so explicitly, including that the fallback will
  not reach full GPU power.
- New dependencies: `Vortice.Direct3D11` and `Vortice.D3DCompiler`.

## [2.0.32] - 2026-09-19

### Fixed
- **CPU fan showed as "not detected" while its RPM appeared under case fans.** Super I/O chips
  name their headers generically ("ITE IT8686E - Fan #1"), so the name-based grouping added in
  2.0.28 found no CPU match and put every motherboard fan in the System group. When no fan is
  classified as CPU, the inventory now reuses the existing ranked CPU fan selection so the CPU
  card, the thermal test and the inventory all agree on which header is the CPU one.

## [2.0.31] - 2026-09-19

### Fixed
- **App failed to start: type initializer for `SafeWpfInputLanguageSource` threw.** The
  `InvariantGlobalization=true` publish flag added in 2.0.30 strips the ICU globalization data,
  which WPF's input-language plumbing requires during startup. The flag has been removed from all
  three publish commands. `SatelliteResourceLanguages=en` and `DebugType=none` are kept - those
  are safe and account for most of the non-runtime saving.
- **Setup flagged the Inventory and Compatibility Appraisal service and then failed to close it.**
  Inno Setup's default `CloseApplicationsFilter` is `*.exe,*.dll,*.chm`. With the payload changed
  from three single-file executables to a shared-runtime folder, that default matched every .NET
  runtime DLL and pulled in unrelated processes holding them - including a Windows service Setup
  has no ability to close. The filter is now scoped to `CentricDeviceMonitor*.exe`.

## [2.0.30] - 2026-09-19

### Fixed
- **VRAM still reported as 4 GB.** The registry fallback was not finding
  `HardwareInformation.qwMemorySize` on this driver. New `DxgiAdapterService` reads
  `DXGI_ADAPTER_DESC1.DedicatedVideoMemory`, a native 64-bit value from the driver and the same
  source Task Manager uses. Order is now DXGI, then registry, then the 32-bit WMI value.

### Added
- GPU power, GPU load and VRAM usage on the thermal load test window, alongside the fan rows.
- `DxgiAdapterService.GetAdapters()` enumerates every graphics adapter with vendor, dedicated and
  shared memory, and a discrete/integrated classification. Groundwork for per-adapter load testing.

### Changed
- **Installer size.** The three executables were each published self-contained AND single-file,
  so each embedded its own full copy of the .NET runtime - roughly three times the runtime in one
  installer. They now publish into a shared folder against one copy of the runtime. Also added
  `SatelliteResourceLanguages=en`, `InvariantGlobalization=true`, `DebugType=none`, removal of
  `.pdb`/`.xml` from the payload, and `lzma2/ultra64` compression. The build now prints the
  published payload size so the effect is measurable.

## [2.0.29] - 2026-09-19

### Fixed
- **New fan and GPU sensor rows never populated.** They were read after the early return that
  fires whenever the LocalSystem background service supplies the CPU temperature, which is the
  normal case, so the cards stayed on "Checking...". Fan and GPU sensors are now read in
  `RefreshFanAndGpuSensorsAsync()` before that branch, so both paths populate them.
- **VRAM reported as 4 GB on cards with more.** `Win32_VideoController.AdapterRAM` is a 32-bit
  value that saturates at 4 GB, so an 11 GB RTX 2080 Ti read as exactly 4.0 GB. The registry
  value `HardwareInformation.qwMemorySize` is now preferred outright rather than being compared
  with `Math.Max` against the capped WMI figure, and when the adapter-name match against
  `DriverDesc` fails the largest size reported by any display-class subkey is used.
- **Setup could not close the tray icon.** `CentricDeviceMonitorTray` has no top-level window, so
  Inno Setup's close request could not reach it and the in-use prompt appeared on every install.
  `CloseApplications` is now `force`, and `StopRunningComponents()` terminates the tray and
  dashboard at the Ready page, before Setup scans for files in use.

### Added
- CPU, case and GPU fan RPM on the CPU & GPU thermal load test window, where whether the coolers
  ramp up is the entire point of the test.

## [2.0.28] - 2026-09-19

### Added
- **All fans are now reported, grouped by location.** The dashboard previously showed only the
  single highest-ranked fan sensor, so a machine with a CPU fan, a case fan and three GPU fans
  displayed one RPM figure. `HardwareMonitorService.GetFanInventoryReading()` returns every fan
  sensor classified as CPU, System or GPU. CPU and case fans appear on the CPU Temperature card;
  GPU fans appear on the GPU card. Duplicates are removed, since the same fan can surface under
  both the motherboard and its embedded controller.
- **GPU temperature, utilisation and VRAM** on the GPU card, via new
  `GetGpuUtilisationReading()` and `GetGpuMemoryReading()` readers. Utilisation prefers the
  GPU Core sensor (the figure Task Manager shows) over memory-controller and video-engine load.
  VRAM reports dedicated memory only; shared memory is system RAM and is excluded.

## [2.0.27] - 2026-09-03

### Fixed
- **Network status stayed "Connected" with every link down.** An adapter was treated as connected
  purely on `OperationalStatus == Up`. Windows reports filter and virtual adapters as Ethernet and
  leaves them permanently Up, so the WFP Native MAC Layer LightWeight Filter pseudo-adapter kept
  the status green while Wi-Fi was disconnected and there was no IPv4 address at all. Virtual and
  filter adapters (WFP, Hyper-V, VMware, VirtualBox, TAP/Wintun/WireGuard, WAN Miniport, Bluetooth
  PAN, `Local Area Connection*`) are now excluded, and a real adapter must hold a routable IPv4
  address - APIPA 169.254.x.x no longer counts as connectivity.
- The Network Speed window made the same claim through `NetworkInterface.GetIsNetworkAvailable()`.
  It now requires both a default gateway and a non-APIPA local address.

### Added
- **Continuous packet-loss testing.** "Continuous" in the Packets list runs until Stop is pressed.
  The progress bar switches to indeterminate and partial results are kept when the run is stopped,
  since stopping is the normal way to end a continuous test.
- **Sub-100 ms ping intervals** for fast links: 5 ms, 10 ms, 25 ms, 50 ms and 100 ms alongside the
  existing options. Default remains 250 ms.
- **Sub-millisecond latency resolution.** `PingReply.RoundtripTime` reports whole milliseconds, so
  a local gateway read a flat 0 ms and jitter collapsed to zero. When the reported time is 0 the
  measured elapsed time is used instead, and latency now displays two decimals.

## [2.0.26] - 2026-08-31

### Fixed
- **Read speeds could still be reported from RAM.** Three causes, all addressed:
  - The unbuffered-I/O probe only tested a 4 KB *write*. A volume can accept an unbuffered write
    and still reject an unbuffered read, and a 4 KB transfer can succeed where a 1 MiB one fails.
    The probe now exercises both directions at the real block size and alignment.
  - The fallback to buffered I/O was silent. It was recorded in the app log only, so a cached
    read was presented in the UI as though it were a drive measurement.
  - Short reads were ignored. If the file was smaller than expected the remaining transfers
    completed instantly, the timer stopped early, and throughput was computed against the full
    byte count. `ReadSequentialAsync` now throws when fewer bytes arrive than were requested.

### Added
- `StorageBenchmarkResult.CacheBypassed` and `.Profile`. When the cache could not be bypassed the
  Speed test tab now labels the read as "cached - not drive speed" and shows a warning banner
  rather than printing a figure a technician might act on.
- `Profile` and `CacheBypassed` columns in `storage-benchmarks.csv`, so past rows stay interpretable.

## [2.0.25] - 2026-08-31

### Changed
- **Speed test now mirrors CrystalDiskMark's default sequential profile.** The tab runs SEQ1M
  Q8T1 (1 MiB transfers, queue depth 8, single thread, cache bypassed) against separate write and
  read handles, so its numbers can be placed next to CrystalDiskMark's directly. Note the unit
  convention differs: this app reports MiB/s (1048576 bytes) while CrystalDiskMark reports MB/s
  (1000000 bytes), so CDM will read roughly 5% higher for identical throughput.
- Capacity verification and the speed test no longer share one transfer profile. Verification uses
  8 MiB blocks at queue depth 8 because it is throughput-bound; the benchmark uses the CDM-matching
  profile above.

### Fixed
- **Chunk-boundary stalls in capacity verification.** Each test file drained the device queue,
  closed its handle and created the next file before I/O could resume, leaving the drive idle at
  every boundary and holding Task Manager's active time well below saturation. Test files are now
  4 GB instead of 512 MB (a 10 GB run crosses two boundaries instead of nineteen), and the next
  file's handle is opened on a background thread while the current file is still transferring.
  Both the write and verify passes do this.

## [2.0.24] - 2026-08-31

### Fixed
- **Storage tests were measuring the CPU, not the drive.** Capacity verification generated every
  4 MB block with `RandomNumberGenerator.Fill` and hashed it with SHA-256 on the same thread as
  the write, capping throughput near 180 MB/s regardless of the drive under test. Data generation
  now uses a deterministic xoshiro256** generator (`FastBlockGenerator`) running at several GB/s,
  and verification regenerates and compares blocks directly instead of hashing them. Block content
  is still unique per block and per run, so counterfeit-flash detection is unchanged.
- **Sequential transfers ran at queue depth 1.** NVMe devices are internally parallel and cannot
  reach rated speed with a single outstanding I/O. Reads and writes now keep four 8 MB transfers
  in flight via `RandomAccess`.
- **Read results were served from the Windows page cache.** The benchmark read back a file it had
  just written, so the reported read figure reflected RAM, not the drive. Transfers now use
  FILE_FLAG_NO_BUFFERING with sector-aligned pinned buffers (`AlignedIoBuffer`), with an automatic
  probe and fallback to buffered write-through on volumes that reject unbuffered I/O.
- **Version number was not bumped**, so `BUILD-INSTALLER.bat` produced
  `WindowsUtilityBySajith-Setup-2.0.23.exe` for a 2.0.24 build. `AppVersion` in
  `Directory.Build.props` is the single source of truth and is now 2.0.24; the fallback default in
  `installer/CentricDeviceMonitor.iss` was aligned to match.

### Added
- **Smart App Control shortcut** in the Windows Shortcuts card. Opens the Windows
  Security page for Smart App Control via `windowsdefender://smartappcontrol`.
  - `WindowsUtilityService.OpenSmartAppControlSettings()` launches the page.
  - `WindowsUtilityService.IsSmartAppControlAvailable()` gates on build 22621
    (Windows 11 22H2), so Windows 10 shows a clear message instead of failing silently.
  - Windows Shortcuts `UniformGrid` expanded from 2 rows to 3 to fit the new tile.

### Note for technicians
Smart App Control cannot be re-enabled after a user switches it off; a clean Windows
reinstall is required. Treat the toggle as one-way when advising customers.
