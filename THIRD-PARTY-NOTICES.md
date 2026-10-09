# Third-party notices

## LibreHardwareMonitorLib

This application references LibreHardwareMonitorLib 0.9.7-pre708 for CPU sensor readings.

Project: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor
License: Mozilla Public License 2.0
License text: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LICENSE

The library may include additional third-party components and notices. Review the package and upstream THIRD-PARTY-NOTICES file before commercial redistribution.

## System.Management

This application references the Microsoft System.Management package 10.0.9 for Windows Management Instrumentation sensor fallbacks.

Project: https://github.com/dotnet/runtime
License: MIT

## Hwinfo.SharedMemory.Net

This application references Hwinfo.SharedMemory.Net 2.1.0 to read sensor values published by HWiNFO through local shared memory.

Project: https://github.com/Seraksab/Hwinfo.SharedMemory.Net
License: MIT

HWiNFO itself is optional external software and is not distributed with Windows Utility. HWiNFO licensing terms apply separately.

## System.ServiceProcess.ServiceController

The background Windows Service references the Microsoft System.ServiceProcess.ServiceController package 10.0.2 for the .NET ServiceBase implementation.

Project: https://github.com/dotnet/runtime
License: MIT


## PawnIO

The Windows installer bundles the official signed PawnIO 2.2.0 setup program and installs it silently when a compatible/newer PawnIO version is not already present. PawnIO provides the low-level hardware access used by current LibreHardwareMonitor builds.

Project: https://github.com/namazso/PawnIO
Official setup releases: https://github.com/namazso/PawnIO.Setup/releases
Website: https://pawnio.eu/

PawnIO is distributed under its upstream license and special exception. Review the upstream license before commercial redistribution. Windows Utility does not uninstall PawnIO automatically because other installed applications may also depend on it.

## Optional external technician tools

Windows Utility can open the official installer/launcher commands for Chris Titus Tech WinUtil, Raphire Win11Debloat and Winhance. These tools are not bundled with, copied into, or redistributed by Windows Utility. Their upstream project pages, licenses and terms apply separately.

- Chris Titus Tech WinUtil: https://github.com/ChrisTitusTech/winutil
- Raphire Win11Debloat: https://github.com/Raphire/Win11Debloat
- Winhance: https://github.com/memstechtips/Winhance

The application checks the official project README files at startup to refresh the currently documented HTTPS launch endpoints before offering to run them.

## Public IP and geolocation lookup

The System health tile can query `https://ipwho.is/` over HTTPS to display the current public IP address, approximate IP-based location and ISP/ASN information. The request necessarily reveals the connection's public IP address to that service. Results are cached locally in memory for five minutes and the dashboard falls back gracefully when the lookup is unavailable.

Service: https://ipwho.is/
Documentation: https://ipwhois.io/docs

## FAST.com / Netflix internet speed test

The Network Speed & Bandwidth window launches the official `https://fast.com/` website in the user's default browser for public internet speed testing. FAST.com performs its own downloads/uploads to Netflix servers and can show download speed, upload speed and connection latency. Windows Utility does not use an undocumented FAST.com API and does not scrape or store the browser test results. Normal browser/network metadata, including the public IP address, is necessarily visible to FAST.com/Netflix while the site is used.

Service: https://fast.com/

