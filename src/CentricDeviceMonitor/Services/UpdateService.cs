using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CentricDeviceMonitor.Services;

/// <summary>Contents of update.json published next to each installer.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("released")]
    public string Released { get; set; } = string.Empty;

    /// <summary>Installer size in bytes (written by publish-update.ps1 from 2.0.53; 0 when absent).</summary>
    [JsonPropertyName("size")]
    public long Size { get; set; }
}

/// <summary>Download progress with size and speed for the update window and dashboard banner.</summary>
public sealed record DownloadProgress(long Received, long? Total, double BytesPerSecond)
{
    public double Fraction => Total is > 0 ? Math.Clamp(Received / (double)Total.Value, 0, 1) : 0;

    public string Describe()
    {
        string amount = Total is > 0
            ? $"{UpdateService.FormatBytes(Received)} of {UpdateService.FormatBytes(Total.Value)} ({Fraction:P0})"
            : $"{UpdateService.FormatBytes(Received)} downloaded";
        if (BytesPerSecond <= 0)
        {
            return amount;
        }

        string text = $"{amount}  •  {UpdateService.FormatBytes(BytesPerSecond)}/s";
        if (Total is > 0 && Received < Total)
        {
            double secondsLeft = (Total.Value - Received) / BytesPerSecond;
            text += secondsLeft < 60 ? $"  •  about {Math.Max(1, secondsLeft):0} s left" : $"  •  about {secondsLeft / 60:0} min left";
        }

        return text;
    }
}

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version CurrentVersion,
    UpdateManifest? Manifest,
    Version? LatestVersion,
    string Message);

