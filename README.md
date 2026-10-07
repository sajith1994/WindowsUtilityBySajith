# Windows Utility by Sajith 2.0.23

Windows Utility by Sajith is a .NET 10 WPF technician dashboard with a LocalSystem background monitoring service plus an interactive service-status tray companion. It combines network-device monitoring, hardware/system health, Windows administration, storage and battery diagnostics, cleanup tools, disk management and common technician shortcuts.

## Current release

**2.0.23** adds live reported CPU clock speed and real CPU package power to the thermal-test screen. Integrated-only graphics can now run the GPU render test without a separate GPU sensor by using the CPU package temperature as the shared safety sensor. Dedicated, hybrid and unknown graphics configurations still require real GPU temperature telemetry. The 105 °C CPU option, visible Start/Stop footer and all supervised/time/telemetry safety stops remain in place.

In **2.0.21**, Utility Tools added a guarded **CPU & GPU Thermal Load Test**. It can run CPU-only, GPU-render-only or combined loads at 50%, 75% or 100% for 30 seconds to 5 minutes. The test requires relevant temperature telemetry before starting, shows live and peak CPU/GPU temperatures, elapsed/remaining time and load state, and stops automatically at the configured temperature limit, when required telemetry disappears, when time expires or when the window closes.

The GPU workload uses an animated WPF 3D scene and Direct3D hardware rendering when Windows reports an available rendering tier. It is a simple cooler-response check rather than a vendor-specific maximum-power benchmark, so the window asks the technician to confirm activity using the visible render preview and temperature rise and warns that some discrete or multi-GPU systems may not be fully loaded.

In **2.0.20**, Network Diagnostics added a packet-loss and link-quality test, live graphical upload/download meters for the two-PC LAN throughput test, direct enable/start and disable/stop controls for the LocalSystem background service, and clearer master controls for the system power scheduler. The packet test reports loss, received/sent counts, average/minimum/maximum latency, jitter and an Excellent/Good/Fair/Poor quality grade.

Packet loss is presented as a practical indicator rather than proof of a physical cable fault. Testing the local gateway or another wired PC can expose intermittent cable, connector, NIC or switch-port problems; a physical cable tester is still required to certify the cable itself.

In **2.0.17**, the previous Cloudflare request shape was adjusted for HTTP 403 compatibility. 2.0.18 removes that fragile integration from the user-facing workflow entirely in favour of FAST.com plus the native LAN test.

## Interface

In **2.0.15**, Network Diagnostics adds a **Speed & bandwidth test** that always reports the active Ethernet/Wi-Fi link locally and, when internet access is available, can run a user-started latency/download/upload throughput test. Network and Utility shortcuts now include concise use-case guidance, and the Windows Repair & Recovery dialog now resolves its shared styles from application resources.

The dashboard uses an Apple-inspired navigation shell with four focused areas: **System Stats**, **Network Diagnostics**, **Utility Tools**, and **Power & Battery**. A shared design-token system provides System/Light/Dark themes, system-blue primary actions (`#007AFF`), neutral cards, subtle borders, rounded controls and segmented tabs. The **System** theme follows the current Windows app-theme preference and updates while the dashboard is running.


In **2.0.12**, System Stats now acts as a compact hardware inventory: PC identity/serial, RAM modules, displays with resolution/refresh/physical size, multiple graphics adapters and reported VRAM, physical disks and per-volume used/capacity, real GPU power telemetry, and Windows activation/grace status. GPU power is taken from an exposed hardware Power sensor rather than estimated from utilization; unsupported hardware displays **Not available**.

In **2.0.11**, the approved End User Licence Agreement, Terms of Use and Disclaimer is included with every build. Interactive installer runs display the licence page and require acceptance before installation can continue. The same document is available after installation from **About Windows Utility -> Terms & Conditions**. The terms explain administrative/destructive capabilities, local diagnostic-data handling, authorised-use requirements, backup responsibility and applicable warranty/liability limitations.

In **2.0.10**, the managed-device editor layout is corrected for Windows display/text scaling: existing device names and IPv4 addresses render as normal editable text, the input fields use a larger text viewport, and Cancel/Save stay in a dedicated non-scrolling action row. The editor is also resizable as a fallback on constrained displays.

