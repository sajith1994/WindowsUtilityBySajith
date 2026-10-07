# Windows Utility by Sajith 2.0.23 validation

- Utility Tools now opens a dedicated CPU/GPU thermal-load window with CPU-only, GPU-render-only and combined modes.
- The acknowledgement and Start/Stop/Close controls use a fixed footer, and both temperature controls fit side by side in the compact settings card.
- The CPU stop slider now supports and defaults to 105 °C while retaining its high-temperature warning and all existing preflight/automatic-stop safeguards.
- CPU load is cancellable, uses one long-running worker per logical processor and applies a 100 ms duty cycle for the 50% and 75% selections.
- GPU load uses a dependency-free animated WPF 3D scene and refuses to start when Windows reports software-only rendering.
- CPU load requires real CPU-specific telemetry. Dedicated/hybrid/unknown GPU configurations require a real GPU sensor; integrated-only graphics may conservatively reuse the real CPU package sensor because the integrated GPU shares that package.
- Thermal metrics include live CPU clock speed from LibreHardwareMonitor with `Win32_Processor.CurrentClockSpeed` fallback, plus real CPU package power that is never estimated.
- Start requires acknowledgement plus confirmation. Manual Stop remains available and automatic stop paths cover the configured temperature limit, missing telemetry/read failure, elapsed duration and window closure.
- Existing packet quality, generated-memory LAN throughput, background-service controls, power-scheduler behavior and disk/network safety checks are unchanged.
- Application project version: 2.0.23.
- Background service project version: 2.0.23.
- Service-status tray companion version: 2.0.23.
- Installer version: 2.0.23.
- Application manifest version: 2.0.23.0 and still requests Administrator privileges.


Static validation performed in the packaging environment:

- 2.0.16 resolves both `CS0246` publish errors for `Stream` in `NetworkSpeedTestService.cs` by importing `System.IO`.
- 2.0.13 resolves the Windows compiler errors for `DriveInfo`/`DriveType` by importing `System.IO`.
- The two CS8629 nullable warnings shown in the 2.0.12 Windows build output were also removed.

- All WPF XAML files parse as XML.
- XAML event-handler references resolve to code-behind methods.
- Main-window x:Name values are unique.
- Disk-management x:Name values are unique.
- Disk service contains explicit boot/system-disk refusal before Clean, Initialize, Create Partition, Format Partition, Delete Partition and Erase + Prepare operations.
- Partition deletion re-queries the selected partition and blocks boot/system partitions.
- System health service contains Windows/build, battery percentage, Wi-Fi signal, local IPv4 and cached HTTPS public-IP/location/ISP lookup paths.
- Public lookup uses `https://ipwho.is/` with timeout and cached/offline fallback behavior.

A final application/service/tray compile still needs to be performed on a Windows machine with the .NET 10 SDK; the packaging environment does not include dotnet/MSBuild.

- Disk-management UI exposes separate Clean, Initialize, Create, Format, Delete and Erase + Prepare actions; custom-size partitions can be repeated to build multiple partitions.

- Managed-device status refresh is triggered automatically when Network Diagnostics opens and after the scanner closes.
- Network Scanner performs an initial live refresh of saved managed devices.
- Storage Health & Speed is located in Utility Tools and the Power & Battery page no longer contains the storage diagnostics card.
- Storage Sense launches the Windows `ms-settings:storagepolicies` page.
- Release version is centralized in `Directory.Build.props` and passed to the application, background-service and tray-companion publish operations and Inno Setup.
- Capacity verification reports current, running-average and fastest sampled throughput during the active test.
- Capacity verification ETA is based on total remaining write + verify bytes and the running average throughput, so it represents time left for the full test.
- Capacity speed/ETA display values reset at the start of each new verification run.


## 2.0.5 feature checks

