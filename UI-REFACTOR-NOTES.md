# Apple-Inspired UI Refactor Notes

This refactor changes presentation and navigation only. Existing monitoring, Windows administration, network, storage, battery, disk-management and background-service behavior remains wired to the original handlers/services.

## 1. Design tokens and controls

`src/CentricDeviceMonitor/App.xaml`

- Primary accent: `#007AFF`.
- Light palette: page `#F5F5F7`, card `#FFFFFF`, text `#1D1D1F`.
- Dark palette: page `#1E1E1E`, card `#2C2C2E`, text `#F5F5F7`.
- Theme-aware 6% light / 8% dark borders.
- 8px rounded primary/secondary/icon buttons.
- 14px dashboard cards with a 4px/16px, 4%-opacity shadow.
- Apple-style toggle switches and segmented radio buttons.
- Visible WPF tabs are restyled globally as segmented controls.
- DataGrid surfaces, headers and selected rows use the same theme tokens.

## 2. Runtime theming

`src/CentricDeviceMonitor/Services/ThemeManager.cs`

- Adds `System`, `Light` and `Dark` modes.
- `System` reads Windows `AppsUseLightTheme`.
- The dashboard checks for Windows theme changes while running.
- Theme brushes are updated centrally, not repeated per page.

`src/CentricDeviceMonitor/Models/AppSettings.cs`

- Adds `UiThemePreference`, defaulting to `System`.
- Existing JSON settings files remain compatible because the new property has a default value.

## 3. Main navigation shell

`src/CentricDeviceMonitor/MainWindow.xaml`

The old multi-colored 12+ tile dashboard is replaced with:

- Left navigation sidebar.
- Semi-translucent top toolbar.
- Contextual primary toolbar action.
- Four content sections:
  - System Stats
  - Network Diagnostics
  - Utility Tools
  - Power & Battery
- System / Light / Dark segmented theme selector.
- Neutral cards, unified icons and increased spacing.

All original `x:Name` controls and original XAML event handlers were preserved so existing `MainWindow.xaml.cs` logic remains source-compatible.

## 4. Main-window code additions

`src/CentricDeviceMonitor/MainWindow.xaml.cs`

- Navigation page switching and contextual toolbar action.
- Theme selection/persistence.
- Live System-theme refresh.
- Theme-aware status brushes instead of assuming normal readings are white text.

## 5. Secondary windows

Dialog XAML files now use shared card/background/text/border resources instead of hardcoded white/light-gray surfaces. This keeps Battery Diagnostics, Storage Diagnostics, IP Scanner, CPU/System logs, Disk Preparation and report dialogs coherent in Dark mode.

## Build compatibility fix

- The project intentionally enables both WPF and Windows Forms because the tray icon uses `System.Windows.Forms.NotifyIcon`.
- UI theme code now uses explicit WPF aliases for `RadioButton`, `Application`, `Color`, and `ColorConverter`, preventing `CS0104` ambiguous-reference errors when compiling with both UI frameworks enabled.

## Validation performed

- All application XAML files parse as XML.
- Every original `MainWindow.xaml` named control still exists after the refactor.
- Every original `MainWindow.xaml` event binding still exists.
- Every event handler referenced by the new `MainWindow.xaml` resolves to a method in `MainWindow.xaml.cs`.
- Requested palette values and border-alpha tokens are present.

A full `dotnet build` could not be executed in the editing environment because the .NET SDK/MSBuild is not installed there. Build on Windows with the repository's existing .NET 10 workflow (`BUILD-INSTALLER.bat` or `dotnet build CentricDeviceMonitor.sln`).

## Network / power follow-up

- `Network Diagnostics` now exposes the managed-device `DataGrid` directly in the lower page area. Existing selection, double-click log, background-service status, and manual refresh handlers are reused.
- `Power & Battery` now places Storage Health & Speed in the left column and adds a Windows Power Plan card in the right column.
- `PowerPlanService` uses the built-in `powercfg.exe` interface to enumerate schemes, identify the active scheme, and set a selected scheme active. Native Control Panel shortcuts open Power Options and lid/power-button settings.
- Company/website branding was removed from user-facing About content, installer publisher metadata, and assembly company metadata. Legacy internal identifiers (service/data compatibility names) remain unchanged so existing upgrades, settings, logs, and service management continue to work.
- About now includes: **Made for IT technicians by an IT technician.**

## Disk-management workflow update

The DiskPart assistant now treats disk operations as separate state transitions instead of one combined action:

1. **Clean disk** returns an initialized data/removable disk to RAW.
2. **Initialize disk** changes a RAW disk to GPT or MBR only.
3. **Create partition** accepts maximum free space or a custom size in GB and formats the new partition. Repeat with custom sizes to create multiple partitions.
4. **Format selected** reformats only the selected non-system partition.
5. **Delete selected** removes only the selected non-system partition.
6. **Erase + prepare full disk** is the guarded one-click path for a previously formatted SSD that must be wiped and rebuilt as one full-size volume.