In **2.0.9**, managed-device Add/Edit dialogs are hardened for Windows custom/BCP-47 locale configurations that can expose the transient LCID/LANGID `0x1000` (`4096`). The dashboard registers a culture-safe WPF input-language source and no longer forces a per-control input culture, so device names and IPv4 addresses can be edited without changing the user's normal keyboard layout.

In **2.0.8**, the sequential storage benchmark can run **1-5 passes or Continuous** with pass-by-pass progress and aggregate write/read statistics. **System Stats** also includes live **CPU Package Power** from an actual hardware power sensor, plus average and maximum wattage recorded during the current dashboard session. If the platform does not expose package power through LibreHardwareMonitor/WMI, the UI reports it as unavailable instead of estimating wattage from CPU utilization.

**Network Diagnostics** now shows the managed IP-address table directly on the page with live status, response time, hostname and last-check time. **Power & Battery** shows the currently active Windows power plan, can switch between installed plans, and opens native Power Options/lid settings. **Storage Health & Speed** and **Storage Sense** are available from Utility Tools.

## System & network health

The System Stats and Network Diagnostics pages show:

- Network connected/disconnected status and active adapter summary
- Windows edition/display version and OS build number
- Laptop battery percentage plus charging/on-battery state when a battery exists
- Wi-Fi signal percentage and SSID when connected through Wi-Fi
- Preferred local IPv4 address
- Default gateway and DNS server for the preferred active adapter
- Public IPv4 address
- IP-based city, region and country
- ISP/ASN/organization information for the public connection

Public-IP/location details are retrieved from `https://ipwho.is/` over HTTPS and cached for five minutes. The dashboard continues working if that service or the internet is unavailable. A Refresh button forces a new extended-health lookup.

### CPU & GPU Thermal Load Test

Open **Utility Tools -> CPU & GPU Thermal Load Test**. Choose CPU, GPU render or Combined; select a 30-second, 1-minute, 2-minute or 5-minute duration; choose 50%, 75% or 100% load; and set separate CPU/GPU stop temperatures. The CPU stop control supports 70-105 °C and defaults to 105 °C; that upper setting can be near a processor's thermal-throttling point. The CPU generator uses one cancellable long-running worker per logical processor with a duty cycle for reduced load levels. The GPU path renders a dense animated WPF 3D scene only when Windows reports hardware rendering.

The test is intentionally supervised and finite. CPU load requires a CPU-specific temperature sensor. Dedicated, hybrid or unclassified GPU configurations require a real GPU temperature sensor. On a conservatively detected integrated-only system, the GPU shares the processor package and the app can use the CPU package temperature as its shared safety sensor when no separate graphics temperature exists. Hardware WPF rendering is always required for GPU load.

The acknowledgement and Start/Stop actions remain visible in the fixed footer. Start requires acknowledgement and confirmation, Stop remains available throughout, and the workload is cancelled automatically if a limit is reached, required telemetry becomes unavailable, the timer expires or the window closes. Live readings show CPU temperature, average reported CPU core speed, real CPU package power when exposed, GPU or shared-package temperature, peaks, elapsed time and load state. Package watts are never estimated. This feature helps compare temperature rise and fan/cooler response; it does not certify the cooler or guarantee system stability.

### Network Speed & Bandwidth

The **Current Connection** card includes **Packet loss & speed tests**. Local-link information works with no internet connection and reports the preferred active adapter, negotiated Ethernet link speed or Wi-Fi receive/transmit link rates, signal/radio/channel details, gateway latency and current adapter traffic. These values describe the local adapter/link; negotiated link rate is not presented as guaranteed end-to-end LAN throughput.

The same window includes **Packet loss & link quality**. It automatically offers the detected default gateway as the target, accepts another IP address or hostname, and sends 10/25/50/100 controlled ping samples at 250/500/1000 ms intervals. Results include packet loss, received/sent counts, average and range latency, jitter and a clear quality rating. Cancellation is available during longer tests.

For public internet speed, the window opens the official `https://fast.com/` website in the default browser. FAST.com automatically measures download speed; **Show more info** on the website provides upload speed and unloaded/loaded latency. Windows Utility intentionally does not call or scrape an undocumented FAST.com API.

For **actual local-network throughput**, use the built-in two-PC test. On PC A, open Network Speed & Bandwidth and start the LAN test server (default TCP port 5201). On PC B, enter PC A's IPv4 address and run the LAN test. The app measures generated TCP traffic in both directions and reports LAN latency, upload Mbps and download Mbps. Live upload/download meters show the running average on a scale based on the active adapter and automatically expand for faster measured links. Internet access is not required. 1/2/4/8 parallel streams and 5/10/20/30-second durations are available. No local files are read or transferred. The result is limited by the slowest link/device in the path and is therefore useful for checking switches, cabling, Ethernet negotiation and Wi-Fi/AP performance.

