using CentricDeviceMonitor.Models;
using CentricDeviceMonitor.Services;

namespace CentricDeviceMonitor.ServiceHost;

internal sealed class MonitoringEngine : IDisposable
{
    private const double CpuOverheatThresholdCelsius = 90.0;
    private const double CpuHighUsageThresholdPercent = 95.0;
    private const double RamHighUsageThresholdPercent = 90.0;
    private static readonly TimeSpan CpuHighUsageRequiredDuration = TimeSpan.FromMinutes(1);

    private readonly DeviceStorageService _deviceStorage = new();
    private readonly AppSettingsService _settingsService = new();
    private readonly DeviceLogService _deviceLogService = new();
    private readonly CpuTemperatureLogService _cpuLogService = new();
    private readonly CpuTemperatureSampleLogService _cpuSampleLogService = new();
    private readonly SystemHealthLogService _systemHealthLogService = new();
    private readonly SystemResourceMonitorService _systemResourceMonitor = new();
    private readonly BackgroundServiceRuntimeStore _runtimeStore = new();
    private readonly PrivilegedCommandProcessor _privilegedCommandProcessor = new();
    private readonly SystemPowerService _systemPowerService = new();
    private readonly NetworkDeviceService _networkService = new();
    private readonly HardwareMonitorService _hardwareMonitor = new();
    private readonly Dictionary<Guid, DeviceRuntimeStatus> _deviceStatuses = new();
    private readonly Dictionary<Guid, DeviceIncidentTracker> _deviceIncidentTrackers = new();
    private readonly List<CpuTemperatureLogEntry> _cpuLogs = new();
    private readonly List<SystemHealthLogEntry> _systemHealthLogs = new();
    private readonly Dictionary<string, SystemHealthLogEntry> _activeNetworkEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _lastNetworkAdapterStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkAdapterState> _lastKnownNetworkAdapters = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _runTask;
    private CpuTemperatureLogEntry? _activeCpuEvent;
    private SystemHealthLogEntry? _activeCpuUsageEvent;
    private SystemHealthLogEntry? _activeRamUsageEvent;
    private DateTime? _cpuHighUsagePendingSince;
    private double _cpuHighUsagePendingStartValue;
    private double _cpuHighUsagePendingPeak;
    private bool _networkAdapterStatesInitialized;
    private DateTime _serviceStartedAt;
    private DateTime _nextCpuReadAt;
    private DateTime? _nextPingAt;
    private DateTime _lastSnapshotAt;
    private DateTime _lastCpuSampleLoggedAt = DateTime.MinValue;
    private DateTime _lastSystemHealthLogSaveAt;
    private int _lastIntervalSeconds = 60;
    private bool _lastMonitoringEnabled = true;
    private bool _pingCycleRunning;
    private double _cpuAlertThresholdCelsius = 98.0;
    private AppSettings _lastSettings = new();
    private DateTime _lastPowerScheduleCheckAt;
    private bool _cpuAlertWasActive;
    private SystemResourceReading _lastSystemResourceReading = new(
        null,
        0,
        0,
        0,
        false,
        "Checking...",
        Array.Empty<NetworkAdapterState>(),
        "CPU model unavailable",
        "RAM speed unavailable");
    private CpuTemperatureReading _lastCpuReading = new(
        false,
        null,
        "Not available",
        "Not available",
        "Waiting for the first background sensor scan.",
        "Background service",
        false);
    private CpuFanReading _lastCpuFanReading = new(
        false,
        null,
        "Fan: RPM unavailable",
        string.Empty,
        "Background service",
        "Waiting for the first background fan scan.");
    private CpuPackagePowerReading _lastCpuPackagePowerReading = new(
        false,
        null,
        "Not available",
        string.Empty,
        "Background service",
        "Waiting for the first background CPU package power scan.");
    private DateTime? _lastCpuPackagePowerRecordedAt;
    private GpuPowerReading _lastGpuPowerReading = new(
        false,
        null,
        "Not available",
        string.Empty,
        "Background service",
        "Waiting for the first background GPU power scan.");
    private DateTime? _lastGpuPowerRecordedAt;

