using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using Xunit;

namespace CentricDeviceMonitor.Tests;

public class TileAndReportTests
{
    private static RemotePcView View(string name, Action<RemoteStatsPayload>? change = null, bool online = true)
    {
        RemoteStatsPayload stats = new()
        {
            MachineName = name,
            ReadingsFresh = true,
            AppVersion = UpdateService.GetCurrentVersion().ToString(3),
            CpuTemperatureIsCpuSpecific = true,
            CpuTemperatureCelsius = 50,
            CpuTemperatureAlertThresholdCelsius = 90,
            RamUsagePercent = 40,
            TotalMemoryBytes = 16UL << 30,
            Disks = { new RemoteDiskInfo { Name = "C:", TotalBytes = 200L << 30, FreeBytes = 100L << 30 } }
        };
        change?.Invoke(stats);
        RemotePcView view = new(new RemotePcEntry { DisplayName = name, Host = "10.0.0.9" });
        view.Update(new RemoteStatsResult(RemoteStatsState.Online, stats, "Online"));
        if (!online)
        {
            view.Update(new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline"));
        }

        return view;
    }

    [Fact]
    public void Tile_ShowsOnlineWhenAllIsWell()
    {
        RemotePcView view = View("PC1");
        Assert.Equal("Online", view.StatusKind);
        Assert.Equal("Online", view.StatusLabel);
    }

    [Fact]
    public void Tile_IsAmberWhenThePcsReadingsAreStale()
    {
        RemotePcView view = View("PC1", stats => stats.ReadingsFresh = false);
        Assert.Equal("Warning", view.StatusKind);
    }

    [Fact]
    public void Tile_ShowsHotAboveThatPcsOwnThreshold()
    {
        RemotePcView view = View("PC1", stats => stats.CpuTemperatureCelsius = 91);
        Assert.Equal(("Warning", "Hot"), (view.StatusKind, view.StatusLabel));
    }

    [Fact]
    public void Tile_ShowsDiskLowUnderTenPercentFree()
    {
        RemotePcView view = View("PC1", stats => stats.Disks[0].FreeBytes = 10L << 30);
        Assert.Equal(("Warning", "Disk low"), (view.StatusKind, view.StatusLabel));
    }

    [Fact]
    public void Tile_ShowsAtRiskWhenTheFirewallIsOff()
    {
        RemotePcView view = View("PC1", stats => stats.Security = new RemoteSecurityInfo { AntivirusOn = true, FirewallOn = false });
        Assert.Equal(("Warning", "At risk"), (view.StatusKind, view.StatusLabel));
        Assert.True(view.ProtectionAtRisk);
    }

    [Fact]
    public void Tile_OffersUpdateOnlyForOlderVersions()
    {
        Assert.False(View("PC1").CanUpdate);
        Assert.True(View("PC2", stats => stats.AppVersion = "2.0.1").CanUpdate);
    }

    [Fact]
    public void Tile_MatchesDiskActivityToTheDriveLetter()
    {
        RemotePcView view = View("PC1", stats => stats.DiskActivity.Add(new DiskActivityInfo { Name = "Disk 0 (C: D:)", Letters = "C: D:", ActivePercent = 12 }));
        Assert.True(view.Disks.Single().HasActivity);
        Assert.Equal(12, view.Disks.Single().ActivePercent);
    }

    [Fact]
    public void Tile_KeepsTheLastReadingsWhileOffline()
    {
        RemotePcView view = View("PC1", online: false);
        Assert.False(view.IsOnline);
        Assert.Null(view.Stats);
        Assert.Equal("PC1", view.LastKnownStats?.MachineName);
    }

    [Fact]
    public void Csv_QuotesCommasAndQuotesAndNeutralisesFormulas()
    {
        string csv = InventoryReportService.BuildCsv(new[]
        {
            View("Workshop, \"bench\""),
            View("=HYPERLINK(\"x\")")
        });
        Assert.Contains("\"Workshop, \"\"bench\"\"\"", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
    }

    [Fact]
    public void Csv_ListsOfflinePcsWithTheirLastReadings()
    {
        string csv = InventoryReportService.BuildCsv(new[] { View("NB-002", online: false) });
        string row = csv.Split(Environment.NewLine)[1];
        Assert.StartsWith("NB-002,Offline,", row);
        Assert.Contains("C: 100 GB free of 200 GB", row);
    }

    [Fact]
    public void Html_EncodesNamesAndCountsProblems()
    {
        string html = InventoryReportService.BuildHtml(new[]
        {
            View("<script>", stats => stats.Security = new RemoteSecurityInfo { AntivirusOn = false }),
            View("PC2")
        }, "TEST");
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<b>1</b><span>at risk (antivirus or firewall off)</span>", html);
    }

    [Theory]
    [InlineData(512, "512 B/s")]
    [InlineData(2048, "2 KB/s")]
    [InlineData(3 * 1024 * 1024, "3.0 MB/s")]
    public void ActivityMonitor_FormatsTransferRates(double bytes, string expected) =>
        Assert.Equal(expected, ActivityMonitor.FormatBytesPerSecond(bytes));

    [Theory]
    [InlineData(800_000, "800 Kbps")]
    [InlineData(2_300_000, "2.3 Mbps")]
    [InlineData(1_000_000_000, "1.0 Gbps")]
    public void ActivityMonitor_FormatsNetworkRates(double bits, string expected) =>
        Assert.Equal(expected, ActivityMonitor.FormatBitsPerSecond(bits));
}
