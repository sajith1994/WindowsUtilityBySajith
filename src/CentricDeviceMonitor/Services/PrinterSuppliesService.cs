using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace CentricDeviceMonitor.Services;

/// <summary>One toner, ink, drum or waste container. Percent is how full it is (for waste, how full the box is).</summary>
public sealed record PrinterSupply(string Name, int? Percent, SupplyState State, bool IsWaste);

public enum SupplyState
{
    Ok,
    Low,
    Empty,
    /// <summary>The printer only says "some left" or nothing measurable.</summary>
    Unknown
}

public sealed record PrinterSuppliesResult(string Address, string Source, IReadOnlyList<PrinterSupply> Supplies, string? Error)
{
    public bool NeedsAttention => Supplies.Any(supply => supply.State is SupplyState.Low or SupplyState.Empty);
}

/// <summary>
/// Toner and ink levels of network printers, read the way printers publish them: the Printer MIB over SNMP v1
/// (community "public", read-only), or the marker attributes of IPP for printers without SNMP (common on inkjets).
/// Nothing is changed on the printer.
/// </summary>
public static class PrinterSuppliesService
{
    public const int LowPercent = 15;

    // Printer MIB, prtMarkerSuppliesTable (RFC 3805).
    private const string SuppliesClass = "1.3.6.1.2.1.43.11.1.1.4";
    private const string SuppliesDescription = "1.3.6.1.2.1.43.11.1.1.6";
    private const string SuppliesMaxCapacity = "1.3.6.1.2.1.43.11.1.1.8";
    private const string SuppliesLevel = "1.3.6.1.2.1.43.11.1.1.9";

