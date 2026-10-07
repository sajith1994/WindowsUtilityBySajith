using System.IO;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class BackgroundServiceRuntimeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task<BackgroundServiceSnapshot?> LoadAsync()
    {
        try
        {
            if (!File.Exists(SharedDataPaths.ServiceSnapshotFilePath))
            {
                return null;
            }

            await using FileStream stream = new(
                SharedDataPaths.ServiceSnapshotFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            BackgroundServiceSnapshot? snapshot =
                await JsonSerializer.DeserializeAsync<BackgroundServiceSnapshot>(stream, JsonOptions);

            if (snapshot is null)
            {
                return null;
            }

            // Older versions and partially-written/externally-edited snapshots can
            // contain explicit nulls even for properties that are non-nullable in
            // the current model. Normalize them before the WPF UI consumes them.
            snapshot.Devices ??= new List<DeviceRuntimeStatus>();
            snapshot.CpuDisplay = string.IsNullOrWhiteSpace(snapshot.CpuDisplay) ? "Not available" : snapshot.CpuDisplay;
            snapshot.CpuSensorName = string.IsNullOrWhiteSpace(snapshot.CpuSensorName) ? "Not available" : snapshot.CpuSensorName;
            snapshot.CpuSource = string.IsNullOrWhiteSpace(snapshot.CpuSource) ? "Background service" : snapshot.CpuSource;
            snapshot.CpuDiagnostic ??= string.Empty;
            snapshot.NetworkSummary = string.IsNullOrWhiteSpace(snapshot.NetworkSummary) ? "Checking..." : snapshot.NetworkSummary;
            snapshot.CpuModel = string.IsNullOrWhiteSpace(snapshot.CpuModel) ? "CPU model unavailable" : snapshot.CpuModel;
            snapshot.RamSpeedDisplay = string.IsNullOrWhiteSpace(snapshot.RamSpeedDisplay) ? "RAM speed unavailable" : snapshot.RamSpeedDisplay;

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(BackgroundServiceSnapshot snapshot)
    {
        SharedDataPaths.EnsureDirectories();
        string path = SharedDataPaths.ServiceSnapshotFilePath;
        string temporaryPath = path + ".tmp";

        await using (FileStream stream = new(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }
}