- Capacity progress model exposes separate write and verify current/average/fastest throughput, elapsed time and ETA.
- Capacity XAML contains independent Step 1 and Step 2 progress cards.
- Local IP Scanner contains a selected-device common TCP port scan action and open-port result column.
- Network Diagnostics contains shortcuts for ipconfig, netsh, route, arp, netstat, getmac, ping, tracert, nslookup and net use.
- Target-based command shortcuts reject shell metacharacters by accepting only valid IP addresses or DNS hostnames.

## 2.0.6 layout checks

- Storage diagnostics uses proportional health/test rows instead of a fixed 260px test row.
- Capacity Verification is wrapped in a vertical ScrollViewer so Step 1/Step 2 metrics remain accessible after resize or on lower-resolution displays.
- Write and verification metric controls remain present and connected to the existing live progress handler.


## 2.0.8 feature checks

- Storage speed-test pass selector contains exactly 1, 2, 3, 4, 5 and Continuous options.
- Finite benchmark logic stops after the selected pass count; Continuous mode requires Stop / Cancel.
- Benchmark controls are locked during a run and restored in `finally`, including the new pass selector.
- CPU Package Power uses LibreHardwareMonitor `SensorType.Power` and CPU/package-name filtering, with WMI fallback.
- Background-service snapshots include package watts, display/sensor/source/diagnostic fields and a sample timestamp.
- Dashboard session average/max only accumulate new package-power samples and reset naturally when the dashboard process starts.
- When no real package-power sensor is exposed, the UI reports `Not available` rather than estimating power from utilization.


## 2.0.7 feature checks

- Managed-device incident schema includes reachability event type/message fields and remains backward compatible with existing schema-2 incident JSONL files.
- Both LocalSystem service monitoring and dashboard fallback monitoring write the shutdown/restart inference when an outage recovers.
- Device history displays Event, Event at, recovery, downtime, status, event details and original ping result.
- A separate WinForms tray-companion project is included in the solution and release pipeline.
- Tray status is based on `CentricDeviceMonitorService` plus the existing background `status.json` heartbeat, and the tray companion is installed in the common Startup folder for interactive sessions.
- The full dashboard notification-area menu also exposes service, monitoring and heartbeat state.
- Network Diagnostics contains Release/Renew, Flush DNS, Winsock/TCP-IP Reset, ARP Clear and Full Repair Administrator shortcuts.
- Destructive/network-disruptive repair actions require explicit confirmation, with an additional RDP-session warning when applicable.

- Final static packaging pass parsed 13 XAML files, 3 project files and `Directory.Build.props` with no XML errors.
- All checked XAML event-handler bindings resolve and no duplicate `x:Name` values were found outside reusable App.xaml templates.
- Modified C# files passed lexical and balanced-delimiter checks, including the dashboard tray-status overlay and the independent tray companion.
- Both tray implementations release dynamically created Win32 icon handles through `DestroyIcon`; the dashboard alias explicitly maps its P/Invoke entry point to `DestroyIcon`.
- The service-status icon uses green (running + fresh heartbeat), orange (service running + stale heartbeat), and red (stopped/unavailable).
- The lightweight tray companion stays visible during the dashboard's delayed startup and hides only after the dashboard publishes a current-session tray-ready marker, preventing both a false gap and duplicate icons.

## 2.0.8 final packaging checks

- Parsed all 13 WPF XAML files, all 3 project files, `Directory.Build.props`, and the application manifest with no XML errors.
- New Storage Health & Speed pass selector/event references resolve; legacy Continuous toggle references are absent.
- New CPU Package Power dashboard controls resolve to code-behind fields and use the LocalSystem snapshot when fresh, with direct hardware-monitor fallback.
- Modified C# files passed balanced-delimiter/static structure checks after the final sensor sampling-order adjustment.
- Release-bearing files are synchronized to 2.0.8 / 2.0.8.0.

## 2.0.9 managed-device editor culture regression checks

