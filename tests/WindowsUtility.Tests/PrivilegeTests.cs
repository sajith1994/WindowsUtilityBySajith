extern alias service;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;
using ServiceProcessor = service::WindowsUtility.ServiceHost.PrivilegedCommandProcessor;
using ServiceRequest = service::WindowsUtility.Models.PrivilegedServiceCommandRequest;
using ServiceStartupTask = service::WindowsUtility.ServiceHost.DashboardStartupTask;

namespace WindowsUtility.Tests;

/// <summary>
/// The dashboard asking the background service to do administrator work, end to end through real request and
/// response files (in temporary folders) with a stand-in for the work itself.
/// </summary>
public sealed class PrivilegedChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wu-commands-{Guid.NewGuid():N}");
    private readonly List<(string Action, Dictionary<string, string> Arguments, string Requester)> _done = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _maintenanceAllowed = true;
    private bool _administrator = true;
    private Func<ServiceRequest, string>? _behaviour;

    private string Requests => Path.Combine(_root, "requests");

    private string Responses => Path.Combine(_root, "responses");

    private ServiceProcessor NewProcessor(bool realWork = false) => new(
        updater: null,
        requestsDirectory: Requests,
        responsesDirectory: Responses,
        standardUsersMayMaintain: () => _maintenanceAllowed,
        execute: realWork ? null : (request, requester, _) =>
        {
            lock (_done)
            {
                _done.Add((request.Action, request.Arguments, requester));
            }

            string message = _behaviour?.Invoke(request) ?? "done";
            return Task.FromResult((message, JsonSerializer.Serialize(new MaintenanceResult(true, message, "output"))));
        },
        sentByAdministrator: _ => _administrator);

    private PrivilegedCommandClient Client() => new(Requests, Responses);

    private void StartService(bool realWork = false) => _ = NewProcessor(realWork).RunAsync(_stop.Token);

    [Fact]
    public async Task ARequest_IsCarriedOutWithItsArgumentsForTheUserWhoAsked()
    {
        StartService();
        PrivilegedServiceCommandResponse response = await Client().SendAsync(PrivilegedServiceActions.NetworkRepair,
            new Dictionary<string, string> { ["kind"] = NetworkRepairKinds.FlushDns }, TimeSpan.FromSeconds(10));

        Assert.Equal("done", response.Message);
        Assert.Equal("output", JsonSerializer.Deserialize<MaintenanceResult>(response.Data)!.Output);
        (string action, Dictionary<string, string> arguments, string requester) = Assert.Single(_done);
        Assert.Equal(PrivilegedServiceActions.NetworkRepair, action);
        Assert.Equal(NetworkRepairKinds.FlushDns, arguments["kind"]);
        Assert.Equal(ExpectedOwner(), requester, ignoreCase: true);
        Assert.Empty(Directory.EnumerateFiles(Requests));
        Assert.Empty(Directory.EnumerateFiles(Responses));
    }

    [Fact]
    public async Task Maintenance_IsRefusedWhenAnAdministratorTurnedItOff()
    {
        _maintenanceAllowed = false;
        StartService();
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().SendAsync(PrivilegedServiceActions.ClearWindowsTemp, null, TimeSpan.FromSeconds(10)));
        Assert.Contains("turned off maintenance tools", refused.Message);
        Assert.Empty(_done);
    }

    [Theory]
    [InlineData(PrivilegedServiceActions.InstallUpdate)]
    [InlineData(PrivilegedServiceActions.RestartPrintSpooler)]
    [InlineData(PrivilegedServiceActions.DetectWindowsDrive)]
    [InlineData(PrivilegedServiceActions.SetDashboardStartup)]
    public async Task UpdatesAndEverydayActions_StillWorkWhenMaintenanceIsOff(string action)
    {
        _maintenanceAllowed = false;
        StartService();
        await Client().SendAsync(action, null, TimeSpan.FromSeconds(10));
        Assert.Equal(action, Assert.Single(_done).Action);
    }

    [Theory]
    [InlineData(PrivilegedServiceActions.SaveImageBackup)]
    [InlineData(PrivilegedServiceActions.RemoveImageBackup)]
    [InlineData(PrivilegedServiceActions.BackUpImageNow)]
    public async Task ImageBackups_AreSetUpOnlyForAdministratorAccounts(string action)
    {
        _administrator = false;
        StartService();
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().SendAsync(action, null, TimeSpan.FromSeconds(10)));
        Assert.Contains("administrator account", refused.Message);
        Assert.Empty(_done);
    }

    [Fact]
    public async Task AnyoneMayRunABackupAnAdministratorSetUp()
    {
        _administrator = false;
        StartService();
        await Client().SendAsync(PrivilegedServiceActions.RunImageBackup, new Dictionary<string, string> { ["id"] = Guid.NewGuid().ToString() }, TimeSpan.FromSeconds(10));
        Assert.Equal(PrivilegedServiceActions.RunImageBackup, Assert.Single(_done).Action);
    }

    [Fact]
    public void AnUnknownAccount_IsNotAnAdministrator() =>
        Assert.False(service::WindowsUtility.ServiceHost.AdministratorAccounts.IsMember(@"NOSUCHDOMAIN\nobody-4711"));
    [Fact]
    public async Task AFailure_ComesBackWithItsReason()
    {
        _behaviour = _ => throw new InvalidOperationException("The printer is still in use.");
        StartService();
        InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().SendAsync(PrivilegedServiceActions.RemovePrinter, new Dictionary<string, string> { ["name"] = "Office" }, TimeSpan.FromSeconds(10)));
        Assert.Equal("The printer is still in use.", failed.Message);
    }

    [Fact]
    public async Task AnUnknownAction_IsRefusedByTheRealService()
    {
        StartService(realWork: true);
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client().SendAsync("format-everything", null, TimeSpan.FromSeconds(10)));
        Assert.Contains("not supported", refused.Message);
    }

    [Fact]
    public async Task ARequestThatWaitedTooLong_IsNotCarriedOut()
    {
        Directory.CreateDirectory(Requests);
        Directory.CreateDirectory(Responses);
        Guid id = Guid.NewGuid();
        string path = Path.Combine(Requests, $"{id:N}.json");
        // The sender's own time stamp says "now"; the file's time (which the sender does not control here) is old.
        File.WriteAllText(path, JsonSerializer.Serialize(new ServiceRequest { Id = id, Action = PrivilegedServiceActions.NetworkRepair, CreatedAt = DateTime.UtcNow }));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        await NewProcessor().ProcessPendingAsync(CancellationToken.None);

        Assert.Empty(_done);
        PrivilegedServiceCommandResponse response = JsonSerializer.Deserialize<PrivilegedServiceCommandResponse>(
            File.ReadAllText(Path.Combine(Responses, $"{id:N}.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.False(response.Success);
        Assert.Contains("waited too long", response.Message);
    }

    [Fact]
    public async Task WithoutTheService_TheDashboardSaysSoInsteadOfWaitingForever()
    {
        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(() =>
            Client().SendAsync(PrivilegedServiceActions.RestartPrintSpooler, null, TimeSpan.FromSeconds(1)));
        Assert.Contains("background service did not answer", timeout.Message);
        Assert.Empty(Directory.EnumerateFiles(Requests));
    }

    [Fact]
    public void TheRequester_IsTakenFromTheFilesOwner()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "owner.json");
        File.WriteAllText(path, "{}");
        Assert.Equal(ExpectedOwner(), ServiceProcessor.RequestedBy(path), ignoreCase: true);
        Assert.Equal("unknown user", ServiceProcessor.RequestedBy(Path.Combine(_root, "missing.json")));
    }

    [Fact]
    public async Task TheSignInTask_IsOnlyChangedForANamedAccount()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceStartupTask.SetAsync("unknown user", true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ServiceStartupTask.SetAsync("S-1-5-21-1-2-3-1001", true, CancellationToken.None));
    }

    /// <summary>
    /// The owner Windows gives a new file: the user for a standard user (the case this is for), but the
    /// Administrators group for an elevated administrator (as on the build server).
    /// </summary>
    private static string ExpectedOwner()
    {
        string path = Path.GetTempFileName();
        try
        {
            return new FileInfo(path).GetAccessControl().GetOwner(typeof(NTAccount))!.Value;
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class MaintenanceTests
{
    [Fact]
    public void NetworkRepairs_RunOnlyTheirFixedCommands()
    {
        Assert.Equal(new[] { ("ipconfig.exe", "/flushdns") }, NetworkRepairKinds.Commands(NetworkRepairKinds.FlushDns));
        Assert.Equal(6, NetworkRepairKinds.Commands(NetworkRepairKinds.Full).Count);
        Assert.Empty(NetworkRepairKinds.Commands("del C:\\*"));
    }

    [Fact]
    public async Task BadArguments_AreRefusedBeforeAnythingRuns()
    {
        Assert.False((await MaintenanceOperations.AllowSpeedTestPortAsync(80)).Ok);
        Assert.False((await MaintenanceOperations.NetworkRepairAsync("format")).Ok);
        PrinterInstallResult printer = await PrinterOperations.InstallAsync("192.168.1.30; Remove-Item C:\\", new[] { "IPP" }, "Evil", "x");
        Assert.Equal(PrinterInstallOutcome.Failed, printer.Outcome);
    }

    [Fact]
    public void PrinterNames_AreQuotedSafelyInTheRemoveScript()
    {
        string script = PrinterOperations.BuildRemoveScript("Bob's printer'; Remove-Item C:\\ -Recurse; '", "IP_10.0.0.5", removePort: true, force: false);
        Assert.Contains("$name = 'Bob''s printer''; Remove-Item C:\\ -Recurse; '''", script);
        Assert.Contains("Remove-PrinterPort", script);
        Assert.DoesNotContain("Remove-PrinterPort", PrinterOperations.BuildRemoveScript("Printer", "USB001", removePort: true, force: false));
    }

    [Theory]
    [InlineData("IP_192.168.1.30", true)]
    [InlineData("WSD-067e923e", true)]
    [InlineData("USB001", false)]
    [InlineData("LPT1:", false)]
    [InlineData("PORTPROMPT:", false)]
    [InlineData("nul:", false)]
    public void OnlyNetworkPorts_AreRemoved(string port, bool removable) => Assert.Equal(removable, PrinterOperations.IsRemovablePort(port));

    [Fact]
    public void TwoCleanups_AreShownAsOne()
    {
        TemporaryFileCleanupResult user = new(10, 2, 1, 1000, Array.Empty<TemporaryFileCleanupItem>(),
            new[] { new TemporaryFileCleanupVolumeResult(@"C:\", 5000, 6000) }, "log.csv");
        TemporaryFileCleanupResult windows = new(5, 1, 3, 500, Array.Empty<TemporaryFileCleanupItem>(),
            new[] { new TemporaryFileCleanupVolumeResult(@"C:\", 6000, 6500) }, "log.csv");
        TemporaryFileCleanupResult both = TemporaryFileCleanupService.Combine(user, windows);
        Assert.Equal((15, 3, 4, 1500L), (both.FilesDeleted, both.DirectoriesDeleted, both.ItemsSkipped, both.BytesFreed));
        TemporaryFileCleanupVolumeResult volume = Assert.Single(both.Volumes);
        Assert.Equal(1500, volume.ObservedIncrease);

        // The service's half travels as JSON.
        TemporaryFileCleanupResult copy = JsonSerializer.Deserialize<TemporaryFileCleanupResult>(JsonSerializer.Serialize(windows))!;
        Assert.Equal(500, copy.BytesFreed);
    }

    [Fact]
    public void AdapterRequests_SurviveTheTripToTheElevatedHelper()
    {
        AdapterSettingsRequest request = new("Ethernet 2", true, true, false, "192.168.1.50", "255.255.255.0", "192.168.1.1", false, "1.1.1.1", "8.8.8.8");
        Assert.Equal(request, JsonSerializer.Deserialize<AdapterSettingsRequest>(JsonSerializer.Serialize(request)));
        Assert.EndsWith(".result.json", ElevatedTool.ResultPath(@"C:\x\abc.json"));
    }

    [Fact]
    public void UpdateResults_TravelBackFromTheService()
    {
        RemoteUpdateResult result = new() { Status = "Started", CurrentVersion = "2.0.77", LatestVersion = "2.0.78", Message = "Updating" };
        RemoteUpdateResult copy = JsonSerializer.Deserialize<RemoteUpdateResult>(JsonSerializer.Serialize(result), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(("Started", "2.0.78"), (copy.Status, copy.LatestVersion));
    }
}
