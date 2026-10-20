using System.Management;
using CentricDeviceMonitor.Models;
using Microsoft.Win32;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Windows Update, antivirus and firewall state for the remote stats. Reading it takes a few seconds (Windows Update
/// history, WMI), so it is refreshed in the background every 30 minutes and the last result is served. Nothing is
/// changed: pending updates come from Windows Update's own cached scan (no download, no online search).
/// </summary>
internal sealed class SecurityStatusProvider
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private readonly object _lock = new();
    private RemoteSecurityInfo? _current;
    private DateTime _readAt = DateTime.MinValue;
    private bool _refreshing;

    public RemoteSecurityInfo? Current
    {
        get
        {
            lock (_lock)
            {
                if (!_refreshing && DateTime.UtcNow - _readAt > Lifetime)
                {
                    _refreshing = true;
                    _ = Task.Run(Refresh);
                }

                return _current;
            }
        }
    }

    private void Refresh()
    {
        RemoteSecurityInfo info = new() { CheckedUtc = DateTime.UtcNow };
        try
        {
            ReadWindowsUpdate(info);
            ReadRebootRequired(info);
            ReadAntivirus(info);
            ReadFirewall(info);
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Remote monitoring security status", exception);
        }
        finally
        {
            lock (_lock)
            {
                _current = info;
                _readAt = DateTime.UtcNow;
                _refreshing = false;
            }
        }
    }

    /// <summary>Last successfully installed update (definition updates excluded) and the pending count from the last scan.</summary>
    private static void ReadWindowsUpdate(RemoteSecurityInfo info)
    {
        try
        {
            Type? sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
            {
                return;
            }

            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            int total = searcher.GetTotalHistoryCount();
            if (total > 0)
            {
                dynamic history = searcher.QueryHistory(0, Math.Min(total, 200));
                int count = history.Count;
                for (int index = 0; index < count; index++)
                {
                    dynamic entry = history.Item(index);
                    string title = entry.Title ?? string.Empty;
                    int operation = entry.Operation;   // 1 = installation
                    int result = entry.ResultCode;     // 2 = succeeded, 3 = succeeded with errors
                    if (operation != 1 || result is not (2 or 3) || IsDefinitionUpdate(title))
                    {
                        continue;
                    }

                    DateTime date = entry.Date;
                    DateTime utc = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                    if (info.LastUpdateInstalledUtc is null || utc > info.LastUpdateInstalledUtc)
                    {
                        info.LastUpdateInstalledUtc = utc;
                        info.LastUpdateTitle = title;
                    }
                }
            }

            // Results of Windows Update's own last scan; no online search is started.
            searcher.Online = false;
            dynamic pending = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            int pendingCount = 0;
            int updates = pending.Updates.Count;
            for (int index = 0; index < updates; index++)
            {
                string title = pending.Updates.Item(index).Title ?? string.Empty;
                if (!IsDefinitionUpdate(title))
                {
                    pendingCount++;
                }
            }

            info.PendingUpdates = pendingCount;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidCastException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            ServiceLog.Write("Remote monitoring", $"Windows Update status unavailable: {exception.Message}");
        }
    }

    private static bool IsDefinitionUpdate(string title) =>
        title.Contains("Security Intelligence", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Definition Update", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Malicious Software Removal", StringComparison.OrdinalIgnoreCase);

    private static void ReadRebootRequired(RemoteSecurityInfo info)
    {
        using RegistryKey? windowsUpdate = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        using RegistryKey? servicing = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        info.RestartRequired = windowsUpdate is not null || servicing is not null;
    }

    /// <summary>Microsoft Defender details, or the registered third-party antivirus when Defender is not the active one.</summary>
    private static void ReadAntivirus(RemoteSecurityInfo info)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\Microsoft\Windows\Defender",
                "SELECT AMServiceEnabled, AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime, FullScanEndTime FROM MSFT_MpComputerStatus");
            foreach (ManagementBaseObject status in searcher.Get())
            {
                using (status)
                {
                    if (status["AMServiceEnabled"] is true && status["AntivirusEnabled"] is true)
                    {
                        info.AntivirusName = "Microsoft Defender";
                        info.AntivirusOn = true;
                        info.RealTimeProtectionOn = status["RealTimeProtectionEnabled"] as bool?;
                        info.DefinitionsUpdatedUtc = ToUtc(status["AntivirusSignatureLastUpdated"]);
                        DateTime? quick = ToUtc(status["QuickScanEndTime"]);
                        DateTime? full = ToUtc(status["FullScanEndTime"]);
                        info.LastScanUtc = quick > full || full is null ? quick : full;
                        return;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Defender is not installed or is replaced (e.g. on Windows Server without it).
        }

        // Defender is off or passive: a third-party product usually took over. Security Center lists it (client Windows).
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
            foreach (ManagementBaseObject product in searcher.Get())
            {
                using (product)
                {
                    string name = product["displayName"] as string ?? string.Empty;
                    uint state = Convert.ToUInt32(product["productState"] ?? 0u);
                    bool on = (state >> 12 & 0xF) is 1 or 3; // scanner state nibble: 1 on, 3 snoozed-on
                    if (name.Contains("Defender", StringComparison.OrdinalIgnoreCase) && !on)
                    {
                        continue;
                    }

                    info.AntivirusName = name;
                    info.AntivirusOn = on;
                    info.DefinitionsUpToDate = (state >> 4 & 0xF) == 0;
                    if (on)
                    {
                        return;
                    }
                }
            }
        }
        catch (ManagementException)
        {
            // Security Center is not available on Windows Server.
        }

        info.AntivirusOn ??= false;
        if (string.IsNullOrEmpty(info.AntivirusName))
        {
            info.AntivirusName = "No antivirus found";
        }
    }

    private static void ReadFirewall(RemoteSecurityInfo info)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile");
            List<string> off = new();
            int profiles = 0;
            foreach (ManagementBaseObject profile in searcher.Get())
            {
                using (profile)
                {
                    profiles++;
                    // Enabled: 1 = true, 0 = false, 2 = not configured (treated as on, the Windows default).
                    if (Convert.ToInt32(profile["Enabled"] ?? 1) == 0)
                    {
                        off.Add(profile["Name"] as string ?? "?");
                    }
                }
            }

            if (profiles > 0)
            {
                info.FirewallOn = off.Count == 0;
                info.FirewallOffProfiles = string.Join(", ", off);
            }
        }
        catch (ManagementException)
        {
        }
    }

    private static DateTime? ToUtc(object? value)
    {
        string? text = value?.ToString();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            DateTime local = ManagementDateTimeConverter.ToDateTime(text);
            return local.Year < 2000 ? null : local.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