- Application startup registers `SafeWpfInputLanguageSource` before constructing `MainWindow`.
- The safe source never returns LCID `0x1000` to WPF; custom/BCP-47 cultures are normalized by culture name/language with `en-US` as the final compatibility fallback.
- Neither the dashboard managed-device editor nor `DeviceEditorWindow` assigns a non-invariant `InputLanguage` to its TextBoxes.
- Both managed-device editors defer initial TextBox focus until the editor UI is rendered.
- Existing device-name and IPv4 validation/persistence code remains unchanged.
- Release-bearing files are synchronized to 2.0.9 / 2.0.9.0.


### 2.0.9 final static packaging pass

- Parsed 18 WPF/project/manifest XML files with zero XML errors.
- All checked XAML event-handler references resolve to C# methods.
- The 2.0.9 codebase contains no forced `InputLanguageManager.SetInputLanguage(...)` calls.
- The obsolete per-editor `InputLanguageCompatibilityService` workaround was removed; culture protection is registered once at application startup.
- Changed C# files passed balanced brace/parenthesis structural checks.
- Final Windows/.NET 10 compilation still needs to be run with `BUILD-INSTALLER.bat` on a Windows machine with the .NET 10 SDK.

## 2.0.10 managed-device editor layout checks

- `DeviceEditorWindow` uses a 40px text viewport with reduced vertical padding for both Device name and IPv4 address fields.
- Existing values are still assigned directly to `NameTextBox.Text` and `IpTextBox.Text`; save-time trimming/IPv4 validation is unchanged.
- Editor content is inside a vertical `ScrollViewer`, while Cancel and Save changes live in a separate non-scrolling Grid row.
- The dialog default size increased to 470x365, minimum height is 330, and resizing is enabled for DPI/text-scaling accessibility.
- The 2.0.9 culture-safe WPF input-language source remains registered globally.
- Release-bearing files are synchronized to 2.0.10 / 2.0.10.0.



## 2.0.11 terms and installer acceptance checks

- `TERMS-AND-CONDITIONS.txt` is present at the source root and is linked into the WPF project as published content.
- Inno Setup uses `LicenseFile=..\TERMS-AND-CONDITIONS.txt`; interactive installations therefore show the standard licence page and require acceptance before continuing.
- The About window includes a Terms & Conditions button that opens `TermsWindow`.
- `TermsWindow` loads the same published terms file from `AppContext.BaseDirectory`, so installer and in-app text stay synchronized.
- Release-bearing files are synchronized to 2.0.11 / 2.0.11.0.


## 2.0.12 System Stats hardware inventory checks

- System Stats contains PC name, manufacturer, model, system SKU and BIOS serial fields.
- `SystemHardwareInventoryService` reads RAM modules/slots, active displays, graphics adapters, physical disks and mounted volumes without external network calls.
- Display entries use the current native `EnumDisplaySettings` mode for resolution/Hz and `WmiMonitorBasicDisplayParams` for physical diagonal size where EDID data is exposed.
- Multiple graphics adapters and disks/volumes are presented as separate entries.
- RAM card shows populated module count and reported slot count; the inventory lists module size/speed/manufacturer/part number.
- GPU power is a real LibreHardwareMonitor/WMI Power sensor reading and is propagated through `BackgroundServiceSnapshot`; no wattage is derived from GPU utilization.
- Windows activation reports Activated, Not activated/activation required, or grace/trial status; grace remaining time is included when `GracePeriodRemaining` is reported.
- Release-bearing files are synchronized to 2.0.12 / 2.0.12.0.


## 2.0.14 Windows repair/recovery checks

- Utility Tools contains direct elevated shortcuts for SFC `/scannow`, DISM Online CheckHealth/ScanHealth/RestoreHealth and CHKDSK `/f /r`.
- `WindowsRepairWindow` exposes the same online repair tools plus offline DISM/SFC, selected-drive CHKDSK and BootRec/WinRE commands.
- Offline drive input is restricted to a single drive-letter form such as `C:` or `D:` before it is interpolated into a command.
- BCDEdit OS-device detection parses only `partition=X:` drive-letter output and does not execute user-controlled command text.
- Offline repair warns when the selected volume has no detectable `Windows` directory; the full offline sequence and CHKDSK require explicit confirmation.
- BootRec changes require explicit confirmation when `bootrec.exe` is present. If BootRec is unavailable in the current session, the shortcut copies the fixed command text for use in WinRE instead of attempting an invalid desktop execution.
- Release-bearing files are synchronized to 2.0.14 / 2.0.14.0.

