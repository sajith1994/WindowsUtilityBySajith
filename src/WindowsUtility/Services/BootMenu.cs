using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsUtility.Services;

/// <summary>A Windows entry of a boot menu ("Choose an operating system").</summary>
/// <param name="Partition">The partition it starts Windows from ("\Device\HarddiskVolume3"); null when Windows cannot find it on this PC.</param>
public sealed record BootMenuEntry(string Id, string Description, string? Partition, bool InMenu, bool IsDefault)
{
    public string DisplayText => $"{Description}{(Partition is null ? " (its disk is not in this PC)" : string.Empty)}";
}

/// <summary>
/// Reads and cleans a boot store (the boot menu Windows shows at start) through Windows' boot configuration WMI
/// provider, which, unlike bcdedit's output, reads the same in every language. A clone copies the old PC's store
/// along with the EFI partition, so its menu lists the old Windows next to the copy; on another PC that entry points
/// nowhere and Windows asks "Choose an operating system" at every start.
/// </summary>
public static class BootMenu
{
    private const string BootManagerId = "{9dea862c-5cdd-4e70-acc1-f32b344d4795}";
    private const uint WindowsLoaderType = 0x10200003;
    private const uint ApplicationDevice = 0x11000001;
    private const uint DescriptionElement = 0x12000004;
    private const uint DefaultObject = 0x23000003;
    private const uint ResumeObject = 0x23000006;
    private const uint DisplayOrder = 0x24000001;
    private const uint OsDevice = 0x21000001;
    private const uint WinPe = 0x26000022;

