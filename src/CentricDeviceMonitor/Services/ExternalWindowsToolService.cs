using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CentricDeviceMonitor.Services;

public sealed record WindowsActivationInfo(
    bool IsActivated,
    bool IsGracePeriod,
    bool RequiresActivation,
    string Display,
    string ProductName,
    int? GracePeriodRemainingMinutes);

public sealed class ExternalWindowsToolService
{
    public const string ChrisTitusProjectUrl = "https://github.com/ChrisTitusTech/winutil";
    public const string Win11DebloatProjectUrl = "https://github.com/Raphire/Win11Debloat";
    public const string WinhanceProjectUrl = "https://github.com/memstechtips/Winhance";

    private const string MicrosoftActivationHelpFallbackUrl = "https://support.microsoft.com/windows/activation/activate-windows";
    private const string ChrisTitusReadmeUrl = "https://raw.githubusercontent.com/ChrisTitusTech/winutil/main/README.md";
    private const string Win11DebloatReadmeUrl = "https://raw.githubusercontent.com/Raphire/Win11Debloat/master/README.md";
    private const string WinhanceReadmeUrl = "https://raw.githubusercontent.com/memstechtips/Winhance/main/README.md";
    private const string DefaultChrisTitusEndpoint = "https://christitus.com/win";
    private const string DefaultWin11DebloatEndpoint = "https://debloat.raphi.re/";
    private const string DefaultWinhanceEndpoint = "https://get.winhance.net";
    private const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";
    private static string UserAgent => $"WindowsUtilityBySajith/{typeof(ExternalWindowsToolService).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";

    private string _chrisTitusEndpoint = DefaultChrisTitusEndpoint;
    private string _win11DebloatEndpoint = DefaultWin11DebloatEndpoint;
    private string _winhanceEndpoint = DefaultWinhanceEndpoint;
    private string _microsoftActivationHelpUrl = MicrosoftActivationHelpFallbackUrl;

    public string ChrisTitusEndpoint => _chrisTitusEndpoint;
    public string Win11DebloatEndpoint => _win11DebloatEndpoint;
    public string WinhanceEndpoint => _winhanceEndpoint;

    public string ChrisTitusLaunchCommand => $"irm '{_chrisTitusEndpoint}' | iex";
    public string Win11DebloatLaunchCommand => $"& ([scriptblock]::Create((irm '{_win11DebloatEndpoint}')))";
    public string WinhanceLaunchCommand => $"irm '{_winhanceEndpoint}' | iex";

    public string MicrosoftActivationHelpUrl => _microsoftActivationHelpUrl;

    public string ChrisTitusCommandStatus { get; private set; } = "Using embedded official stable endpoint; checking upstream on startup...";
    public string Win11DebloatCommandStatus { get; private set; } = "Using embedded official quick-install endpoint; checking upstream on startup...";
    public string WinhanceCommandStatus { get; private set; } = "Using embedded official install endpoint; checking upstream on startup...";

    public async Task RefreshChrisTitusCommandAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string readme = await DownloadTextAsync(ChrisTitusReadmeUrl, cancellationToken);
            Match match = Regex.Match(
                readme,
                "irm\\s+[\\\"']?(?<url>https://[^\\s\\\"'`]+)[\\\"']?\\s*\\|\\s*iex",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (TryAcceptOfficialReadmeEndpoint(match, out string? endpoint))
            {
                _chrisTitusEndpoint = endpoint!;
                ChrisTitusCommandStatus = $"Official command refreshed at {DateTime.Now:HH:mm:ss}.";
            }
            else
            {
                ChrisTitusCommandStatus = "Official README checked, but no valid HTTPS launch command was found. Using embedded fallback.";
            }
        }
        catch (OperationCanceledException)
        {
            ChrisTitusCommandStatus = "Upstream command refresh timed out. Using embedded official fallback.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Chris Titus WinUtil command refresh", exception);
            ChrisTitusCommandStatus = "Could not refresh upstream command. Using embedded official fallback.";
        }
    }

    public async Task RefreshWin11DebloatCommandAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string readme = await DownloadTextAsync(Win11DebloatReadmeUrl, cancellationToken);
            Match match = Regex.Match(
                readme,
                "scriptblock.*?irm\\s+[\\\"'](?<url>https://[^\\\"']+)[\\\"']",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

            if (TryAcceptOfficialReadmeEndpoint(match, out string? endpoint))
            {
                _win11DebloatEndpoint = endpoint!;
                Win11DebloatCommandStatus = $"Official quick command refreshed at {DateTime.Now:HH:mm:ss}.";
            }
            else
            {
                Win11DebloatCommandStatus = "Official README checked, but no valid HTTPS quick command was found. Using embedded fallback.";
            }
        }
        catch (OperationCanceledException)
        {
            Win11DebloatCommandStatus = "Upstream command refresh timed out. Using embedded official fallback.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Win11Debloat command refresh", exception);
            Win11DebloatCommandStatus = "Could not refresh upstream command. Using embedded official fallback.";
        }
    }

    public async Task RefreshWinhanceCommandAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            string readme = await DownloadTextAsync(WinhanceReadmeUrl, cancellationToken);
            Match match = Regex.Match(
                readme,
                "irm\\s+[\\\"'](?<url>https://[^\\\"']+)[\\\"']\\s*\\|\\s*iex",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (TryAcceptOfficialReadmeEndpoint(match, out string? endpoint))
            {
                _winhanceEndpoint = endpoint!;
                WinhanceCommandStatus = $"Official install command refreshed at {DateTime.Now:HH:mm:ss}.";
            }
            else
            {
                WinhanceCommandStatus = "Official README checked, but no valid HTTPS install command was found. Using embedded fallback.";
            }
        }
        catch (OperationCanceledException)
        {
            WinhanceCommandStatus = "Upstream command refresh timed out. Using embedded official fallback.";
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Winhance command refresh", exception);
            WinhanceCommandStatus = "Could not refresh upstream command. Using embedded official fallback.";
        }
    }

    public async Task RefreshMicrosoftActivationHelpUrlAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpClient client = CreateHttpClient();
            using HttpRequestMessage request = new(HttpMethod.Get, MicrosoftActivationHelpFallbackUrl);
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            Uri? finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is not null &&
                string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(finalUri.Host, "support.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                 finalUri.Host.EndsWith(".support.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            {
                _microsoftActivationHelpUrl = finalUri.AbsoluteUri;
            }
        }
        catch (OperationCanceledException)
        {
            _microsoftActivationHelpUrl = MicrosoftActivationHelpFallbackUrl;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Microsoft activation help link refresh", exception);
            _microsoftActivationHelpUrl = MicrosoftActivationHelpFallbackUrl;
        }
    }

    public void LaunchChrisTitusWinUtilElevated() => LaunchRemotePowerShellTool(ChrisTitusLaunchCommand, _chrisTitusEndpoint, "Chris Titus Tech WinUtil");

    public void LaunchWin11DebloatElevated() => LaunchRemotePowerShellTool(Win11DebloatLaunchCommand, _win11DebloatEndpoint, "Win11Debloat");

    public void LaunchWinhanceElevated() => LaunchRemotePowerShellTool(WinhanceLaunchCommand, _winhanceEndpoint, "Winhance");

    public void OpenChrisTitusProject() => OpenShell(ChrisTitusProjectUrl);

    public void OpenWin11DebloatProject() => OpenShell(Win11DebloatProjectUrl);

    public void OpenWinhanceProject() => OpenShell(WinhanceProjectUrl);

    public void OpenWindowsActivationSettings() => OpenShell("ms-settings:activation");

    public void OpenMicrosoftActivationHelp() => OpenShell(_microsoftActivationHelpUrl);

    public async Task<string> GetWindowsActivationStatusAsync() =>
        (await GetWindowsActivationInfoAsync()).Display;

    public async Task<WindowsActivationInfo> GetWindowsActivationInfoAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string query = $"SELECT Name, LicenseStatus, PartialProductKey, GracePeriodRemaining FROM SoftwareLicensingProduct WHERE ApplicationID='{WindowsApplicationId}' AND PartialProductKey IS NOT NULL";
                using ManagementObjectSearcher searcher = new(query);
                foreach (ManagementObject item in searcher.Get())
                {
                    string name = Convert.ToString(item["Name"]) ?? string.Empty;
                    if (!name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    uint status = Convert.ToUInt32(item["LicenseStatus"] ?? 0u);
                    int? graceMinutes = null;
                    try
                    {
                        graceMinutes = Convert.ToInt32(item["GracePeriodRemaining"] ?? 0);
                    }
                    catch
                    {
                    }

                    bool activated = status == 1;
                    bool grace = status is 2 or 3 or 4 or 6;
                    bool requiresActivation = status is 0 or 5;

                    string statusText = status switch
                    {
                        1 => "Activated",
                        2 => "Trial / initial grace period",
                        3 => "Additional grace period",
                        4 => "Non-genuine grace period",
                        5 => "Not activated - activation required",
                        6 => "Extended grace period",
                        _ => "Not activated / unknown"
                    };

                    if (grace && graceMinutes.GetValueOrDefault() > 0)
                    {
                        TimeSpan remaining = TimeSpan.FromMinutes(graceMinutes.GetValueOrDefault());
                        string timeText = remaining.TotalDays >= 1
                            ? $"{Math.Floor(remaining.TotalDays):0} day(s) {remaining.Hours} hour(s) remaining"
                            : $"{Math.Max(0, remaining.Hours)} hour(s) {remaining.Minutes} minute(s) remaining";
                        statusText += $" • {timeText}";
                    }

                    string partialKey = Convert.ToString(item["PartialProductKey"]) ?? string.Empty;
                    string display = string.IsNullOrWhiteSpace(partialKey)
                        ? statusText
                        : $"{statusText} • key ending {partialKey}";

                    return new WindowsActivationInfo(
                        activated,
                        grace,
                        requiresActivation,
                        display,
                        name.Trim(),
                        graceMinutes);
                }

                return new WindowsActivationInfo(
                    false,
                    false,
                    false,
                    "Windows activation status not reported.",
                    string.Empty,
                    null);
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Windows activation status", exception);
                return new WindowsActivationInfo(
                    false,
                    false,
                    false,
                    "Activation status unavailable.",
                    string.Empty,
                    null);
            }
        });
    }

    private static async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using HttpClient client = CreateHttpClient();
        return await client.GetStringAsync(url, cancellationToken);
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static bool TryAcceptOfficialReadmeEndpoint(Match match, out string? endpoint)
    {
        endpoint = null;
        if (!match.Success)
        {
            return false;
        }

        string candidate = match.Groups["url"].Value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) || !IsSafeRemoteScriptEndpoint(uri))
        {
            return false;
        }

        endpoint = uri.AbsoluteUri.TrimEnd('/');
        if (candidate.EndsWith("/", StringComparison.Ordinal) && !endpoint.EndsWith("/", StringComparison.Ordinal))
        {
            endpoint += "/";
        }
        return true;
    }

    private static bool IsSafeRemoteScriptEndpoint(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Uri.CheckHostName(uri.Host) != UriHostNameType.Dns ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            IPAddress[] addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses.Any(IsPrivateOrLoopbackAddress))
            {
                return false;
            }
        }
        catch
        {
            // DNS may be temporarily unavailable during startup. The URL still came from the
            // project's official GitHub README and must be HTTPS, so keep the endpoint usable.
        }

        return true;
    }

    private static bool IsPrivateOrLoopbackAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }

    private static void LaunchRemotePowerShellTool(string command, string endpoint, string displayName)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || !IsSafeRemoteScriptEndpoint(uri))
        {
            throw new InvalidOperationException($"The {displayName} endpoint failed the HTTPS/public-host safety check.");
        }

        string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -NoExit -EncodedCommand {encodedCommand}"
        };

        Process.Start(startInfo);
    }

    private static void OpenShell(string target)
    {
        Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true
        });
    }
}