### 2.0.14 final static packaging pass

- Parsed 20 WPF/project/manifest XML files successfully after adding `WindowsRepairWindow`.
- Checked 174 XAML event-handler bindings across the WPF project; all referenced handlers resolve in code-behind.
- MainWindow and WindowsRepairWindow contain no duplicate `x:Name` values.
- New repair commands are fixed application-defined strings. The only user-controlled command component is the offline/CHKDSK drive letter, which is restricted to `^[A-Za-z]:$` before use.
- The packaging environment still does not contain the .NET 10 SDK/MSBuild, so the final compiler/publish check must be run with `BUILD-INSTALLER.bat` on Windows.


## 2.0.15 network speed, shortcut guidance and repair-window checks

- Current Connection contains a **Speed & bandwidth test** action and a live local **Link bandwidth** summary.
- `NetworkSpeedTestService` selects a preferred active physical adapter, reports negotiated Ethernet/Wi-Fi link rate without requiring public internet, parses Wi-Fi receive/transmit rates/signal/radio/channel from `netsh wlan show interfaces`, measures optional gateway latency and samples current adapter traffic counters.
- Internet throughput runs only when requested and uses HTTPS Cloudflare speed-test endpoints for latency plus multi-stream download/upload measurements. Local file contents are never used as the upload payload.
- Network Troubleshooting, Network Repair, Windows Shortcuts and Essential Windows Repair expose visible one-line use-case guidance plus command/action tooltips.
- Shared `CardStyle`, card text styles, info styles and `IconGlyphStyle` are available at Application scope, resolving the 2.0.14 `WindowsRepairWindow` `StaticResourceExtension` runtime failure.
- Terms/third-party notices disclose the optional Cloudflare speed test and public-IP/request metadata exposure.
- Release-bearing files are synchronized to 2.0.15 / 2.0.15.0.

### 2.0.15 final static packaging pass

- Parsed 21 WPF/project/props/manifest XML files with zero XML errors after adding `NetworkSpeedTestWindow`.
- Checked 179 XAML event-handler bindings; every referenced handler resolves in the corresponding code-behind.
- `WindowsRepairWindow` and `NetworkSpeedTestWindow` resolve all of their shared `StaticResource` style keys from `App.xaml`.
- No duplicate `x:Name` values are present outside reusable App.xaml control templates.
- The Current Connection refresh path requests only local link metadata (no automatic Cloudflare speed test); public throughput testing requires the user to open the speed window and explicitly start the test.
- Local-link reporting remains available when the speed-test service is unavailable, including negotiated adapter rate, Wi-Fi link rates where reported, gateway latency and sampled adapter traffic.
- Final Windows/.NET 10 compilation still needs to be run with `BUILD-INSTALLER.bat` on a Windows machine because this packaging environment does not contain dotnet/MSBuild.

## 2.0.17 network speed 403 compatibility checks

- Current Cloudflare upload URL includes `bytes=<payload-size>` on every upload request.
- Synthetic upload payload is filled with printable ASCII `0` bytes rather than binary NUL bytes.
- Primary upload request is HTTP/1.1, disables `Expect: 100-continue`, and uses `text/plain; charset=UTF-8`.
- HTTP 403/415 triggers one retry using the minimal `POST /__up?bytes=<n>` request form without the optional content type/cache-buster.
- The speed-test progress model carries completed latency/download/upload values; the UI updates those cards immediately instead of waiting for the entire test to finish.
- 21 WPF/project/props/manifest XML files parsed successfully in the packaging environment.
- Modified network-speed C# files have balanced brace/parenthesis counts in the static packaging pass.
- Final Windows/.NET 10 compilation must still be run with `BUILD-INSTALLER.bat` because this environment does not contain dotnet/MSBuild.


