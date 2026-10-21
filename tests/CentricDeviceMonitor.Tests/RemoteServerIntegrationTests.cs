extern alias service;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;
using Xunit;
using ServiceSnapshot = service::CentricDeviceMonitor.Models.BackgroundServiceSnapshot;

namespace CentricDeviceMonitor.Tests;

/// <summary>
/// The real background-service endpoint and the real dashboard client talking over localhost (no administrator
/// rights or firewall changes needed: the server listens on "localhost" instead of all addresses).
/// </summary>
public sealed class RemoteServerIntegrationTests : IDisposable
{
    private const string Code = "OFFICE-2026-HQ";
    private readonly int _port = FreePort();
    private readonly string _viewersFile = Path.Combine(Path.GetTempPath(), $"wu-viewers-{Guid.NewGuid():N}.json");
    private readonly object _server;
    private readonly Type _serverType;

    public RemoteServerIntegrationTests()
    {
        _serverType = typeof(ServiceSnapshot).Assembly.GetType("CentricDeviceMonitor.ServiceHost.RemoteStatsServer")!;
        Func<ServiceSnapshot?> snapshot = () => new ServiceSnapshot { UpdatedAt = DateTime.Now, CpuUsagePercent = 12, RamUsagePercent = 34, CpuModel = "Test CPU" };
        _server = Activator.CreateInstance(_serverType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { snapshot, "localhost", _viewersFile }, null)!;
        Apply(enabled: true, Code);
    }

    private void Apply(bool enabled, string code) => _serverType.GetMethod("Apply")!.Invoke(_server, new object[] { enabled, _port, code });

    private RemotePcEntry Entry(string code = Code) => new() { Host = "localhost", Port = _port, PairingCode = code };

    [Fact]
    public async Task Stats_AreReturnedForTheRightCode()
    {
        RemoteStatsResult result = await new RemoteMonitorClient().GetStatsAsync("localhost", _port, "office 2026 hq");
        Assert.Equal(RemoteStatsState.Online, result.State);
        Assert.Equal(Environment.MachineName, result.Stats!.MachineName);
        Assert.Equal(12, result.Stats.CpuUsagePercent);
        Assert.Equal("Test CPU", result.Stats.CpuModel);
    }

    [Fact]
    public async Task Stats_AreRefusedForAWrongCode()
    {
        RemoteStatsResult result = await new RemoteMonitorClient().GetStatsAsync("localhost", _port, "WRONG-CODE-1");
        Assert.Equal(RemoteStatsState.WrongCode, result.State);
        Assert.Null(result.Stats);
    }

    [Fact]
    public async Task UnsignedRequests_AreRefused()
    {
        using HttpClient http = new();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.StatsPath}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.UpdatePath}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.UpdatePath}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://localhost:{_port}/wu/v1/admin")).StatusCode);
    }

    [Fact]
    public async Task ChangingTheCode_TakesEffectAtOnce()
    {
        Apply(enabled: true, "NEWCODE-1234");
        Assert.Equal(RemoteStatsState.WrongCode, (await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code)).State);
        Assert.Equal(RemoteStatsState.Online, (await new RemoteMonitorClient().GetStatsAsync("localhost", _port, "NEWCODE-1234")).State);
    }

    [Fact]
    public async Task TurningSharingOff_StopsAnswering()
    {
        Apply(enabled: false, Code);
        RemoteStatsResult result = await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code);
        Assert.Equal(RemoteStatsState.NotSharing, result.State);
    }

    [Fact]
    public async Task LanSpeedTest_MeasuresBothDirections()
    {
        RemoteMonitorClient client = new();
        Assert.True(await client.MeasureLatencyAsync("localhost", _port) >= 0);
        Assert.True(await client.MeasureLanDownloadAsync(Entry(), TimeSpan.FromSeconds(1)) > 1);
        Assert.True(await client.MeasureLanUploadAsync(Entry(), TimeSpan.FromSeconds(1)) > 1);
    }

    [Fact]
    public async Task AConnectingPc_IsRecordedAsAViewerUntilTheCodeChanges()
    {
        await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code);
        IList<object> viewers = ((System.Collections.IEnumerable)_serverType.GetProperty("Viewers")!.GetValue(_server)!).Cast<object>().ToList();
        Assert.Single(viewers);

        Apply(enabled: true, "NEWCODE-1234");
        viewers = ((System.Collections.IEnumerable)_serverType.GetProperty("Viewers")!.GetValue(_server)!).Cast<object>().ToList();
        Assert.Empty(viewers);
    }

    [Fact]
    public async Task Details_ReturnProgramsSessionsAndProblems()
    {
        RemotePcDetails details = await new RemoteMonitorClient().GetDetailsAsync(Entry());
        Assert.Equal(Environment.MachineName, details.MachineName);
        Assert.NotEmpty(details.TopByMemory);
        Assert.True(details.LastBootUtc < DateTime.UtcNow);
        Assert.NotNull(details.Last7Days);
        Assert.NotNull(details.Last30Days);
    }
    public void Dispose()
    {
        ((IDisposable)_server).Dispose();
        File.Delete(_viewersFile);
    }

    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
