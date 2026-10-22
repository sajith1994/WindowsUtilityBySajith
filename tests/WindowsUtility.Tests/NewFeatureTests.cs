extern alias service;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WindowsUtility.Controls;
using WindowsUtility.Dialogs;
using WindowsUtility.Models;
using WindowsUtility.Services;
using Xunit;
using ServiceHistory = service::WindowsUtility.ServiceHost.HistoryRecorder;
using ServiceSample = service::WindowsUtility.ServiceHost.HistorySample;
using SoftwareInventory = service::WindowsUtility.ServiceHost.SoftwareInventoryProvider;
using ServiceProgram = service::WindowsUtility.Models.RemoteProgram;
using AdapterWake = service::WindowsUtility.ServiceHost.AdapterWakeProvider;

namespace WindowsUtility.Tests;

public class HistoryTests
{
    [Fact]
    public void ALine_RoundTripsWithEmptyReadings()
    {
        string line = ServiceHistory.Format(1_760_000_000, new ServiceSample(12.34, null, 56.78, 0, 1_234_567.8, null));
        Assert.Equal("1760000000,12.3,,56.8,0,1234568,", line);
        Assert.True(ServiceHistory.TryParse(line, out long time, out ServiceSample sample));
        Assert.Equal(1_760_000_000, time);
        Assert.Equal(12.3, sample.Cpu);
        Assert.Null(sample.Temperature);
        Assert.Null(sample.Send);
        Assert.False(ServiceHistory.TryParse("garbage", out _, out _));
    }

