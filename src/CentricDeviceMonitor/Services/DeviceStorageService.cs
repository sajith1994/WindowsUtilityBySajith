using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class DeviceStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string DeviceFilePath => SharedDataPaths.DevicesFilePath;

    public async Task<IReadOnlyList<DeviceEntry>> LoadAsync()
    {
        try
        {
            SharedDataPaths.EnsureDirectories();
            if (!File.Exists(DeviceFilePath))
            {
                return Array.Empty<DeviceEntry>();
            }

            await using FileStream stream = new(DeviceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            List<DeviceEntry>? devices = await JsonSerializer.DeserializeAsync<List<DeviceEntry>>(stream, JsonOptions);
            return devices is null ? Array.Empty<DeviceEntry>() : devices;
        }
        catch
        {
            return Array.Empty<DeviceEntry>();
        }
    }

    public async Task SaveAsync(IEnumerable<DeviceEntry> devices)
    {
        SharedDataPaths.EnsureDirectories();

        string temporaryPath = DeviceFilePath + ".tmp";
        await using (FileStream stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, devices.ToList(), JsonOptions);
        }

        File.Move(temporaryPath, DeviceFilePath, overwrite: true);
    }
}
