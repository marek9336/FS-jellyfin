using System.Net;
using Jellyfin.Plugin.Fastshare.Configuration;
using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Background worker: drží až <see cref="PluginConfiguration.MaxParallelDownloads"/>
/// souběžných stahování. Fastshare je úložiště s předplatným, ne streamovací katalog,
/// takže žádné „lidské" pauzy ani náhodné rozptyly jako u Stream Cinema — jen to, co
/// chce uživatel: časová okna, volitelný limit rychlosti, denní strop, volné místo.
/// </summary>
public sealed class DownloadWorkerService : BackgroundService
{
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BlockedPoll = TimeSpan.FromMinutes(2);

    private readonly FsState _state;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<DownloadWorkerService> _logger;
    private readonly Random _random = new();
    private readonly List<Task> _tasks = new();

    // Položky zastavené kvůli konci okna — vrací se do fronty bez započtení pokusu
    private readonly HashSet<Guid> _stoppedByWindow = new();

    private volatile bool _scanPending;
    private DateTime _lastScanUtc = DateTime.MinValue;

    public DownloadWorkerService(FsState state, ILibraryManager libraryManager, ILogger<DownloadWorkerService> logger)
    {
        _state = state;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Fastshare worker startuje");
        await SafeDelay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var wait = Tick(ct);
                MaybeScan();
                if (wait > TimeSpan.Zero)
                {
                    await _state.Queue.WaitOrWakeAsync(wait, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fastshare worker: neočekávaná chyba, pokračuji");
                _state.Status.LastMessage = $"Chyba workeru: {ex.Message}";
                await SafeDelay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
            }
        }

        // Vypínání serveru: počkat, až běžící stahování uloží stav (vrátí se do fronty)
        try
        {
            await Task.WhenAll(_tasks.ToArray()).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ignorovat — při startu se Downloading stejně vrací do fronty
        }
    }

