using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility;

/// <summary>System Stats: live disk and network activity tiles (like Task Manager's Performance page).</summary>
public partial class MainWindow
{
    private readonly ActivityMonitor _activityMonitor = new();

    private sealed record DiskActivityRow(string Name, double ActivePercent, string PercentText, string Detail);

    private sealed record NetworkActivityRow(string Name, string Rates, string Detail);

    /// <summary>Called with the hardware refresh (every 3 seconds); the first call only primes the counters.</summary>
    private void RefreshActivityTiles()
    {
        ActivitySample sample;
        try
        {
            sample = _activityMonitor.Sample();
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Disk/network activity", exception);
            return;
        }

        if (sample.Disks.Count > 0)
        {
            DiskActivityInfo busiest = sample.Disks.MaxBy(disk => disk.ActivePercent)!;
            DiskActivityHeadlineText.Text = $"{busiest.ActivePercent:0}%";
            DiskActivitySummaryText.Text = sample.Disks.Count == 1
                ? "Active time (how busy the disk is)"
                : $"Busiest: {busiest.Name}. Active time is how busy each disk is.";
            DiskActivityList.ItemsSource = sample.Disks.Select(disk => new DiskActivityRow(
                disk.Name,
                disk.ActivePercent,
                $"{disk.ActivePercent:0}%",
                $"Read {ActivityMonitor.FormatBytesPerSecond(disk.ReadBytesPerSec)}  •  write {ActivityMonitor.FormatBytesPerSecond(disk.WriteBytesPerSec)}")).ToList();
        }

        if (sample.Adapters.Count > 0)
        {
            double receive = sample.Adapters.Sum(adapter => adapter.ReceiveBitsPerSec);
            double send = sample.Adapters.Sum(adapter => adapter.SendBitsPerSec);
            NetworkActivityHeadlineText.Text = $"↓ {ActivityMonitor.FormatBitsPerSecond(receive)}   ↑ {ActivityMonitor.FormatBitsPerSecond(send)}";
            NetworkActivitySummaryText.Text = "Receive and send rate over all connected adapters.";
            NetworkActivityList.ItemsSource = sample.Adapters.Select(adapter => new NetworkActivityRow(
                adapter.Name,
                $"↓ {ActivityMonitor.FormatBitsPerSecond(adapter.ReceiveBitsPerSec)}  ↑ {ActivityMonitor.FormatBitsPerSecond(adapter.SendBitsPerSec)}",
                adapter.LinkBitsPerSec > 0 ? $"{adapter.Kind}  •  {ActivityMonitor.FormatBitsPerSecond(adapter.LinkBitsPerSec)} link" : adapter.Kind)).ToList();
        }
        else if (DiskActivityHeadlineText.Text != "--%")
        {
            NetworkActivityHeadlineText.Text = "Not connected";
            NetworkActivitySummaryText.Text = "No Ethernet, Wi-Fi or mobile broadband connection is active.";
            NetworkActivityList.ItemsSource = null;
        }
    }
}