## Background monitor and power scheduler controls

The **Background Monitoring** card can now enable and start the LocalSystem service with Automatic (Delayed Start), disable and stop it, restart it, or open its log. Disabling requires confirmation and preserves saved settings while pausing unattended device checks, sensor alerts and scheduled power actions.

The **System Power Schedule** card has an explicit master switch plus an Enable/Disable scheduler button. The configured action/time remain saved while disabled. If a schedule is enabled while the background service is stopped, the countdown displays **Paused** and explains that the service must be enabled for unattended execution.

## Managed-device restart visibility and service tray

Managed-device history now records a dedicated shutdown/restart reachability event when a previously unreachable device comes back online. The event stores the outage timestamp, recovery timestamp, downtime and original ping error. Because remote ping monitoring cannot prove whether the cause was a Windows restart, power-off or network interruption, the UI explicitly labels the shutdown/restart event as **inferred from loss of reachability** rather than presenting it as confirmed telemetry.

The installer also deploys `CentricDeviceMonitorTray.exe`, a lightweight non-elevated notification-area companion. Windows services run in Session 0 and cannot directly display UI in the signed-in user's tray, so this companion starts at user logon and immediately verifies the LocalSystem service and its `status.json` heartbeat. Green indicates a running service with a fresh heartbeat, orange indicates a running service with a stale heartbeat, and red indicates stopped/unavailable. Double-click shows a status balloon; right-click shows service state, monitoring state, managed-device count and heartbeat age.

## Network repair shortcuts

Network Diagnostics includes guarded Administrator shortcuts for:

- `ipconfig /release` followed by `ipconfig /renew`
- `ipconfig /flushdns`
- `netsh winsock reset` followed by `netsh int ip reset`
- `arp -d *`
- a Full Repair action that runs the commands in the requested order

Actions that can interrupt connectivity show a confirmation first and warn more strongly when the dashboard detects an RDP/terminal session. Winsock/TCP-IP reset actions remind the technician that Windows must be restarted afterwards.

## Essential Windows repair & recovery

Utility Tools includes elevated quick actions for `sfc /scannow`, DISM Online CheckHealth/ScanHealth/RestoreHealth and CHKDSK `/f /r` on the current system drive. A dedicated **Windows Repair & Recovery** window adds selected-drive CHKDSK, offline DISM, offline SFC and BootRec commands.

The offline repair area can detect the BCD `osdevice` drive, accepts only a validated drive letter such as `C:`/`D:`, and warns if the selected volume does not contain a detectable `Windows` directory. BootRec actions (`/fixmbr`, `/fixboot`, `/rebuildbcd`) require confirmation when available. Because BootRec is normally provided in Windows Recovery Environment, the app copies the fixed BootRec command to the clipboard with WinRE guidance when `bootrec.exe` is not available in the current desktop session.

## DiskPart & disk management

The disk-management window enumerates physical disks and partitions using Windows Storage Management. It shows disk number, model, serial, capacity, allocated/unallocated space, partition count, bus, partition style and health plus the partitions on the selected disk.

Disk actions are intentionally separated so the current Windows disk state is clear:

- **Clean disk** - erases the selected non-system disk's partition table and returns it to RAW. Two confirmations are required.
- **Initialize disk** - initializes a RAW disk as GPT or MBR without automatically creating a partition.
- **Create partition** - creates and quick-formats either the maximum available free space or a custom GB size. Use a custom size and repeat the action to create multiple partitions.
- **Format selected** - quick-formats only the selected non-system partition as NTFS or exFAT and applies the chosen label.
- **Delete selected** - removes only the selected non-system partition and returns its space to unallocated space.
- **Erase + prepare full disk** - for a previously formatted SSD that should be reused from scratch. After two confirmations it erases the entire selected disk, initializes it as GPT/MBR, creates a maximum-size partition and formats it.

Windows `Initialize-Disk` only applies to RAW disks. Therefore an existing GPT/MBR SSD must be cleaned before it can be initialized again; the **Erase + prepare full disk** action performs that complete workflow in one guarded operation.

