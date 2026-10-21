using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.InteropServices;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.ServiceHost;

/// <summary>
/// The Details window's data: programs using the PC right now (CPU measured over one second), signed-in users and
/// whether their screen is locked, the last restart, and problems from the event logs (blue screens, unexpected
/// shutdowns, app crashes and freezes, disk and hardware errors). Reading the logs takes a moment, so the problem
/// list is cached for five minutes and the 7-day counts on the tiles are refreshed every fifteen.
/// </summary>
internal sealed class PcDetailsProvider
{
    private static readonly TimeSpan ProblemListLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SummaryLifetime = TimeSpan.FromMinutes(15);
    private readonly object _lock = new();
    private List<RemoteProblem> _problems = new();
    private DateTime _problemsReadAt = DateTime.MinValue;
    private RemoteProblemSummary? _summary;
    private DateTime _summaryReadAt = DateTime.MinValue;
    private bool _summaryRefreshing;

    /// <summary>7-day counts for the stats payload (refreshed in the background).</summary>
    public RemoteProblemSummary? Summary
    {
        get
        {
            lock (_lock)
            {
                if (!_summaryRefreshing && DateTime.UtcNow - _summaryReadAt > SummaryLifetime)
                {
                    _summaryRefreshing = true;
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            List<RemoteProblem> problems = ReadProblems();
                            lock (_lock)
                            {
                                _summary = Summarise(problems, 7, DateTime.UtcNow);
                            }
                        }
                        finally
                        {
                            lock (_lock)
                            {
                                _summaryReadAt = DateTime.UtcNow;
                                _summaryRefreshing = false;
                            }
                        }
                    });
                }

