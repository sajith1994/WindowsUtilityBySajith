using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public class RemoteAlertTrackerTests
{
    private readonly RemoteAlertTracker _tracker = new();
    private readonly AppSettings _settings = new();
    private readonly RemotePcEntry _pc = new() { Host = "10.0.0.5" };
    private readonly DateTime _start = new(2026, 10, 20, 12, 0, 0);

    private static RemoteStatsResult Online(double temperature = 50, double ram = 40, long freeGb = 200, RemoteSecurityInfo? security = null) =>
        new(RemoteStatsState.Online, new RemoteStatsPayload
        {
            MachineName = "WORKSHOP",
            CpuTemperatureIsCpuSpecific = true,
            CpuTemperatureCelsius = temperature,
            CpuTemperatureAlertThresholdCelsius = 90,
            RamUsagePercent = ram,
            TotalMemoryBytes = 16UL << 30,
            AvailableMemoryBytes = 1UL << 30,
            Disks = { new RemoteDiskInfo { Name = "C:", TotalBytes = 200L << 30, FreeBytes = freeGb << 30 } },
            Security = security
        }, "Online");

    private static readonly RemoteStatsResult Offline = new(RemoteStatsState.Unreachable, null, "Offline");

    private List<string> At(int seconds, RemoteStatsResult result) =>
        _tracker.Evaluate(_pc, "Workshop", result, _settings, _start.AddSeconds(seconds)).Select(alert => alert.Title).ToList();

    [Fact]
    public void Overheating_IsAnnouncedOnlyAfterAMinuteAndOnlyOnce()
    {
        Assert.Empty(At(0, Online(temperature: 95)));
        Assert.Empty(At(30, Online(temperature: 95)));
        Assert.Equal(new[] { "Workshop is overheating" }, At(61, Online(temperature: 95)));
        Assert.Empty(At(120, Online(temperature: 95)));
    }

    [Fact]
    public void Overheating_IsAnnouncedAgainAfterItCleared()
    {
        At(0, Online(temperature: 95));
        At(61, Online(temperature: 95));
        Assert.Empty(At(90, Online(temperature: 60)));
        At(100, Online(temperature: 95));
        Assert.Equal(new[] { "Workshop is overheating" }, At(161, Online(temperature: 95)));
    }

    [Fact]
    public void ADriveGoingFromNearlyFullToFull_AnnouncesEachLevelOnce()
    {
        Assert.Equal(new[] { "Workshop: drive C: is nearly full" }, At(0, Online(freeGb: 15)));
        Assert.Equal(new[] { "Workshop: drive C: is full" }, At(30, Online(freeGb: 1)));
        Assert.Empty(At(60, Online(freeGb: 15)));
    }

    [Fact]
    public void AFullDrive_IsNotAlsoAnnouncedAsNearlyFull()
    {
        Assert.Equal(new[] { "Workshop: drive C: is full" }, At(0, Online(freeGb: 1)));
    }

    [Fact]
    public void Offline_IsOnlyAnnouncedForPcsSeenOnlineAndAfterAMinute_ThenBackOnline()
    {
        Assert.Empty(At(0, Offline));
        Assert.Empty(At(90, Offline));
        At(100, Online());
        Assert.Empty(At(130, Offline));
        Assert.Equal(new[] { "Workshop is offline" }, At(191, Offline));
        Assert.Equal(new[] { "Workshop is back online" }, At(220, Online()));
    }

    [Fact]
    public void AProblemThatStaysWhileOffline_IsNotAnnouncedAgainWhenThePcReturns()
    {
        At(0, Online(freeGb: 1));
        At(30, Offline);
        At(91, Offline);
        Assert.Equal(new[] { "Workshop is back online" }, At(120, Online(freeGb: 1)));
    }

    [Fact]
    public void SecurityProblems_AreAnnouncedAndCanBeTurnedOff()
    {
        RemoteSecurityInfo risky = new()
        {
            AntivirusName = "Microsoft Defender",
            AntivirusOn = true,
            RealTimeProtectionOn = false,
            FirewallOn = false,
            FirewallOffProfiles = "Public",
            LastUpdateInstalledUtc = DateTime.UtcNow.AddDays(-45)
        };
        Assert.Equal(3, At(0, Online(security: risky)).Count);

        RemoteAlertTracker quiet = new();
        _settings.RemoteAlertSecurity = false;
        Assert.Empty(quiet.Evaluate(_pc, "Workshop", Online(security: risky), _settings, _start));
    }

    [Fact]
    public void AWrongCode_KeepsTheKnownStateAndRaisesNothing()
    {
        At(0, Online(freeGb: 1));
        Assert.Empty(At(30, new RemoteStatsResult(RemoteStatsState.WrongCode, null, "Wrong pairing code.")));
        Assert.Empty(At(60, Online(freeGb: 1)));
    }
}
