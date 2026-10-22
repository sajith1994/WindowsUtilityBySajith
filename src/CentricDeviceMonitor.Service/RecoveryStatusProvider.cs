using System.Diagnostics.Eventing.Reader;
using System.Management;
using CentricDeviceMonitor.Models;
using Microsoft.Win32;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// Restore points and Windows backups for the remote stats: whether System Protection is on for the Windows drive,
/// the newest restore point, and the last successful Windows Backup. Refreshed in the background every hour (reading
/// restore points asks the shadow copy service, which takes a moment).
/// </summary>
internal sealed class RecoveryStatusProvider
{
    private const string SystemRestoreKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <summary>SPP client id of System Restore; its value lists the protected volumes, e.g. "\\?\Volume{...}\:(C%3A)".</summary>
    private const string SystemRestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly object _lock = new();
    private RemoteRecoveryInfo? _current;
    private DateTime _readAt = DateTime.MinValue;
    private bool _refreshing;

    public RemoteRecoveryInfo? Current
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

    /// <summary>Read again on the next request (after a restore point was created).</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _readAt = DateTime.MinValue;
        }
    }

    private void Refresh()
    {
        RemoteRecoveryInfo info = new() { CheckedUtc = DateTime.UtcNow };
        try
        {
            info.SystemProtectionOn = ReadSystemProtection();
            ReadRestorePoints(info);
            info.LastBackupUtc = ReadLastBackup();
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("Remote monitoring recovery status", exception);
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

    /// <summary>Whether restore points are made for the Windows drive; null when it cannot be told.</summary>
    internal static bool? ReadSystemProtection()
    {
        using (RegistryKey? policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore"))
        {
            if (policy?.GetValue("DisableSR") is int disabled && disabled == 1)
            {
                return false;
            }
        }

        string drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\').TrimEnd(':');
        try
        {
            using RegistryKey? clients = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");
            if (clients is not null)
            {
                return clients.GetValue(SystemRestoreClient) is string[] volumes
                    && volumes.Any(volume => volume.Contains($"({drive}%3A)", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }

        using RegistryKey? restore = Registry.LocalMachine.OpenSubKey(SystemRestoreKey);
        return restore?.GetValue("RPSessionInterval") is int interval ? interval > 0 : null;
    }

    private static void ReadRestorePoints(RemoteRecoveryInfo info)
    {
        try
        {
            using ManagementObjectSearcher searcher = new(@"root\default", "SELECT CreationTime, Description FROM SystemRestore");
            foreach (ManagementBaseObject point in searcher.Get())
            {
                using (point)
                {
                    info.RestorePointCount++;
                    string? text = point["CreationTime"] as string;
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    DateTime created = ManagementDateTimeConverter.ToDateTime(text).ToUniversalTime();
                    if (info.LastRestorePointUtc is null || created > info.LastRestorePointUtc)
                    {
                        info.LastRestorePointUtc = created;
                        info.LastRestorePointDescription = point["Description"] as string ?? string.Empty;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is ManagementException or ArgumentOutOfRangeException or System.Runtime.InteropServices.COMException)
        {
            // System Restore is not available (e.g. Windows Server): left empty.
        }
    }

    /// <summary>Event 4 of Windows Backup: "The backup operation has finished successfully".</summary>
    private static DateTime? ReadLastBackup()
    {
        try
        {
            EventLogQuery query = new("Microsoft-Windows-Backup", PathType.LogName, "*[System[(EventID=4)]]") { ReverseDirection = true };
            using EventLogReader reader = new(query);
            using EventRecord? record = reader.ReadEvent();
            return record?.TimeCreated?.ToUniversalTime();
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a restore point now. Windows normally allows one every 24 hours, so that limit is lifted for this call
    /// and put back afterwards.
    /// </summary>
    public static RemoteActionResult CreateRestorePoint(string requestedBy)
    {
        if (ReadSystemProtection() == false)
        {
            return new RemoteActionResult
            {
                Ok = false,
                Message = "System Protection is off for the Windows drive, so no restore point can be made. Turn it on in Control Panel > System > System Protection on that PC."
            };
        }

        object? previousFrequency = null;
        try
        {
            using RegistryKey? restore = Registry.LocalMachine.CreateSubKey(SystemRestoreKey, writable: true);
            previousFrequency = restore?.GetValue("SystemRestorePointCreationFrequency");
            restore?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);

            using ManagementClass systemRestore = new(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using ManagementBaseObject parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = $"Windows Utility (requested by {requestedBy})";
            parameters["RestorePointType"] = 12u;  // MODIFY_SETTINGS
            parameters["EventType"] = 100u;        // BEGIN_SYSTEM_CHANGE
            using ManagementBaseObject result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
            uint code = Convert.ToUInt32(result["ReturnValue"] ?? 1u);
            return code == 0
                ? new RemoteActionResult { Ok = true, Message = "Restore point created. If something goes wrong after a change, that PC can be put back to this point." }
                : new RemoteActionResult { Ok = false, Message = $"Windows could not create the restore point (code {code}). Check that System Protection is on and the drive has free space." };
        }
        catch (Exception exception) when (exception is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new RemoteActionResult { Ok = false, Message = $"Windows could not create the restore point: {exception.Message}" };
        }
        finally
        {
            try
            {
                using RegistryKey? restore = Registry.LocalMachine.OpenSubKey(SystemRestoreKey, writable: true);
                if (previousFrequency is null)
                {
                    restore?.DeleteValue("SystemRestorePointCreationFrequency", throwOnMissingValue: false);
                }
                else
                {
                    restore?.SetValue("SystemRestorePointCreationFrequency", previousFrequency, RegistryValueKind.DWord);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
            }
        }
    }
}
