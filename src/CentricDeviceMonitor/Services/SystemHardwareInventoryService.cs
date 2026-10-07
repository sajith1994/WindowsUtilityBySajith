using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using CentricDeviceMonitor.Models;
using Microsoft.Win32;
using WinFormsScreen = System.Windows.Forms.Screen;

namespace CentricDeviceMonitor.Services;

public sealed class SystemHardwareInventoryService
{
    public SystemHardwareInventory Read()
    {
        try
        {
            (string manufacturer, string model, string sku) = ReadComputerSystem();
            string serial = ReadSerialNumber();
            IReadOnlyList<MemoryModuleInventoryInfo> modules = ReadMemoryModules();

            return new SystemHardwareInventory
            {
                ComputerName = Environment.MachineName,
                Manufacturer = manufacturer,
                Model = model,
                SystemSku = sku,
                SerialNumber = serial,
                MemorySlots = ReadMemorySlotCount(modules.Count),
                MemoryModules = modules,
                Displays = ReadDisplays(),
                GraphicsAdapters = ReadGraphicsAdapters(),
                PhysicalDisks = ReadPhysicalDisks(),
                Volumes = ReadVolumes(),
                UpdatedAt = DateTime.Now
            };
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("System hardware inventory", exception);
            return new SystemHardwareInventory { UpdatedAt = DateTime.Now };
        }
    }

    private static (string Manufacturer, string Model, string Sku) ReadComputerSystem()
    {
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                return (
                    Clean(item["Manufacturer"], "Not reported"),
                    Clean(item["Model"], "Not reported"),
                    Clean(item["SystemSKUNumber"], "Not reported"));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Computer manufacturer/model inventory", exception);
        }