/// <summary>
/// Checks a manifest published with each GitHub release, downloads the installer it names,
/// verifies the SHA-256 and runs it silently. The manifest URL is a compile-time constant on
/// purpose: the settings file is writable by standard users while this app runs elevated, so a
/// configurable URL would be a privilege-escalation path.
/// </summary>
public static class UpdateService
{
    // Public releases-only repository. update.json is attached to every release, and GitHub's
    // "latest" alias always resolves to the newest one.
    public const string ManifestUrl =
        "https://github.com/sajith1994/WindowsUtilityBySajith/releases/latest/download/update.json";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsUtilityBySajith-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    public static Version GetCurrentVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? new Version(0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>Date the running build was produced (stamped by Directory.Build.props), if known.</summary>
    public static DateTime? GetCurrentReleaseDate()
    {
        string? value = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ReleaseDate")?.Value;
        return ParseReleaseDate(value);
    }

    /// <summary>Parses the yyyy-MM-dd dates used by the build stamp and update.json.</summary>
    public static DateTime? ParseReleaseDate(string? value)
    {
        return DateTime.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date
            : null;
    }

    public static string FormatReleaseDate(DateTime date) => date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        Version current = GetCurrentVersion();
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            using HttpResponseMessage response = await Http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // No release has been published yet, so nothing can be newer than this build.
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, null, null,
                    $"You are on the latest version ({current}).");
            }

            response.EnsureSuccessStatusCode();
            EnsureTrustedHost(response.RequestMessage?.RequestUri);

            string json = (await response.Content.ReadAsStringAsync(timeout.Token)).TrimStart('﻿');
            UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(json);
            if (manifest is null || !Version.TryParse(manifest.Version, out Version? latest))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, null,
                    "The update information was not in the expected format.");
            }

            if (latest <= current)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, current, manifest, latest,
                    $"You are on the latest version ({current}).");
            }

            if (!IsTrustedUrl(manifest.Url) || manifest.Sha256.Trim().Length != 64)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, latest,
                    "A newer version exists but its download details were rejected as unsafe.");
            }

            return new UpdateCheckResult(UpdateCheckStatus.Available, current, manifest, latest,
                $"Version {latest} is available. You have {current}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, null, "The update check timed out. Check the internet connection and try again.");
        }
        catch (HttpRequestException exception)
        {
            ApplicationLogService.WriteException("Update check", exception);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, null,
                "The update server could not be reached. Check the internet connection and try again.");
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Update check", exception);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, current, null, null, $"Could not check for updates: {exception.Message}");
        }
    }

    public const string DeveloperName = "Sajith Varghese";

    /// <summary>Installer size in bytes: from update.json when published with it, otherwise asked from the server.</summary>
    public static async Task<long?> GetDownloadSizeAsync(UpdateManifest manifest, CancellationToken cancellationToken)
    {
        if (manifest.Size > 0)
        {
            return manifest.Size;
        }

        if (!IsTrustedUrl(manifest.Url))
        {
            return null;
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, manifest.Url);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024:0} KB",
        _ => $"{bytes:0} bytes"
    };

    /// <summary>Downloads the installer to a temporary folder and returns its verified path.</summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!IsTrustedUrl(manifest.Url))
        {
            throw new InvalidOperationException("The installer address is not a trusted HTTPS location.");
        }

        string folder = Path.Combine(Path.GetTempPath(), "WindowsUtilityBySajith", "Update");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder, "*.exe"))
        {
            try { File.Delete(old); } catch { /* an old installer still in use is harmless */ }
        }

        string fileName = Path.GetFileName(new Uri(manifest.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = $"WindowsUtilityBySajith-Setup-{manifest.Version}.exe";
        }

        string destination = Path.Combine(folder, fileName);

        using HttpResponseMessage response = await Http.GetAsync(manifest.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureTrustedHost(response.RequestMessage?.RequestUri);

        long? total = response.Content.Headers.ContentLength;
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            Stopwatch clock = Stopwatch.StartNew();
            TimeSpan lastReport = TimeSpan.Zero;
            Queue<(TimeSpan Time, long Bytes)> window = new();
            window.Enqueue((TimeSpan.Zero, 0));
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;

                // Report about four times a second; speed is measured over the last two seconds.
                TimeSpan now = clock.Elapsed;
                if (now - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    lastReport = now;
                    window.Enqueue((now, received));
                    while (window.Count > 2 && now - window.Peek().Time > TimeSpan.FromSeconds(2))
                    {
                        window.Dequeue();
                    }

                    (TimeSpan startTime, long startBytes) = window.Peek();
                    double seconds = (now - startTime).TotalSeconds;
                    double speed = seconds > 0 ? (received - startBytes) / seconds : 0;
                    progress?.Report(new DownloadProgress(received, total, speed));
                }
            }

            progress?.Report(new DownloadProgress(received, total, clock.Elapsed.TotalSeconds > 0 ? received / clock.Elapsed.TotalSeconds : 0));
        }

        string actual;
        await using (FileStream verify = new(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken));
        }

        if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(destination); } catch { }
            throw new InvalidDataException("The downloaded installer did not match its published checksum, so it was discarded.");
        }

        return destination;
    }

    private static string RelaunchMarkerPath => Path.Combine(SharedDataPaths.RootDirectory, "update-relaunch.flag");

    /// <summary>Removes the self-update marker once the updated dashboard is running.</summary>
    public static void ClearRelaunchMarker()
    {
        try
        {
            if (File.Exists(RelaunchMarkerPath))
            {
                File.Delete(RelaunchMarkerPath);
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Clear update relaunch marker", exception);
        }
    }

    /// <summary>Starts the verified installer silently. The caller should shut the app down right after.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        // The installer reopens the dashboard when it sees /RELAUNCH or this marker.
        try
        {
            SharedDataPaths.EnsureDirectories();
            File.WriteAllText(RelaunchMarkerPath, DateTime.Now.ToString("O"));
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Write update relaunch marker", exception);
        }

        ApplicationLogService.WriteMessage("Update", $"Starting installer {Path.GetFileName(installerPath)}; the dashboard will reopen when it finishes.");

        ProcessStartInfo startInfo = new(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty
        };

        Process.Start(startInfo);
    }

    private static bool IsTrustedUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && IsTrustedUri(uri);
    }

    private static bool IsTrustedUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureTrustedHost(Uri? finalUri)
    {
        if (finalUri is null || !IsTrustedUri(finalUri))
        {
            throw new InvalidOperationException("The update was served from an untrusted location and was rejected.");
        }
    }
}
