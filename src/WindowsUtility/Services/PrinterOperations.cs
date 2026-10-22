using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WindowsUtility.Services;

public enum PrinterInstallOutcome
{
    Installed,
    AlreadyInstalled,
    NeedsDriver,
    Failed
}

public sealed record PrinterInstallResult(PrinterInstallOutcome Outcome, string PrinterName, string Message);

public sealed record PrinterRemoveResult(bool Removed, string Message, string PortMessage);

/// <summary>
/// Adding and removing printers, which needs administrator rights. Shared by the dashboard (when it runs as an
/// administrator) and the background service (for standard users), so both build exactly the same scripts. The
/// scripts are built here from validated details only; nothing a caller sends is run as a script.
/// </summary>
public static class PrinterOperations
{
    private static readonly string[] VirtualPortPrefixes =
    {
        "PORTPROMPT:", "NUL:", "FILE:", "SHRFAX:", "ONENOTE", "XPS", "PDF", "MICROSOFT.OFFICE", "AD_PORT"
    };

    /// <summary>Host-name prefixes printers commonly advertise, mapped to the text their Windows drivers contain.</summary>
    private static readonly (string Prefix, string DriverText)[] VendorPrefixes =
    {
        ("EPSON", "EPSON"), ("HP", "HP"), ("NPI", "HP"), ("BRN", "Brother"), ("BRW", "Brother"),
        ("CANON", "Canon"), ("KONICA", "KONICA"), ("KM", "KONICA"), ("XRX", "Xerox"), ("XEROX", "Xerox"),
        ("RICOH", "RICOH"), ("RNP", "RICOH"), ("KYOCERA", "Kyocera"), ("LEXMARK", "Lexmark"),
        ("SEC", "Samsung"), ("SAMSUNG", "Samsung"), ("SHARP", "SHARP"), ("OKI", "OKI"), ("ZEBRA", "ZDesigner")
    };