    /// <summary>The Windows entries of a store ("" for this PC's own), without the recovery and support ones.</summary>
    public static List<BootMenuEntry> Read(string storePath)
    {
        ManagementObject store = OpenStore(storePath);
        string? defaultId = ObjectElement(storePath, BootManagerId, DefaultObject);
        List<string> order = ObjectListElement(storePath, BootManagerId, DisplayOrder);
        List<BootMenuEntry> entries = new();
        foreach (string id in LoaderIds(store))
        {
            if (BooleanElement(storePath, id, WinPe))
            {
                continue; // a recovery or support environment, started from the menu's tools, not a Windows
            }

            string? partition = PartitionPath(storePath, id, OsDevice) ?? PartitionPath(storePath, id, ApplicationDevice);
            entries.Add(new BootMenuEntry(id, StringElement(storePath, id, DescriptionElement) ?? "Windows", partition,
                order.Contains(id, StringComparer.OrdinalIgnoreCase), string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
        }

        return entries;
    }

    /// <summary>
    /// The entries to remove from a menu so that only <paramref name="keepPartition"/>'s Windows is left of the copies:
    /// entries whose disk is not in the PC, further entries for that same Windows, and, with
    /// <paramref name="belongs"/>, entries for partitions it says are not part of the disk (a clone's store lists
    /// the old disk's Windows). The entry kept for <paramref name="keepPartition"/> is the default one if it is one
    /// of them, otherwise the first in the menu. Entries for other Windows installed on the PC stay.
    /// </summary>
    public static List<BootMenuEntry> Stale(IReadOnlyList<BootMenuEntry> entries, string keepPartition, Func<string, bool>? belongs = null)
    {
        List<BootMenuEntry> same = entries.Where(entry => SamePartition(entry.Partition, keepPartition)).ToList();
        BootMenuEntry? kept = same.FirstOrDefault(entry => entry.IsDefault) ?? same.FirstOrDefault(entry => entry.InMenu) ?? same.FirstOrDefault();
        return entries.Where(entry => entry != kept
                && (entry.Partition is null || SamePartition(entry.Partition, keepPartition) || (belongs is not null && !belongs(entry.Partition))))
            .ToList();
    }

    /// <summary>
    /// Removes entries from a store (with their hibernation-resume entries and their place in the menu) and, when the
    /// default one goes, makes <paramref name="newDefault"/> the default.
    /// </summary>
    public static void Remove(string storePath, IReadOnlyList<BootMenuEntry> entries, string? newDefault)
    {
        if (entries.Count == 0)
        {
            return;
        }

        ManagementObject store = OpenStore(storePath);
        HashSet<string> removed = entries.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> order = ObjectListElement(storePath, BootManagerId, DisplayOrder);
        SetObjectListElement(storePath, BootManagerId, DisplayOrder, order.Where(id => !removed.Contains(id)).ToArray());
        if (entries.Any(entry => entry.IsDefault) && newDefault is not null)
        {
            SetObjectElement(storePath, BootManagerId, DefaultObject, newDefault);
        }

        foreach (BootMenuEntry entry in entries)
        {
            string? resume = ObjectElement(storePath, entry.Id, ResumeObject);
            Invoke(store, "DeleteObject", ("Id", entry.Id));
            if (resume is not null)
            {
                try
                {
                    Invoke(store, "DeleteObject", ("Id", resume));
                }
                catch (Exception exception) when (exception is ManagementException or COMException)
                {
                    // Shared with an entry that stays, or already gone.
                }
            }
        }
    }

    /// <summary>Reads a store, removes its <see cref="Stale"/> entries and returns them.</summary>
    public static List<BootMenuEntry> Clean(string storePath, string keepPartition, Func<string, bool>? belongs = null)
    {
        List<BootMenuEntry> entries = Read(storePath);
        List<BootMenuEntry> stale = Stale(entries, keepPartition, belongs);
        string? kept = entries.Except(stale).FirstOrDefault(entry => SamePartition(entry.Partition, keepPartition))?.Id;
        Remove(storePath, stale, kept);
        return stale;
    }

    /// <summary>Saves a copy of a store, so the menu can be put back with "bcdedit /import".</summary>
    public static void Export(string storePath, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        Invoke(OpenStore(storePath), "ExportStore", ("File", file));
    }

    /// <summary>The kernel name of a volume ("\Device\HarddiskVolume3") from its "\\?\Volume{...}\" path, as boot stores give partitions.</summary>
    public static string? DeviceNameOf(string volumePath)
    {
        string name = volumePath.Trim().TrimEnd('\\');
        name = name.StartsWith(@"\\?\", StringComparison.Ordinal) ? name[4..] : name;
        StringBuilder target = new(1024);
        return name.Length > 0 && QueryDosDevice(name, target, target.Capacity) > 0 ? target.ToString() : null;
    }

    /// <summary>The kernel name of the volume Windows runs from.</summary>
    public static string? RunningWindowsPartition()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        StringBuilder volume = new(64);
        return GetVolumeNameForVolumeMountPoint(root, volume, volume.Capacity) ? DeviceNameOf(volume.ToString()) : null;
    }

    private static bool SamePartition(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- WMI plumbing -------------------------------------------------------------------------------------------

    private static ManagementScope Scope()
    {
        ManagementScope scope = new(@"\\.\root\WMI", new ConnectionOptions { EnablePrivileges = true, Impersonation = ImpersonationLevel.Impersonate });
        scope.Connect();
        return scope;
    }

    private static string Escape(string value) => value.Replace(@"\", @"\\").Replace("\"", "\\\"");

    private static ManagementObject OpenStore(string storePath) =>
        new(Scope(), new ManagementPath($"BcdStore.FilePath=\"{Escape(storePath)}\""), null);

    private static ManagementObject OpenObject(string storePath, string id) =>
        new(Scope(), new ManagementPath($"BcdObject.Id=\"{id}\",StoreFilePath=\"{Escape(storePath)}\""), null);

    private static ManagementBaseObject Invoke(ManagementObject target, string method, params (string Name, object Value)[] arguments)
    {
        ManagementBaseObject input = target.GetMethodParameters(method);
        foreach ((string name, object value) in arguments)
        {
            input[name] = value;
        }

        ManagementBaseObject output = target.InvokeMethod(method, input, null);
        if (output["ReturnValue"] is false)
        {
            throw new ManagementException($"The boot store refused {method}.");
        }

        return output;
    }

    private static IEnumerable<string> LoaderIds(ManagementObject store)
    {
        ManagementBaseObject output = Invoke(store, "EnumerateObjects", ("Type", WindowsLoaderType));
        return (output["Objects"] as ManagementBaseObject[] ?? Array.Empty<ManagementBaseObject>()).Select(item => (string)item["Id"]).ToList();
    }

    /// <summary>An element of an object; null when it does not have it.</summary>
    private static ManagementBaseObject? Element(string storePath, string id, uint type)
    {
        ManagementObject target = OpenObject(storePath, id);
        ManagementBaseObject input = target.GetMethodParameters("GetElement");
        input["Type"] = type;
        try
        {
            ManagementBaseObject result = target.InvokeMethod("GetElement", input, null);
            return result["ReturnValue"] is true ? result["Element"] as ManagementBaseObject : null;
        }
        catch (Exception exception) when (exception is ManagementException or COMException)
        {
            return null;
        }
    }

    private static string? StringElement(string storePath, string id, uint type) => Element(storePath, id, type)?["String"] as string;

    private static bool BooleanElement(string storePath, string id, uint type) => Element(storePath, id, type)?["Boolean"] is true;

    private static string? ObjectElement(string storePath, string id, uint type) => Element(storePath, id, type)?["Id"] as string;

    private static List<string> ObjectListElement(string storePath, string id, uint type) =>
        (Element(storePath, id, type)?["Ids"] as string[] ?? Array.Empty<string>()).ToList();

    /// <summary>The partition a device element names, as a kernel volume name; null when Windows cannot find it.</summary>
    private static string? PartitionPath(string storePath, string id, uint type)
    {
        if (Element(storePath, id, type)?["Device"] is not ManagementBaseObject device)
        {
            return null;
        }

        // BcdDevicePartitionData has the partition's Path; an unknown device (BcdDeviceUnknownData) has none.
        return device.Properties.Cast<PropertyData>().Any(property => property.Name == "Path") && device["Path"] is string { Length: > 0 } path ? path : null;
    }

    private static void SetObjectElement(string storePath, string id, uint type, string value) =>
        Invoke(OpenObject(storePath, id), "SetObjectElement", ("Type", type), ("Id", value));

    private static void SetObjectListElement(string storePath, string id, uint type, string[] values) =>
        Invoke(OpenObject(storePath, id), "SetObjectListElement", ("Type", type), ("Ids", values));

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int QueryDosDevice(string deviceName, StringBuilder targetPath, int max);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