    [Fact]
    public void AWeek_IsGroupedIntoHalfHours()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wu-history-{Guid.NewGuid():N}.csv");
        try
        {
            DateTime clock = DateTime.UtcNow.AddHours(-3);
            ServiceHistory history = new(path, () => clock);
            for (int minute = 0; minute < 120; minute++)
            {
                clock = DateTime.UtcNow.AddHours(-3).AddMinutes(minute);
                history.Add(new ServiceSample(minute < 60 ? 10 : 90, 50, 40, 1, 100, 100));
            }

            history.Flush();
            clock = DateTime.UtcNow;
            RemoteHistory week = Convert(history.Read(7));
            Assert.Equal(30, week.StepMinutes);
            Assert.InRange(week.Points.Count(point => point.CpuPercent is not null), 4, 5);
            Assert.Contains(week.Points, point => point.CpuPercent is 10);
            Assert.Contains(week.Points, point => point.CpuPercent is 90);
            Assert.NotNull(week.RecordingSinceUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MinutesOlderThanEightDays_AreDropped()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wu-history-{Guid.NewGuid():N}.csv");
        try
        {
            long old = DateTimeOffset.UtcNow.AddDays(-9).ToUnixTimeSeconds();
            long recent = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
            File.WriteAllLines(path, new[] { $"{old},1,1,1,1,1,1", $"{recent},2,2,2,2,2,2" });
            DateTime clock = DateTime.UtcNow;
            ServiceHistory history = new(path, () => clock);
            history.Add(new ServiceSample(3, 3, 3, 3, 3, 3));
            clock = clock.AddMinutes(1);
            history.Add(new ServiceSample(4, 4, 4, 4, 4, 4)); // a new minute writes the last one (and prunes)
            string[] lines = File.ReadAllLines(path);
            Assert.DoesNotContain(lines, line => line.StartsWith(old.ToString(), StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith(recent.ToString(), StringComparison.Ordinal));
            Assert.Equal(2, lines.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Summary_GivesAveragePeakAndOffTime()
    {
        DateTime start = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
        RemoteHistory history = new()
        {
            Days = 1,
            StepMinutes = 5,
            Points =
            {
                new RemoteHistoryPoint { TimeUtc = start, CpuPercent = 10, TemperatureCelsius = 50, RamPercent = 40 },
                new RemoteHistoryPoint { TimeUtc = start.AddMinutes(5), CpuPercent = 30, TemperatureCelsius = 71, RamPercent = 60 },
                new RemoteHistoryPoint { TimeUtc = start.AddMinutes(10) }
            }
        };
        string text = RemotePcHistoryWindow.Summarize(history);
        Assert.StartsWith("Average CPU 20% (peak 30%", text);
        Assert.Contains("hottest 71 °C", text);
        Assert.Contains("memory up to 60%", text);
        Assert.Contains("no readings for 5 min (off or asleep)", text);
    }

    [Fact]
    public void LongRanges_GetHourAndDayTicks()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), LiveChart.NiceTimeStep(TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromHours(6), LiveChart.NiceTimeStep(TimeSpan.FromDays(1)));
        Assert.Equal(TimeSpan.FromDays(1), LiveChart.NiceTimeStep(TimeSpan.FromDays(7)));
    }

    /// <summary>The service's copy of the model, read back as the dashboard's (same JSON the client receives).</summary>
    private static RemoteHistory Convert(object serviceHistory) =>
        System.Text.Json.JsonSerializer.Deserialize<RemoteHistory>(System.Text.Json.JsonSerializer.Serialize(serviceHistory))!;
}

public class SoftwareTests
{
    private static Dictionary<string, object?> Entry(params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AProgram_IsReadWithItsDetails()
    {
        ServiceProgram program = SoftwareInventory.ToProgram(Entry(("DisplayName", " AnyDesk "), ("DisplayVersion", "9.0.4"), ("Publisher", "AnyDesk Software GmbH"),
            ("InstallDate", "20260115"), ("EstimatedSize", 2048)), "All users")!;
        Assert.Equal("AnyDesk", program.Name);
        Assert.Equal("9.0.4", program.Version);
        Assert.Equal(new DateTime(2026, 1, 15), program.InstalledOn);
        Assert.Equal(2L << 20, program.SizeBytes);
    }

    [Theory]
    [InlineData("SystemComponent", 1)]
    [InlineData("ParentKeyName", "Office16")]
    [InlineData("ReleaseType", "Security Update")]
    public void UpdatesAndHiddenParts_AreLeftOut(string name, object value)
    {
        Assert.Null(SoftwareInventory.ToProgram(Entry(("DisplayName", "Something"), (name, value)), "All users"));
        Assert.Null(SoftwareInventory.ToProgram(Entry(("DisplayVersion", "1.0")), "All users"));
    }

    [Fact]
    public void TheSameProgramTwice_IsOneRow()
    {
        List<ServiceProgram> programs = SoftwareInventory.Deduplicate(new[]
        {
            new ServiceProgram { Name = "Zoom", Version = "6.1", InstalledFor = "maria" },
            new ServiceProgram { Name = "zoom", Version = "6.1", InstalledFor = "tom" },
            new ServiceProgram { Name = "7-Zip", Version = "24.08", InstalledFor = "All users" },
            new ServiceProgram { Name = "7-Zip", Version = "24.08", InstalledFor = "maria" }
        });
        Assert.Equal(new[] { "7-Zip", "Zoom" }, programs.Select(program => program.Name));
        Assert.Equal("All users", programs[0].InstalledFor);
        Assert.Equal("maria, tom", programs[1].InstalledFor);
    }
}

public class WakeAndVpnTests
{
    [Fact]
    public void TheMagicPacket_IsSixFFsThenTheAddressSixteenTimes()
    {
        byte[] packet = WakeOnLanService.BuildMagicPacket("dc:04:5a:06:a5:a6");
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), value => Assert.Equal(0xFF, value));
        byte[] mac = { 0xDC, 0x04, 0x5A, 0x06, 0xA5, 0xA6 };
        for (int copy = 0; copy < 16; copy++)
        {
            Assert.Equal(mac, packet.Skip(6 + copy * 6).Take(6));
        }
    }

    [Theory]
    [InlineData("DC-04-5A-06-A5-A6", true)]
    [InlineData("dc045a06a5a6", true)]
    [InlineData("DC-04-5A-06-A5", false)]
    [InlineData("Not available", false)]
    [InlineData("", false)]
    public void AdapterAddresses_AreRecognised(string text, bool valid) => Assert.Equal(valid, WakeOnLanService.ParseMac(text) is not null);

    [Fact]
    public void TheSubnetBroadcast_IsWorkedOutFromTheMask()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.255"), WakeOnLanService.DirectedBroadcast(IPAddress.Parse("192.168.1.118"), IPAddress.Parse("255.255.255.0")));
        Assert.Equal(IPAddress.Parse("10.0.15.255"), WakeOnLanService.DirectedBroadcast(IPAddress.Parse("10.0.12.7"), IPAddress.Parse("255.255.252.0")));
        Assert.True(WakeOnLanService.SameSubnet(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.118"), IPAddress.Parse("255.255.255.0")));
        Assert.False(WakeOnLanService.SameSubnet(IPAddress.Parse("192.168.2.20"), IPAddress.Parse("192.168.1.118"), IPAddress.Parse("255.255.255.0")));
    }

    [Theory]
    [InlineData("DC045A06A5A6", "DC-04-5A-06-A5-A6")]
    [InlineData("00-00-00-00-00-00", null)]
    [InlineData("FFFFFFFFFFFF", null)]
    [InlineData("12345", null)]
    public void ReportedAddresses_AreFormatted(string raw, string? expected) => Assert.Equal(expected, AdapterWake.FormatMac(raw));

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("office-pc.tail1234.ts.net", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData("OFFICE-PC", false)]
    [InlineData("", false)]
    public void VpnAddresses_AreRecognised(string host, bool vpn) => Assert.Equal(vpn, VpnAddresses.IsVpnHost(host));

    [Fact]
    public void TailscalePeers_AreReadFromItsStatus()
    {
        const string json = """
            { "Self": { "TailscaleIPs": ["100.64.0.1"] },
              "Peer": {
                "nodekey:a": { "HostName": "office-pc", "Online": true, "TailscaleIPs": ["100.101.1.2", "fd7a:115c:a1e0::2"] },
                "nodekey:b": { "HostName": "old-laptop", "Online": false, "TailscaleIPs": ["100.101.1.3"] },
                "nodekey:c": { "HostName": "phone", "TailscaleIPs": ["fd7a:115c:a1e0::4"] } } }
            """;
        Assert.Equal(new[] { "100.101.1.2" }, RemoteMonitorClient.ParseTailscalePeers(json));
        Assert.Empty(RemoteMonitorClient.ParseTailscalePeers("not json"));
    }
}

public class TileFeatureTests
{
    private static RemotePcView View(string host, Action<RemoteStatsPayload>? change = null)
    {
        RemoteStatsPayload stats = new() { MachineName = "PC1", ReadingsFresh = true, RamUsagePercent = 40, TotalMemoryBytes = 8UL << 30 };
        change?.Invoke(stats);
        RemotePcView view = new(new RemotePcEntry { Host = host, Group = "Office" });
        view.Update(new RemoteStatsResult(RemoteStatsState.Online, stats, "Online"));
        return view;
    }

    [Fact]
    public void WakeDetails_AreRememberedWiredFirst()
    {
        RemotePcView view = View("192.168.1.40", stats => stats.Adapters = new()
        {
            new RemoteAdapterInfo { Name = "Wi-Fi", MacAddress = "84-FD-D1-59-8E-E5", IsWired = false },
            new RemoteAdapterInfo { Name = "Ethernet", MacAddress = "DC-04-5A-06-A5-A6", IsWired = true, WakeOnMagicPacket = true }
        });
        Assert.True(view.RememberWakeDetails());
        Assert.Equal(new[] { "DC-04-5A-06-A5-A6", "84-FD-D1-59-8E-E5" }, view.Entry.WakeMacAddresses);
        Assert.True(view.Entry.WakeOnLanEnabled);
        Assert.Equal("192.168.1.40", view.Entry.LastKnownAddress);
        Assert.False(view.RememberWakeDetails());

        Assert.False(view.CanWake);
        view.Update(new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline"));
        Assert.True(view.CanWake);
        view.MarkWakeSent();
        Assert.True(view.IsWaking);
        Assert.Contains("Wake signal sent", view.WakeText);
    }

    [Fact]
    public void APcOverTheVpn_CannotBeWoken()
    {
        RemotePcView view = View("100.101.1.2");
        view.Entry.WakeMacAddresses.Add("DC-04-5A-06-A5-A6");
        view.Update(new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline"));
        Assert.False(view.CanWake);
        Assert.EndsWith("via VPN", view.Subtitle);
    }

    [Fact]
    public void RestorePoints_AreShownAndOldOnesFlagged()
    {
        RemotePcView fresh = View("10.0.0.1", stats => stats.Recovery = new RemoteRecoveryInfo { SystemProtectionOn = true, LastRestorePointUtc = DateTime.UtcNow.AddDays(-3) });
        Assert.Equal("Restore point 3 days ago", fresh.RecoveryText);
        Assert.False(fresh.RecoveryNeedsAttention);

        RemotePcView off = View("10.0.0.2", stats => stats.Recovery = new RemoteRecoveryInfo { SystemProtectionOn = false });
        Assert.Equal("System Protection off  •  No restore point", off.RecoveryText);
        Assert.True(off.RecoveryNeedsAttention);
    }

    [Fact]
    public void ProblemsFirst_PutsOfflineThenWarningsFirst()
    {
        RemotePcView fine = View("10.0.0.1");
        RemotePcView low = View("10.0.0.2", stats => stats.Disks.Add(new RemoteDiskInfo { Name = "C:", TotalBytes = 100L << 30, FreeBytes = 5L << 30 }));
        RemotePcView offline = View("10.0.0.3");
        offline.Update(new RemoteStatsResult(RemoteStatsState.Unreachable, null, "Offline"));
        Assert.Equal(new[] { offline, low, fine }, new[] { fine, low, offline }.OrderBy(view => view.SortRank));
    }

    [Fact]
    public void Search_MatchesGroupModelAndUsers()
    {
        RemotePcView view = View("10.0.0.1", stats =>
        {
            stats.SystemModel = "Latitude 5400";
            stats.Sessions.Add(new RemoteSessionInfo { User = @"OFFICE\maria", State = "Active" });
        });
        Assert.Contains("Office", view.SearchText);
        Assert.Contains("Latitude 5400", view.SearchText);
        Assert.Contains("maria", view.SearchText);
    }
}

public class PrinterSuppliesTests
{
    [Theory]
    [InlineData(800, 1000, false, 80, SupplyState.Ok)]
    [InlineData(80, 1000, false, 8, SupplyState.Low)]
    [InlineData(0, 1000, false, 0, SupplyState.Empty)]
    [InlineData(45, -2, false, 45, SupplyState.Ok)]
    [InlineData(90, 100, true, 90, SupplyState.Low)]
    public void Levels_BecomePercentAndState(int level, int max, bool waste, int percent, SupplyState state)
    {
        PrinterSupply supply = PrinterSuppliesService.ToSupply("Toner", level, max, waste);
        Assert.Equal(percent, supply.Percent);
        Assert.Equal(state, supply.State);
    }

    [Fact]
    public void SomeRemaining_IsOkWithoutAPercent()
    {
        PrinterSupply supply = PrinterSuppliesService.ToSupply("Ink", -3, -2, false);
        Assert.Null(supply.Percent);
        Assert.Equal(SupplyState.Ok, supply.State);
        Assert.Equal("OK", PrinterSuppliesService.Describe(supply));
        Assert.Equal("Low (8%)", PrinterSuppliesService.Describe(PrinterSuppliesService.ToSupply("Toner", 8, 100, false)));
    }

    [Theory]
    [InlineData("1.3.6.1.2.1.43.11.1.1.9.1.1")]
    [InlineData("1.3.6.1.4.1.311.21.300")]
    public void Oids_RoundTrip(string oid)
    {
        byte[] encoded = PrinterSuppliesService.Snmp.EncodeOid(oid);
        Assert.Equal(0x06, encoded[0]);
        Assert.Equal(oid, PrinterSuppliesService.Snmp.DecodeOid(encoded, 2, encoded[1]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(-3)]
    [InlineData(70000)]
    public void Integers_RoundTrip(long value)
    {
        byte[] encoded = PrinterSuppliesService.Snmp.EncodeInteger(value);
        Assert.Equal(value, PrinterSuppliesService.Snmp.DecodeInteger(encoded, 2, encoded[1]));
    }

    [Fact]
    public async Task Supplies_AreWalkedFromAPrinterOverSnmp()
    {
        SortedDictionary<string, object> table = new(Comparer<string>.Create(CompareOids))
        {
            ["1.3.6.1.2.1.43.11.1.1.4.1.1"] = 3L,
            ["1.3.6.1.2.1.43.11.1.1.4.1.2"] = 4L,
            ["1.3.6.1.2.1.43.11.1.1.6.1.1"] = "Black Toner",
            ["1.3.6.1.2.1.43.11.1.1.6.1.2"] = "Waste Toner Box",
            ["1.3.6.1.2.1.43.11.1.1.8.1.1"] = 1000L,
            ["1.3.6.1.2.1.43.11.1.1.8.1.2"] = 100L,
            ["1.3.6.1.2.1.43.11.1.1.9.1.1"] = 80L,
            ["1.3.6.1.2.1.43.11.1.1.9.1.2"] = 30L,
            ["1.3.6.1.2.1.43.12.1.1.4.1.1"] = "black"
        };
        using UdpClient agent = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)agent.Client.LocalEndPoint!).Port;
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));
        Task serving = ServeAsync(agent, table, stop.Token);

        List<PrinterSupply> supplies = await PrinterSuppliesService.ReadSnmpAsync("127.0.0.1", stop.Token, port);
        stop.Cancel();

        Assert.Equal(2, supplies.Count);
        Assert.Equal(new PrinterSupply("Black Toner", 8, SupplyState.Low, false), supplies[0]);
        Assert.Equal(new PrinterSupply("Waste Toner Box", 30, SupplyState.Ok, true), supplies[1]);
        await Task.WhenAny(serving, Task.Delay(1000));
    }

    [Fact]
    public void IppMarkers_AreReadWithTheirLowLevels()
    {
        using MemoryStream ipp = new();
        ipp.Write(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04 });
        Attribute(ipp, 0x41, "marker-names", Encoding.UTF8.GetBytes("Cyan"));
        Attribute(ipp, 0x41, string.Empty, Encoding.UTF8.GetBytes("Black"));
        Attribute(ipp, 0x21, "marker-levels", Int(50));
        Attribute(ipp, 0x21, string.Empty, Int(3));
        Attribute(ipp, 0x21, "marker-low-levels", Int(10));
        Attribute(ipp, 0x21, string.Empty, Int(10));
        Attribute(ipp, 0x44, "marker-types", Encoding.ASCII.GetBytes("ink-cartridge"));
        Attribute(ipp, 0x44, string.Empty, Encoding.ASCII.GetBytes("ink-cartridge"));
        ipp.WriteByte(0x03);

        List<PrinterSupply> supplies = PrinterSuppliesService.ParseIppMarkers(ipp.ToArray());
        Assert.Equal(new[] { ("Cyan", 50, SupplyState.Ok), ("Black", 3, SupplyState.Low) },
            supplies.Select(supply => (supply.Name, supply.Percent ?? -1, supply.State)));
    }

    private static void Attribute(Stream stream, byte tag, string name, byte[] value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        stream.WriteByte(tag);
        stream.Write(new[] { (byte)(nameBytes.Length >> 8), (byte)nameBytes.Length });
        stream.Write(nameBytes);
        stream.Write(new[] { (byte)(value.Length >> 8), (byte)value.Length });
        stream.Write(value);
    }

    private static byte[] Int(int value) => new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };

    // ---- A stand-in printer answering SNMP GetNext from a table ----

    private static int CompareOids(string first, string second)
    {
        long[] a = first.Split('.').Select(long.Parse).ToArray();
        long[] b = second.Split('.').Select(long.Parse).ToArray();
        for (int index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            if (a[index] != b[index])
            {
                return a[index].CompareTo(b[index]);
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    private static async Task ServeAsync(UdpClient agent, SortedDictionary<string, object> table, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult request = await agent.ReceiveAsync(cancellationToken);
                (long requestId, string oid) = ParseGetNext(request.Buffer);
                KeyValuePair<string, object>? next = table.Where(entry => CompareOids(entry.Key, oid) > 0).Select(entry => (KeyValuePair<string, object>?)entry).FirstOrDefault();
                byte[] value = next?.Value switch
                {
                    long number => PrinterSuppliesService.Snmp.EncodeInteger(number),
                    string text => Tlv(0x04, Encoding.ASCII.GetBytes(text)),
                    _ => new byte[] { 0x82, 0x00 } // endOfMibView
                };
                byte[] varBind = Tlv(0x30, PrinterSuppliesService.Snmp.EncodeOid(next?.Key ?? oid).Concat(value).ToArray());
                byte[] pdu = Tlv(0xA2, PrinterSuppliesService.Snmp.EncodeInteger(requestId).Concat(PrinterSuppliesService.Snmp.EncodeInteger(0))
                    .Concat(PrinterSuppliesService.Snmp.EncodeInteger(0)).Concat(Tlv(0x30, varBind)).ToArray());
                byte[] message = Tlv(0x30, PrinterSuppliesService.Snmp.EncodeInteger(0).Concat(Tlv(0x04, Encoding.ASCII.GetBytes("public"))).Concat(pdu).ToArray());
                await agent.SendAsync(message, request.RemoteEndPoint, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (long RequestId, string Oid) ParseGetNext(byte[] data)
    {
        // SEQUENCE { version, community, GetNext { request-id, error, index, SEQUENCE { SEQUENCE { OID, NULL } } } }
        int position = 0;
        Enter(data, ref position);
        Skip(data, ref position);
        Skip(data, ref position);
        Enter(data, ref position);
        int length = Enter(data, ref position);
        long requestId = PrinterSuppliesService.Snmp.DecodeInteger(data, position, length);
        position += length;
        Skip(data, ref position);
        Skip(data, ref position);
        Enter(data, ref position);
        Enter(data, ref position);
        length = Enter(data, ref position);
        return (requestId, PrinterSuppliesService.Snmp.DecodeOid(data, position, length));
    }

    /// <summary>Moves past a tag and its (short or long form) length to the content; returns the length.</summary>
    private static int Enter(byte[] data, ref int position)
    {
        position++;
        int length = data[position++];
        if (length >= 0x80)
        {
            int count = length & 0x7F;
            length = 0;
            for (int index = 0; index < count; index++)
            {
                length = (length << 8) | data[position++];
            }
        }

        return length;
    }

    private static void Skip(byte[] data, ref int position)
    {
        // Not "position += Enter(...)": that would add to the position read before Enter moved it.
        int length = Enter(data, ref position);
        position += length;
    }

    private static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] length = content.Length < 0x80 ? new[] { (byte)content.Length } : new byte[] { 0x81, (byte)content.Length };
        return new[] { tag }.Concat(length).Concat(content).ToArray();
    }
}

public class SummaryAndPhoneTests
{
    private static RemotePcView View(string name, Action<RemoteStatsPayload>? change = null, bool online = true)
    {
        RemoteStatsPayload stats = new() { MachineName = name, ReadingsFresh = true, RamUsagePercent = 40, TotalMemoryBytes = 8UL << 30, CpuUsagePercent = 12 };
        stats.Disks.Add(new RemoteDiskInfo { Name = "C:", TotalBytes = 200L << 30, FreeBytes = 120L << 30 });
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
    public void TheSummary_ListsWhatNeedsAttention()
    {
        RemotePcView[] views =
        {
            View("FINE-PC"),
            View("FULL<PC>", stats => stats.Disks[0].FreeBytes = 4L << 30),
            View("GONE-PC", online: false)
        };
        RemoteAlert[] alerts = { new(DateTime.Now.AddHours(-2), "FULL<PC>", "FULL<PC>: drive C: is nearly full", "4 GB free of 200 GB", false) };
        (string, PrinterSuppliesResult)[] printers =
        {
            ("Office printer", new PrinterSuppliesResult("192.168.1.30", "SNMP", new[] { PrinterSuppliesService.ToSupply("Black Toner", 5, 100, false) }, null))
        };

        DailySummary summary = DailySummaryService.Build(views, alerts, printers, DateTime.Now, "MAIN");
        Assert.Equal("Daily summary: 2 of 3 PCs need attention", summary.Headline);
        Assert.Contains("Offline: GONE-PC", summary.Message);
        Assert.Contains("Check: FULL<PC>", summary.Message);
        Assert.Contains("Low ink or toner: Office printer", summary.Message);
        Assert.Contains("FULL&lt;PC&gt;", summary.Html);
        Assert.DoesNotContain("FULL<PC>", summary.Html);
        Assert.Contains("Black Toner: Low (5%)", summary.Html);
    }

    [Fact]
    public void AllFine_SaysSo()
    {
        DailySummary summary = DailySummaryService.Build(new[] { View("PC1"), View("PC2") }, Array.Empty<RemoteAlert>(), Array.Empty<(string, PrinterSuppliesResult)>(), DateTime.Now, "MAIN");
        Assert.Equal("Daily summary: all 2 PCs are fine", summary.Headline);
        Assert.Equal(0, summary.PcsNeedingAttention);
    }

    [Fact]
    public void ThePhonePage_GetsWhatTheTilesShow()
    {
        PhoneViewData data = PhoneViewService.Build(new[] { View("PC1"), View("PC2", online: false) }, "MAIN", DateTime.UtcNow);
        Assert.Equal("MAIN", data.MainPcName);
        PhoneViewPc online = data.Pcs[0];
        Assert.Equal(("PC1", "Online", 12.0), (online.Name, online.StatusKind, online.CpuPercent!.Value));
        Assert.Equal("120 GB", online.Disks.Single().Free);
        PhoneViewPc offline = data.Pcs[1];
        Assert.Equal("Offline", offline.StatusKind);
        Assert.Null(offline.CpuPercent);
        Assert.StartsWith("Last seen", offline.LastSeen);
    }

    [Fact]
    public void ThePhoneLink_CarriesTheKey()
    {
        Assert.Equal("http://192.168.1.118:47810/wu/phone?k=ABCDEFGH2345", PhoneViewService.Link("192.168.1.118", 47810, "ABCDEFGH2345"));
        string key = PhoneViewService.NewKey();
        Assert.Equal(12, key.Length);
        Assert.DoesNotContain(key, character => character is '0' or 'O' or '1' or 'I');
    }

    [Fact]
    public void TheAlertLog_KeepsAlertsAcrossRestarts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wu-alerts-{Guid.NewGuid():N}.json");
        try
        {
            new RemoteAlertLog(path).Add(new RemoteAlert(DateTime.Now.AddHours(-1), "PC1", "PC1 is offline", "No answer", false));
            new RemoteAlertLog(path).Add(new RemoteAlert(DateTime.Now.AddDays(-3), "PC2", "PC2 is hot", "95 °C", false));
            IReadOnlyList<RemoteAlert> recent = new RemoteAlertLog(path).Since(DateTime.Now.AddDays(-1));
            Assert.Equal("PC1 is offline", Assert.Single(recent).Title);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
