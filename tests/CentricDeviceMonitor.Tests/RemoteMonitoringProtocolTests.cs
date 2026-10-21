using CentricDeviceMonitor.Models;
using Xunit;

namespace CentricDeviceMonitor.Tests;

public class RemoteMonitoringProtocolTests
{
    private const string Path = RemoteMonitoringProtocol.StatsPath;

    [Theory]
    [InlineData("office-2026 hq", "OFFICE2026HQ")]
    [InlineData("  Abcd-Efgh-Jklm ", "ABCDEFGHJKLM")]
    [InlineData(null, "")]
    public void NormalizeCode_IgnoresCaseSpacesAndDashes(string? code, string expected) =>
        Assert.Equal(expected, RemoteMonitoringProtocol.NormalizeCode(code));

    [Fact]
    public void NewPairingCode_IsTwelveUnambiguousCharactersInGroupsOfFour()
    {
        string code = RemoteMonitoringProtocol.NewPairingCode();
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code);
    }

    [Fact]
    public void Verify_AcceptsTheSameCodeTypedDifferently()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = RemoteMonitoringProtocol.Sign("OFFICE-2026-HQ", now, Path);
        Assert.True(RemoteMonitoringProtocol.Verify("office 2026 hq", now.ToString(), signature, Path, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Verify_RejectsAWrongCode()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = RemoteMonitoringProtocol.Sign("OFFICE-2026-HQ", now, Path);
        Assert.False(RemoteMonitoringProtocol.Verify("OFFICE-2026-HX", now.ToString(), signature, Path, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Verify_RejectsASignatureForAnotherPath()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string signature = RemoteMonitoringProtocol.Sign("OFFICE-2026-HQ", now, RemoteMonitoringProtocol.HelloPath);
        Assert.False(RemoteMonitoringProtocol.Verify("OFFICE-2026-HQ", now.ToString(), signature, Path, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(-600, false)]
    [InlineData(-240, true)]
    [InlineData(240, true)]
    [InlineData(600, false)]
    public void Verify_OnlyAcceptsRequestsWithinFiveMinutes(int offsetSeconds, bool accepted)
    {
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds;
        string signature = RemoteMonitoringProtocol.Sign("OFFICE-2026-HQ", time, Path);
        Assert.Equal(accepted, RemoteMonitoringProtocol.Verify("OFFICE-2026-HQ", time.ToString(), signature, Path, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(null, "sig")]
    [InlineData("not-a-number", "sig")]
    [InlineData("1700000000", null)]
    public void Verify_RejectsMissingOrMalformedHeaders(string? time, string? signature) =>
        Assert.False(RemoteMonitoringProtocol.Verify("OFFICE-2026-HQ", time, signature, Path, DateTimeOffset.UtcNow));

    [Fact]
    public void Verify_RejectsAnEmptyCode()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.False(RemoteMonitoringProtocol.Verify("", now.ToString(), RemoteMonitoringProtocol.Sign("", now, Path), Path, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Fingerprint_IsTheSameForEquivalentCodesAndDiffersOtherwise()
    {
        Assert.Equal(RemoteMonitoringProtocol.Fingerprint("abcd-efgh-jklm"), RemoteMonitoringProtocol.Fingerprint("ABCDEFGHJKLM"));
        Assert.NotEqual(RemoteMonitoringProtocol.Fingerprint("ABCDEFGHJKLM"), RemoteMonitoringProtocol.Fingerprint("ABCDEFGHJKLN"));
    }
}
