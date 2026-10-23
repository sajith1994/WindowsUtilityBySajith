using System.IO;
using WindowsUtility.Models;
using WindowsUtility.Services;

namespace WindowsUtility.ServiceHost;

/// <summary>
/// Keeps the phone page current when the dashboard is not running (after a restart, before anyone opens it): when
/// the dashboard has not written the page's data for two minutes, the service checks the monitored PCs itself, the
/// same way the dashboard does, and writes it. While the dashboard writes it, the service leaves it alone.
/// </summary>
internal sealed class PhoneViewPoller
{
    private static readonly TimeSpan DashboardQuietFor = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);
    private readonly AppSettingsService _settings = new();
    private readonly RemoteMonitorClient _client = new();
    private readonly Dictionary<Guid, RemotePcView> _views = new();
    private DateTime _lastWrittenByServiceUtc = DateTime.MinValue;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollIfNeededAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                ServiceLog.WriteException("Phone page", exception);
            }

            try
            {
                await Task.Delay(CheckEvery, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollIfNeededAsync(CancellationToken cancellationToken)
    {
        AppSettings settings = await _settings.LoadAsync();
        if (!settings.PhoneViewEnabled || settings.RemotePcs.Count == 0)
        {
            return;
        }

        string path = SharedDataPaths.PhoneViewDataFilePath;
        DateTime writtenUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (DashboardIsWriting(writtenUtc, _lastWrittenByServiceUtc, DateTime.UtcNow))
        {
            return;
        }

        foreach (Guid gone in _views.Keys.Where(id => settings.RemotePcs.All(pc => pc.Id != id)).ToList())
        {
            _views.Remove(gone);
        }

        List<RemotePcView> views = settings.RemotePcs.Select(entry =>
        {
            // A PC whose address or code was edited starts again; otherwise its last-seen time is kept.
            if (!_views.TryGetValue(entry.Id, out RemotePcView? view) || view.Entry.Host != entry.Host || view.Entry.Port != entry.Port
                || view.Entry.PairingCode != entry.PairingCode || view.Entry.DisplayName != entry.DisplayName || view.Entry.Group != entry.Group)
            {
                view = new RemotePcView(entry);
                _views[entry.Id] = view;
            }

            return view;
        }).ToList();

        await Task.WhenAll(views.Select(async view =>
            view.Update(await _client.GetStatsAsync(view.Entry.Host, view.Entry.Port, view.Entry.PairingCode, cancellationToken))));
        PhoneViewService.Write(PhoneViewService.Build(views, Environment.MachineName, DateTime.UtcNow), path);
        _lastWrittenByServiceUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    }

    /// <summary>
    /// True when the dashboard wrote the phone data recently: the file changed since the service last wrote it, less
    /// than two minutes ago.
    /// </summary>
    internal static bool DashboardIsWriting(DateTime writtenUtc, DateTime lastWrittenByServiceUtc, DateTime nowUtc) =>
        writtenUtc != lastWrittenByServiceUtc && nowUtc - writtenUtc < DashboardQuietFor;
}
