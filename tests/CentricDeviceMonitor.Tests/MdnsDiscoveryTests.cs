using System.Text;
using CentricDeviceMonitor.Services;
using Xunit;

namespace CentricDeviceMonitor.Tests;

/// <summary>Parses hand-built mDNS answers like the ones NAS boxes send (with name compression).</summary>
public class MdnsDiscoveryTests
{
    [Fact]
    public void Parse_ReadsServiceInstanceHostAndTxt()
    {
        List<byte> packet = new() { 0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0 }; // response, 4 answers
        int serviceOffset = packet.Count;
        AddName(packet, "_http._tcp.local");                       // PTR owner
        AddRecord(packet, 12, Name("DiskStation", pointer: serviceOffset));
        int instanceOffset = packet.Count;
        AddName(packet, "DiskStation", pointer: serviceOffset);     // SRV owner (compressed)
        List<byte> srv = new() { 0, 0, 0, 0, 0x13, 0x88 };          // priority, weight, port 5000
        srv.AddRange(Name("DiskStation.local"));
        AddRecord(packet, 33, srv.ToArray());
        AddPointer(packet, instanceOffset);                          // TXT owner
        AddRecord(packet, 16, Txt("vendor=Synology", "model=DS920+"));
        AddName(packet, "DiskStation.local");                       // A owner
        AddRecord(packet, 1, new byte[] { 192, 168, 1, 20 });

        MdnsDevice device = new() { Address = "192.168.1.20" };
        MdnsDiscovery.Parse(packet.ToArray(), device);

        Assert.Contains("_http._tcp.local", device.Services);
        Assert.Contains("DiskStation", device.InstanceNames);
        Assert.Equal("DiskStation", device.HostName);
        Assert.Equal("Synology", device.Txt["vendor"]);
        Assert.Equal("DS920+", device.Txt["model"]);
    }

    [Fact]
    public void Parse_IgnoresATruncatedPacket()
    {
        MdnsDevice device = new() { Address = "192.168.1.20" };
        byte[] truncated = { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 5, (byte)'_', (byte)'h' };
        Exception? error = Record.Exception(() => MdnsDiscovery.Parse(truncated, device));
        Assert.True(error is null or IndexOutOfRangeException or ArgumentException);
    }

    private static byte[] Name(string name, int? pointer = null)
    {
        List<byte> bytes = new();
        AddName(bytes, name, pointer);
        return bytes.ToArray();
    }

    private static void AddName(List<byte> packet, string name, int? pointer = null)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] text = Encoding.UTF8.GetBytes(label);
            packet.Add((byte)text.Length);
            packet.AddRange(text);
        }

        if (pointer is int offset)
        {
            AddPointer(packet, offset);
        }
        else
        {
            packet.Add(0);
        }
    }

    private static void AddPointer(List<byte> packet, int offset)
    {
        packet.Add((byte)(0xC0 | offset >> 8));
        packet.Add((byte)offset);
    }

    private static void AddRecord(List<byte> packet, int type, byte[] data)
    {
        packet.AddRange(new byte[] { 0, (byte)type, 0, 1, 0, 0, 0x11, 0x94, (byte)(data.Length >> 8), (byte)data.Length });
        packet.AddRange(data);
    }

    private static byte[] Txt(params string[] entries)
    {
        List<byte> bytes = new();
        foreach (string entry in entries)
        {
            byte[] text = Encoding.UTF8.GetBytes(entry);
            bytes.Add((byte)text.Length);
            bytes.AddRange(text);
        }

        return bytes.ToArray();
    }
}
