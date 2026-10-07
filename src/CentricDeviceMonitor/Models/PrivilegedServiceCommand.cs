namespace CentricDeviceMonitor.Models;

public static class PrivilegedServiceActions
{
    public const string StartPrintSpooler = "spooler-start";
    public const string StopPrintSpooler = "spooler-stop";
    public const string RestartPrintSpooler = "spooler-restart";
}

public sealed class PrivilegedServiceCommandRequest
{
    public Guid Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

public sealed class PrivilegedServiceCommandResponse
{
    public Guid Id { get; set; }

    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public DateTime CompletedAt { get; set; }
}