## 2.0.18 FAST.com and LAN throughput checks

- `NetworkSpeedTestService` no longer contacts any third-party speed-test endpoint during local-link refresh; it only checks Windows network-path/default-gateway state and contains no Cloudflare `__down`/`__up` endpoints.
- `OpenFastComButton_Click` launches `https://fast.com/` through the default browser instead of emulating an undocumented API.
- `LanThroughputService` implements a temporary TCP server and client protocol using generated memory buffers only; no filesystem read path exists in the LAN data loop.
- Upload tests measure bytes received by the server after the client half-closes its send side; download tests measure bytes received by the client from the server.
- Multiple TCP streams are aggregated using a common wall-clock duration to better exercise Gigabit Ethernet and modern Wi-Fi links.
- The LAN test works independently of the default gateway/internet availability; only IP reachability to the peer PC is required.
- The Network Speed window explains the difference between negotiated link rate, FAST.com ISP speed and actual PC-to-PC LAN throughput.
- Terms & Conditions and third-party notices were updated from Cloudflare to FAST.com/Netflix plus local generated LAN traffic.
- Release-bearing files are synchronized to 2.0.19 / 2.0.19.0.

## 2.0.19 LAN speed-test build-fix checks

- `NetworkSpeedTestWindow.xaml.cs` aliases `System.Windows.Controls.ComboBox` as `WpfComboBox`.
- `ReadComboTag` explicitly accepts `WpfComboBox`, eliminating the `CS0104` collision with `System.Windows.Forms.ComboBox`.
- A source scan confirms the remaining explicit `ComboBox` usages in the WPF application are also WPF-qualified/aliased.

## 2.0.20 packet quality, LAN meter and service-control checks

- Final static packaging pass parsed all 21 XAML/project/props/manifest XML files successfully, resolved 174 unique code-behind event handlers, found no duplicate `x:Name` values in concrete windows, confirmed balanced delimiters in every changed C# file and verified synchronized 2.0.20 release values.
- All 11 `StaticResource` keys used by Network Speed & Bandwidth resolve at application scope; all 22 new packet/meter fields and five background-service/scheduler controls are declared once and wired from code-behind.
- `NetworkQualityTestService` validates non-empty targets, 5-200 packets and 100-5000 ms intervals; the UI exposes only the documented safe presets.
- Per-packet failures are counted as loss while cancellation propagates immediately; a complete no-response run is distinguished from poor-quality successful replies.
- Jitter is calculated as the mean absolute difference between consecutive successful round-trip samples.
- Live LAN transfer counters use `Interlocked` aggregation across all selected TCP streams. The final Mbps result still uses the existing server-received/client-received totals and common wall clock.
- Meter values are capped visually at the active scale and the scale expands through common 10/100/1000/2500/5000/10000+ Mbps steps when required.
- Disabling the LocalSystem service stops it before changing startup to Disabled; enabling sets Automatic plus `DelayedAutostart=1` before starting it.
- Saved application settings, managed devices, logs, service name, installer AppId and `%PROGRAMDATA%\CentricDeviceMonitor` paths are unchanged.
- A real application/service/tray compile and installer build still needs to be run on Windows with the .NET 10 SDK and Inno Setup 6 because the packaging environment contains neither dotnet/MSBuild nor Windows.

## 2.0.21 CPU/GPU thermal-load safety checks