The disk enumerator now includes allocated size, largest free extent, partition count, offline/read-only state and treats the uninitialized MSFT_Disk partition-style value as the RAW state used by `Initialize-Disk`. Destructive actions still re-query the physical disk immediately before execution and block Windows boot/system disks and boot/system partitions.

## 2.0.3 follow-up

- Network Diagnostics now performs an immediate managed-device reachability refresh when the page opens and after the scanner closes, so the dashboard does not wait for the service's next scheduled ping.
- The Network Scanner also refreshes saved managed-device status on open.
- Storage Health & Speed moved from Power & Battery into Utility Tools.
- Utility Tools now includes a Storage Sense shortcut to `ms-settings:storagepolicies`.
- Release versioning is centralized in `Directory.Build.props` (`AppVersion`). Both projects inherit it and `build-release.ps1` passes the same value to the installer.


## 2.0.4 follow-up

Storage capacity verification now surfaces current, average and fastest throughput together with an ETA for the full write-and-verify workload. The running average is based on all bytes processed over total elapsed time, while fastest speed records the highest one-second throughput sample from either phase.


## 2.0.5 follow-up

- Capacity verification now has dedicated Step 1 write and Step 2 verify metric cards.
- Local IP Scanner can inspect common TCP ports on discovered hosts.
- Network Diagnostics has a dedicated troubleshooting-shortcuts card using built-in Windows commands.


## 2.0.7 follow-up

- Managed-device history now distinguishes the reachability event from the raw ping error and records a shutdown/restart inference on recovery. The wording intentionally states that the classification is inferred because agentless ping monitoring cannot prove a Windows power event.
- Added a separate `CentricDeviceMonitor.Tray` WinForms project for interactive service-health visibility. It starts at user logon because the LocalSystem service itself runs in Session 0 and cannot safely render notification-area UI.
- The tray companion reads the existing service heartbeat and uses a green/orange/red overlay dot for healthy/stale/stopped state. It hides while the main dashboard tray icon is present.
- Network Diagnostics now groups non-destructive diagnostic shortcuts separately from guarded Administrator repair shortcuts.

## 2.0.8 follow-up

- The Storage Health & Speed sequential benchmark now uses a pass-count ComboBox with 1-5 finite passes and Continuous mode; run status identifies the active pass and the aggregate summary keeps average, peak and minimum throughput across completed passes.
- System Stats now includes a fifth metric card for CPU Package Power. The current value comes from a real CPU power sensor exposed by LibreHardwareMonitor/WMI, while the dashboard tracks average and maximum wattage for the current application session.
- The LocalSystem service publishes the package-power sensor reading in `BackgroundServiceSnapshot`, allowing the dashboard to prefer the privileged service sensor path and avoid estimating power from CPU usage.

## 2.0.9 follow-up

- Managed-device editors no longer force WPF's `InputLanguage` attached property. This avoids the legacy WPF path that reconstructs the active keyboard language from a numeric LANGID and fails for Windows custom/transient `0x1000` locales.
- A culture-safe `IInputLanguageSource` is registered once at application startup before any window receives focus. It normalizes custom LCID cultures to a compatible specific input culture for WPF bookkeeping while Windows continues to own the actual keyboard layout.
- Both the dashboard editor and the standalone Network Scanner editor defer initial TextBox focus until their UI is rendered.

## 2.0.10 follow-up

- The managed-device editor now uses taller, lower-padding text fields so existing names/IP addresses are not vertically clipped into dot/dash fragments at scaled DPI.
- Editor content can scroll independently while Cancel/Save remain pinned in a dedicated bottom action row.
- The dialog is slightly larger and resizable to remain usable with Windows display/text scaling.



## 2.0.11 follow-up

- About now exposes a secondary **Terms & Conditions** action beside Close.
- A dedicated Apple-style `TermsWindow` presents the installed EULA/Terms/Disclaimer in a resizable, scrollable, read-only view.
- Interactive installer acceptance and the in-app viewer use the same `TERMS-AND-CONDITIONS.txt` source to avoid wording drift.


## 2.0.12 follow-up

System Stats now includes a hardware-inventory layer backed by `SystemHardwareInventoryService`: PC identity, RAM modules, native display modes/physical monitor size, graphics adapters/VRAM, physical disks and mounted volume usage. GPU power uses the same LibreHardwareMonitor/LocalSystem sensor pipeline as CPU package power, with dashboard-session average and peak values. Windows activation status is mirrored into System Stats with grace/trial remaining time where Windows reports it.


## 2.0.13 build correction

- Added the missing `System.IO` namespace required by the new storage-volume hardware inventory.
- Cleaned the two nullable-value warnings reported by the Windows 2.0.12 build.
- No UI or hardware inventory capability was removed.

## 2.0.14 Windows Repair & Recovery

- Utility Tools now includes an **Essential Windows Repair** card for direct elevated SFC, DISM and CHKDSK shortcuts.
- Added `WindowsRepairWindow` for online repair, selected-drive CHKDSK, offline DISM/SFC and BootRec/WinRE commands without overcrowding the main toolbox page.
- Offline image actions validate drive-letter input and warn if the selected drive does not expose a `Windows` folder.
- BCDEdit `osdevice` detection can prefill the offline Windows drive, while still requiring the technician to confirm the target.
- BootRec actions are guarded with explicit warnings. When `bootrec.exe` is unavailable in the live Windows session, the requested command is copied for use in WinRE rather than failing silently.