    /// <summary>Jedno kolo: doplní volné sloty. Vrací, jak dlouho čekat na další kolo.</summary>
    private TimeSpan Tick(CancellationToken ct)
    {
        _tasks.RemoveAll(t => t.IsCompleted);

        var cfg = Plugin.Instance?.Configuration;
        var status = _state.Status;
        status.Paused = _state.Queue.WorkerPaused;
        status.DailyBytes = _state.Queue.GetDailyBytes();
        status.Account = _state.Client.Account;

        if (cfg == null || _state.Queue.WorkerPaused)
        {
            status.NextActionUtc = null;
            if (_state.Queue.WorkerPaused)
            {
                status.LastMessage = "Pozastaveno";
            }

            return IdlePoll;
        }

        var slots = Math.Clamp(cfg.MaxParallelDownloads, 1, 8);
        var now = DateTime.Now;
        var windowOpen = Schedule.IsOpen(
            cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour, 0, now, cfg.WindowExtraRanges);

        // Konec okna: volitelně zastavit i rozjetá stahování (bez „Stáhnout teď")
        if (!windowOpen && cfg.StopAtWindowEnd)
        {
            foreach (var a in _state.Active.Values.ToList())
            {
                var item = _state.Queue.GetAll().FirstOrDefault(i => i.Id == a.ItemId);
                if (item is { ForceNow: false })
                {
                    lock (_stoppedByWindow)
                    {
                        _stoppedByWindow.Add(a.ItemId);
                    }

                    _state.CancelDownload(a.ItemId);
                }
            }
        }

        if (_state.RunningCount >= slots)
        {
            status.LastMessage = null;
            return IdlePoll;
        }

        // ── Kontroly, které blokují nová stahování ─────────────────
        if (!_state.Client.HasCredentials)
        {
            status.LastMessage = "Chybí přihlášení k Fastshare v nastavení";
            return BlockedPoll;
        }

        var outage = _state.OutageRemaining();
        var dailyCapHit = cfg.DailyCapGb > 0
            && _state.Queue.GetDailyBytes() >= (long)cfg.DailyCapGb * 1024 * 1024 * 1024;

        var free = DownloadEngine.GetFreeSpace(cfg.MoviesPath);
        var freeSeries = DownloadEngine.GetFreeSpace(cfg.SeriesPath);
        status.FreeSpaceBytes = free;
        var minFree = (long)cfg.MinFreeSpaceGb * 1024 * 1024 * 1024;

        var nowUtc = DateTime.UtcNow;
        var started = 0;
        while (_state.RunningCount < slots)
        {
            var item = _state.Queue.TakeNext(i =>
            {
                if (i.RetryAfterUtc > nowUtc && !i.ForceNow)
                {
                    return false;
                }

                if (i.ForceNow)
                {
                    return true; // obchází okno i výpadek; strop a místo níž platí dál
                }

                return windowOpen && outage == TimeSpan.Zero && !dailyCapHit;
            });

            if (item == null)
            {
                break;
            }

            var targetFree = item.MediaType == MediaKind.Episode ? freeSeries : free;
            if (targetFree > 0 && targetFree < minFree)
            {
                _state.Queue.Update(item.Id, i => i.Status = QueueItemStatus.Queued);
                status.LastMessage = $"Málo volného místa ({targetFree / (1024L * 1024 * 1024)} GB), nová stahování nezačnu";
                return BlockedPoll;
            }

            if (dailyCapHit)
            {
                _state.Queue.Update(item.Id, i => i.Status = QueueItemStatus.Queued);
                break;
            }

            var token = _state.BeginDownload(item.Id, ct);
            _state.Active[item.Id] = new ActiveDownload
            {
                ItemId = item.Id,
                Title = DisplayTitle(item),
                BytesTotal = item.SizeBytes,
            };
            _tasks.Add(Task.Run(() => RunItemAsync(item, token, ct), CancellationToken.None));
            started++;
        }

        // ── Stavová hláška ────────────────────────────────────────
        if (_state.RunningCount > 0)
        {
            status.LastMessage = null;
            status.NextActionUtc = null;
            return IdlePoll;
        }

        var queued = _state.Queue.GetAll().Where(i => i.Status == QueueItemStatus.Queued).ToList();
        if (queued.Count == 0)
        {
            status.LastMessage = "Fronta je prázdná";
            status.NextActionUtc = null;
            return IdlePoll;
        }

        if (dailyCapHit)
        {
            status.LastMessage = $"Denní limit {cfg.DailyCapGb} GB vyčerpán, pokračuji zítra";
            return BlockedPoll;
        }

        if (outage > TimeSpan.Zero)
        {
            status.LastMessage = $"{_state.OutageReason ?? "Fastshare je dočasně mimo"} — další pokus v {DateTime.Now.Add(outage):H:mm}";
            status.NextActionUtc = DateTime.UtcNow.Add(outage);
            return outage < BlockedPoll ? outage : BlockedPoll;
        }

        if (!windowOpen)
        {
            var today = Schedule.Describe(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour, now, cfg.WindowExtraRanges);
            var next = Schedule.NextOpen(
                cfg.UseWeeklyWindow, cfg.WeeklyWindow, cfg.WindowFromHour, cfg.WindowToHour, now, cfg.WindowExtraRanges);
            status.LastMessage = next != null
                ? $"Mimo časové okno ({today}), další okno {next.Value:d.M. H:mm}"
                : $"Mimo časové okno ({today})";
            status.NextActionUtc = next?.ToUniversalTime();
            return BlockedPoll;
        }

        var nextRetry = queued.Where(i => i.RetryAfterUtc > nowUtc).Min(i => i.RetryAfterUtc);
        if (nextRetry != null)
        {
            status.LastMessage = $"Čekám na další pokus ({nextRetry.Value.ToLocalTime():H:mm})";
            status.NextActionUtc = nextRetry;
            var w = nextRetry.Value - nowUtc;
            return w < BlockedPoll ? w : BlockedPoll;
        }

        return IdlePoll;
    }

    private async Task RunItemAsync(QueueItem item, CancellationToken itemCt, CancellationToken serverCt)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var active = _state.Active.TryGetValue(item.Id, out var a) ? a : new ActiveDownload { ItemId = item.Id };
        var dlStart = DateTime.UtcNow;

