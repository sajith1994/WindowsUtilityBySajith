using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CentricDeviceMonitor.Services;

/// <summary>A folder shared from this PC.</summary>
public sealed record LocalShareInfo(string Name, string Path, string Description)
{
    public string UncPath => $@"\\{Environment.MachineName}\{Name}";
    public string Detail => string.IsNullOrWhiteSpace(Description) ? Path : $"{Path}  •  {Description}";
}

/// <summary>
/// A share (or a PC/NAS whose shares need a sign-in to list) found on the local network. DeviceLabel says what the
/// device is ("NAS • Synology DS920+", "Windows PC"); AdminUrl is a NAS's web admin page when one was found.
/// </summary>
public sealed record NetworkShareInfo(string Host, string Address, string ShareName, string Remark, bool NeedsSignIn,
    string DeviceLabel = "", string? AdminUrl = null, bool IsNas = false)
{
    public string UncPath => string.IsNullOrEmpty(ShareName) ? $@"\\{Host}" : $@"\\{Host}\{ShareName}";

    public string Title => NeedsSignIn ? $@"\\{Host}" : UncPath;

    public string Detail => string.Join("  •  ", new[]
    {
        DeviceLabel,
        Address,
        NeedsSignIn ? "Shares are hidden until you sign in. Open it to browse with a user name and password." : Remark
    }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public System.Windows.Visibility AdminVisibility => AdminUrl is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>Name used for the File Explorer network location.</summary>
    public string LocationName => string.IsNullOrEmpty(ShareName) ? Host : $"{ShareName} ({Host})";
}

public enum AddedFolderKind
{
    NetworkLocation,
    MappedDrive
}

public enum ConnectionState
{
    NotTested,
    Testing,
    Connected,
    NeedsSignIn,
    Failed
}

/// <summary>
/// A network folder added to this PC: a network location under This PC, or a mapped network drive. The connection
/// status changes as it is tested, so the list updates in place.
/// </summary>
public sealed class AddedNetworkFolder : INotifyPropertyChanged
{
    private ConnectionState _state = ConnectionState.NotTested;
    private string _statusText = "Not tested";

    public AddedNetworkFolder(AddedFolderKind kind, string name, string target, string? locationPath = null, string? driveLetter = null, bool reconnects = false)
    {
        Kind = kind;
        Name = name;
        Target = target;
        LocationPath = locationPath;
        DriveLetter = driveLetter;
        Reconnects = reconnects;
    }

    public AddedFolderKind Kind { get; }
    public string Name { get; }
    public string Target { get; }

    /// <summary>The network-location folder (or shortcut file) under Network Shortcuts.</summary>
    public string? LocationPath { get; }

    /// <summary>Drive letter with colon (Z:) for mapped drives.</summary>
    public string? DriveLetter { get; }

    /// <summary>Mapped drive saved in the user profile, reconnected at every sign-in.</summary>
    public bool Reconnects { get; }

    public string Title => Kind == AddedFolderKind.MappedDrive ? $"{DriveLetter}  {Target}" : Name;

    public string Detail => Kind == AddedFolderKind.MappedDrive
        ? Reconnects ? "Mapped network drive  •  reconnects at sign-in" : "Mapped network drive  •  this session only"
        : $"Network location  •  {Target}";

    /// <summary>What Open hands to File Explorer.</summary>
    public string OpenPath => Kind == AddedFolderKind.MappedDrive ? Target : LocationPath ?? Target;

    public ConnectionState State
    {
        get => _state;
        private set { _state = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { _statusText = value; OnPropertyChanged(); }
    }

    public void SetStatus(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record FolderRemoveResult(bool Removed, string Message);

/// <summary>
/// Lists folders shared from this PC (WMI Win32_Share) and shares on other PCs of the local subnet (SMB port 445 +
/// NetShareEnum), adds shares to File Explorer as network locations under This PC, and lists, tests and removes the
/// network locations and mapped drives already added.
/// </summary>
public sealed class SharedFoldersService
{
    private readonly LocalIpScannerService _subnetService = new();

    public Task<IReadOnlyList<LocalShareInfo>> GetLocalSharesAsync() => Task.Run<IReadOnlyList<LocalShareInfo>>(() =>
    {
        List<LocalShareInfo> shares = new();
        using ManagementObjectSearcher searcher = new("SELECT Name, Path, Description, Type FROM Win32_Share");
        foreach (ManagementBaseObject share in searcher.Get())
        {
            using (share)
            {
                string name = share["Name"] as string ?? string.Empty;
                uint type = share["Type"] is uint value ? value : 0;
                // Type 0 = disk folder; names ending in $ are hidden administrative shares (C$, ADMIN$, print$).
                if (type != 0 || name.EndsWith('$'))
                {
                    continue;
                }

                shares.Add(new LocalShareInfo(name, share["Path"] as string ?? string.Empty, share["Description"] as string ?? string.Empty));
            }
        }

        return shares.OrderBy(share => share.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>
    /// PCs and NAS boxes with file sharing on every local subnet: a TCP check of SMB (445, then 139) on each address,
    /// plus mDNS (Bonjour) announcements, which find NAS boxes that ignore ping or only offer NFS/AFP. Each device is
    /// identified (NAS brand and model, Windows PC) and its shares are listed when Windows is allowed to.
    /// </summary>
    public async Task<IReadOnlyList<NetworkShareInfo>> ScanNetworkSharesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalSubnetInfo> subnets = _subnetService.GetLocalSubnets();
        if (subnets.Count == 0)
        {
            throw new InvalidOperationException("No active Ethernet or Wi-Fi network was found to scan.");
        }

        // Every address of this PC (a PC with Wi-Fi and a cable on one network has two), so it never lists itself.
        HashSet<string> own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(unicast => unicast.Address.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Task<IReadOnlyDictionary<string, MdnsDevice>> mdnsTask = MdnsDiscovery.DiscoverAsync(TimeSpan.FromSeconds(3), cancellationToken);

        ConcurrentDictionary<string, bool> smbHosts = new(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(subnets.SelectMany(subnet => subnet.Addresses).Distinct(),
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                // A slow Wi-Fi device or a NAS waking its network port can take most of a second to answer.
                if (await IsPortOpenAsync(address, 445, 1000, token) || await IsPortOpenAsync(address, 139, 700, token))
                {
                    smbHosts[address] = true;
                }
            });

        IReadOnlyDictionary<string, MdnsDevice> mdns = await mdnsTask;
        IReadOnlyDictionary<string, string> arp = await LocalIpScannerService.ReadArpCacheAsync(cancellationToken);
        HashSet<string> candidates = new(smbHosts.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (MdnsDevice device in mdns.Values.Where(device => device.Services.Any(IsFileService)))
        {
            candidates.Add(device.Address);
        }

        candidates.ExceptWith(own);

        ConcurrentBag<NetworkShareInfo> shares = new();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                mdns.TryGetValue(address, out MdnsDevice? announced);
                arp.TryGetValue(address, out string? mac);
                Task<string> hostTask = ResolveHostAsync(address, token);
                Task<DeviceIdentity> identityTask = IdentifyAsync(address, announced, mac, token);
                string host = await hostTask;
                DeviceIdentity identity = await identityTask;
                if (host == address && identity.Name.Length == 0 && !string.IsNullOrEmpty(announced?.HostName))
                {
                    identity = identity with { Name = announced.HostName };
                }

                string label = identity.Name.Length > 0 && !string.Equals(identity.Name, host, StringComparison.OrdinalIgnoreCase)
                    ? $"{identity.Label} ({identity.Name})"
                    : identity.Label;

                if (!smbHosts.ContainsKey(address))
                {
                    shares.Add(new NetworkShareInfo(host, address, string.Empty,
                        "No Windows file sharing (SMB) answered; it may only offer NFS, AFP or its web interface.", false,
                        label, identity.AdminUrl, identity.IsNas));
                    return;
                }

                IReadOnlyList<(string Name, string Remark)>? found = await Task.Run(() => EnumerateShares(host), token)
                    .WaitAsync(TimeSpan.FromSeconds(8), token)
                    .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
                if (found is null && host != address)
                {
                    // Some NAS boxes answer by address but not by the name DNS returned.
                    found = await Task.Run(() => EnumerateShares(address), token)
                        .WaitAsync(TimeSpan.FromSeconds(8), token)
                        .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
                }

                if (found is null || found.Count == 0)
                {
                    shares.Add(new NetworkShareInfo(host, address, string.Empty, string.Empty, found is null, label, identity.AdminUrl, identity.IsNas));
                    return;
                }

                foreach ((string name, string remark) in found)
                {
                    shares.Add(new NetworkShareInfo(host, address, name, remark, false, label, identity.AdminUrl, identity.IsNas));
                }
            });

        List<NetworkShareInfo> ordered = shares
            .OrderByDescending(share => share.IsNas)
            .ThenBy(share => share.NeedsSignIn)
            .ThenBy(share => share.Host, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(share => share.ShareName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplicationLogService.WriteMessage("Network share scan",
            $"Searched {string.Join(", ", subnets.Select(subnet => subnet.Cidr))}: {smbHosts.Count} with SMB, {mdns.Count} answered mDNS, "
            + $"{ordered.Select(share => share.Address).Distinct().Count()} devices listed ({ordered.Where(share => share.IsNas).Select(share => share.Address).Distinct().Count()} NAS).");
        return ordered;
    }

    private static bool IsFileService(string service) =>
        service.StartsWith("_smb.", StringComparison.OrdinalIgnoreCase)
        || service.StartsWith("_afpovertcp.", StringComparison.OrdinalIgnoreCase)
        || service.StartsWith("_nfs.", StringComparison.OrdinalIgnoreCase)
        || service.StartsWith("_adisk.", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> IsPortOpenAsync(string address, int port, int timeoutMs, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed record DeviceIdentity(string Label, string Name, string? AdminUrl, bool IsNas);

    private static readonly (string Marker, string Brand)[] NasBrands =
    {
        ("synology", "Synology"), ("diskstation", "Synology"), ("rackstation", "Synology"), ("qnap", "QNAP"),
        ("truenas", "TrueNAS"), ("freenas", "TrueNAS"), ("ixsystems", "TrueNAS"), ("unraid", "Unraid"), ("asustor", "Asustor"),
        ("terramaster", "TerraMaster"), ("readynas", "Netgear ReadyNAS"), ("wdmycloud", "WD My Cloud"), ("mycloud", "WD My Cloud"),
        ("linkstation", "Buffalo"), ("terastation", "Buffalo"), ("openmediavault", "OpenMediaVault"), ("ugreen", "UGREEN"), ("drobo", "Drobo")
    };

    // Network-card makers used only by NAS brands (first three bytes of the MAC address).
    private static readonly (string Prefix, string Brand)[] NasMacPrefixes =
    {
        ("00-11-32", "Synology"), ("90-09-D0", "Synology"), ("00-08-9B", "QNAP"), ("24-5E-BE", "QNAP"),
        ("00-90-A9", "Western Digital"), ("00-14-EE", "Western Digital")
    };

    // Models Bonjour uses only to pick an icon on Macs; Samba-based NAS boxes announce these, so they mean "file server".
    private static readonly string[] ServerIconModels = { "Xserve", "RackMac", "MacPro", "MacSamba", "TimeCapsule", "PowerMac" };

    private static async Task<DeviceIdentity> IdentifyAsync(string address, MdnsDevice? announced, string? mac, CancellationToken cancellationToken)
    {
        int[] ports = { 5000, 5001, 8080, 443, 80, 2049, 548, 3389 };
        bool[] results = await Task.WhenAll(ports.Select(port => IsPortOpenAsync(address, port, 700, cancellationToken)));
        HashSet<int> open = ports.Where((_, index) => results[index]).ToHashSet();

        string announcedText = announced is null
            ? string.Empty
            : string.Join(" ", new[] { announced.HostName }.Concat(announced.InstanceNames).Concat(announced.Txt.Values));
        string haystack = announcedText.ToLowerInvariant();
        string? brand = NasBrands.FirstOrDefault(entry => haystack.Contains(entry.Marker, StringComparison.Ordinal)).Brand;
        string macPrefix = (mac ?? string.Empty).ToUpperInvariant().Replace(':', '-');
        brand ??= NasMacPrefixes.FirstOrDefault(entry => macPrefix.StartsWith(entry.Prefix, StringComparison.Ordinal)).Brand;

        string model = announced?.Txt.TryGetValue("model", out string? announcedModel) == true ? announcedModel.Trim() : string.Empty;
        bool serverIcon = ServerIconModels.Any(hint => model.StartsWith(hint, StringComparison.OrdinalIgnoreCase));
        if (serverIcon || model.Length > 40)
        {
            model = string.Empty;
        }

        bool fileServices = announced?.Services.Any(service =>
            service.StartsWith("_afpovertcp.", StringComparison.OrdinalIgnoreCase)
            || service.StartsWith("_nfs.", StringComparison.OrdinalIgnoreCase)
            || service.StartsWith("_adisk.", StringComparison.OrdinalIgnoreCase)) == true;
        bool isNas = brand is not null || serverIcon || fileServices
            || open.Contains(5000) || open.Contains(5001) || open.Contains(2049) || open.Contains(548);
        string name = announced?.HostName ?? string.Empty;

        if (!isNas)
        {
            bool windows = open.Contains(3389)
                || System.Text.RegularExpressions.Regex.IsMatch(name, "^(DESKTOP|LAPTOP)-", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return new DeviceIdentity(windows ? "Windows PC" : string.Empty, name, null, false);
        }

        string? adminUrl = brand switch
        {
            "Synology" when open.Contains(5001) => $"https://{address}:5001",
            "Synology" when open.Contains(5000) => $"http://{address}:5000",
            "QNAP" when open.Contains(443) => $"https://{address}",
            "QNAP" when open.Contains(8080) => $"http://{address}:8080",
            _ when open.Contains(5001) => $"https://{address}:5001",
            _ when open.Contains(5000) => $"http://{address}:5000",
            _ when open.Contains(443) => $"https://{address}",
            _ when open.Contains(80) => $"http://{address}",
            _ when open.Contains(8080) => $"http://{address}:8080",
            _ => null
        };

        string label = string.Join(" ", new[] { "NAS •", brand ?? string.Empty, model }.Where(part => part.Length > 0));
        return new DeviceIdentity(label == "NAS •" ? "NAS" : label, name, adminUrl, true);
    }
    private static async Task<string> ResolveHostAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(1.5));
            IPHostEntry entry = await Dns.GetHostEntryAsync(address, timeout.Token);
            string name = entry.HostName.Split('.')[0];
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(entry.HostName, out _) ? address : name;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return address;
        }
    }

    /// <summary>Visible disk shares on a host, or null when the host refuses to list them (sign-in needed).</summary>
    private static IReadOnlyList<(string Name, string Remark)>? EnumerateShares(string host)
    {
        int resume = 0;
        int result = NetShareEnum(host, 1, out IntPtr buffer, -1, out int entries, out _, ref resume);
        try
        {
            if (result != 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            List<(string, string)> shares = new();
            int size = Marshal.SizeOf<SHARE_INFO_1>();
            for (int index = 0; index < entries; index++)
            {
                SHARE_INFO_1 info = Marshal.PtrToStructure<SHARE_INFO_1>(buffer + index * size);
                bool isDisk = (info.shi1_type & 0xFF) == 0;
                bool isSpecial = (info.shi1_type & 0x80000000) != 0;
                if (isDisk && !isSpecial && !string.IsNullOrEmpty(info.shi1_netname) && !info.shi1_netname.EndsWith('$'))
                {
                    shares.Add((info.shi1_netname, info.shi1_remark ?? string.Empty));
                }
            }

            return shares;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    /// <summary>
    /// Adds a share to File Explorer as a network location (This PC > Network locations), the same structure Windows'
    /// "Add a network location" creates. Nothing is mapped and no credentials are stored.
    /// </summary>
    public static string AddToFileExplorer(string uncPath, string displayName)
    {
        if (!uncPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only network paths (\\\\computer\\share) can be added.");
        }

        string safeName = string.Join("_", displayName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Network Shortcuts");
        string folder = Path.Combine(root, string.IsNullOrWhiteSpace(safeName) ? "Network share" : safeName);
        if (Directory.Exists(folder))
        {
            return folder;
        }

        Directory.CreateDirectory(folder);
        CreateShortcut(Path.Combine(folder, "target.lnk"), uncPath);

        string desktopIni = Path.Combine(folder, "desktop.ini");
        File.WriteAllText(desktopIni, "[.ShellClassInfo]\r\nCLSID2={0AFACED1-E828-11D1-9187-B532F1E9575D}\r\nFlags=2\r\n");
        File.SetAttributes(desktopIni, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
        return folder;
    }

    private static string NetworkShortcutsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Network Shortcuts");

    /// <summary>
    /// Network locations under This PC (Network Shortcuts folder) and mapped network drives. Mapped drives come from
    /// the user profile (HKCU\Network, the ones that reconnect at sign-in) because this elevated dashboard runs in a
    /// separate logon session that cannot see the drives mapped in the normal Explorer session; drives connected in
    /// this session are added too.
    /// </summary>
    public Task<IReadOnlyList<AddedNetworkFolder>> GetAddedNetworkFoldersAsync() => Task.Run<IReadOnlyList<AddedNetworkFolder>>(() =>
    {
        List<AddedNetworkFolder> folders = new();
        string root = NetworkShortcutsRoot;
        if (Directory.Exists(root))
        {
            foreach (string folder in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                string link = Path.Combine(folder, "target.lnk");
                if (File.Exists(link))
                {
                    folders.Add(new AddedNetworkFolder(AddedFolderKind.NetworkLocation, Path.GetFileName(folder), ReadShortcutTarget(link), folder));
                }
            }

            foreach (string link in Directory.EnumerateFiles(root, "*.lnk").OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase))
            {
                folders.Add(new AddedNetworkFolder(AddedFolderKind.NetworkLocation, Path.GetFileNameWithoutExtension(link), ReadShortcutTarget(link), link));
            }
        }

        SortedDictionary<string, AddedNetworkFolder> drives = new(StringComparer.OrdinalIgnoreCase);
        using (RegistryKey? network = Registry.CurrentUser.OpenSubKey("Network"))
        {
            foreach (string name in network?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using RegistryKey? drive = network!.OpenSubKey(name);
                if (name.Length == 1 && char.IsLetter(name[0]) && drive?.GetValue("RemotePath") is string remote && remote.Length > 0)
                {
                    string letter = name.ToUpperInvariant() + ":";
                    drives[letter] = new AddedNetworkFolder(AddedFolderKind.MappedDrive, letter, remote, driveLetter: letter, reconnects: true);
                }
            }
        }

        try
        {
            using ManagementObjectSearcher searcher = new("SELECT DeviceID, ProviderName FROM Win32_LogicalDisk WHERE DriveType = 4");
            foreach (ManagementBaseObject disk in searcher.Get())
            {
                using (disk)
                {
                    if (disk["DeviceID"] is string letter && disk["ProviderName"] is string remote && remote.Length > 0 && !drives.ContainsKey(letter))
                    {
                        drives[letter] = new AddedNetworkFolder(AddedFolderKind.MappedDrive, letter, remote, driveLetter: letter, reconnects: false);
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // The saved drives from the profile are still listed.
        }

        folders.AddRange(drives.Values);
        return folders;
    });

    /// <summary>Checks that the folder's computer answers and that the folder itself can be opened.</summary>
    public static async Task TestConnectionAsync(AddedNetworkFolder folder, CancellationToken cancellationToken = default)
    {
        folder.SetStatus(ConnectionState.Testing, "Testing...");
        (ConnectionState state, string text) = await TestTargetAsync(folder.Target, cancellationToken);
        folder.SetStatus(state, text);
    }

    private static async Task<(ConnectionState State, string Text)> TestTargetAsync(string target, CancellationToken cancellationToken)
    {
        if (target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string host = target.TrimStart('\\').Split('\\')[0];
            long? ms = await ConnectAsync(host, 445, cancellationToken);
            if (ms is null)
            {
                return (ConnectionState.Failed, $"Not reachable: {host} did not answer on file sharing (port 445). It may be off, asleep, renamed or blocked by a firewall.");
            }

            if (target.TrimStart('\\').TrimEnd('\\').IndexOf('\\') < 0)
            {
                // A whole computer (\\PC) rather than one shared folder: check that its shares can be listed.
                IReadOnlyList<(string Name, string Remark)>? shares = await Task.Run(() => EnumerateShares(host), cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
                    .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);
                return shares is null
                    ? (ConnectionState.NeedsSignIn, $"Reachable ({ms} ms). Its shared folders are hidden until you sign in; open it to enter a user name and password.")
                    : (ConnectionState.Connected, $"Connected  •  {ms} ms  •  {shares.Count} shared folder{(shares.Count == 1 ? "" : "s")}");
            }

            try
            {
                await Task.Run(() =>
                {
                    using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
                    entries.MoveNext();
                }, cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                return (ConnectionState.Connected, $"Connected  •  {ms} ms");
            }
            catch (UnauthorizedAccessException)
            {
                return (ConnectionState.NeedsSignIn, $"Reachable ({ms} ms), but access was denied. Open it to sign in.");
            }
            catch (DirectoryNotFoundException)
            {
                return (ConnectionState.Failed, $"{host} answered ({ms} ms), but the shared folder was not found.");
            }
            catch (TimeoutException)
            {
                return (ConnectionState.Failed, $"{host} answered ({ms} ms), but the folder did not respond within 10 seconds.");
            }
            catch (IOException exception)
            {
                int code = exception.HResult & 0xFFFF;
                // 1326 wrong password, 1219 other credentials in use, 1244 not authenticated, 86 bad password,
                // 1331 account disabled, 1909 account locked, 1907 password must change.
                if (code is 1326 or 1219 or 1244 or 86 or 1331 or 1907 or 1909)
                {
                    return (ConnectionState.NeedsSignIn, $"Reachable ({ms} ms), sign-in needed. Open it to enter a user name and password.");
                }

                // 67 network name not found, 53 network path not found, 3 path not found.
                return code is 67 or 53 or 3
                    ? (ConnectionState.Failed, $"{host} answered ({ms} ms), but the shared folder was not found.")
                    : (ConnectionState.Failed, $"{host} answered ({ms} ms), but the folder could not be opened: {exception.Message}");
            }
        }

        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0)
        {
            if (uri.Scheme is "http" or "https")
            {
                try
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    using HttpRequestMessage request = new(HttpMethod.Head, uri);
                    using HttpResponseMessage response = await WebClient.SendAsync(request, cancellationToken);
                    long elapsed = watch.ElapsedMilliseconds;
                    return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        ? (ConnectionState.NeedsSignIn, $"Reachable ({elapsed} ms), sign-in needed.")
                        : (ConnectionState.Connected, $"Connected  •  {elapsed} ms");
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    return (ConnectionState.Failed, $"Not reachable: {uri.Host} did not answer.");
                }
            }

            long? ms = await ConnectAsync(uri.Host, uri.Port, cancellationToken);
            return ms is null
                ? (ConnectionState.Failed, $"Not reachable: {uri.Host} did not answer on port {uri.Port}.")
                : (ConnectionState.Connected, $"Connected  •  {ms} ms");
        }

        return (ConnectionState.Failed, "The address of this location could not be read.");
    }

    private static readonly HttpClient WebClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>TCP connect time in milliseconds, or null when the host cannot be found or does not answer.</summary>
    private static async Task<long?> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            IPAddress[] addresses = IPAddress.TryParse(host, out IPAddress? literal)
                ? new[] { literal }
                : await Dns.GetHostAddressesAsync(host, timeout.Token);
            addresses = addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            if (addresses.Length == 0)
            {
                return null;
            }

            using TcpClient client = new(addresses[0].AddressFamily);
            Stopwatch watch = Stopwatch.StartNew();
            await client.ConnectAsync(addresses[0], port, timeout.Token);
            return Math.Max(1, watch.ElapsedMilliseconds);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes a network location from This PC, or disconnects a mapped drive and stops it reconnecting at sign-in.
    /// The drive is disconnected in the user's normal session too (through a one-off scheduled task that runs as the
    /// signed-in user without elevation), because the elevated dashboard cannot see that session's drives.
    /// </summary>
    public async Task<FolderRemoveResult> RemoveAddedFolderAsync(AddedNetworkFolder folder, CancellationToken cancellationToken = default)
    {
        if (folder.Kind == AddedFolderKind.NetworkLocation)
        {
            return await Task.Run(() => RemoveNetworkLocation(folder), cancellationToken);
        }

        string letter = folder.DriveLetter ?? string.Empty;
        if (letter.Length != 2 || !char.IsLetter(letter[0]) || letter[1] != ':')
        {
            return new FolderRemoveResult(false, "The drive letter could not be read.");
        }

        string output = await RunPowerShellAsync(BuildDisconnectDriveScript(letter), cancellationToken);
        string[] lines = output.Split('\n').Select(line => line.Trim()).ToArray();
        bool removed = lines.Any(line => line == "RESULT:REMOVED");
        bool sessionDone = lines.Any(line => line == "SESSION:DONE");
        if (!removed)
        {
            return new FolderRemoveResult(false, $"{letter} could not be removed from your profile. {output.Trim()}");
        }

        return new FolderRemoveResult(true, sessionDone
            ? $"{letter} ({folder.Target}) was disconnected and will not reconnect at sign-in."
            : $"{letter} ({folder.Target}) will not reconnect at sign-in. It may stay in File Explorer until you sign out.");
    }

    internal static string BuildDisconnectDriveScript(string letter) => $$"""
        $letter = '{{letter}}'
        $task = 'WindowsUtility-DisconnectDrive-' + [guid]::NewGuid().ToString('N')
        $session = 'SKIPPED'
        try {
            $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
            $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument ('/c net use ' + $letter + ' /delete /y')
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Force -ErrorAction Stop | Out-Null
            Start-ScheduledTask -TaskName $task -ErrorAction Stop
            $deadline = (Get-Date).AddSeconds(15)
            do { Start-Sleep -Milliseconds 500; $state = (Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue).State }
            while (($state -eq 'Running' -or $state -eq 'Queued') -and (Get-Date) -lt $deadline)
            $session = 'DONE'
        } catch { $session = 'FAILED ' + $_.Exception.Message }
        finally { Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue }
        & net.exe use $letter /delete /y 2>&1 | Out-Null
        $key = 'HKCU:\Network\' + $letter.TrimEnd(':')
        Remove-Item -Path $key -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $key) { 'RESULT:FAIL' } else { 'RESULT:REMOVED' }
        'SESSION:' + $session
        """;

    private static FolderRemoveResult RemoveNetworkLocation(AddedNetworkFolder folder)
    {
        string root = Path.GetFullPath(NetworkShortcutsRoot) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(folder.LocationPath ?? string.Empty);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return new FolderRemoveResult(false, "Only network locations in This PC > Network locations can be removed here.");
        }

        if (Directory.Exists(path))
        {
            // Network-location folders are read-only and their desktop.ini is hidden + system.
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            File.SetAttributes(path, FileAttributes.Directory);
            Directory.Delete(path, recursive: true);
            SHChangeNotify(ShcneRmdir, ShcnfPathW, path, IntPtr.Zero);
        }
        else if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            SHChangeNotify(ShcneDelete, ShcnfPathW, path, IntPtr.Zero);
        }
        else
        {
            return new FolderRemoveResult(true, $"\"{folder.Name}\" was already removed.");
        }

        return new FolderRemoveResult(true, $"\"{folder.Name}\" was removed from This PC > Network locations.");
    }

    /// <summary>Target of a shortcut: the path WScript reports, or the first network address stored in the file.</summary>
    private static string ReadShortcutTarget(string shortcutPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is not null)
            {
                dynamic shell = Activator.CreateInstance(shellType)!;
                try
                {
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    string target = shortcut.TargetPath as string ?? string.Empty;
                    Marshal.FinalReleaseComObject(shortcut);
                    if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.Contains("://", StringComparison.Ordinal))
                    {
                        return target;
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }
        catch (Exception)
        {
            // Fall back to reading the shortcut file.
        }

        try
        {
            // Shortcuts made by "Add a network location" keep the target in their item list; it is stored as text.
            byte[] bytes = File.ReadAllBytes(shortcutPath);
            foreach (string text in new[] { System.Text.Encoding.Unicode.GetString(bytes), System.Text.Encoding.Latin1.GetString(bytes) })
            {
                System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(text,
                    @"(\\\\[A-Za-z0-9._\-$]+(?:\\[^\x00-\x1f\\""<>|*?]+)*|(?:https?|ftp)://[^\x00-\x20""<>]+)");
                if (match.Success)
                {
                    return match.Value.TrimEnd('\\');
                }
            }
        }
        catch (IOException)
        {
        }

        return "(address unknown)";
    }

    private static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell could not be started.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(1));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("Disconnecting the drive took too long and was stopped.");
        }

        return (await output) + Environment.NewLine + (await error);
    }

    private const int ShcneDelete = 0x4;
    private const int ShcneRmdir = 0x10;
    private const uint ShcnfPathW = 0x5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

    private static void CreateShortcut(string shortcutPath, string target)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = target;
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHARE_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string shi1_netname;
        public uint shi1_type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? shi1_remark;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetShareEnum(string serverName, int level, out IntPtr buffer, int preferredMaximumLength,
        out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
