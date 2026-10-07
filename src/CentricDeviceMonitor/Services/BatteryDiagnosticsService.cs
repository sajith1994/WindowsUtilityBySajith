using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class BatteryDiagnosticsService
{
    private static readonly string BatteryReportPath = Path.Combine(SharedDataPaths.LogsDirectory, "battery-report.html");
    private static readonly string BatteryReportXmlPath = Path.Combine(SharedDataPaths.LogsDirectory, "battery-report.xml");

    public async Task<IReadOnlyList<BatteryHealthInfo>> GetBatteryHealthAsync(CancellationToken cancellationToken = default)
    {
        const string script = """
$ErrorActionPreference = 'SilentlyContinue'

function Get-FirstPropertyValue($obj, [string[]]$names) {
    if ($null -eq $obj) { return $null }
    foreach ($name in $names) {
        $property = $obj.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
            return $property.Value
        }
    }
    return $null
}

function Get-ChemistryName($value) {
    if ($null -eq $value) { return 'Not reported' }
    $text = ([string]$value).Trim()
    $number = 0
    if ([int]::TryParse($text, [ref]$number)) {
        switch ($number) {
            1 { return 'Other' }
            2 { return 'Unknown' }
            3 { return 'Lead Acid' }
            4 { return 'Nickel Cadmium' }
            5 { return 'Nickel Metal Hydride' }
            6 { return 'Lithium-ion' }
            7 { return 'Zinc Air' }
            8 { return 'Lithium Polymer' }
        }
    }

    switch -Regex ($text.ToUpperInvariant()) {
        'LION|LI-ION|LITHIUM.?ION' { return 'Lithium-ion' }
        'LIPO|LI-POLY|LITHIUM.?POLY' { return 'Lithium Polymer' }
        'NIMH|NICKEL.?METAL' { return 'Nickel Metal Hydride' }
        'NICD|NICKEL.?CADMIUM' { return 'Nickel Cadmium' }
        default { if ([string]::IsNullOrWhiteSpace($text)) { return 'Not reported' } else { return $text } }
    }
}

function Get-Win32State($battery) {
    if ($null -eq $battery) { return 'Unknown' }
    $code = 0
    [void][int]::TryParse([string]$battery.BatteryStatus, [ref]$code)
    switch ($code) {
        3 { return 'Fully charged' }
        4 { return 'Low' }
        5 { return 'Critical' }
        6 { return 'Charging' }
        7 { return 'Charging' }
        8 { return 'Charging - low' }
        9 { return 'Charging - critical' }
        11 { return 'Partially charged' }
        default {
            if (-not [string]::IsNullOrWhiteSpace([string]$battery.Status)) { return [string]$battery.Status }
            return 'Unknown'
        }
    }
}

$win32 = @(Get-CimInstance -ClassName Win32_Battery)
$static = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData)
$full = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity)
$status = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryStatus)
$cycles = @(Get-CimInstance -Namespace root/wmi -ClassName BatteryCycleCount)

# powercfg XML is the most reliable fallback on many Modern Standby laptops where
# root\wmi does not expose BatteryStaticData/FullChargedCapacity to normal WMI queries.
$reportBatteries = @()
$cacheDirectory = Join-Path $env:ProgramData 'CentricDeviceMonitor\cache'
$reportPath = Join-Path $cacheDirectory 'battery-static-cache.xml'
try {
    New-Item -ItemType Directory -Path $cacheDirectory -Force | Out-Null
    $needReportFallback = ($static.Count -eq 0 -or $full.Count -eq 0 -or $cycles.Count -eq 0)
    if ($needReportFallback) {
        $refreshReport = -not (Test-Path -LiteralPath $reportPath)
        if (-not $refreshReport) {
            $age = (Get-Date) - (Get-Item -LiteralPath $reportPath).LastWriteTime
            $refreshReport = $age.TotalMinutes -ge 10
        }
        if ($refreshReport) {
            & powercfg.exe /batteryreport /output $reportPath /xml | Out-Null
        }
        if (Test-Path -LiteralPath $reportPath) {
            [xml]$batteryReport = Get-Content -LiteralPath $reportPath -Raw
            $reportBatteries = @($batteryReport.BatteryReport.Batteries.Battery)
        }
    }
}
catch { }

$count = [Math]::Max($win32.Count, [Math]::Max($static.Count, $reportBatteries.Count))
$result = New-Object System.Collections.Generic.List[object]

for ($i = 0; $i -lt $count; $i++) {
    $s = if ($i -lt $static.Count) { $static[$i] } else { $null }
    $w = if ($i -lt $win32.Count) { $win32[$i] } elseif ($win32.Count -gt 0) { $win32[0] } else { $null }
    $r = if ($i -lt $reportBatteries.Count) { $reportBatteries[$i] } else { $null }

    $instance = [string](Get-FirstPropertyValue $s @('InstanceName'))
    if ([string]::IsNullOrWhiteSpace($instance)) { $instance = [string](Get-FirstPropertyValue $w @('DeviceID')) }
    if ([string]::IsNullOrWhiteSpace($instance)) { $instance = [string](Get-FirstPropertyValue $r @('Id')) }
    if ([string]::IsNullOrWhiteSpace($instance)) { $instance = "Battery-$i" }

    $f = $full | Where-Object { [string]$_.InstanceName -eq [string](Get-FirstPropertyValue $s @('InstanceName')) } | Select-Object -First 1
    if ($null -eq $f -and $i -lt $full.Count) { $f = $full[$i] }
    $st = $status | Where-Object { [string]$_.InstanceName -eq [string](Get-FirstPropertyValue $s @('InstanceName')) } | Select-Object -First 1
    if ($null -eq $st -and $i -lt $status.Count) { $st = $status[$i] }
    $cy = $cycles | Where-Object { [string]$_.InstanceName -eq [string](Get-FirstPropertyValue $s @('InstanceName')) } | Select-Object -First 1
    if ($null -eq $cy -and $i -lt $cycles.Count) { $cy = $cycles[$i] }

    $design = Get-FirstPropertyValue $s @('DesignedCapacity','DesignCapacity')
    if ($null -eq $design) { $design = Get-FirstPropertyValue $r @('DesignCapacity','DesignedCapacity') }
    $fullCapacity = Get-FirstPropertyValue $f @('FullChargedCapacity','FullChargeCapacity')
    if ($null -eq $fullCapacity) { $fullCapacity = Get-FirstPropertyValue $r @('FullChargeCapacity','FullChargedCapacity') }
    $remaining = Get-FirstPropertyValue $st @('RemainingCapacity')

    $chargePercent = Get-FirstPropertyValue $w @('EstimatedChargeRemaining')
    if ($null -eq $remaining -and $null -ne $fullCapacity -and $null -ne $chargePercent) {
        $remaining = [math]::Round(([double]$fullCapacity * [double]$chargePercent) / 100.0)
    }

    $health = $null
    if ($null -ne $design -and [double]$design -gt 0 -and $null -ne $fullCapacity -and [double]$fullCapacity -gt 0) {
        $health = ([double]$fullCapacity / [double]$design) * 100.0
    }

    $runtime = Get-FirstPropertyValue $w @('EstimatedRunTime')
    if (($null -eq $runtime -or [int64]$runtime -ge 1000000) -and $null -ne $remaining -and $null -ne $st -and $null -ne $st.Rate -and [double]$st.Rate -lt 0) {
        $runtime = [math]::Round(([double]$remaining / [math]::Abs([double]$st.Rate)) * 60.0)
    }

    $manufacturer = Get-FirstPropertyValue $s @('ManufactureName','ManufacturerName')
    if ($null -eq $manufacturer) { $manufacturer = Get-FirstPropertyValue $r @('Manufacturer') }
    if ($null -eq $manufacturer) { $manufacturer = Get-FirstPropertyValue $w @('Manufacturer') }

    $model = Get-FirstPropertyValue $s @('DeviceName')
    if ($null -eq $model) { $model = Get-FirstPropertyValue $w @('Name','Caption') }
    if ($null -eq $model) { $model = Get-FirstPropertyValue $r @('Id') }

    $serial = Get-FirstPropertyValue $s @('SerialNumber')
    if ($null -eq $serial) { $serial = Get-FirstPropertyValue $r @('SerialNumber') }
    if ($null -eq $serial) { $serial = Get-FirstPropertyValue $w @('DeviceID') }

    $uniqueId = Get-FirstPropertyValue $s @('UniqueID')
    if ($null -eq $uniqueId) { $uniqueId = Get-FirstPropertyValue $r @('Id') }

    $chemistryValue = Get-FirstPropertyValue $r @('Chemistry')
    if ($null -eq $chemistryValue) { $chemistryValue = Get-FirstPropertyValue $w @('Chemistry') }
    if ($null -eq $chemistryValue) { $chemistryValue = Get-FirstPropertyValue $s @('Chemistry') }

    $cycleCount = Get-FirstPropertyValue $cy @('CycleCount')
    if ($null -eq $cycleCount) { $cycleCount = Get-FirstPropertyValue $r @('CycleCount') }

    $charging = if ($null -ne $st) { [bool]$st.Charging } else { $null }
    if ($null -eq $charging -and $null -ne $w) {
        $statusCode = 0
        [void][int]::TryParse([string]$w.BatteryStatus, [ref]$statusCode)
        $charging = ($statusCode -ge 6 -and $statusCode -le 9)
    }

    $discharging = if ($null -ne $st) { [bool]$st.Discharging } else { $null }
    $powerOnline = if ($null -ne $st) { [bool]$st.PowerOnline } else { $null }
    $stateText = if ($charging -eq $true) { 'Charging' } elseif ($discharging -eq $true) { 'Discharging' } elseif ($powerOnline -eq $true) { 'Plugged in' } else { Get-Win32State $w }

    $sourceParts = New-Object System.Collections.Generic.List[string]
    if ($null -ne $s -or $null -ne $f -or $null -ne $st) { $sourceParts.Add('root\\wmi') }
    if ($null -ne $r) { $sourceParts.Add('powercfg XML') }
    if ($null -ne $w) { $sourceParts.Add('Win32_Battery') }

    $result.Add([pscustomobject]@{
        InstanceName = $instance
        Model = if ([string]::IsNullOrWhiteSpace([string]$model)) { 'Not reported' } else { [string]$model }
        Manufacturer = if ([string]::IsNullOrWhiteSpace([string]$manufacturer)) { 'Not reported' } else { [string]$manufacturer }
        SerialNumber = if ([string]::IsNullOrWhiteSpace([string]$serial)) { 'Not reported' } else { [string]$serial }
        UniqueId = if ([string]::IsNullOrWhiteSpace([string]$uniqueId)) { 'Not reported' } else { [string]$uniqueId }
        Chemistry = Get-ChemistryName $chemistryValue
        DesignedCapacityMWh = if ($null -ne $design -and [double]$design -gt 0) { [double]$design } else { $null }
        FullChargeCapacityMWh = if ($null -ne $fullCapacity -and [double]$fullCapacity -gt 0) { [double]$fullCapacity } else { $null }
        RemainingCapacityMWh = if ($null -ne $remaining -and [double]$remaining -gt 0) { [double]$remaining } else { $null }
        HealthPercent = $health
        CycleCount = if ($null -ne $cycleCount -and [uint64]$cycleCount -lt 4294967295) { [uint32]$cycleCount } else { $null }
        EstimatedChargeRemainingPercent = if ($null -ne $chargePercent) { [int]$chargePercent } else { $null }
        EstimatedRunTimeMinutes = if ($null -ne $runtime -and [int64]$runtime -lt 1000000) { [int]$runtime } else { $null }
        VoltageMillivolts = if ($null -ne $st -and $null -ne $st.Voltage) { [double]$st.Voltage } elseif ($null -ne $w -and $null -ne $w.DesignVoltage) { [double]$w.DesignVoltage } else { $null }
        RateMilliwatts = if ($null -ne $st -and $null -ne $st.Rate) { [double]$st.Rate } else { $null }
        Charging = $charging
        Discharging = $discharging
        PowerOnline = $powerOnline
        Critical = if ($null -ne $st) { [bool]$st.Critical } else { $null }
        Status = if ([string]::IsNullOrWhiteSpace([string]$stateText)) { 'Unknown' } else { [string]$stateText }
        Source = ($sourceParts -join ' + ')
    })
}

$result | ConvertTo-Json -Depth 4 -Compress
""";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Windows PowerShell for battery diagnostics.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows battery query failed." : error);
        }

        if (string.IsNullOrWhiteSpace(output) || output == "null" || output == "[]")
        {
            return Array.Empty<BatteryHealthInfo>();
        }

        using JsonDocument document = JsonDocument.Parse(output);
        List<BatteryHealthInfo> batteries = new();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                BatteryHealthInfo? battery = JsonSerializer.Deserialize<BatteryHealthInfo>(element.GetRawText());
                if (battery is not null)
                {
                    batteries.Add(battery);
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            BatteryHealthInfo? battery = JsonSerializer.Deserialize<BatteryHealthInfo>(document.RootElement.GetRawText());
            if (battery is not null)
            {
                batteries.Add(battery);
            }
        }

        return batteries;
    }

    public async Task<string> GenerateWindowsBatteryReportAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(SharedDataPaths.LogsDirectory);
        TryDelete(BatteryReportPath);
        TryDelete(BatteryReportXmlPath);

        await RunPowerCfgBatteryReportAsync(BatteryReportXmlPath, xml: true, cancellationToken);
        await RunPowerCfgBatteryReportAsync(BatteryReportPath, xml: false, cancellationToken);

        if (!File.Exists(BatteryReportPath))
        {
            throw new InvalidOperationException("Windows did not create the HTML battery report.");
        }

        return BatteryReportPath;
    }

    private static async Task RunPowerCfgBatteryReportAsync(string path, bool xml, CancellationToken cancellationToken)
    {
        string arguments = xml
            ? $"/batteryreport /output \"{path}\" /xml"
            : $"/batteryreport /output \"{path}\"";

        ProcessStartInfo startInfo = new()
        {
            FileName = "powercfg.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start powercfg.exe.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();

        if (process.ExitCode != 0 || !File.Exists(path))
        {
            string message = !string.IsNullOrWhiteSpace(error) ? error : output;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Windows could not generate the battery report." : message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A previous report can be overwritten by powercfg if deletion is not possible.
        }
    }
}