        try
        {
            // Už staženo ve stejné kvalitě? Nestahovat znovu (↻ v historii nastaví Overwrite).
            if (!item.Overwrite)
            {
                var dup = MediaOrganizer.FindDuplicate(cfg.MoviesPath, cfg.SeriesPath, item, cfg.DuplicateSizeTolerancePercent);
                if (dup != null)
                {
                    _state.Queue.Update(item.Id, i =>
                    {
                        i.Status = QueueItemStatus.Skipped;
                        i.TargetPath = dup.Path;
                        i.CompletedUtc = DateTime.UtcNow;
                        i.ErrorMessage = $"Stejná kvalita už v knihovně ({dup.Quality ?? "?"}, {Dedup.FormatSize(dup.SizeBytes)}) — ↻ stáhne znovu";
                        i.ForceNow = false;
                    });
                    return;
                }
            }

            var hash = await _state.Client.GetHashAsync(itemCt).ConfigureAwait(false);
            if (hash == null)
            {
                var why = _state.Client.LastError ?? "přihlášení selhalo";
                if (why.Contains("heslo", StringComparison.OrdinalIgnoreCase) || why.Contains("chybí", StringComparison.OrdinalIgnoreCase))
                {
                    // Špatné heslo se samo nespraví — nezkoušet to dokola (a neblokovat účet)
                    _state.MarkOutage(TimeSpan.FromHours(6), "Přihlášení k Fastshare: " + why + " — oprav v Nastavení");
                }
                else
                {
                    _state.MarkOutage(TimeSpan.FromMinutes(_random.Next(20, 41)), "Fastshare: " + why);
                }

                Requeue(item.Id, "Přihlášení selhalo: " + why);
                return;
            }

            var account = _state.Client.Account;
            if (account is { Unlimited: false } && item.SizeBytes > 0
                && account.CreditMb * 1024L * 1024 < item.SizeBytes)
            {
                throw new FastsharePermanentException(
                    $"Málo kreditu ({account.CreditMb} MB, soubor {Dedup.FormatSize(item.SizeBytes)})");
            }

            var extension = string.IsNullOrEmpty(Path.GetExtension(item.FileName)) ? ".mkv" : Path.GetExtension(item.FileName).ToLowerInvariant();
            var finalPath = MediaOrganizer.BuildTargetPath(cfg.MoviesPath, cfg.SeriesPath, item, extension);
            if (!item.Overwrite)
            {
                finalPath = MediaOrganizer.EnsureUniquePath(finalPath);
            }

            var partPath = finalPath + ".part";
            _logger.LogInformation("Fastshare: stahuji \"{Title}\" → {Path}", DisplayTitle(item), finalPath);

            var lastSave = DateTime.UtcNow;
            void Progress(long done, long total, long speed)
            {
                active.BytesDone = done;
                active.BytesTotal = total;
                active.SpeedBps = speed;
                if ((DateTime.UtcNow - lastSave).TotalSeconds >= 5)
                {
                    lastSave = DateTime.UtcNow;
                    _state.Queue.Update(item.Id, i =>
                    {
                        i.BytesDone = done;
                        i.BytesTotal = total;
                    });
                }
            }

            long sessionBytes;
            try
            {
                sessionBytes = await _state.Engine.DownloadAsync(
                    item.DownloadUrl, partPath, r => FastshareClient.PrepareDownload(r, hash), _state.Limiter,
                    item.SizeBytes, Progress, itemCt).ConfigureAwait(false);
            }
            catch (NotAFileException)
            {
                // Klíč mohl vypršet dřív, než si myslíme — jednou se přihlásit znovu a zkusit
                _state.Client.InvalidateSession();
                hash = await _state.Client.GetHashAsync(itemCt, force: true).ConfigureAwait(false)
                    ?? throw new FastshareException("Přihlášení selhalo: " + _state.Client.LastError);
                sessionBytes = await _state.Engine.DownloadAsync(
                    item.DownloadUrl, partPath, r => FastshareClient.PrepareDownload(r, hash), _state.Limiter,
                    item.SizeBytes, Progress, itemCt).ConfigureAwait(false);
            }

            File.Move(partPath, finalPath, overwrite: true);
            _state.Queue.AddDailyBytes(sessionBytes);
            _state.ClearOutage();

            var seconds = (DateTime.UtcNow - dlStart).TotalSeconds;
            if (sessionBytes >= 50L * 1024 * 1024 && seconds >= 10)
            {
                _state.Queue.RecordSpeed((long)(sessionBytes / seconds));
            }

            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Done;
                i.CompletedUtc = DateTime.UtcNow;
                i.TargetPath = finalPath;
                i.BytesDone = i.BytesTotal = new FileInfo(finalPath).Length;
                i.ErrorMessage = null;
                i.ForceNow = false;
                i.RetryAfterUtc = null;
            });
            _logger.LogInformation("Fastshare: dokončeno \"{Title}\"", DisplayTitle(item));

            if (cfg.TriggerLibraryScan)
            {
                _scanPending = true;
            }

            if (cfg.PauseBetweenSeconds > 0)
            {
                await SafeDelay(TimeSpan.FromSeconds(cfg.PauseBetweenSeconds), serverCt).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
        {
            // Vypínání serveru — po startu se naváže
            Requeue(item.Id, null, Percent(active));
        }
        catch (OperationCanceledException)
        {
            bool byWindow;
            lock (_stoppedByWindow)
            {
                byWindow = _stoppedByWindow.Remove(item.Id);
            }

            _logger.LogInformation("Fastshare: \"{Title}\" zastaveno ({Why})", DisplayTitle(item), byWindow ? "konec okna" : "uživatel");
            Requeue(item.Id, byWindow ? "Zastaveno na konci časového okna — naváže se" : null, Percent(active));
        }
        catch (FastsharePermanentException ex)
        {
            _logger.LogWarning("Fastshare: \"{Title}\" — trvalá chyba: {Msg}", DisplayTitle(item), ex.Message);
            _state.Queue.Update(item.Id, i =>
            {
                i.Status = QueueItemStatus.Error;
                i.ErrorMessage = ex.Message;
                i.StoppedPercent = Percent(active);
                i.ForceNow = false;
            });
        }
        catch (Exception ex)
        {
            var message = ex is HttpRequestException hre
                ? $"HTTP {(int?)hre.StatusCode ?? 0}: {hre.Message}"
                : ex.Message;
            _logger.LogWarning("Fastshare: stahování \"{Title}\" selhalo: {Msg}", DisplayTitle(item), message);

            // Server Fastshare mimo (5xx / nedovoláme se) — ostatní stahování by dopadla stejně
            if (ex is HttpRequestException h && ((int?)h.StatusCode is null or >= 500))
            {
                _state.MarkOutage(TimeSpan.FromMinutes(_random.Next(10, 21)), "Fastshare nedostupný (" + message + ")");
            }

            var failCount = item.FailCount + 1;
            var giveUp = failCount > Math.Max(0, cfg.RetryAttempts);
            var backoff = TimeSpan.FromMinutes(Math.Min(60, 2 * Math.Pow(3, failCount - 1)));
            _state.Queue.Update(item.Id, i =>
            {
                i.FailCount = failCount;
                i.ErrorMessage = message;
                i.StoppedPercent = Percent(active);
                i.Status = giveUp ? QueueItemStatus.Error : QueueItemStatus.Queued;
                i.RetryAfterUtc = giveUp ? null : DateTime.UtcNow.Add(backoff);
                if (giveUp)
                {
                    i.ForceNow = false;
                }
            });
        }
        finally
        {
            _state.EndDownload(item.Id);
            _state.Queue.Wake();
        }
    }

    private void Requeue(Guid id, string? message, int stoppedPercent = 0)
    {
        _state.Queue.Update(id, i =>
        {
            i.Status = QueueItemStatus.Queued;
            i.ErrorMessage = message;
            i.StoppedPercent = stoppedPercent;
        });
    }

    /// <summary>Sken knihovny: až doběhnou všechna stahování, nebo nejvýš 1× za 10 min.</summary>
    private void MaybeScan()
    {
        if (!_scanPending)
        {
            return;
        }

        if (_state.RunningCount > 0 && DateTime.UtcNow - _lastScanUtc < TimeSpan.FromMinutes(10))
        {
            return;
        }

        _scanPending = false;
        _lastScanUtc = DateTime.UtcNow;
        try
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("Fastshare: zařazen sken knihovny");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fastshare: sken knihovny se nepodařilo spustit");
        }
    }

    private static int Percent(ActiveDownload a) =>
        a.BytesTotal > 0 ? (int)Math.Clamp(a.BytesDone * 100 / a.BytesTotal, 0L, 100L) : 0;

    public static string DisplayTitle(QueueItem item) =>
        item.MediaType == MediaKind.Episode
            ? $"{item.SeriesTitle ?? item.Title} S{item.Season:D2}E{item.Episode:D2}"
            : item.Year.HasValue ? $"{item.Title} ({item.Year})" : item.Title;

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
