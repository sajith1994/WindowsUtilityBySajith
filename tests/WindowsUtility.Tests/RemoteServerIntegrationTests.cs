extern alias service;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;
using ServiceSnapshot = service::WindowsUtility.Models.BackgroundServiceSnapshot;
using ServiceActionRequest = service::WindowsUtility.Models.RemoteActionRequest;
using ServiceActionResult = service::WindowsUtility.Models.RemoteActionResult;
using ServiceServer = service::WindowsUtility.ServiceHost.RemoteStatsServer;
using ServiceServerSettings = service::WindowsUtility.ServiceHost.RemoteServerSettings;
using ServiceHistory = service::WindowsUtility.ServiceHost.HistoryRecorder;
using ServiceSample = service::WindowsUtility.ServiceHost.HistorySample;

namespace WindowsUtility.Tests;

/// <summary>
/// The real background-service endpoint and the real dashboard client talking over localhost (no administrator
/// rights or firewall changes needed: the server listens on "localhost" instead of all addresses).
/// </summary>
public sealed class RemoteServerIntegrationTests : IDisposable
{
    private const string Code = "OFFICE-2026-HQ";
    private readonly int _port = FreePort();
    private readonly string _viewersFile = Path.Combine(Path.GetTempPath(), $"wu-viewers-{Guid.NewGuid():N}.json");
    private readonly string _historyFile = Path.Combine(Path.GetTempPath(), $"wu-history-{Guid.NewGuid():N}.csv");
    private readonly string _phoneFile = Path.Combine(Path.GetTempPath(), $"wu-phone-{Guid.NewGuid():N}.json");
    private readonly ServiceServer _server;
    private readonly ServiceHistory _history;
    private readonly List<string> _actionsRun = new();
    private DateTime _clock = DateTime.UtcNow.AddMinutes(-30);

    public RemoteServerIntegrationTests()
    {
        Func<ServiceSnapshot?> snapshot = () => new ServiceSnapshot { UpdatedAt = DateTime.Now, CpuUsagePercent = 12, RamUsagePercent = 34, CpuModel = "Test CPU" };
        _history = new ServiceHistory(_historyFile, () => _clock);
        _server = new ServiceServer(snapshot, "localhost", _viewersFile, FakeAction, _history, _phoneFile);
        Apply(enabled: true, Code);
    }

    private void Apply(bool enabled, string code) => _server.Apply(enabled, _port, code);

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
        Assert.Single(_server.Viewers);

        Apply(enabled: true, "NEWCODE-1234");
        Assert.Empty(_server.Viewers);
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
    /// <summary>Stand-in for the real actions: records them instead of restarting or messaging this PC.</summary>
    private Task<ServiceActionResult> FakeAction(ServiceActionRequest request)
    {
        lock (_actionsRun)
        {
            _actionsRun.Add(request.Action);
        }

        return Task.FromResult(new ServiceActionResult { Ok = true, Message = $"fake {request.Action}" });
    }

    private void AllowActions(bool allowed) => _server.ActionsEnabled = allowed;

    [Fact]
    public async Task Actions_AreRefusedUntilThePcAllowsThem()
    {
        RemoteActionResult result = await new RemoteMonitorClient().RunActionAsync(Entry(), new RemoteActionRequest { Action = RemoteActions.Lock });
        Assert.False(result.Ok);
        Assert.Contains("does not allow actions", result.Message);
        Assert.Empty(_actionsRun);
    }

    [Fact]
    public async Task AnAllowedAction_ReachesThePc()
    {
        AllowActions(true);
        RemoteActionResult result = await new RemoteMonitorClient().RunActionAsync(Entry(), new RemoteActionRequest { Action = RemoteActions.Message, Message = "Hello" });
        Assert.True(result.Ok);
        Assert.Equal(new[] { RemoteActions.Message }, _actionsRun);
    }

    [Fact]
    public async Task ARepeatedRequest_IsRefused()
    {
        AllowActions(true);
        RemoteActionRequest request = new() { Action = RemoteActions.Lock };
        RemoteMonitorClient client = new();
        Assert.True((await client.RunActionAsync(Entry(), request)).Ok);
        RemoteActionResult again = await client.RunActionAsync(Entry(), request);
        Assert.False(again.Ok);
        Assert.Single(_actionsRun);
    }