Windows boot/system disks are blocked from destructive actions. Boot/system partitions are blocked from formatting and deletion. Always verify disk number, model, size and serial before destructive operations.

## Windows shortcuts and command tools

The dashboard includes Devices & Printers, Task Manager, standard/Admin Command Prompt, standard/Admin PowerShell, Disk Cleanup (Admin), DiskPart (Admin), Disk Management and the graphical disk-management assistant. Dedicated CMD and PowerShell command-entry tiles can launch entered commands in normal or Administrator mode.

## External technician utilities

At dashboard startup the application refreshes launch metadata from the official upstream README files for Chris Titus Tech WinUtil, Raphire Win11Debloat and Winhance. Only accepted HTTPS endpoints from the expected official source are launched, and the exact command is shown for confirmation first.

## Other capabilities

- Local IP scanner and managed-device ping monitoring with outage/recovery history
- CPU model/usage/temperature/fan monitoring with alert logging
- Supervised CPU/GPU thermal load testing with live temperatures and automatic safety stops
- RAM usage/capacity/speed monitoring
- Storage health, NVMe benchmarks, continuous speed tests and real-capacity verification
- Battery diagnostics and Windows battery-report generation
- Temporary-file cleanup with deleted/skipped item report and reclaimed-space totals
- User-application termination report
- Exact daily shutdown/restart scheduling
- Print Spooler control through the LocalSystem service
- Elevated dashboard startup and LocalSystem background monitoring


## Terms, privacy and installer acceptance

`TERMS-AND-CONDITIONS.txt` is shipped with the published application. Interactive Inno Setup installations use the same file as the installer licence agreement, so the user must accept it before continuing. Installed users can reopen the document from **About Windows Utility -> Terms & Conditions**.

The terms state that Windows Utility is a legitimate technician/admin utility, is not designed as malware, is not designed to collect personal information for advertising/profiling/resale, and does not intentionally transmit diagnostic or managed-device information to the developer during normal operation. Technical data such as IP addresses, MAC addresses, hostnames, device names, timestamps, system information and logs is processed/stored locally and may be personal data under applicable law depending on context.

## Upgrades

The internal service name remains `CentricDeviceMonitorService` for compatibility. The installer retains the permanent AppId used by previous releases. Choose **Upgrade existing installation** to preserve `%PROGRAMDATA%\CentricDeviceMonitor` devices, settings and logs.

## Build

Requirements on the build PC: Windows 10/11 x64, .NET 10 SDK, Inno Setup 6 and internet access on the first build if PawnIO is not cached.

Run:

```text
BUILD-INSTALLER.bat
```

Expected installer:

```text
dist\installer\WindowsUtilityBySajith-Setup-2.0.23.exe
```

The installed application is self-contained; target PCs do not need the .NET SDK.


## Versioning

The release version is defined once in `Directory.Build.props` as `AppVersion`. Update that value for each application revision. The application, background service and tray companion inherit it automatically, and `build-release.ps1` passes the same value to the installer. About reads the compiled assembly version, so it stays aligned with the release build.

### 2.0.6 storage verification layout fix

- Rebalanced the Storage Health & Speed window so the capacity-verification area receives more vertical space.
- Made the capacity-verification tab vertically scrollable at smaller window sizes.
- Step 1 and Step 2 metric rows (current/average/peak speed, elapsed time, ETA and progress) remain reachable instead of being clipped below the window.

### 2.0.5 diagnostics additions

- Capacity verification shows Step 1 write/fill metrics and Step 2 read/verification metrics separately, including current/average/peak speed, elapsed time and ETA.
- Local IP Scanner results can run a common TCP open-port check on selected discovered devices.
- Network Diagnostics includes one-click Windows troubleshooting commands for IP configuration, interface configuration, routes, ARP, connections/listening ports, MAC addresses, ping, trace route, DNS lookup and mapped network resources.

## Publishing an update

1. Bump `AppVersion` in `Directory.Build.props` and `MyAppVersion` in `installer\CentricDeviceMonitor.iss`, add a CHANGELOG entry.
2. Run `.\publish-update.ps1 -Upload` (needs the GitHub CLI signed in). It builds the installer, writes `update.json` with the SHA-256 and creates a GitHub release containing both files.
3. Installed copies pick it up from **About -> Check for updates**, or at the next start (prompted once per version).

The update address is fixed in `Services\UpdateService.cs` (`ManifestUrl`). The release repository must be public.