        return ("Not reported", "Not reported", "Not reported");
    }

    private static string ReadSerialNumber()
    {
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT SerialNumber FROM Win32_BIOS");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                string serial = Clean(item["SerialNumber"], string.Empty);
                if (!string.IsNullOrWhiteSpace(serial))
                {
                    return serial;
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("BIOS serial inventory", exception);
        }

        return "Not reported";
    }

    private static IReadOnlyList<MemoryModuleInventoryInfo> ReadMemoryModules()
    {
        List<MemoryModuleInventoryInfo> modules = new();
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT DeviceLocator, BankLabel, Manufacturer, PartNumber, Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                string locator = Clean(item["DeviceLocator"], string.Empty);
                if (string.IsNullOrWhiteSpace(locator))
                {
                    locator = Clean(item["BankLabel"], "RAM module");
                }

                uint speed = ToUInt32(item["ConfiguredClockSpeed"]);
                if (speed == 0)
                {
                    speed = ToUInt32(item["Speed"]);
                }

                modules.Add(new MemoryModuleInventoryInfo(
                    locator,
                    Clean(item["Manufacturer"], "Unknown manufacturer"),
                    Clean(item["PartNumber"], "").Trim(),
                    ToUInt64(item["Capacity"]),
                    speed));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("RAM module inventory", exception);
        }

        return modules
            .OrderBy(module => module.Location, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ReadMemorySlotCount(int populatedCount)
    {
        try
        {
            int total = 0;
            using ManagementObjectSearcher searcher = new("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                total += (int)ToUInt32(item["MemoryDevices"]);
            }

            return Math.Max(total, populatedCount);
        }
        catch
        {
            return populatedCount;
        }
    }

    private static IReadOnlyList<DisplayInventoryInfo> ReadDisplays()
    {
        List<(double Inches, string Name)> physicalSizes = ReadPhysicalMonitorSizes();
        List<DisplayInventoryInfo> displays = new();
        WinFormsScreen[] screens = WinFormsScreen.AllScreens;

        for (int index = 0; index < screens.Length; index++)
        {
            WinFormsScreen screen = screens[index];
            (int width, int height, int refresh) = ReadDisplayMode(
                screen.DeviceName,
                screen.Bounds.Width,
                screen.Bounds.Height);
            string physicalSize = index < physicalSizes.Count && physicalSizes[index].Inches > 0
                ? $"{physicalSizes[index].Inches:0.#} in"
                : "size not reported";
            string monitorName = index < physicalSizes.Count && !string.IsNullOrWhiteSpace(physicalSizes[index].Name)
                ? physicalSizes[index].Name
                : $"Display {index + 1}";

            displays.Add(new DisplayInventoryInfo(
                monitorName,
                $"{width} × {height}",
                refresh > 1 ? $"{refresh} Hz" : "refresh not reported",
                physicalSize,
                screen.Primary));
        }

        return displays;
    }

    private static List<(double Inches, string Name)> ReadPhysicalMonitorSizes()
    {
        List<(double Inches, string Name)> monitors = new();
        try
        {
            Dictionary<string, string> friendlyNames = ReadMonitorFriendlyNames();
            ManagementScope scope = new(@"\\.\root\wmi");
            scope.Connect();
            using ManagementObjectSearcher searcher = new(
                scope,
                new ObjectQuery("SELECT InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize, Active FROM WmiMonitorBasicDisplayParams"));

            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                if (item["Active"] is bool active && !active)
                {
                    continue;
                }

                double widthCm = ToUInt32(item["MaxHorizontalImageSize"]);
                double heightCm = ToUInt32(item["MaxVerticalImageSize"]);
                double inches = widthCm > 0 && heightCm > 0
                    ? Math.Sqrt(widthCm * widthCm + heightCm * heightCm) / 2.54d
                    : 0;
                string instance = Clean(item["InstanceName"], string.Empty);
                string name = friendlyNames.TryGetValue(instance, out string? friendlyName) && !string.IsNullOrWhiteSpace(friendlyName)
                    ? friendlyName
                    : ExtractMonitorName(instance);
                monitors.Add((inches, name));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Physical monitor size inventory", exception);
        }

        return monitors;
    }

    private static Dictionary<string, string> ReadMonitorFriendlyNames()
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            ManagementScope scope = new(@"\\.\root\wmi");
            scope.Connect();
            using ManagementObjectSearcher searcher = new(
                scope,
                new ObjectQuery("SELECT InstanceName, UserFriendlyName, Active FROM WmiMonitorID"));
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                if (item["Active"] is bool active && !active)
                {
                    continue;
                }

                string instance = Clean(item["InstanceName"], string.Empty);
                string friendly = DecodeMonitorText(item["UserFriendlyName"]);
                if (!string.IsNullOrWhiteSpace(instance) && !string.IsNullOrWhiteSpace(friendly))
                {
                    names[instance] = friendly;
                }
            }
        }
        catch
        {
        }

        return names;
    }

    private static string DecodeMonitorText(object? value)
    {
        if (value is ushort[] words)
        {
            return new string(words.TakeWhile(word => word != 0).Select(word => (char)word).ToArray()).Trim();
        }

        if (value is byte[] bytes)
        {
            return new string(bytes.TakeWhile(item => item != 0).Select(item => (char)item).ToArray()).Trim();
        }

        return string.Empty;
    }

    private static string ExtractMonitorName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return string.Empty;
        }

        string[] parts = instanceName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : instanceName;
    }

    private static (int Width, int Height, int RefreshRate) ReadDisplayMode(
        string deviceName,
        int fallbackWidth,
        int fallbackHeight)
    {
        DevMode mode = new()
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (short)Marshal.SizeOf<DevMode>()
        };

        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
        {
            return (fallbackWidth, fallbackHeight, 0);
        }

        int width = mode.dmPelsWidth > 0 ? mode.dmPelsWidth : fallbackWidth;
        int height = mode.dmPelsHeight > 0 ? mode.dmPelsHeight : fallbackHeight;
        return (width, height, mode.dmDisplayFrequency);
    }

    private static IReadOnlyList<GraphicsAdapterInventoryInfo> ReadGraphicsAdapters()
    {
        List<GraphicsAdapterInventoryInfo> adapters = new();
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Name, AdapterRAM, DriverVersion, VideoProcessor, Status FROM Win32_VideoController");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                string name = Clean(item["Name"], "Graphics adapter");
                ulong wmiMemory = ToUInt64(item["AdapterRAM"]);

                // Source order matters. DXGI reports a native 64-bit value straight from the
                // driver and is what Task Manager shows. The registry qwMemorySize is correct
                // when present but is not always written. AdapterRAM is 32-bit and saturates at
                // exactly 4 GB, so it is the last resort.
                ulong memory = DxgiAdapterService.GetDedicatedMemoryBytes(name);
                if (memory == 0)
                {
                    memory = TryReadGpuMemoryFromRegistry(name);
                }

                if (memory == 0)
                {
                    memory = wmiMemory;
                }

                string memoryDisplay = memory > 0
                    ? $"{SystemHardwareInventoryServiceFormatting.FormatBytes(memory)} VRAM"
                    : "VRAM shared / not reported";

                adapters.Add(new GraphicsAdapterInventoryInfo(
                    name,
                    memoryDisplay,
                    Clean(item["DriverVersion"], "not reported"),
                    Clean(item["VideoProcessor"], "not reported"),
                    Clean(item["Status"], "Unknown")));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Graphics adapter inventory", exception);
        }

        return adapters;
    }

    private static ulong TryReadGpuMemoryFromRegistry(string adapterName)
    {
        const string displayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(displayClass);
            if (classKey is null)
            {
                return 0;
            }

            ulong best = 0;
            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                if (adapterKey is null)
                {
                    continue;
                }

                string description = Convert.ToString(adapterKey.GetValue("DriverDesc")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(description) ||
                    (!adapterName.Contains(description, StringComparison.OrdinalIgnoreCase) &&
                     !description.Contains(adapterName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                best = Math.Max(best, ReadAdapterMemory(adapterKey));
            }

            // The DriverDesc match can fail when WMI and the driver spell the adapter
            // differently. Rather than fall back to the 32-bit WMI value, which silently caps
            // at 4 GB, take the largest dedicated size reported by any display-class subkey.
            if (best == 0)
            {
                foreach (string subKeyName in classKey.GetSubKeyNames())
                {
                    using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                    if (adapterKey is not null)
                    {
                        best = Math.Max(best, ReadAdapterMemory(adapterKey));
                    }
                }
            }

            return best;
        }
        catch
        {
            return 0;
        }
    }

    private static IReadOnlyList<PhysicalDiskInventoryInfo> ReadPhysicalDisks()
    {
        List<PhysicalDiskInventoryInfo> disks = new();
        try
        {
            using ManagementObjectSearcher searcher = new(
                "SELECT Index, Model, InterfaceType, MediaType, Size FROM Win32_DiskDrive");
            foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
            {
                disks.Add(new PhysicalDiskInventoryInfo(
                    (int)ToUInt32(item["Index"]),
                    Clean(item["Model"], "Storage device"),
                    Clean(item["InterfaceType"], "Unknown interface"),
                    Clean(item["MediaType"], "Media type not reported"),
                    ToUInt64(item["Size"])));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Physical disk inventory", exception);
        }

        return disks.OrderBy(disk => disk.Index).ToArray();
    }

    private static IReadOnlyList<StorageVolumeInventoryInfo> ReadVolumes()
    {
        List<StorageVolumeInventoryInfo> volumes = new();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network or DriveType.NoRootDirectory)
                {
                    continue;
                }

                volumes.Add(new StorageVolumeInventoryInfo(
                    drive.Name.TrimEnd('\\'),
                    SafeDriveValue(() => drive.VolumeLabel, ""),
                    SafeDriveValue(() => drive.DriveFormat, "Unknown FS"),
                    (ulong)Math.Max(0L, drive.TotalSize),
                    (ulong)Math.Max(0L, drive.AvailableFreeSpace),
                    drive.DriveType.ToString()));
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage volume inventory", exception);
        }

        return volumes.OrderBy(volume => volume.DriveLetter, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string SafeDriveValue(Func<string> reader, string fallback)
    {
        try
        {
            string value = reader();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    private static string Clean(object? value, string fallback)
    {
        string text = Convert.ToString(value)?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static uint ToUInt32(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt32(value); }
        catch { return 0; }
    }

    private static ulong ToUInt64(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt64(value); }
        catch { return 0; }
    }

    /// <summary>
    /// Reads dedicated video memory from a display-class registry key.
    /// </summary>
    /// <remarks>
    /// Win32_VideoController.AdapterRAM is a 32-bit value, so any card with more than 4 GB is
    /// reported as exactly 4 GB - an 11 GB RTX 2080 Ti shows as 4.0 GB. qwMemorySize is the
    /// 64-bit value the driver writes and is the only reliable source. MemorySize is the older
    /// 32-bit name and carries the same cap, so it is only a last resort.
    /// </remarks>
    private static ulong ReadAdapterMemory(RegistryKey adapterKey)
    {
        ulong qword = RegistryValueToUInt64(adapterKey.GetValue("HardwareInformation.qwMemorySize"));
        if (qword > 0)
        {
            return qword;
        }

        return RegistryValueToUInt64(adapterKey.GetValue("HardwareInformation.MemorySize"));
    }

    private static ulong RegistryValueToUInt64(object? value)
    {
        try
        {
            return value switch
            {
                null => 0,
                ulong ulongValue => ulongValue,
                long longValue when longValue > 0 => (ulong)longValue,
                uint uintValue => uintValue,
                int intValue when intValue > 0 => (uint)intValue,
                byte[] bytes8 when bytes8.Length >= 8 => BitConverter.ToUInt64(bytes8, 0),
                byte[] bytes4 when bytes4.Length >= 4 => BitConverter.ToUInt32(bytes4, 0),
                _ => Convert.ToUInt64(value)
            };
        }
        catch
        {
            return 0;
        }
    }

    private const int EnumCurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }
}