    public void Start()
    {
        if (_runTask is not null)
        {
            return;
        }

        SharedDataPaths.EnsureDirectories();
        _serviceStartedAt = DateTime.Now;
        _lastPowerScheduleCheckAt = _serviceStartedAt;
        _nextCpuReadAt = DateTime.MinValue;
        _lastSnapshotAt = DateTime.MinValue;
        _lastSystemHealthLogSaveAt = DateTime.MinValue;
        _nextPingAt = DateTime.Now.AddSeconds(3);
        _cancellationTokenSource = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        ServiceLog.Write("Service", $"Started as {Environment.UserName}. Version {typeof(MonitoringEngine).Assembly.GetName().Version}.");
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? source = _cancellationTokenSource;
        Task? runTask = _runTask;
        if (source is null || runTask is null)
        {
            return;
        }

        source.Cancel();
        try
        {
            await runTask.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            ServiceLog.Write("Service", "Timed out while waiting for the monitoring loop to stop.");
        }
        finally
        {
            source.Dispose();
            _cancellationTokenSource = null;
            _runTask = null;
        }

        ServiceLog.Write("Service", "Stopped.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await LoadCpuLogsAsync();
        await LoadSystemHealthLogsAsync();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                AppSettings settings = await _settingsService.LoadAsync();
                int intervalSeconds = NormalizePingInterval(settings.PingIntervalSeconds);
                bool monitoringEnabled = settings.AutomaticMonitoringEnabled;
                _cpuAlertThresholdCelsius = NormalizeCpuAlertThreshold(settings.CpuTemperatureAlertThresholdCelsius);
                NormalizePowerSchedule(settings);
                _lastSettings = settings;

                if (monitoringEnabled != _lastMonitoringEnabled || intervalSeconds != _lastIntervalSeconds)
                {
                    _lastMonitoringEnabled = monitoringEnabled;
                    _lastIntervalSeconds = intervalSeconds;
                    _nextPingAt = monitoringEnabled ? DateTime.Now.AddSeconds(1) : null;
                    ServiceLog.Write(
                        "Settings",
                        monitoringEnabled
                            ? $"Automatic ping monitoring set to every {intervalSeconds} seconds."
                            : "Automatic ping monitoring disabled.");
                }

                await _privilegedCommandProcessor.ProcessPendingAsync(cancellationToken);
                await ProcessPowerScheduleAsync(settings);
                await ReadSystemResourcesAsync();

                if (DateTime.Now >= _nextCpuReadAt)
                {
                    await ReadCpuTemperatureAsync();
                    _nextCpuReadAt = DateTime.Now.AddSeconds(3);
                }

                if (monitoringEnabled && _nextPingAt.HasValue && DateTime.Now >= _nextPingAt.Value)
                {
                    await RunPingCycleAsync(cancellationToken);
                    _nextPingAt = DateTime.Now.AddSeconds(intervalSeconds);
                }
                else if (!monitoringEnabled)
                {
                    _nextPingAt = null;
                }

                if (DateTime.Now - _lastSnapshotAt >= TimeSpan.FromSeconds(2))
                {
                    await SaveSnapshotAsync(monitoringEnabled, intervalSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                ServiceLog.WriteException("Monitoring loop", exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            await SaveSystemHealthLogsAsync();
            await SaveSnapshotAsync(_lastMonitoringEnabled, _lastIntervalSeconds);
        }
        catch
        {
        }
    }

    private async Task RunPingCycleAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DeviceEntry> devices = await _deviceStorage.LoadAsync();
        _pingCycleRunning = true;
        await SaveSnapshotAsync(_lastMonitoringEnabled, _lastIntervalSeconds, devices.Count);

        try
        {
            HashSet<Guid> currentIds = devices.Select(device => device.Id).ToHashSet();
            foreach (Guid staleId in _deviceStatuses.Keys.Where(id => !currentIds.Contains(id)).ToList())
            {
                _deviceStatuses.Remove(staleId);
            }

            foreach (Guid staleId in _deviceIncidentTrackers.Keys.Where(id => !currentIds.Contains(id)).ToList())
            {
                _deviceIncidentTrackers.Remove(staleId);
            }

            // Load each device incident state before parallel checks so the dictionary is not mutated concurrently.
            foreach (DeviceEntry device in devices)
            {
                await EnsureDeviceIncidentTrackerAsync(device);
            }

            using SemaphoreSlim limiter = new(8, 8);
            IEnumerable<Task> checks = devices.Select(async device =>
            {
                await limiter.WaitAsync(cancellationToken);
                try
                {
                    await CheckDeviceAsync(device, cancellationToken);
                }
                finally
                {
                    limiter.Release();
                }
            });

            await Task.WhenAll(checks);
        }
        finally
        {
            _pingCycleRunning = false;
        }
    }

    private async Task CheckDeviceAsync(DeviceEntry device, CancellationToken cancellationToken)
    {
        DateTime checkedAt = DateTime.Now;
        try
        {
            NetworkCheckResult result = await _networkService.CheckAsync(device.IpAddress, cancellationToken);
            string statusText = result.IsOnline ? "Online" : "Offline";

            lock (_deviceStatuses)
            {
                _deviceStatuses[device.Id] = new DeviceRuntimeStatus
                {
                    DeviceId = device.Id,
                    Status = statusText,
                    Hostname = result.Hostname,
                    MacAddress = result.MacAddress,
                    RoundTripTime = result.RoundTripTime,
                    LastChecked = checkedAt,
                    LastError = result.ErrorMessage
                };
            }

            await TrackDeviceConnectivityAsync(device, result.IsOnline, checkedAt, result.ErrorMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            lock (_deviceStatuses)
            {
                _deviceStatuses[device.Id] = new DeviceRuntimeStatus
                {
                    DeviceId = device.Id,
                    Status = "Error",
                    Hostname = "Not available",
                    MacAddress = "Not available",
                    LastChecked = checkedAt,
                    LastError = exception.Message
                };
            }

            // Treat a check exception as a failed connectivity check for incident tracking.
            await TrackDeviceConnectivityAsync(device, false, checkedAt, exception.Message);
        }
    }

    private async Task EnsureDeviceIncidentTrackerAsync(DeviceEntry device)
    {
        if (_deviceIncidentTrackers.ContainsKey(device.Id))
        {
            return;
        }

        IReadOnlyList<DevicePingLogEntry> saved = await _deviceLogService.LoadAsync(device.Id);
        List<DevicePingLogEntry> incidents = saved.OrderByDescending(entry => entry.LostAt).ToList();
        DevicePingLogEntry? activeIncident = incidents.FirstOrDefault(entry => entry.IsActive);
        DateTime? lastRecoveredAt = incidents
            .Where(entry => entry.RecoveredAt.HasValue)
            .Select(entry => entry.RecoveredAt)
            .OrderByDescending(value => value)
            .FirstOrDefault();

        _deviceIncidentTrackers[device.Id] = new DeviceIncidentTracker
        {
            Incidents = incidents,
            ActiveIncident = activeIncident,
            LastOnline = activeIncident is null ? null : false,
            OnlineSince = activeIncident is null ? lastRecoveredAt ?? _serviceStartedAt : null
        };
    }

    private async Task TrackDeviceConnectivityAsync(
        DeviceEntry device,
        bool isOnline,
        DateTime checkedAt,
        string errorMessage)
    {
        await EnsureDeviceIncidentTrackerAsync(device);
        DeviceIncidentTracker tracker = _deviceIncidentTrackers[device.Id];

        if (isOnline)
        {
            if (tracker.ActiveIncident is not null)
            {
                tracker.ActiveIncident.RecoveredAt = checkedAt;
                tracker.ActiveIncident.SchemaVersion = Math.Max(tracker.ActiveIncident.SchemaVersion, 3);
                tracker.ActiveIncident.EventType = "ShutdownRestartInferred";
                tracker.ActiveIncident.EventMessage =
                    $"Device turned off / restarted at {tracker.ActiveIncident.LostAt:dd MMM yyyy HH:mm:ss} " +
                    $"(inferred from loss of reachability). Connection restored at {checkedAt:dd MMM yyyy HH:mm:ss}. " +
                    "A network outage can produce the same symptom when remote boot telemetry is unavailable.";
                await _deviceLogService.ReplaceAsync(device.Id, tracker.Incidents);
                TimeSpan lostDuration = checkedAt - tracker.ActiveIncident.LostAt;
                ServiceLog.Write(
                    "Device incident",
                    $"{device.Name} ({device.IpAddress}) recovered after {FormatDuration(lostDuration)} offline. " +
                    $"Shutdown/restart event recorded for {tracker.ActiveIncident.LostAt:dd MMM yyyy HH:mm:ss} (reachability inferred).");
                tracker.ActiveIncident = null;
            }

            if (tracker.LastOnline != true && !tracker.OnlineSince.HasValue)
            {
                tracker.OnlineSince = checkedAt;
            }

            tracker.LastOnline = true;
            return;
        }

        if (tracker.ActiveIncident is null)
        {
            DateTime activeSince = tracker.OnlineSince ?? _serviceStartedAt;
            double activeSeconds = Math.Max(0, (checkedAt - activeSince).TotalSeconds);
            DevicePingLogEntry incident = new()
            {
                SchemaVersion = 3,
                LostAt = checkedAt,
                ActiveDurationBeforeLossSeconds = activeSeconds,
                ErrorMessage = errorMessage,
                EventType = "ReachabilityLoss",
                EventMessage =
                    $"Device became unreachable at {checkedAt:dd MMM yyyy HH:mm:ss}. " +
                    "A shutdown, restart or network interruption is possible; the exact cause is confirmed only if remote boot telemetry is available."
            };

            tracker.Incidents.Insert(0, incident);
            tracker.ActiveIncident = incident;
            tracker.OnlineSince = null;
            await _deviceLogService.ReplaceAsync(device.Id, tracker.Incidents);
            ServiceLog.Write(
                "Device incident",
                $"{device.Name} ({device.IpAddress}) lost connection after {FormatDuration(TimeSpan.FromSeconds(activeSeconds))} active.");
        }
        else if (!string.IsNullOrWhiteSpace(errorMessage) &&
                 !string.Equals(tracker.ActiveIncident.ErrorMessage, errorMessage, StringComparison.Ordinal))
        {
            tracker.ActiveIncident.ErrorMessage = errorMessage;
        }

        tracker.LastOnline = false;
    }

    private async Task ReadSystemResourcesAsync()
    {
        try
        {
            _lastSystemResourceReading = _systemResourceMonitor.Read();
            bool logChanged = false;

            if (_lastSystemResourceReading.CpuUsagePercent.HasValue)
            {
                logChanged |= TrackCpuUsage(_lastSystemResourceReading.CpuUsagePercent.Value);
            }

            logChanged |= TrackRamUsage(_lastSystemResourceReading.RamUsagePercent);
            logChanged |= TrackNetworkAdapters(_lastSystemResourceReading.NetworkAdapters);

            bool hasActiveEvents = _activeCpuUsageEvent is not null ||
                _activeRamUsageEvent is not null ||
                _activeNetworkEvents.Count > 0;

            if (logChanged ||
                (hasActiveEvents && DateTime.Now - _lastSystemHealthLogSaveAt >= TimeSpan.FromSeconds(10)))
            {
                await SaveSystemHealthLogsAsync();
            }
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("System resources", exception);
        }
    }

    private bool TrackCpuUsage(double usagePercent)
    {
        DateTime now = DateTime.Now;
        if (usagePercent >= CpuHighUsageThresholdPercent)
        {
            if (_activeCpuUsageEvent is not null)
            {
                if (!_activeCpuUsageEvent.PeakValue.HasValue || usagePercent > _activeCpuUsageEvent.PeakValue.Value)
                {
                    _activeCpuUsageEvent.PeakValue = usagePercent;
                }

                return false;
            }

            if (!_cpuHighUsagePendingSince.HasValue)
            {
                _cpuHighUsagePendingSince = now;
                _cpuHighUsagePendingStartValue = usagePercent;
                _cpuHighUsagePendingPeak = usagePercent;
                return false;
            }

            _cpuHighUsagePendingPeak = Math.Max(_cpuHighUsagePendingPeak, usagePercent);
            if (now - _cpuHighUsagePendingSince.Value < CpuHighUsageRequiredDuration)
            {
                return false;
            }

            _activeCpuUsageEvent = new SystemHealthLogEntry
            {
                EventType = "CPU high usage",
                Component = "Processor",
                StartedAt = _cpuHighUsagePendingSince.Value,
                ConfirmedAt = now,
                StartValue = _cpuHighUsagePendingStartValue,
                PeakValue = _cpuHighUsagePendingPeak,
                Unit = "%",
                Details = "CPU usage remained at or above 95% continuously for more than 1 minute."
            };
            _systemHealthLogs.Insert(0, _activeCpuUsageEvent);
            ServiceLog.Write(
                "System health",
                $"CPU usage stayed at or above 95% for 1 minute. Event began at {_activeCpuUsageEvent.StartedAt:yyyy-MM-dd HH:mm:ss}; peak {_activeCpuUsageEvent.PeakValue.GetValueOrDefault():0.0}%.");
            _cpuHighUsagePendingSince = null;
            return true;
        }

        _cpuHighUsagePendingSince = null;
        _cpuHighUsagePendingPeak = 0;
        _cpuHighUsagePendingStartValue = 0;

        if (_activeCpuUsageEvent is null)
        {
            return false;
        }

        _activeCpuUsageEvent.EndedAt = now;
        TimeSpan duration = now - _activeCpuUsageEvent.StartedAt;
        ServiceLog.Write(
            "System health",
            $"CPU usage returned below 95%. Duration {FormatDuration(duration)}; peak {_activeCpuUsageEvent.PeakValue.GetValueOrDefault():0.0}%.");
        _activeCpuUsageEvent = null;
        return true;
    }

    private bool TrackRamUsage(double usagePercent)
    {
        DateTime now = DateTime.Now;
        if (usagePercent > RamHighUsageThresholdPercent)
        {
            if (_activeRamUsageEvent is null)
            {
                _activeRamUsageEvent = new SystemHealthLogEntry
                {
                    EventType = "RAM high usage",
                    Component = "Physical memory",
                    StartedAt = now,
                    ConfirmedAt = now,
                    StartValue = usagePercent,
                    PeakValue = usagePercent,
                    Unit = "%",
                    Details = "Physical RAM usage exceeded 90%."
                };
                _systemHealthLogs.Insert(0, _activeRamUsageEvent);
                ServiceLog.Write("System health", $"RAM usage exceeded 90% at {usagePercent:0.0}%.");
                return true;
            }

            if (!_activeRamUsageEvent.PeakValue.HasValue || usagePercent > _activeRamUsageEvent.PeakValue.Value)
            {
                _activeRamUsageEvent.PeakValue = usagePercent;
            }

            return false;
        }

        if (_activeRamUsageEvent is null)
        {
            return false;
        }

        _activeRamUsageEvent.EndedAt = now;
        TimeSpan duration = now - _activeRamUsageEvent.StartedAt;
        ServiceLog.Write(
            "System health",
            $"RAM usage returned to 90% or lower. Duration {FormatDuration(duration)}; peak {_activeRamUsageEvent.PeakValue.GetValueOrDefault():0.0}%.");
        _activeRamUsageEvent = null;
        return true;
    }

    private bool TrackNetworkAdapters(IReadOnlyList<NetworkAdapterState> adapters)
    {
        Dictionary<string, NetworkAdapterState> current = adapters.ToDictionary(
            adapter => adapter.Id,
            adapter => adapter,
            StringComparer.OrdinalIgnoreCase);

        if (!_networkAdapterStatesInitialized)
        {
            foreach (NetworkAdapterState adapter in adapters)
            {
                _lastNetworkAdapterStates[adapter.Id] = adapter.IsConnected;
                _lastKnownNetworkAdapters[adapter.Id] = adapter;
            }

            _networkAdapterStatesInitialized = true;
            return ReconcilePersistedNetworkEvents(current);
        }

        bool changed = false;
        HashSet<string> ids = _lastNetworkAdapterStates.Keys
            .Concat(current.Keys)
            .Concat(_activeNetworkEvents.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string id in ids)
        {
            bool wasConnected = _lastNetworkAdapterStates.TryGetValue(id, out bool previousState) && previousState;
            bool isConnected = current.TryGetValue(id, out NetworkAdapterState? adapter) && adapter.IsConnected;

            if (adapter is not null)
            {
                _lastKnownNetworkAdapters[id] = adapter;
            }

            if (wasConnected && !isConnected && !_activeNetworkEvents.ContainsKey(id))
            {
                NetworkAdapterState? previousAdapter = _lastKnownNetworkAdapters.GetValueOrDefault(id);
                string component = previousAdapter is null
                    ? "Network adapter"
                    : $"{previousAdapter.Type}: {previousAdapter.Name}";
                SystemHealthLogEntry entry = new()
                {
                    EventType = "Network connection lost",
                    Component = component,
                    SourceId = id,
                    StartedAt = DateTime.Now,
                    ConfirmedAt = DateTime.Now,
                    Details = previousAdapter is null
                        ? "A previously active network adapter lost its connection."
                        : $"Previously active {previousAdapter.Type} connection was lost. Adapter: {previousAdapter.Description}."
                };
                _activeNetworkEvents[id] = entry;
                _systemHealthLogs.Insert(0, entry);
                ServiceLog.Write("Network", $"Connection lost: {component}.");
                changed = true;
            }
            else if (isConnected && _activeNetworkEvents.TryGetValue(id, out SystemHealthLogEntry? activeEvent))
            {
                activeEvent.EndedAt = DateTime.Now;
                NetworkAdapterState recoveredAdapter = adapter!;
                activeEvent.Details = $"{activeEvent.Details} Connection recovered on {recoveredAdapter.Type}; IPv4: {recoveredAdapter.IpAddresses}.";
                TimeSpan duration = activeEvent.EndedAt.Value - activeEvent.StartedAt;
                ServiceLog.Write("Network", $"Connection recovered: {activeEvent.Component}. Duration {FormatDuration(duration)}.");
                _activeNetworkEvents.Remove(id);
                changed = true;
            }

            _lastNetworkAdapterStates[id] = isConnected;
        }

        return changed;
    }

    private bool ReconcilePersistedNetworkEvents(IReadOnlyDictionary<string, NetworkAdapterState> current)
    {
        bool changed = false;
        foreach ((string id, SystemHealthLogEntry activeEvent) in _activeNetworkEvents.ToList())
        {
            if (current.TryGetValue(id, out NetworkAdapterState? adapter) && adapter.IsConnected)
            {
                activeEvent.EndedAt = DateTime.Now;
                activeEvent.Details = $"{activeEvent.Details} Connection was available when the monitoring service restarted.";
                _activeNetworkEvents.Remove(id);
                changed = true;
            }
        }

        return changed;
    }

    private async Task LoadSystemHealthLogsAsync()
    {
        try
        {
            IReadOnlyList<SystemHealthLogEntry> saved = await _systemHealthLogService.LoadAsync();
            _systemHealthLogs.Clear();
            _systemHealthLogs.AddRange(saved);
            _activeCpuUsageEvent = _systemHealthLogs
                .Where(entry => entry.IsActive && entry.EventType == "CPU high usage")
                .OrderByDescending(entry => entry.StartedAt)
                .FirstOrDefault();
            _activeRamUsageEvent = _systemHealthLogs
                .Where(entry => entry.IsActive && entry.EventType == "RAM high usage")
                .OrderByDescending(entry => entry.StartedAt)
                .FirstOrDefault();

            foreach (SystemHealthLogEntry entry in _systemHealthLogs.Where(entry =>
                         entry.IsActive &&
                         entry.EventType == "Network connection lost" &&
                         !string.IsNullOrWhiteSpace(entry.SourceId)))
            {
                _activeNetworkEvents[entry.SourceId] = entry;
            }
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("System health log load", exception);
        }
    }

    private async Task SaveSystemHealthLogsAsync()
    {
        try
        {
            await _systemHealthLogService.SaveAsync(_systemHealthLogs);
            _lastSystemHealthLogSaveAt = DateTime.Now;
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("System health log save", exception);
        }
    }

    private async Task ReadCpuTemperatureAsync()
    {
        try
        {
            // Read package power first. LibreHardwareMonitor derives CPU power from
            // energy deltas between hardware updates, so reading it before the other
            // sensor scans preserves the full ~3 second sampling interval.
            _lastCpuPackagePowerReading = _hardwareMonitor.GetCpuPackagePowerReading();
            _lastCpuPackagePowerRecordedAt = DateTime.Now;
            _lastGpuPowerReading = _hardwareMonitor.GetGpuPowerReading();
            _lastGpuPowerRecordedAt = DateTime.Now;
            _lastCpuReading = _hardwareMonitor.GetCpuTemperatureReading();
            _lastCpuFanReading = _hardwareMonitor.GetCpuFanReading();
            if (_lastCpuReading.IsAvailable &&
                _lastCpuReading.IsCpuSpecific &&
                _lastCpuReading.ValueCelsius.HasValue)
            {
                await TrackCpuTemperatureAsync(
                    _lastCpuReading.ValueCelsius.Value,
                    $"{_lastCpuReading.SensorName} ({_lastCpuReading.Source})");

                if (DateTime.Now - _lastCpuSampleLoggedAt >= TimeSpan.FromMinutes(1))
                {
                    await _cpuSampleLogService.AppendAsync(new CpuTemperatureSampleEntry
                    {
                        RecordedAt = DateTime.Now,
                        TemperatureCelsius = _lastCpuReading.ValueCelsius.Value,
                        SensorName = _lastCpuReading.SensorName,
                        Source = _lastCpuReading.Source
                    });
                    _lastCpuSampleLoggedAt = DateTime.Now;
                }

                bool alertActive = _lastCpuReading.ValueCelsius.Value > _cpuAlertThresholdCelsius;
                if (alertActive != _cpuAlertWasActive)
                {
                    ServiceLog.Write(
                        "CPU alert",
                        alertActive
                            ? $"Temperature exceeded configured alert threshold {_cpuAlertThresholdCelsius:0} C at {_lastCpuReading.ValueCelsius.Value:0.0} C."
                            : $"Temperature returned to or below configured alert threshold {_cpuAlertThresholdCelsius:0} C at {_lastCpuReading.ValueCelsius.Value:0.0} C.");
                    _cpuAlertWasActive = alertActive;
                }
            }
        }
        catch (Exception exception)
        {
            _lastCpuReading = new CpuTemperatureReading(
                false,
                null,
                "Not available",
                "Not available",
                $"Background sensor error: {exception.Message}",
                "Background service",
                false);
            _lastCpuPackagePowerReading = new CpuPackagePowerReading(
                false,
                null,
                "Not available",
                string.Empty,
                "Background service",
                $"Background CPU package power error: {exception.Message}");
            _lastCpuPackagePowerRecordedAt = DateTime.Now;
            _lastGpuPowerReading = new GpuPowerReading(
                false,
                null,
                "Not available",
                string.Empty,
                "Background service",
                $"Background GPU power error: {exception.Message}");
            _lastGpuPowerRecordedAt = DateTime.Now;
            ServiceLog.WriteException("CPU/GPU hardware sensors", exception);
        }
    }

    private async Task TrackCpuTemperatureAsync(double temperatureCelsius, string sensorName)
    {
        bool isAboveThreshold = temperatureCelsius > CpuOverheatThresholdCelsius;
        bool hasReturnedBelowThreshold = temperatureCelsius < CpuOverheatThresholdCelsius;

        if (isAboveThreshold && _activeCpuEvent is null)
        {
            _activeCpuEvent = new CpuTemperatureLogEntry
            {
                StartedAt = DateTime.Now,
                StartTemperatureCelsius = temperatureCelsius,
                PeakTemperatureCelsius = temperatureCelsius,
                SensorName = sensorName
            };
            _cpuLogs.Insert(0, _activeCpuEvent);
            await SaveCpuLogsAsync();
            ServiceLog.Write("CPU", $"Temperature exceeded 90 C at {temperatureCelsius:0.0} C.");
            return;
        }

        if (_activeCpuEvent is null)
        {
            return;
        }

        bool changed = false;
        if (temperatureCelsius > _activeCpuEvent.PeakTemperatureCelsius)
        {
            _activeCpuEvent.PeakTemperatureCelsius = temperatureCelsius;
            changed = true;
        }

        if (hasReturnedBelowThreshold)
        {
            _activeCpuEvent.EndedAt = DateTime.Now;
            TimeSpan duration = _activeCpuEvent.EndedAt.Value - _activeCpuEvent.StartedAt;
            ServiceLog.Write("CPU", $"Temperature returned below 90 C. Duration {FormatDuration(duration)}.");
            _activeCpuEvent = null;
            changed = true;
        }

        if (changed)
        {
            await SaveCpuLogsAsync();
        }
    }

    private async Task LoadCpuLogsAsync()
    {
        try
        {
            IReadOnlyList<CpuTemperatureLogEntry> saved = await _cpuLogService.LoadAsync();
            _cpuLogs.Clear();
            _cpuLogs.AddRange(saved);
            _activeCpuEvent = _cpuLogs
                .Where(entry => entry.IsActive)
                .OrderByDescending(entry => entry.StartedAt)
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("CPU log load", exception);
        }
    }

    private async Task SaveCpuLogsAsync()
    {
        try
        {
            await _cpuLogService.SaveAsync(_cpuLogs);
        }
        catch (Exception exception)
        {
            ServiceLog.WriteException("CPU log save", exception);
        }
    }

    private async Task SaveSnapshotAsync(bool monitoringEnabled, int intervalSeconds, int? knownDeviceCount = null)
    {
        List<DeviceRuntimeStatus> statuses;
        lock (_deviceStatuses)
        {
            statuses = _deviceStatuses.Values
                .Select(status => new DeviceRuntimeStatus
                {
                    DeviceId = status.DeviceId,
                    Status = status.Status,
                    Hostname = status.Hostname,
                    MacAddress = status.MacAddress,
                    RoundTripTime = status.RoundTripTime,
                    LastChecked = status.LastChecked,
                    LastError = status.LastError
                })
                .ToList();
        }

        int deviceCount = knownDeviceCount ?? (await _deviceStorage.LoadAsync()).Count;
        BackgroundServiceSnapshot snapshot = new()
        {
            ServiceStartedAt = _serviceStartedAt,
            UpdatedAt = DateTime.Now,
            MonitoringEnabled = monitoringEnabled,
            PingIntervalSeconds = intervalSeconds,
            NextPingAt = monitoringEnabled ? _nextPingAt : null,
            PingCycleRunning = _pingCycleRunning,
            DeviceCount = deviceCount,
            OnlineDeviceCount = statuses.Count(status => status.Status == "Online"),
            CpuIsAvailable = _lastCpuReading.IsAvailable,
            CpuIsCpuSpecific = _lastCpuReading.IsCpuSpecific,
            CpuTemperatureCelsius = _lastCpuReading.ValueCelsius,
            CpuDisplay = _lastCpuReading.Display,
            CpuSensorName = _lastCpuReading.SensorName,
            CpuSource = _lastCpuReading.Source,
            CpuDiagnostic = _lastCpuReading.Diagnostic,
            CpuFanRpm = _lastCpuFanReading.Rpm,
            CpuFanDisplay = _lastCpuFanReading.Display,
            CpuFanSensorName = _lastCpuFanReading.SensorName,
            CpuFanSource = _lastCpuFanReading.Source,
            CpuFanDiagnostic = _lastCpuFanReading.Diagnostic,
            CpuPackagePowerWatts = _lastCpuPackagePowerReading.Watts,
            CpuPackagePowerDisplay = _lastCpuPackagePowerReading.Display,
            CpuPackagePowerSensorName = _lastCpuPackagePowerReading.SensorName,
            CpuPackagePowerSource = _lastCpuPackagePowerReading.Source,
            CpuPackagePowerDiagnostic = _lastCpuPackagePowerReading.Diagnostic,
            CpuPackagePowerRecordedAt = _lastCpuPackagePowerRecordedAt,
            GpuPowerWatts = _lastGpuPowerReading.Watts,
            GpuPowerDisplay = _lastGpuPowerReading.Display,
            GpuPowerSensorName = _lastGpuPowerReading.SensorName,
            GpuPowerSource = _lastGpuPowerReading.Source,
            GpuPowerDiagnostic = _lastGpuPowerReading.Diagnostic,
            GpuPowerRecordedAt = _lastGpuPowerRecordedAt,
            PowerScheduleEnabled = _lastSettings.PowerScheduleEnabled,
            PowerScheduleAction = _lastSettings.PowerScheduleAction,
            PowerScheduleTimeText = $"{_lastSettings.PowerScheduleHour:00}:{_lastSettings.PowerScheduleMinute:00}",
            NextPowerActionAt = CalculateNextPowerAction(_lastSettings, DateTime.Now),
            ForcePowerAction = _lastSettings.ForcePowerAction,
            CpuUsagePercent = _lastSystemResourceReading.CpuUsagePercent,
            RamUsagePercent = _lastSystemResourceReading.RamUsagePercent,
            TotalPhysicalMemoryBytes = _lastSystemResourceReading.TotalPhysicalMemoryBytes,
            AvailablePhysicalMemoryBytes = _lastSystemResourceReading.AvailablePhysicalMemoryBytes,
            NetworkConnected = _lastSystemResourceReading.NetworkConnected,
            NetworkSummary = _lastSystemResourceReading.NetworkSummary,
            CpuModel = _lastSystemResourceReading.CpuModel,
            RamSpeedDisplay = _lastSystemResourceReading.RamSpeedDisplay,
            CpuTemperatureAlertThresholdCelsius = _cpuAlertThresholdCelsius,
            CpuTemperatureAlertActive = _lastCpuReading.IsCpuSpecific &&
                _lastCpuReading.ValueCelsius.HasValue &&
                _lastCpuReading.ValueCelsius.Value > _cpuAlertThresholdCelsius,
            Devices = statuses
        };

        await _runtimeStore.SaveAsync(snapshot);
        _lastSnapshotAt = DateTime.Now;
    }

    private async Task ProcessPowerScheduleAsync(AppSettings settings)
    {
        DateTime now = DateTime.Now;
        DateTime previousCheck = _lastPowerScheduleCheckAt == default
            ? now
            : _lastPowerScheduleCheckAt;
        _lastPowerScheduleCheckAt = now;

        if (!settings.PowerScheduleEnabled)
        {
            return;
        }

        // Detect crossing of the configured wall-clock time instead of requiring the
        // service loop to run at an exact second. This also survives sleep/resume.
        DateTime date = previousCheck.Date;
        while (date <= now.Date)
        {
            DateTime scheduled = date
                .AddHours(settings.PowerScheduleHour)
                .AddMinutes(settings.PowerScheduleMinute);

            string marker = scheduled.ToString("yyyy-MM-dd");
            bool crossed = scheduled > previousCheck && scheduled <= now;
            bool alreadyTriggered = string.Equals(
                settings.PowerScheduleLastTriggeredDate,
                marker,
                StringComparison.Ordinal);

            if (crossed && !alreadyTriggered)
            {
                settings.PowerScheduleLastTriggeredDate = marker;
                await _settingsService.SaveAsync(settings);
                _lastSettings = settings;

                bool restart = string.Equals(settings.PowerScheduleAction, "Restart", StringComparison.OrdinalIgnoreCase);
                string action = restart ? "restart" : "shutdown";
                ServiceLog.Write(
                    "Power schedule",
                    $"Starting scheduled Windows {action} for {scheduled:yyyy-MM-dd HH:mm}. " +
                    $"Execution time {now:yyyy-MM-dd HH:mm:ss}. Force close apps: {settings.ForcePowerAction}.");

                try
                {
                    if (restart)
                    {
                        _systemPowerService.RestartNow(settings.ForcePowerAction);
                    }
                    else
                    {
                        _systemPowerService.ShutdownNow(settings.ForcePowerAction);
                    }
                }
                catch (Exception exception)
                {
                    settings.PowerScheduleLastTriggeredDate = string.Empty;
                    await _settingsService.SaveAsync(settings);
                    _lastSettings = settings;
                    ServiceLog.WriteException("Power schedule", exception);
                }

                return;
            }

            date = date.AddDays(1);
        }
    }

    private static void NormalizePowerSchedule(AppSettings settings)
    {
        settings.PowerScheduleHour = Math.Clamp(settings.PowerScheduleHour, 0, 23);
        settings.PowerScheduleMinute = Math.Clamp(settings.PowerScheduleMinute, 0, 59);
        settings.PowerScheduleAction = string.Equals(
            settings.PowerScheduleAction,
            "Shutdown",
            StringComparison.OrdinalIgnoreCase)
            ? "Shutdown"
            : "Restart";
    }

    private DateTime? CalculateNextPowerAction(AppSettings settings, DateTime now)
    {
        if (!settings.PowerScheduleEnabled)
        {
            return null;
        }

        DateTime today = now.Date
            .AddHours(settings.PowerScheduleHour)
            .AddMinutes(settings.PowerScheduleMinute);
        string todayMarker = now.ToString("yyyy-MM-dd");

        if (today > now &&
            !string.Equals(settings.PowerScheduleLastTriggeredDate, todayMarker, StringComparison.Ordinal))
        {
            return today;
        }

        return today.AddDays(1);
    }

    private static int NormalizePingInterval(int seconds)
    {
        int[] allowed = { 10, 15, 30, 60, 120, 300, 600, 1800 };
        return allowed.Contains(seconds)
            ? seconds
            : allowed.OrderBy(value => Math.Abs(value - seconds)).First();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : duration.ToString(@"hh\:mm\:ss");

    private static double NormalizeCpuAlertThreshold(double value) =>
        Math.Clamp(Math.Round(value), 70.0, 105.0);

    private sealed class DeviceIncidentTracker
    {
        public bool? LastOnline { get; set; }

        public DateTime? OnlineSince { get; set; }

        public DevicePingLogEntry? ActiveIncident { get; set; }

        public List<DevicePingLogEntry> Incidents { get; set; } = new();
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }

        _hardwareMonitor.Dispose();
    }
}
