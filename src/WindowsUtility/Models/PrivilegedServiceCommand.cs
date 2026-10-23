namespace WindowsUtility.Models;

/// <summary>
/// Work the dashboard asks the LocalSystem background service to do because it needs administrator rights. The
/// service builds every command itself from these names and validated arguments; nothing sent is run as a script.
/// </summary>
public static class PrivilegedServiceActions
{
    public const string StartPrintSpooler = "spooler-start";
    public const string StopPrintSpooler = "spooler-stop";
    public const string RestartPrintSpooler = "spooler-restart";

    /// <summary>Installs the latest official release (checked and verified by the service itself).</summary>
    public const string InstallUpdate = "install-update";

    /// <summary>Arguments: address, services ("IPP,RAW,LPD"), model, hostName.</summary>
    public const string InstallPrinter = "install-printer";

    /// <summary>Arguments: name, port, removePort, force.</summary>
    public const string RemovePrinter = "remove-printer";

    /// <summary>Empties Windows' own temporary folder (the user's own one needs no administrator rights).</summary>
    public const string ClearWindowsTemp = "clear-windows-temp";

    /// <summary>Arguments: kind (see <see cref="NetworkRepairKinds"/>).</summary>
    public const string NetworkRepair = "network-repair";

    /// <summary>Arguments: port. Opens that TCP port for the LAN speed-test server (Private and Domain networks).</summary>
    public const string AllowSpeedTestPort = "allow-speedtest-port";

    /// <summary>Arguments: enable ("true"/"false"). The dashboard's sign-in task, for the user who asked.</summary>
    public const string SetDashboardStartup = "dashboard-startup";

    /// <summary>Reads the Windows drive from the boot configuration (read-only).</summary>
    public const string DetectWindowsDrive = "detect-windows-drive";

    /// <summary>System image backups: adds or replaces a schedule. Arguments: schedule (JSON), password (empty keeps the saved one).</summary>
    public const string SaveImageBackup = "backup-image-save";

    /// <summary>Arguments: id.</summary>
    public const string RemoveImageBackup = "backup-image-remove";

    /// <summary>Arguments: id. Runs a backup an administrator set up, now; anyone may ask.</summary>
    public const string RunImageBackup = "backup-image-run";

    /// <summary>A one-time image of the Windows disk. Arguments: destination, user, password. Data: the run's id.</summary>
    public const string BackUpImageNow = "backup-image-now";

    /// <summary>
    /// Actions only an administrator account may ask for (no administrator prompt: the service checks the account).
    /// An image holds every user's files, so a standard user may not choose where one goes.
    /// </summary>
    public static readonly string[] AdministratorAccountOnly = { SaveImageBackup, RemoveImageBackup, BackUpImageNow };

    /// <summary>
    /// Actions an administrator can turn off for standard users ("Let standard users run maintenance tools without
    /// an administrator password"). The others are always allowed: the Print Spooler buttons as before, installing
    /// the verified official update, and read-only checks.
    /// </summary>
    public static readonly string[] Maintenance = { InstallPrinter, RemovePrinter, ClearWindowsTemp, NetworkRepair, AllowSpeedTestPort };
}

public static class NetworkRepairKinds
{
    public const string ReleaseRenew = "release-renew";
    public const string FlushDns = "flush-dns";
    public const string ResetStack = "reset-stack";
    public const string ClearArp = "clear-arp";
    public const string Full = "full";

    /// <summary>The commands for each repair, in order (file and arguments; nothing else can be run).</summary>
    public static IReadOnlyList<(string File, string Arguments)> Commands(string kind) => kind switch
    {
        ReleaseRenew => new[] { ("ipconfig.exe", "/release"), ("ipconfig.exe", "/renew") },
        FlushDns => new[] { ("ipconfig.exe", "/flushdns") },
        ResetStack => new[] { ("netsh.exe", "winsock reset"), ("netsh.exe", "int ip reset") },
        ClearArp => new[] { ("arp.exe", "-d *") },
        Full => new[]
        {
            ("ipconfig.exe", "/release"), ("ipconfig.exe", "/renew"), ("ipconfig.exe", "/flushdns"),
            ("netsh.exe", "winsock reset"), ("netsh.exe", "int ip reset"), ("arp.exe", "-d *")
        },
        _ => Array.Empty<(string, string)>()
    };
}

public sealed class PrivilegedServiceCommandRequest
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public Dictionary<string, string> Arguments { get; set; } = new();

    public DateTime CreatedAt { get; set; }
}

public sealed class PrivilegedServiceCommandResponse
{
    public Guid Id { get; set; }

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>A structured result as JSON (e.g. a printer install result), when the action has one.</summary>
    public string Data { get; set; } = string.Empty;

    public DateTime CompletedAt { get; set; }
}
