using System.Globalization;
using Jellyfin.Plugin.Fastshare.Configuration;
using Jellyfin.Plugin.Fastshare.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Hlídač: hlídané seriály a filmy periodicky hledá na Fastshare a do fronty zařadí,
/// co chybí — u seriálu všechny nalezené díly, které ještě nejsou staženy ani ve
/// frontě (nejlepší soubor podle preferencí), u filmu nejlepší soubor, jakmile se objeví.
/// </summary>
public sealed class WatcherService : BackgroundService
{
    private static readonly TimeSpan Round = TimeSpan.FromMinutes(15);

    private readonly FsState _state;
    private readonly ILogger<WatcherService> _logger;

    public WatcherService(FsState state, ILogger<WatcherService> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await _state.WaitWatcherAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckDueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fastshare hlídač: chyba kola");
            }

            await _state.WaitWatcherAsync(Round, ct).ConfigureAwait(false);
        }
    }

    private async Task CheckDueAsync(CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var now = DateTime.UtcNow;
        foreach (var w in _state.Watch.GetAll())
        {
            if (!w.Enabled || (w.Completed && !w.ForceCheck))
            {
                continue;
            }

            var interval = TimeSpan.FromDays(Math.Max(1, w.IntervalDays));
            if (!w.ForceCheck && w.LastCheckedUtc != null && now - w.LastCheckedUtc.Value < interval)
            {
                continue;
            }

            if (!w.ForceCheck && _state.OutageRemaining() > TimeSpan.Zero)
            {
                return;
            }

            string result;
            try
            {
                result = await CheckOneAsync(w, cfg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = "Chyba: " + ex.Message;
                _logger.LogWarning("Fastshare hlídač: \"{Title}\" — {Msg}", w.Title, ex.Message);
            }

            _state.Watch.Update(w.Id, i =>
            {
                i.LastCheckedUtc = DateTime.UtcNow;
                i.LastResult = DateTime.Now.ToString("d.M. H:mm", CultureInfo.InvariantCulture) + " — " + result;
                i.ForceCheck = false;
            });

            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Jedna kontrola. Vrací popis výsledku pro GUI.</summary>
    public async Task<string> CheckOneAsync(WatchItem w, PluginConfiguration cfg, CancellationToken ct)
    {
        var term = string.IsNullOrWhiteSpace(w.SearchTerm) ? w.Title : w.SearchTerm!;
        var opts = FsState.SelectorOptions(w.MaxQuality);
        var queue = _state.Queue.GetAll();

        if (w.Type == "movie")
        {
            var searchTerm = w.Year is > 0 && string.IsNullOrWhiteSpace(w.SearchTerm) ? $"{term} {w.Year}" : term;
            var files = await _state.Client.SearchAllAsync(searchTerm, cfg.ShowAdult, 2, ct).ConfigureAwait(false);
            var t = new SelectTarget { Kind = MediaKind.Movie, Title = w.Title, Year = w.Year };
            var ranked = FileSelector.Rank(files, t, opts);
            if (ranked.Count == 0)
            {
                return $"nic vhodného ({files.Count} souborů ve výsledcích)";
            }

            var item = QueueItemFactory.Create(ranked[0].File, t);
            item.FromWatcher = true;
            var outcome = _state.Queue.Add(item);
            _state.Watch.Update(w.Id, i =>
            {
                i.MovieGrabbed = true;
                i.Completed = true;
            });
            _state.Queue.Wake();
            return outcome == AddOutcome.Added ? "zařazeno: " + ranked[0].Reason : "už ve frontě";
        }

        // ── Seriál ────────────────────────────────────────────────
        var all = await _state.Client.SearchAllAsync(term, cfg.ShowAdult, Math.Clamp(cfg.WatchSearchPages, 1, 10), ct)
            .ConfigureAwait(false);
        var episodes = all
            .Where(f => f.Season != null && f.Episode != null && f.IsVideo)
            .Where(f => w.FromSeason == null || f.Season >= w.FromSeason)
            .Where(f => FileNameParser.MatchesTitle(f, w.Title))
            .GroupBy(f => (Season: f.Season!.Value, Episode: f.Episode!.Value))
            .OrderBy(g => g.Key.Season)
            .ThenBy(g => g.Key.Episode)
            .ToList();

        var added = 0;
        var noMatch = 0;
        var grabbed = new List<string>();
        foreach (var g in episodes)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"S{g.Key.Season:D2}E{g.Key.Episode:D2}");
            if (w.Grabbed.Contains(key))
            {
                continue;
            }

            var t = new SelectTarget
            {
                Kind = MediaKind.Episode, Title = w.Title, Year = w.Year, Season = g.Key.Season, Episode = g.Key.Episode,
            };
            var probe = QueueItemFactory.Create(g.First(), t);

            // Už v knihovně nebo ve frontě (jakákoli kvalita) → nestahovat, jen poznamenat
            if (MediaOrganizer.FindExistingAll(cfg.MoviesPath, cfg.SeriesPath, probe).Count > 0
                || queue.Any(q => q.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading or QueueItemStatus.Done
                    && Dedup.MediaKey(q) == Dedup.MediaKey(probe)))
            {
                grabbed.Add(key);
                continue;
            }

            var ranked = FileSelector.Rank(g, t, opts);
            if (ranked.Count == 0)
            {
                noMatch++;
                continue;
            }

            var item = QueueItemFactory.Create(ranked[0].File, t);
            item.FromWatcher = true;
            if (_state.Queue.Add(item) == AddOutcome.Added)
            {
                added++;
            }

            grabbed.Add(key);
        }

        if (grabbed.Count > 0)
        {
            _state.Watch.Update(w.Id, i => i.Grabbed = i.Grabbed.Union(grabbed).ToList());
        }

        if (added > 0)
        {
            _state.Queue.GroupSeries();
            _state.Queue.Wake();
        }

        return $"nalezeno {episodes.Count} dílů, zařazeno {added}"
            + (noMatch > 0 ? $", {noMatch} bez vhodného souboru (jazyk/kvalita)" : string.Empty);
    }
}
