# .NET 10 package restore notes

Both projects now target `net10.0-windows`. Build with the .NET 10 SDK; do not use the .NET 8 SDK for version 1.8.2. The repository `global.json` keeps SDK selection on .NET 10.


The WPF application keeps the package alignment introduced in version 1.3.1:

```xml
<PackageReference Include="LibreHardwareMonitorLib" Version="0.9.7-pre708" />
<PackageReference Include="System.Management" Version="10.0.9" />
<PackageReference Include="Hwinfo.SharedMemory.Net" Version="2.1.0" />
```

Version 1.8.2 includes a Windows Service project with the same hardware-monitoring dependencies plus:

```xml
<PackageReference Include="System.ServiceProcess.ServiceController" Version="10.0.2" />
```

The build script deletes `bin` and `obj` for both projects and restores each project with `--force-evaluate` before publishing.

If a restore error occurs after modifying package versions, close Visual Studio, delete the `bin` and `obj` directories under both project folders, then run `BUILD-INSTALLER.bat` again.