    private static readonly HttpClient IppClient = new(new HttpClientHandler
    {
        // Printers use self-signed certificates for IPPS; only supply levels are read.
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<PrinterSuppliesResult> ReadAsync(string address, CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(address, out _))
        {
            return new PrinterSuppliesResult(address, string.Empty, Array.Empty<PrinterSupply>(), $"\"{address}\" is not an IP address.");
        }

        string? snmpError = null;
        try
        {
            List<PrinterSupply> supplies = await ReadSnmpAsync(address, cancellationToken);
            if (supplies.Count > 0)
            {
                return new PrinterSuppliesResult(address, "SNMP", supplies, null);
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or InvalidDataException && !cancellationToken.IsCancellationRequested)
        {
            snmpError = exception.Message;
        }

        try
        {
            List<PrinterSupply> supplies = await ReadIppAsync(address, cancellationToken);
            if (supplies.Count > 0)
            {
                return new PrinterSuppliesResult(address, "IPP", supplies, null);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or InvalidDataException && !cancellationToken.IsCancellationRequested)
        {
        }

        return new PrinterSuppliesResult(address, string.Empty, Array.Empty<PrinterSupply>(),
            snmpError is null
                ? "The printer does not report supply levels (SNMP and IPP gave none)."
                : "The printer did not answer (SNMP may be turned off in its settings, and IPP gave no levels).");
    }

    /// <summary>Percent and state from a level and capacity as the Printer MIB or IPP report them.</summary>
    internal static PrinterSupply ToSupply(string name, int level, int maxCapacity, bool isWaste, int? lowPercent = null)
    {
        // Negative levels: -1 other, -2 unknown, -3 "at least one unit remains". Without a capacity, a level up to 100
        // is taken as a percentage (as some printers report it).
        if (level < 0 || maxCapacity == 0 || (maxCapacity < 0 && level > 100))
        {
            return new PrinterSupply(name, null, level == -3 ? SupplyState.Ok : SupplyState.Unknown, isWaste);
        }

        int percent = maxCapacity > 0 ? (int)Math.Round(Math.Clamp(level * 100.0 / maxCapacity, 0, 100)) : level;
        SupplyState state = isWaste
            ? percent >= 95 ? SupplyState.Empty : percent >= 85 ? SupplyState.Low : SupplyState.Ok
            : percent == 0 ? SupplyState.Empty : percent <= (lowPercent ?? LowPercent) ? SupplyState.Low : SupplyState.Ok;
        return new PrinterSupply(name, percent, state, isWaste);
    }

    /// <summary>"45%", "Low (8%)", "Empty", "30% full" for a waste box, or "OK" when only "some left" is reported.</summary>
    public static string Describe(PrinterSupply supply) => supply switch
    {
        { Percent: int percent, IsWaste: true } => supply.State == SupplyState.Ok ? $"{percent}% full" : supply.State == SupplyState.Empty ? "Full" : $"{percent}% full",
        { Percent: int percent } => supply.State == SupplyState.Empty ? "Empty" : supply.State == SupplyState.Low ? $"Low ({percent}%)" : $"{percent}%",
        { State: SupplyState.Ok } => "OK",
        _ => "Not reported"
    };

    // ---- SNMP v1 ---------------------------------------------------------------------------------------------

    /// <param name="port">161; tests use a stand-in printer on another port.</param>
    internal static async Task<List<PrinterSupply>> ReadSnmpAsync(string address, CancellationToken cancellationToken, int port = 161)
    {
        using UdpClient udp = new();
        udp.Connect(IPAddress.Parse(address), port);
        Dictionary<string, object> descriptions = await WalkAsync(udp, SuppliesDescription, cancellationToken);
        if (descriptions.Count == 0)
        {
            return new List<PrinterSupply>();
        }

        Dictionary<string, object> capacities = await WalkAsync(udp, SuppliesMaxCapacity, cancellationToken);
        Dictionary<string, object> levels = await WalkAsync(udp, SuppliesLevel, cancellationToken);
        Dictionary<string, object> classes = await WalkAsync(udp, SuppliesClass, cancellationToken);
        return descriptions.Select(entry =>
            {
                string name = entry.Value is byte[] bytes ? CleanText(bytes) : entry.Value.ToString() ?? "Supply";
                int level = levels.TryGetValue(entry.Key, out object? value) && value is long levelValue ? (int)levelValue : -2;
                int capacity = capacities.TryGetValue(entry.Key, out object? max) && max is long maxValue ? (int)maxValue : -2;
                // Class 4 is a receptacle that fills up (waste toner); 3 is consumed (toner, ink, drum).
                bool waste = classes.TryGetValue(entry.Key, out object? kind) && kind is long classValue && classValue == 4
                    || name.Contains("waste", StringComparison.OrdinalIgnoreCase);
                return ToSupply(name.Length == 0 ? "Supply" : name, level, capacity, waste);
            })
            .ToList();
    }

    /// <summary>Every value under a table column, keyed by the index after the column (GetNext until the column ends).</summary>
    private static async Task<Dictionary<string, object>> WalkAsync(UdpClient udp, string column, CancellationToken cancellationToken)
    {
        Dictionary<string, object> values = new();
        string oid = column;
        for (int step = 0; step < 64; step++)
        {
            (string next, object? value) = await GetNextAsync(udp, oid, cancellationToken);
            if (!next.StartsWith(column + ".", StringComparison.Ordinal) || value is null)
            {
                break;
            }

            values[next[(column.Length + 1)..]] = value;
            oid = next;
        }

        return values;
    }

    private static async Task<(string Oid, object? Value)> GetNextAsync(UdpClient udp, string oid, CancellationToken cancellationToken)
    {
        int requestId = Random.Shared.Next(1, int.MaxValue);
        byte[] request = Snmp.BuildGetNext(requestId, "public", oid);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(request, cancellationToken);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            try
            {
                while (true)
                {
                    UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token);
                    if (Snmp.ParseResponse(reply.Buffer) is { } response && response.RequestId == requestId)
                    {
                        return response.ErrorStatus == 0 ? (response.Oid, response.Value) : (string.Empty, null);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // No answer in time: try once more, then give up.
            }
        }

        throw new TimeoutException("No SNMP answer.");
    }

    private static string CleanText(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes).Replace("\0", string.Empty).Trim();
        return text.Any(character => char.IsControl(character)) ? Encoding.Latin1.GetString(bytes).Replace("\0", string.Empty).Trim() : text;
    }

    /// <summary>Just enough SNMP v1 (BER) for GetNext requests and their responses.</summary>
    internal static class Snmp
    {
        public sealed record Response(int RequestId, int ErrorStatus, string Oid, object? Value);

        public static byte[] BuildGetNext(int requestId, string community, string oid)
        {
            byte[] varBind = Sequence(0x30, Concat(EncodeOid(oid), new byte[] { 0x05, 0x00 }));
            byte[] pdu = Sequence(0xA1, Concat(EncodeInteger(requestId), EncodeInteger(0), EncodeInteger(0), Sequence(0x30, varBind)));
            return Sequence(0x30, Concat(EncodeInteger(0), Tlv(0x04, Encoding.ASCII.GetBytes(community)), pdu));
        }

        public static Response? ParseResponse(byte[] data)
        {
            try
            {
                int position = 0;
                ReadHeader(data, ref position, 0x30);
                ReadInteger(data, ref position);                       // version
                Skip(data, ref position);                              // community
                ReadHeader(data, ref position, 0xA2);                  // GetResponse
                int requestId = (int)ReadInteger(data, ref position);
                int errorStatus = (int)ReadInteger(data, ref position);
                ReadInteger(data, ref position);                       // error index
                ReadHeader(data, ref position, 0x30);                  // var bind list
                ReadHeader(data, ref position, 0x30);                  // first var bind
                int oidLength = ReadHeader(data, ref position, 0x06);
                string oid = DecodeOid(data, position, oidLength);
                position += oidLength;
                byte tag = data[position];
                int valueStart = position;
                int length = ReadLength(data, ref valueStart);
                valueStart++;
                object? value = tag switch
                {
                    0x02 or 0x41 or 0x42 or 0x43 => DecodeInteger(data, valueStart, length),
                    0x04 => data[valueStart..(valueStart + length)],
                    0x06 => DecodeOid(data, valueStart, length),
                    _ => null // NULL, noSuchObject, noSuchInstance, endOfMibView
                };
                return new Response(requestId, errorStatus, oid, value);
            }
            catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
            {
                return null;
            }
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

        private static byte[] Sequence(byte tag, byte[] content) => Tlv(tag, content);

        private static byte[] Tlv(byte tag, byte[] content) => Concat(new[] { tag }, EncodeLength(content.Length), content);

        private static byte[] EncodeLength(int length) => length < 0x80
            ? new[] { (byte)length }
            : length < 0x100 ? new byte[] { 0x81, (byte)length } : new byte[] { 0x82, (byte)(length >> 8), (byte)length };

        internal static byte[] EncodeInteger(long value)
        {
            List<byte> bytes = new();
            do
            {
                bytes.Insert(0, (byte)(value & 0xFF));
                value >>= 8;
            }
            while (value != 0 && value != -1);

            // Keep the sign bit right: a positive number whose top bit is set needs a leading zero (and vice versa).
            if (value == 0 && (bytes[0] & 0x80) != 0)
            {
                bytes.Insert(0, 0x00);
            }
            else if (value == -1 && (bytes[0] & 0x80) == 0)
            {
                bytes.Insert(0, 0xFF);
            }

            return Tlv(0x02, bytes.ToArray());
        }

        internal static byte[] EncodeOid(string oid)
        {
            long[] parts = oid.Split('.').Select(long.Parse).ToArray();
            List<byte> bytes = new() { (byte)(parts[0] * 40 + parts[1]) };
            foreach (long part in parts.Skip(2))
            {
                List<byte> encoded = new() { (byte)(part & 0x7F) };
                for (long rest = part >> 7; rest > 0; rest >>= 7)
                {
                    encoded.Insert(0, (byte)(0x80 | (rest & 0x7F)));
                }

                bytes.AddRange(encoded);
            }

            return Tlv(0x06, bytes.ToArray());
        }

        internal static string DecodeOid(byte[] data, int start, int length)
        {
            if (length == 0)
            {
                return string.Empty;
            }

            List<long> parts = new() { data[start] / 40, data[start] % 40 };
            long value = 0;
            for (int index = start + 1; index < start + length; index++)
            {
                value = (value << 7) | (data[index] & 0x7FL);
                if ((data[index] & 0x80) == 0)
                {
                    parts.Add(value);
                    value = 0;
                }
            }

            return string.Join(".", parts);
        }

        internal static long DecodeInteger(byte[] data, int start, int length)
        {
            long value = (data[start] & 0x80) != 0 ? -1 : 0;
            for (int index = start; index < start + length; index++)
            {
                value = (value << 8) | data[index];
            }

            return value;
        }

        private static int ReadHeader(byte[] data, ref int position, byte expectedTag)
        {
            if (data[position] != expectedTag)
            {
                throw new InvalidDataException($"Unexpected SNMP tag 0x{data[position]:X2}.");
            }

            int length = ReadLength(data, ref position);
            position++;
            return length;
        }

        /// <summary>Reads the length after the tag at <paramref name="position"/>; leaves position on the last length byte.</summary>
        private static int ReadLength(byte[] data, ref int position)
        {
            position++;
            int first = data[position];
            if (first < 0x80)
            {
                return first;
            }

            int count = first & 0x7F;
            int length = 0;
            for (int index = 0; index < count; index++)
            {
                position++;
                length = (length << 8) | data[position];
            }

            return length;
        }

        private static long ReadInteger(byte[] data, ref int position)
        {
            int length = ReadHeader(data, ref position, 0x02);
            long value = DecodeInteger(data, position, length);
            position += length;
            return value;
        }

        private static void Skip(byte[] data, ref int position)
        {
            int length = ReadLength(data, ref position);
            position += 1 + length;
        }
    }

    // ---- IPP ---------------------------------------------------------------------------------------------------

    private static async Task<List<PrinterSupply>> ReadIppAsync(string address, CancellationToken cancellationToken)
    {
        foreach ((string scheme, string uriScheme) in new[] { ("https", "ipps"), ("http", "ipp") })
        {
            try
            {
                byte[] body = BuildIppRequest($"{uriScheme}://{address}:631/ipp/print");
                using ByteArrayContent content = new(body);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
                using HttpResponseMessage response = await IppClient.PostAsync($"{scheme}://{address}:631/ipp/print", content, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                List<PrinterSupply> supplies = ParseIppMarkers(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                if (supplies.Count > 0)
                {
                    return supplies;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Try the other scheme.
            }
        }

        return new List<PrinterSupply>();
    }

    private static byte[] BuildIppRequest(string printerUri)
    {
        using MemoryStream stream = new();
        stream.Write(new byte[] { 0x02, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01, 0x01 }); // IPP/2.0 Get-Printer-Attributes, operation group
        WriteAttribute(stream, 0x47, "attributes-charset", "utf-8");
        WriteAttribute(stream, 0x48, "attributes-natural-language", "en");
        WriteAttribute(stream, 0x45, "printer-uri", printerUri);
        WriteAttribute(stream, 0x44, "requested-attributes", "marker-names");
        foreach (string attribute in new[] { "marker-levels", "marker-low-levels", "marker-types" })
        {
            WriteAttribute(stream, 0x44, string.Empty, attribute); // additional values of requested-attributes
        }

        stream.WriteByte(0x03);
        return stream.ToArray();
    }

    private static void WriteAttribute(Stream stream, byte tag, string name, string value)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);
        stream.WriteByte(tag);
        stream.WriteByte((byte)(nameBytes.Length >> 8));
        stream.WriteByte((byte)nameBytes.Length);
        stream.Write(nameBytes);
        stream.WriteByte((byte)(valueBytes.Length >> 8));
        stream.WriteByte((byte)valueBytes.Length);
        stream.Write(valueBytes);
    }

    /// <summary>marker-names with their marker-levels (percent), marker-low-levels and marker-types from an IPP response.</summary>
    internal static List<PrinterSupply> ParseIppMarkers(byte[] data)
    {
        Dictionary<string, List<object>> attributes = new();
        string? current = null;
        int position = 8;
        while (position < data.Length)
        {
            byte tag = data[position++];
            if (tag == 0x03)
            {
                break;
            }

            if (tag < 0x10 || position + 2 > data.Length)
            {
                continue;
            }

            int nameLength = (data[position] << 8) | data[position + 1];
            position += 2;
            if (position + nameLength + 2 > data.Length)
            {
                break;
            }

            if (nameLength > 0)
            {
                current = Encoding.ASCII.GetString(data, position, nameLength);
            }

            position += nameLength;
            int valueLength = (data[position] << 8) | data[position + 1];
            position += 2;
            if (position + valueLength > data.Length || current is null)
            {
                break;
            }

            object value = tag == 0x21 && valueLength == 4
                ? (object)((data[position] << 24) | (data[position + 1] << 16) | (data[position + 2] << 8) | data[position + 3])
                : Encoding.UTF8.GetString(data, position, valueLength);
            if (!attributes.TryGetValue(current, out List<object>? values))
            {
                attributes[current] = values = new List<object>();
            }

            values.Add(value);
            position += valueLength;
        }

        List<object> names = attributes.GetValueOrDefault("marker-names") ?? new List<object>();
        List<object> levels = attributes.GetValueOrDefault("marker-levels") ?? new List<object>();
        List<object> lows = attributes.GetValueOrDefault("marker-low-levels") ?? new List<object>();
        List<object> types = attributes.GetValueOrDefault("marker-types") ?? new List<object>();
        List<PrinterSupply> supplies = new();
        for (int index = 0; index < names.Count; index++)
        {
            int level = index < levels.Count && levels[index] is int value ? value : -2;
            int? low = index < lows.Count && lows[index] is int lowValue && lowValue > 0 ? lowValue : null;
            string type = index < types.Count ? types[index]?.ToString() ?? string.Empty : string.Empty;
            bool waste = type.StartsWith("waste", StringComparison.OrdinalIgnoreCase);
            supplies.Add(ToSupply(names[index]?.ToString() ?? "Supply", level, 100, waste, low));
        }

        return supplies;
    }
}
