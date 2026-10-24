using WindowsUtility.Services;
using Xunit;

namespace WindowsUtility.Tests;

public sealed class BootMenuTests
{
    private const string OldPc = @"\Device\HarddiskVolume3";
    private const string Clone = @"\Device\HarddiskVolume181";

    [Fact]
    public void AClonesStore_KeepsOnlyTheCopiedWindows()
    {
        // As read from a fresh clone's EFI partition: the old PC's entry came along, bcdboot added the clone's as default.
        BootMenuEntry old = new("{01f7efff}", "Windows 11", OldPc, InMenu: true, IsDefault: false);
        BootMenuEntry copy = new("{1e3d8ef0}", "Windows 11", Clone, InMenu: true, IsDefault: true);
        HashSet<string> onTheNewDisk = new(StringComparer.OrdinalIgnoreCase) { @"\Device\HarddiskVolume180", Clone };

        Assert.Equal(new[] { old }, BootMenu.Stale(new[] { old, copy }, Clone, onTheNewDisk.Contains));
    }

    [Fact]
    public void StartedFromTheClone_TheEntryForAMissingDiskGoes()
    {
        // In the PC the clone was moved to: the old PC's entry points to a partition that is not there.
        BootMenuEntry old = new("{01f7efff}", "Windows 11", null, InMenu: true, IsDefault: false);
        BootMenuEntry running = new("{1e3d8ef0}", "Windows 11", @"\Device\HarddiskVolume5", InMenu: true, IsDefault: true);
        BootMenuEntry otherWindows = new("{aaaa}", "Windows 10", @"\Device\HarddiskVolume9", InMenu: true, IsDefault: false);

        Assert.Equal(new[] { old }, BootMenu.Stale(new[] { old, running, otherWindows }, @"\device\harddiskvolume5"));
    }

    [Fact]
    public void TwoEntriesForTheRunningWindows_TheDefaultOneStays()
    {
        BootMenuEntry first = new("{1}", "Windows 11", OldPc, InMenu: true, IsDefault: false);
        BootMenuEntry second = new("{2}", "Windows 11", OldPc, InMenu: true, IsDefault: true);

        Assert.Equal(new[] { first }, BootMenu.Stale(new[] { first, second }, OldPc));
        Assert.Empty(BootMenu.Stale(new[] { second }, OldPc));
    }
}