    public static bool IsVirtualPort(string portName)
    {
        string upper = portName.Trim().ToUpperInvariant();
        return VirtualPortPrefixes.Any(prefix => upper.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// Ports that belong to Windows itself (USB, LPT, COM, FILE:, PORTPROMPT:, nul:, the PDF/XPS/OneNote ports) are
    /// never removed; only network TCP/IP and WSD ports are.
    /// </summary>
    public static bool IsRemovablePort(string portName)
    {
        string port = portName.Trim();
        if (port.Length == 0 || IsVirtualPort(port))
        {
            return false;
        }

        string upper = port.ToUpperInvariant();
        return !(upper.StartsWith("USB", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)
                 || upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("DOT4", StringComparison.Ordinal)
                 || upper.StartsWith("TS", StringComparison.Ordinal) || upper.EndsWith(':'));
    }

    /// <summary>
    /// Installs a printer found by the network scan. IPP printers use Windows' built-in IPP class driver, the same
    /// way Windows' own "Add device" does. RAW/LPD-only printers need a matching driver already on this PC.
    /// </summary>
    public static async Task<PrinterInstallResult> InstallAsync(string address, IReadOnlyCollection<string> services, string model, string hostName,
        CancellationToken cancellationToken = default)
    {
        if (!IPAddress.TryParse(address, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return new PrinterInstallResult(PrinterInstallOutcome.Failed, string.Empty, "The printer address is not a valid IPv4 address.");
        }

        address = ip.ToString();
        string shortName = hostName.Split('.')[0];
        string baseName = !string.IsNullOrWhiteSpace(model) ? model
            : string.IsNullOrWhiteSpace(shortName) ? "Network printer" : shortName;
        string name = Regex.Replace($"{baseName} ({address})", @"[^A-Za-z0-9 ._()\-]", string.Empty);

        string script;
        if (services.Contains("IPP"))
        {
            script = $$"""
                $ErrorActionPreference = 'Stop'
                if (Get-Printer -Name '{{name}}' -ErrorAction SilentlyContinue) { 'RESULT:EXISTS'; exit 0 }
                $lastError = ''
                foreach ($url in @('https://{{address}}:631/ipp/print', 'http://{{address}}:631/ipp/print', 'https://{{address}}:631/ipp', 'http://{{address}}:631/ipp')) {
                    try { Add-Printer -Name '{{name}}' -IppURL $url; 'RESULT:OK'; exit 0 } catch { $lastError = $_.Exception.Message }
                }
                'RESULT:FAIL ' + $lastError
                exit 1
                """;
        }
        else
        {
            // Only letters (and the like) from the fixed vendor table reach the script.
            string vendor = VendorPrefixes.FirstOrDefault(entry => baseName.StartsWith(entry.Prefix, StringComparison.OrdinalIgnoreCase)).DriverText ?? string.Empty;
            string addPort = services.Contains("RAW")
                ? $"Add-PrinterPort -Name $port -PrinterHostAddress '{address}'"
                : $"Add-PrinterPort -Name $port -LprHostAddress '{address}' -LprQueueName 'lp'";
            script = $$"""
                $ErrorActionPreference = 'Stop'
                if (Get-Printer -Name '{{name}}' -ErrorAction SilentlyContinue) { 'RESULT:EXISTS'; exit 0 }
                $vendor = '{{vendor}}'
                $driver = $null
                if ($vendor) { $driver = Get-PrinterDriver | Where-Object { $_.Name -like "*$vendor*" } | Select-Object -First 1 }
                if (-not $driver) { 'RESULT:NODRIVER'; exit 2 }
                $port = 'IP_{{address}}'
                if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { {{addPort}} }
                try { Add-Printer -Name '{{name}}' -DriverName $driver.Name -PortName $port; 'RESULT:OK ' + $driver.Name; exit 0 }
                catch { 'RESULT:FAIL ' + $_.Exception.Message; exit 1 }
                """;
        }

        string output = await RunPowerShellAsync(script, "Adding the printer", cancellationToken);
        string result = output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal)) ?? string.Empty;

        if (result.StartsWith("RESULT:OK", StringComparison.Ordinal))
        {
            string driver = result.Length > "RESULT:OK".Length ? result["RESULT:OK".Length..].Trim() : "Microsoft IPP Class Driver";
            return new PrinterInstallResult(PrinterInstallOutcome.Installed, name, $"Installed \"{name}\" using {driver}.");
        }

        if (result.StartsWith("RESULT:EXISTS", StringComparison.Ordinal))
        {
            return new PrinterInstallResult(PrinterInstallOutcome.AlreadyInstalled, name, $"\"{name}\" is already installed.");
        }

        if (result.StartsWith("RESULT:NODRIVER", StringComparison.Ordinal))
        {
            return new PrinterInstallResult(PrinterInstallOutcome.NeedsDriver, name,
                "This printer does not accept IPP, and no matching driver is installed on this PC. Add it from Windows Settings (Printers & scanners > Add device), or install the manufacturer's driver first.");
        }

        string detail = result.StartsWith("RESULT:FAIL", StringComparison.Ordinal) ? result["RESULT:FAIL".Length..].Trim() : output.Trim();
        return new PrinterInstallResult(PrinterInstallOutcome.Failed, name,
            string.IsNullOrWhiteSpace(detail) ? "Windows did not report why the printer could not be added." : detail);
    }

    /// <summary>
    /// Removes an installed printer and, when asked, its network port (only if no other printer uses it).
    /// Normal mode cancels queued jobs and calls Remove-Printer. Force mode also restarts the Print Spooler while
    /// clearing the spool folder, falls back to deleting the printer's registry key with the spooler stopped, removes
    /// the printer's Plug and Play queue device (so WSD printers do not reinstall themselves) and retries the port
    /// after the restart.
    /// </summary>
    public static async Task<PrinterRemoveResult> RemoveAsync(string printerName, string portName, bool removePort, bool force,
        CancellationToken cancellationToken = default)
    {
        string output = await RunPowerShellAsync(BuildRemoveScript(printerName, portName, removePort, force), "Removing the printer", cancellationToken);
        return ParseRemoveOutput(printerName, force, output);
    }

    internal static string BuildRemoveScript(string printerName, string portName, bool removePort, bool force)
    {
        // Single-quoted PowerShell strings: doubling ' is the only escaping they need.
        string name = printerName.Replace("'", "''");
        string port = portName == "WSD" ? string.Empty : portName.Replace("'", "''");
        bool registryFallbackAllowed = !printerName.Contains('\\') && !printerName.Contains('/');

        string forceSteps = force
            ? $$"""
                Stop-Service -Name Spooler -Force -ErrorAction SilentlyContinue
                Remove-Item -Path "$env:windir\System32\spool\PRINTERS\*" -Force -ErrorAction SilentlyContinue
                Start-Service -Name Spooler
                Start-Sleep -Seconds 2
                if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { try { Remove-Printer -Name $name -ErrorAction Stop } catch { $lastError = $_.Exception.Message } }
                if ({{(registryFallbackAllowed ? "$true" : "$false")}} -and (Get-Printer -Name $name -ErrorAction SilentlyContinue)) {
                    Stop-Service -Name Spooler -Force -ErrorAction SilentlyContinue
                    Remove-Item -Path ("HKLM:\SYSTEM\CurrentControlSet\Control\Print\Printers\" + $name) -Recurse -Force -ErrorAction SilentlyContinue
                    Start-Service -Name Spooler
                    Start-Sleep -Seconds 2
                }
                Get-PnpDevice -Class PrintQueue -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -eq $name } |
                    ForEach-Object { & pnputil.exe /remove-device $_.InstanceId | Out-Null }
                """
            : string.Empty;

        string portSteps = removePort && IsRemovablePort(portName)
            ? """
                if ($port) {
                    $users = @(Get-Printer -ErrorAction SilentlyContinue | Where-Object { $_.PortName -eq $port })
                    if ($users.Count -gt 0) { 'PORT:INUSE ' + $port }
                    elseif (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { 'PORT:GONE ' + $port }
                    else {
                        try { Remove-PrinterPort -Name $port -ErrorAction Stop; 'PORT:REMOVED ' + $port }
                        catch {
                            if ($force) {
                                Restart-Service -Name Spooler -Force; Start-Sleep -Seconds 2
                                try { Remove-PrinterPort -Name $port -ErrorAction Stop; 'PORT:REMOVED ' + $port } catch { 'PORT:FAILED ' + $_.Exception.Message }
                            } else { 'PORT:FAILED ' + $_.Exception.Message }
                        }
                    }
                }
                """
            : "if ($port) { 'PORT:KEPT ' + $port }";

        return $$"""
            $ErrorActionPreference = 'Stop'
            $name = '{{name}}'
            $port = '{{port}}'
            $force = ${{(force ? "true" : "false")}}
            $lastError = ''
            $wasInstalled = [bool](Get-Printer -Name $name -ErrorAction SilentlyContinue)
            if ($wasInstalled) {
                try { Get-PrintJob -PrinterName $name -ErrorAction SilentlyContinue | Remove-PrintJob -ErrorAction SilentlyContinue } catch { }
                try { Remove-Printer -Name $name -ErrorAction Stop } catch { $lastError = $_.Exception.Message }
            }
            {{forceSteps}}
            if (Get-Printer -Name $name -ErrorAction SilentlyContinue) { 'RESULT:FAIL ' + $lastError; exit 1 }
            if ($wasInstalled) { 'RESULT:REMOVED' } else { 'RESULT:ALREADYGONE' }
            {{portSteps}}
            """;
    }

    private static PrinterRemoveResult ParseRemoveOutput(string printerName, bool force, string output)
    {
        string[] lines = output.Split('\n').Select(line => line.Trim()).ToArray();
        string result = lines.LastOrDefault(line => line.StartsWith("RESULT:", StringComparison.Ordinal)) ?? string.Empty;
        string portLine = lines.LastOrDefault(line => line.StartsWith("PORT:", StringComparison.Ordinal)) ?? string.Empty;

        string portMessage = portLine switch
        {
            _ when portLine.StartsWith("PORT:REMOVED", StringComparison.Ordinal) => $"Port {portLine["PORT:REMOVED".Length..].Trim()} removed.",
            _ when portLine.StartsWith("PORT:INUSE", StringComparison.Ordinal) => $"Port {portLine["PORT:INUSE".Length..].Trim()} kept: another printer still uses it.",
            _ when portLine.StartsWith("PORT:GONE", StringComparison.Ordinal) => "The port was already gone.",
            _ when portLine.StartsWith("PORT:FAILED", StringComparison.Ordinal) => $"The port could not be removed: {portLine["PORT:FAILED".Length..].Trim()}",
            _ when portLine.StartsWith("PORT:KEPT", StringComparison.Ordinal) => $"Port {portLine["PORT:KEPT".Length..].Trim()} kept.",
            _ => string.Empty
        };

        if (result.StartsWith("RESULT:REMOVED", StringComparison.Ordinal))
        {
            return new PrinterRemoveResult(true, $"\"{printerName}\" was removed{(force ? " (forced)" : string.Empty)}.", portMessage);
        }

        if (result.StartsWith("RESULT:ALREADYGONE", StringComparison.Ordinal))
        {
            return new PrinterRemoveResult(true, $"\"{printerName}\" was already removed.", portMessage);
        }

        string detail = result.StartsWith("RESULT:FAIL", StringComparison.Ordinal) ? result["RESULT:FAIL".Length..].Trim() : output.Trim();
        return new PrinterRemoveResult(false,
            string.IsNullOrWhiteSpace(detail) ? "Windows did not say why the printer could not be removed." : detail,
            portMessage);
    }

    private static async Task<string> RunPowerShellAsync(string script, string what, CancellationToken cancellationToken)
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
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{what} took too long and was stopped.");
        }

        return (await output) + Environment.NewLine + (await error);
    }
}
