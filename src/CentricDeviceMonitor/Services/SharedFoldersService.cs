using System.Collections.Concurrent;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

/// <summary>A folder shared from this PC.</summary>
public sealed record LocalShareInfo(string Name, string Path, string Description)
{
    public string UncPath => $@"\\{Environment.MachineName}\{Name}";
    public string Detail => string.IsNullOrWhiteSpace(Description) ? Path : $"{Path}  •  {Description}";
}

/// <summary>A share (or a PC whose shares need a sign-in to list) found on the local network.</summary>
public sealed record NetworkShareInfo(string Host, string Address, string ShareName, string Remark, bool NeedsSignIn)
{
    public string UncPath => string.IsNullOrEmpty(ShareName) ? $@"\\{Host}" : $@"\\{Host}\{ShareName}";

    public string Title => NeedsSignIn ? $@"\\{Host}" : UncPath;

    public string Detail => NeedsSignIn
        ? $"{Address}  •  Shares are hidden until you sign in. Open it to browse with a user name and password."
        : string.IsNullOrWhiteSpace(Remark) ? Address : $"{Address}  •  {Remark}";

    /// <summary>Name used for the File Explorer network location.</summary>
    public string LocationName => string.IsNullOrEmpty(ShareName) ? Host : $"{ShareName} ({Host})";
}

/// <summary>
/// Lists folders shared from this PC (WMI Win32_Share) and shares on other PCs of the local subnet (SMB port 445 +
/// NetShareEnum), and adds shares to File Explorer as network locations under This PC.
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

    public async Task<IReadOnlyList<NetworkShareInfo>> ScanNetworkSharesAsync(CancellationToken cancellationToken = default)
    {
        LocalSubnetInfo subnet = _subnetService.GetLocalSubnet()
            ?? throw new InvalidOperationException("No active Ethernet or Wi-Fi network was found to scan.");

        ConcurrentBag<string> smbHosts = new();
        await Parallel.ForEachAsync(subnet.Addresses, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                using TcpClient client = new();
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(400));
                try
                {
                    await client.ConnectAsync(address, 445, timeout.Token);
                    smbHosts.Add(address);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Not a file-sharing host.
                }
            });

        ConcurrentBag<NetworkShareInfo> shares = new();
        await Parallel.ForEachAsync(smbHosts, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (address, token) =>
            {
                string host = await ResolveHostAsync(address, token);
                IReadOnlyList<(string Name, string Remark)>? found = await Task.Run(() => EnumerateShares(host), token)
                    .WaitAsync(TimeSpan.FromSeconds(8), token)
                    .ContinueWith(task => task.IsCompletedSuccessfully ? task.Result : null, TaskScheduler.Default);

                if (found is null)
                {
                    shares.Add(new NetworkShareInfo(host, address, string.Empty, string.Empty, NeedsSignIn: true));
                    return;
                }

                foreach ((string name, string remark) in found)
                {
                    shares.Add(new NetworkShareInfo(host, address, name, remark, NeedsSignIn: false));
                }
            });

        return shares
            .OrderBy(share => share.NeedsSignIn)
            .ThenBy(share => share.Host, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(share => share.ShareName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
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