- Static packaging validation parses all 22 WPF/project/props/manifest XML files successfully and finds no duplicate `x:Name` values in concrete windows.
- Checked all 198 XAML event bindings covered by the validation scan; all 161 unique referenced handler names resolve in their corresponding code-behind files.
- `ThermalLoadTestWindow` contains finite 30/60/120/300-second duration options and 50%/75%/100% load options; there is no continuous or unattended selection. User-adjustable safety limits are capped at 105 °C for CPU and GPU.
- CPU and GPU start preflight requires relevant real temperature values below the configured limits. CPU additionally requires `IsCpuSpecific`; GPU additionally requires WPF hardware rendering tier 1 or 2.
- The monitor loop stops active loads on CPU/GPU limit, missing telemetry or sensor-read failure. The duration path, manual Stop action and window-closing disposal all cancel the workload.
- Close-time guards also prevent an in-flight sensor preflight or failed-start continuation from showing another prompt or starting load workers after the thermal window has closed.
- CPU worker calculations publish their result to an instance field so the JIT cannot remove the intentional work; cancellation is checked inside every duty cycle.
- GPU temperature candidates are restricted to GPU hardware/identifiers and plausible 0-130 °C values. Hotspot/junction is preferred for conservative safety monitoring, followed by core and memory sensors.
- The WPF GPU scene shares frozen mesh/material resources, animates model transforms through `CompositionTarget.Rendering`, and removes the scene/event subscription on Stop or Dispose.
- No new NuGet dependency or external stress-test executable was added.
- Terms, About, README, changelog, installer fallback, centralized build version and application manifest are synchronized to 2.0.21 / 2.0.21.0.
- This Linux packaging environment does not contain dotnet/MSBuild, Windows WPF/Direct3D or Inno Setup. A real Windows `.NET 10` compile, publish, installer build and hands-on thermal safety test remain required before production deployment.

## 2.0.22 thermal-layout correction and CPU-limit checks

- The supplied 965 × 778 screenshot showed that the 2.0.21 settings card was clipped after the CPU slider in the available window height, hiding the acknowledgement and Start/Stop controls.
- `AcknowledgeRiskCheckBox`, `StartTestButton`, `StopTestButton` and `CloseButton` now live in the window's fixed final grid row, outside the height-constrained settings card.
- The settings card overrides padding to 16, reduces vertical gaps and places the CPU/GPU temperature controls in two columns; the existing control names and handlers are preserved.
- Static layout assertions confirm the fixed footer row contains all four action controls and that none remain nested inside the settings-card border.
- `CpuLimitSlider` has `Minimum="70"`, `Maximum="105"` and `Value="105"`; its initial label is 105 °C. The GPU range remains 65-105 °C with its 90 °C default.
- The footer acknowledgement explicitly warns that 105 °C can be near the CPU thermal limit, while start acknowledgement, Yes/No confirmation, real CPU-specific sensor preflight, selected-limit stop, telemetry-loss stop, finite duration and close-time cancellation remain unchanged.
- Release-bearing files are synchronized to 2.0.22 / 2.0.22.0.
- A real 768-pixel-high Windows visual check plus `.NET 10` application/service/tray compile and Inno Setup build remain required because this packaging environment does not provide Windows WPF, dotnet/MSBuild or Inno Setup.

## 2.0.23 live telemetry and integrated-GPU checks

- The thermal metric strip contains six unique controls for CPU temperature, CPU speed, CPU package power, GPU/shared temperature, elapsed time and load state.
- CPU clock acquisition scans only CPU `SensorType.Clock` values, excludes bus/reference/memory/uncore/fabric clocks, averages the best available live core-clock group and uses `Win32_Processor.CurrentClockSpeed` only as a labelled fallback.
- CPU package power reuses the existing real `SensorType.Power`/WMI package path; no utilization-based or TDP-based estimate was added.
- GPU shared-temperature fallback requires all detected non-software display adapters to be conservatively classified as integrated. Intel Arc, NVIDIA, Radeon RX/Pro, hybrid and unknown adapters cannot enter the shared-sensor path.
- The shared path additionally requires an available CPU-specific temperature reading. It is used in GPU preflight, live display, peak tracking, telemetry-loss handling and the selected GPU-limit automatic stop.
- WPF software rendering remains blocked for every GPU load. Dedicated/hybrid/unknown configurations without a real GPU temperature remain blocked.
- Release-bearing values are synchronized to 2.0.23 / 2.0.23.0.
- The packaging environment still lacks Windows, the .NET 10 SDK, MSBuild and Inno Setup. A Windows compile plus hands-on integrated-GPU and discrete/hybrid safety test remain required.