## 2.0.15 Network Speed & contextual use-case guidance

- Current Connection now links to a dedicated **Network Speed & Bandwidth** window.
- Offline/local diagnostics deliberately separate negotiated adapter link capacity from public internet throughput. Ethernet uses the Windows negotiated interface rate; Wi-Fi also surfaces receive/transmit link rates from `netsh wlan show interfaces`. Gateway latency and current adapter traffic provide additional local diagnostics even when internet access is unavailable.
- The optional public internet test uses Cloudflare's public speed-test endpoints only after explicit user action and reports latency/download/upload separately.
- Shortcut-heavy cards now pair each action with a concise visible use case, while tooltips keep the underlying command/purpose discoverable without overcrowding the interface.
- Dashboard card/text/icon resources were promoted to App scope so secondary windows can reuse them. This also corrects the Windows Repair & Recovery runtime StaticResource lookup failure from 2.0.14.

## 2.0.17 network speed endpoint compatibility

- Cloudflare upload measurements now use the current `POST /__up?bytes=<n>` request shape rather than the older bare `/__up` request.
- Synthetic upload content is printable generated `0` data; no local files are read or transmitted.
- Upload requests use HTTP/1.1 with `Expect: 100-continue` disabled and retry once with a minimal request shape after an HTTP 403/415 response.
- Latency and download values are pushed to the UI immediately after each phase, so a later upload-endpoint failure cannot hide already-valid results.


## 2.0.18 FAST.com + real LAN throughput

- Public internet testing now opens the official FAST.com site in the user's default browser rather than calling undocumented speed-test endpoints.
- The Network Speed & Bandwidth window includes a separate two-PC LAN throughput workflow: PC A starts a temporary TCP server; PC B runs upload/download tests against PC A's IP address.
- LAN results show latency, actual upload Mbps and actual download Mbps and work with internet disconnected.
- Duration and TCP stream count are configurable so technicians can test both modest Wi-Fi and high-speed Gigabit links.
- An optional confirmed firewall helper opens only the selected test TCP port on Windows Private/Domain profiles.
- UI guidance now explicitly distinguishes negotiated adapter link speed from measured end-to-end LAN throughput and from public ISP speed measured by FAST.com.


## 2.0.19 LAN speed-test build fix

- Explicitly aliases the Network Speed & Bandwidth window's pass/stream `ComboBox` as the WPF `System.Windows.Controls.ComboBox` to avoid the project-wide WPF/Windows Forms type collision.

## 2.0.20 network quality and control-state visuals

- Added a neutral, theme-aware **Packet loss & link quality** card with gateway targeting, compact test controls, live progress, four result metrics and a colour-coded quality label.
- Added separate upload/download progress meters to the two-PC LAN test. Both retain the existing system-blue/green palette and use an adaptive common Mbps/Gbps scale so the two directions remain visually comparable.
- Expanded the Background Monitoring card with explicit enable/start, disable/stop and restart actions plus a concise warning about which unattended functions pause when the service is disabled.
- Made the power scheduler's master state visible in both the toggle label and action button. The status surface displays **Paused** when a saved enabled schedule cannot execute because the background service is stopped.

## 2.0.21 thermal-load test visuals

- Utility Tools includes a full-width, warning-accented **CPU & GPU Thermal Load Test** card that opens the guarded diagnostic without crowding the System Stats dashboard.
- The thermal window reuses the shared neutral card system, theme-aware text/borders, rounded buttons and system-blue primary action.
- Test settings remain grouped on the left while a dark hardware-render preview makes GPU activity immediately visible on the right.
- Six compact metric cards show CPU temperature, live reported CPU clock, real package power, GPU/shared-package temperature, elapsed/remaining time and active worker/render load.
- The heat warning, required acknowledgement, manual Stop button, progress surface and automatic-stop status remain visible throughout the workflow.

## 2.0.22 thermal action-footer correction

- Moved acknowledgement plus Start/Stop/Close actions out of the vertically clipped Test settings card and into the persistent bottom row of the thermal window.
- Compacted settings spacing and arranged CPU/GPU stop-temperature sliders side by side, keeping the controls visible without changing the established card hierarchy.

## 2.0.23 integrated-GPU and live telemetry refinement

- The metric row now includes live CPU speed and CPU package power without moving the acknowledgement or Start/Stop controls out of the fixed footer.
- Integrated-only graphics show the CPU package reading as a clearly labelled shared GPU safety temperature when the platform exposes no separate integrated-GPU sensor.
- Dedicated, hybrid and unclassified graphics keep the real-GPU-sensor requirement; the visual language distinguishes a shared package reading from a dedicated GPU reading.
- The CPU slider now presents the requested 105 °C default/maximum together with an explicit thermal-limit warning.