                return _summary;
            }
        }
    }

    public async Task<RemotePcDetails> GetAsync()
    {
        Task<(List<RemoteProcessInfo> ByCpu, List<RemoteProcessInfo> ByMemory)> processes = SampleProcessesAsync(TimeSpan.FromSeconds(1));
        List<RemoteProblem> problems = await Task.Run(CachedProblems);
        DateTime now = DateTime.UtcNow;
        (List<RemoteProcessInfo> byCpu, List<RemoteProcessInfo> byMemory) = await processes;
        return new RemotePcDetails
        {
            MachineName = Environment.MachineName,
            LastBootUtc = now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            Sessions = ReadSessions(),
            TopByCpu = byCpu,
            TopByMemory = byMemory,
            Last7Days = Summarise(problems, 7, now),
            Last30Days = Summarise(problems, 30, now),
            Problems = problems.OrderByDescending(problem => problem.TimeUtc).Take(100).ToList()
        };
    }

    private List<RemoteProblem> CachedProblems()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _problemsReadAt < ProblemListLifetime)
            {
                return _problems;
            }
        }

        List<RemoteProblem> problems = ReadProblems();
        lock (_lock)
        {
            _problems = problems;
            _problemsReadAt = DateTime.UtcNow;
            _summary = Summarise(problems, 7, DateTime.UtcNow);
            _summaryReadAt = DateTime.UtcNow;
        }

        return problems;
    }

    // ---- Problems from the event logs ----------------------------------------------------------------------

    private const long ThirtyDaysMs = 30L * 24 * 60 * 60 * 1000;

    private static List<RemoteProblem> ReadProblems()
    {
        List<RemoteProblem> problems = new();
        string window = $"TimeCreated[timediff(@SystemTime) <= {ThirtyDaysMs}]";
        Read("System",
            $"*[System[(EventID=41 or EventID=6008 or EventID=1001 or EventID=7 or EventID=11 or EventID=51 or EventID=153 or EventID=55 or Provider[@Name='Microsoft-Windows-WHEA-Logger']) and {window}]]",
            problems);
        Read("Application",
            $"*[System[(Provider[@Name='Application Error'] or Provider[@Name='Application Hang']) and (EventID=1000 or EventID=1002) and {window}]]",
            problems);
        return RemoveDuplicateShutdowns(problems);
    }

    private static void Read(string log, string query, List<RemoteProblem> problems)
    {
        try
        {
            using EventLogReader reader = new(new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true });
            for (int read = 0; read < 1000; read++)
            {
                using EventRecord? record = reader.ReadEvent();
                if (record is null)
                {
                    break;
                }

                RemoteProblem? problem = Classify(record.ProviderName ?? string.Empty, record.Id,
                    record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow,
                    record.Properties.Select(property => property.Value?.ToString() ?? string.Empty).ToList());
                if (problem is not null)
                {
                    problems.Add(problem);
                }
            }
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
        {
            ServiceLog.Write("Remote monitoring", $"Could not read the {log} event log: {exception.Message}");
        }
    }

    /// <summary>Turns an event into a problem, or null when the event is not one of the kinds reported.</summary>
    internal static RemoteProblem? Classify(string provider, int id, DateTime timeUtc, IReadOnlyList<string> properties)
    {
        string first = properties.Count > 0 ? properties[0] : string.Empty;
        (string Kind, string Summary)? problem = (provider, id) switch
        {
            ("Microsoft-Windows-Kernel-Power", 41) => ("UnexpectedShutdown", "Restarted without shutting down cleanly (power loss, freeze or forced restart)"),
            ("EventLog", 6008) => ("UnexpectedShutdown", "The previous shutdown was unexpected"),
            ("Microsoft-Windows-WER-SystemErrorReporting", 1001) or ("BugCheck", 1001) =>
                ("BlueScreen", first.Length > 0 ? $"Blue screen (stop code {first})" : "Blue screen"),
            ("disk", 7) => ("DiskError", "Bad block on a disk"),
            ("disk", 11) => ("DiskError", "Disk controller error"),
            ("disk", 51) => ("DiskError", "Error during a paging operation on a disk"),
            ("disk", 153) => ("DiskError", "Disk input/output retried"),
            ("Ntfs", 55) or ("Microsoft-Windows-Ntfs", 55) => ("DiskError", "File system corruption detected"),
            ("Microsoft-Windows-WHEA-Logger", _) => ("HardwareError", "Hardware error reported by the processor, memory or a device"),
            ("Application Error", 1000) => ("AppCrash", first.Length > 0 ? $"{first} crashed" : "An app crashed"),
            ("Application Hang", 1002) => ("AppFreeze", first.Length > 0 ? $"{first} stopped responding" : "An app stopped responding"),
            _ => null
        };

        return problem is { } found ? new RemoteProblem { TimeUtc = timeUtc, Kind = found.Kind, Summary = found.Summary } : null;
    }

    /// <summary>One bad shutdown usually logs both event 41 and event 6008 a few seconds apart; keep only one.</summary>
    internal static List<RemoteProblem> RemoveDuplicateShutdowns(List<RemoteProblem> problems)
    {
        List<RemoteProblem> shutdowns = problems.Where(problem => problem.Kind == "UnexpectedShutdown").OrderBy(problem => problem.TimeUtc).ToList();
        HashSet<RemoteProblem> duplicates = new();
        for (int index = 1; index < shutdowns.Count; index++)
        {
            if (shutdowns[index].TimeUtc - shutdowns[index - 1].TimeUtc < TimeSpan.FromMinutes(10) && !duplicates.Contains(shutdowns[index - 1]))
            {
                duplicates.Add(shutdowns[index]);
            }
        }

        return problems.Where(problem => !duplicates.Contains(problem)).ToList();
    }

    internal static RemoteProblemSummary Summarise(IEnumerable<RemoteProblem> problems, int days, DateTime nowUtc)
    {
        List<RemoteProblem> recent = problems.Where(problem => nowUtc - problem.TimeUtc <= TimeSpan.FromDays(days)).ToList();
        int Count(string kind) => recent.Count(problem => problem.Kind == kind);
        return new RemoteProblemSummary
        {
            Days = days,
            BlueScreens = Count("BlueScreen"),
            UnexpectedShutdowns = Count("UnexpectedShutdown"),
            AppCrashes = Count("AppCrash"),
            AppFreezes = Count("AppFreeze"),
            DiskErrors = Count("DiskError"),
            HardwareErrors = Count("HardwareError")
        };
    }

    // ---- Programs using the PC ------------------------------------------------------------------------------

    private static async Task<(List<RemoteProcessInfo> ByCpu, List<RemoteProcessInfo> ByMemory)> SampleProcessesAsync(TimeSpan interval)
    {
        Dictionary<int, TimeSpan> before = new();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    before[process.Id] = process.TotalProcessorTime;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Protected processes do not report CPU time.
                }
            }
        }

        Stopwatch watch = Stopwatch.StartNew();
        await Task.Delay(interval);
        double elapsedMs = Math.Max(1, watch.Elapsed.TotalMilliseconds) * Environment.ProcessorCount;

        List<(string Name, int Pid, double Cpu, long Memory)> samples = new();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4)
                {
                    continue; // System Idle Process and System
                }

                try
                {
                    double cpu = before.TryGetValue(process.Id, out TimeSpan previous)
                        ? Math.Max(0, (process.TotalProcessorTime - previous).TotalMilliseconds) / elapsedMs * 100
                        : 0;
                    samples.Add((process.ProcessName, process.Id, cpu, process.WorkingSet64));
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    try
                    {
                        samples.Add((process.ProcessName, process.Id, 0, process.WorkingSet64));
                    }
                    catch (InvalidOperationException)
                    {
                        // The process ended while being read.
                    }
                }
            }
        }

        List<RemoteProcessInfo> programs = GroupByProgram(samples);
        List<RemoteProcessInfo> byCpu = programs.OrderByDescending(program => program.CpuPercent).Take(10).ToList();
        List<RemoteProcessInfo> byMemory = programs.OrderByDescending(program => program.MemoryBytes).Take(10).ToList();
        foreach (RemoteProcessInfo program in byCpu.Concat(byMemory).Distinct())
        {
            program.Description = DescribeProgram(program.Name, samples);
        }

        return (byCpu, byMemory);
    }

    /// <summary>All processes of one program together ("chrome" x 24 counts once, with the totals).</summary>
    internal static List<RemoteProcessInfo> GroupByProgram(IEnumerable<(string Name, int Pid, double Cpu, long Memory)> samples) =>
        samples
            .GroupBy(sample => sample.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RemoteProcessInfo
            {
                Name = group.Key,
                Count = group.Count(),
                CpuPercent = Math.Round(Math.Min(100, group.Sum(sample => sample.Cpu)), 1),
                MemoryBytes = group.Sum(sample => sample.Memory)
            })
            .ToList();

    /// <summary>The program's own description ("Google Chrome"), when Windows can read it.</summary>
    private static string DescribeProgram(string name, List<(string Name, int Pid, double Cpu, long Memory)> samples)
    {
        foreach ((string _, int pid, double _, long _) in samples.Where(sample => sample.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(3))
        {
            try
            {
                using Process process = Process.GetProcessById(pid);
                string? description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Protected or ended process: the name is enough.
            }
        }

        return string.Empty;
    }

    // ---- Signed-in users --------------------------------------------------------------------------------------

    internal static List<RemoteSessionInfo> ReadSessions()
    {
        List<RemoteSessionInfo> sessions = new();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr buffer, out int count))
        {
            return sessions;
        }

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(buffer + index * size);
                if (session.State is not (WtsActive or WtsDisconnected))
                {
                    continue;
                }

                string user = QueryString(session.SessionId, WtsUserName);
                if (user.Length == 0)
                {
                    continue;
                }

                string domain = QueryString(session.SessionId, WtsDomainName);
                string state = session.State == WtsDisconnected ? "Disconnected" : IsLocked(session.SessionId) ? "Locked" : "Active";
                sessions.Add(new RemoteSessionInfo { User = domain.Length > 0 ? $@"{domain}\{user}" : user, State = state });
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return sessions;
    }

    private static string QueryString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr value, out _))
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(value);
        }
    }

    /// <summary>WTSINFOEX level 1: SessionFlags is 0 when the session is locked. The data union is 8-byte aligned (it holds
    /// LARGE_INTEGER times), so SessionId is at 8, SessionState at 12 and SessionFlags at 16.</summary>
    private static bool IsLocked(int sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsSessionInfoEx, out IntPtr value, out _))
        {
            return false;
        }

        try
        {
            int level = Marshal.ReadInt32(value);
            return level == 1 && Marshal.ReadInt32(value + 16) == 0;
        }
        finally
        {
            WTSFreeMemory(value);
        }
    }

    private const int WtsActive = 0;
    private const int WtsDisconnected = 4;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int WtsSessionInfoEx = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