    [Fact]
    public async Task ARequestChangedAfterSigning_IsRefused()
    {
        AllowActions(true);
        RemoteMonitorClient client = new();
        await client.HelloAsync("localhost", _port);
        byte[] signedBody = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new RemoteActionRequest { Action = RemoteActions.Message, Message = "Hello" });
        byte[] sentBody = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new RemoteActionRequest { Action = RemoteActions.Shutdown });
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using HttpClient http = new();
        using HttpRequestMessage request = new(HttpMethod.Post, $"http://localhost:{_port}{RemoteMonitoringProtocol.ActionPath}") { Content = new ByteArrayContent(sentBody) };
        request.Headers.Add(RemoteMonitoringProtocol.TimeHeader, time.ToString());
        request.Headers.Add(RemoteMonitoringProtocol.SignatureHeader, RemoteMonitoringProtocol.Sign(Code, time, RemoteMonitoringProtocol.ActionSignedText(signedBody)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(request)).StatusCode);
        Assert.Empty(_actionsRun);
    }

    [Fact]
    public async Task AnUnknownAction_IsRejected()
    {
        AllowActions(true);
        RemoteActionResult result = await new RemoteMonitorClient().RunActionAsync(Entry(), new RemoteActionRequest { Action = "format-c" });
        Assert.False(result.Ok);
        Assert.Empty(_actionsRun);
    }

    [Fact]
    public async Task Stats_ReportWhetherActionsAreAllowed()
    {
        Assert.False((await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code)).Stats!.ActionsAllowed);
        AllowActions(true);
        Assert.True((await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code)).Stats!.ActionsAllowed);
    }

    [Fact]
    public async Task History_ReturnsTheRecordedMinutesWithGapsWhereThePcWasOff()
    {
        // Ten minutes recorded, ten off, then five more.
        for (int minute = 0; minute < 26; minute++)
        {
            if (minute is >= 10 and < 20)
            {
                continue;
            }

            _clock = DateTime.UtcNow.AddMinutes(-30 + minute);
            _history.Add(new ServiceSample(Cpu: 40, Temperature: 60 + minute, Ram: 50, Disk: 5, Receive: 1_000_000, Send: 500_000));
            _history.Add(new ServiceSample(Cpu: 60, Temperature: 55, Ram: 50, Disk: 15, Receive: 3_000_000, Send: 500_000));
        }

        _history.Flush();
        _clock = DateTime.UtcNow;
        RemoteHistory history = await new RemoteMonitorClient().GetHistoryAsync(Entry(), 1);
        Assert.Equal(5, history.StepMinutes);
        List<RemoteHistoryPoint> recorded = history.Points.Where(point => point.CpuPercent is not null).ToList();
        Assert.NotEmpty(recorded);
        Assert.All(recorded, point => Assert.Equal(50, point.CpuPercent!.Value, 1));
        Assert.Equal(85, recorded.Max(point => point.TemperatureCelsius)!.Value, 1);
        Assert.Contains(history.Points, point => point.CpuPercent is null);
        Assert.Equal(2_000_000, recorded[0].ReceiveBitsPerSec!.Value, 0);
    }

    [Fact]
    public async Task Software_ListsTheInstalledPrograms()
    {
        RemoteSoftwareList software = await new RemoteMonitorClient().GetSoftwareAsync(Entry());
        Assert.True(DateTime.UtcNow - software.CollectedUtc < TimeSpan.FromMinutes(11));
        Assert.All(software.Programs, program => Assert.False(string.IsNullOrWhiteSpace(program.Name)));
    }

    [Fact]
    public async Task PhonePage_NeedsTheLinkKey()
    {
        _server.Apply(new ServiceServerSettings(true, _port, Code, PhoneEnabled: true, PhoneKey: "PHONEKEY2345"));
        File.WriteAllText(_phoneFile, "{\"mainPcName\":\"MAIN\",\"pcs\":[{\"name\":\"PC1\"}]}");
        using HttpClient http = new();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.PhonePagePath}?k=WRONGKEY0000")).StatusCode);
        HttpResponseMessage page = await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.PhonePagePath}?k=phonekey2345");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("<title>Network PCs</title>", await page.Content.ReadAsStringAsync());
        Assert.Contains("\"PC1\"", await http.GetStringAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.PhoneDataPath}?k=PHONEKEY2345"));
    }

    [Fact]
    public async Task PhonePageAlone_DoesNotShareThisPcsStats()
    {
        _server.Apply(new ServiceServerSettings(false, _port, Code, PhoneEnabled: true, PhoneKey: "PHONEKEY2345"));
        Assert.False(_server.IsActive);
        Assert.Equal(RemoteStatsState.NotSharing, (await new RemoteMonitorClient().GetStatsAsync("localhost", _port, Code)).State);
        using HttpClient http = new();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.HelloPath}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.PhonePagePath}?k=PHONEKEY2345")).StatusCode);
    }

    [Fact]
    public async Task PhonePage_IsOffUnlessTurnedOn()
    {
        using HttpClient http = new();
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://localhost:{_port}{RemoteMonitoringProtocol.PhonePagePath}?k=PHONEKEY2345")).StatusCode);
    }

    public void Dispose()
    {
        _server.Dispose();
        File.Delete(_viewersFile);
        File.Delete(_historyFile);
        File.Delete(_phoneFile);
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
