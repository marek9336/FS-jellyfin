using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Fastshare.Configuration;
using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Kontrola knihovny filmů pro záložku Navržené:
///  - <b>duplicity</b> — film ve víc verzích (sloučené verze i samostatné položky se stejným TMDb ID):
///    nejlepší nechat, horší navrhnout ke smazání,
///  - <b>lepší verze</b> — nejlepší verze je pod cílovou kvalitou (1080p): najít na Fastshare,
///  - <b>bez metadat</b> — film bez TMDb/IMDb: z názvu souboru vyčíst název a rok pro identifikaci.
/// Nic sama nemaže ani nestahuje — jen navrhuje; rozhoduje uživatel v GUI.
/// </summary>
public sealed class LibraryAdvisor : BackgroundService
{

    private readonly FsState _state;
    private readonly ILibraryManager _library;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILogger<LibraryAdvisor> _logger;
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private CancellationTokenSource? _runCts;

    public LibraryAdvisor(FsState state, ILibraryManager library, IMediaSourceManager mediaSources, ILogger<LibraryAdvisor> logger)
    {
        _state = state;
        _library = library;
        _mediaSources = mediaSources;
        _logger = logger;
        Instance = this;
    }

    /// <summary>Controller se k běžící službě dostane přes tuhle instanci (hosted služby nejdou injektovat přímo).</summary>
    public static LibraryAdvisor? Instance { get; private set; }

    public AdvisorStatus Status { get; } = new();

    /// <summary>Spustí kontrolu hned (tlačítko „Projít knihovnu"). `force` = hledat lepší verze i u nedávno kontrolovaných.</summary>
    public bool Start(bool force)
    {
        if (Status.Running)
        {
            return false;
        }

        _forceNext = force;
        try
        {
            _trigger.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        return true;
    }

    public void Stop() => _runCts?.Cancel();

    private volatile bool _forceNext;

    /// <summary>Kořeny knihoven Jellyfinu — mimo ně se nikdy nic nemaže.</summary>
    public IReadOnlyCollection<string> LibraryRoots() =>
        _library.GetVirtualFolders().SelectMany(f => f.Locations ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Po startu serveru počkat, ať se dokončí sken knihoven (tlačítko čekání zkrátí)
        bool manual;
        try
        {
            manual = await _trigger.WaitAsync(TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var last = _state.Suggestions.LastRun.LastRunUtc;
            var due = cfg.SuggestIntervalDays > 0 && (last == null || DateTime.UtcNow - last.Value > TimeSpan.FromDays(cfg.SuggestIntervalDays));
            if (manual || due)
            {
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    await RunAsync(cfg, _forceNext, _runCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Status.LastResult = "Kontrola zastavena";
                    _state.Suggestions.SetLastRun("zastaveno uživatelem");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Fastshare: kontrola knihovny selhala");
                    Status.LastResult = "Chyba: " + ex.Message;
                }
                finally
                {
                    Status.Running = false;
                    Status.Phase = null;
                    _forceNext = false;
                    _runCts.Dispose();
                    _runCts = null;
                }
            }

            try
            {
                manual = await _trigger.WaitAsync(TimeSpan.FromHours(6), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed class MovieGroup
    {
        public BaseItem Main { get; set; } = null!;

        public List<LibraryVersion> Versions { get; } = new();
    }

    private async Task RunAsync(PluginConfiguration cfg, bool force, CancellationToken ct)
    {
        Status.Running = true;
        Status.Done = 0;
        Status.Phase = "Načítám filmy z knihovny";
        _logger.LogInformation("Fastshare: kontrola knihovny začíná");

        var movies = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
        });

        // ── Seskupit verze filmu: sloučené verze (víc zdrojů jedné položky) i samostatné
        //    položky se stejným TMDb ID („Počátek" 3× jako tři filmy) ──
        var groups = new Dictionary<string, MovieGroup>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var unidentified = new List<BaseItem>();
        foreach (var item in movies)
        {
            ct.ThrowIfCancellationRequested();
            var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
            var imdb = item.GetProviderId(MetadataProvider.Imdb);
            if (string.IsNullOrEmpty(tmdb) && string.IsNullOrEmpty(imdb))
            {
                unidentified.Add(item);
                continue; // bez identity nejde poznat duplicitu ani hledat lepší verzi
            }

            var key = !string.IsNullOrEmpty(tmdb) ? "tmdb:" + tmdb : "imdb:" + imdb;
            if (!groups.TryGetValue(key, out var g))
            {
                g = new MovieGroup { Main = item };
                groups[key] = g;
            }

            foreach (var src in _mediaSources.GetStaticMediaSources(item, false, null))
            {
                if (string.IsNullOrEmpty(src.Path) || !seenPaths.Add(src.Path))
                {
                    continue;
                }

                var video = src.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                var w = video?.Width ?? 0;
                var h = video?.Height ?? 0;
                var size = src.Size ?? (File.Exists(src.Path) ? new FileInfo(src.Path).Length : 0);
                var seconds = item.RunTimeTicks is > 0 ? item.RunTimeTicks.Value / TimeSpan.TicksPerSecond : 0;
                g.Versions.Add(new LibraryVersion
                {
                    ItemId = Guid.TryParse(src.Id, out var sid) ? sid.ToString("N") : item.Id.ToString("N"),
                    Path = src.Path,
                    SizeBytes = size,
                    Width = w,
                    Height = h,
                    Quality = LibraryFiles.QualityOf(w, h),
                    Bitrate = seconds > 0 && size > 0 ? size * 8 / seconds : null,
                });
            }
        }

        var found = new List<Suggestion>();
        var target = string.IsNullOrWhiteSpace(cfg.SuggestTargetQuality) ? "1080p" : cfg.SuggestTargetQuality;
        var targetLevel = LibraryFiles.Level(target);

        // ── Bez metadat ──
        foreach (var item in unidentified)
        {
            var file = new FsFile { FileName = Path.GetFileName(item.Path ?? item.Name) };
            FileNameParser.Fill(file);
            found.Add(new Suggestion
            {
                Id = "id:" + item.Id.ToString("N"),
                Kind = "identify",
                ItemId = item.Id.ToString("N"),
                Title = item.Name,
                Year = item.ProductionYear,
                GuessTitle = string.IsNullOrWhiteSpace(file.Title) ? item.Name : file.Title,
                // rok z vložených metadat bývá nesmysl („h264_opencl 1920x808" → 1920)
                GuessYear = file.Year ?? (item.ProductionYear is >= 1930 && item.ProductionYear <= DateTime.UtcNow.Year + 1 ? item.ProductionYear : null),
                Versions = string.IsNullOrEmpty(item.Path) ? new() : new() { new LibraryVersion { ItemId = item.Id.ToString("N"), Path = item.Path } },
            });
        }

        // ── Duplicity a lepší verze ──
        var list = groups.Values.Where(g => g.Versions.Count > 0).ToList();
        var toSearch = new List<(MovieGroup G, Suggestion? Dup)>();
        foreach (var g in list)
        {
            var best = LibraryFiles.BestFirst(g.Versions);
            g.Versions.Clear();
            g.Versions.AddRange(best);
            var id = g.Main.Id.ToString("N");
            Suggestion? dup = null;
            if (best.Count > 1)
            {
                dup = new Suggestion
                {
                    Id = "dup:" + id,
                    Kind = "duplicate",
                    ItemId = id,
                    Title = g.Main.Name,
                    OriginalTitle = g.Main.OriginalTitle,
                    Year = g.Main.ProductionYear,
                    Versions = best,
                    DeletePaths = best.Skip(1).Select(v => v.Path).ToList(),
                };
                found.Add(dup);
            }

            // Nejlepší verze pod cílem (neznámé rozlišení se nebere — nejde posoudit)
            if (best[0].Quality != null && LibraryFiles.Level(best[0].Quality) < targetLevel)
            {
                toSearch.Add((g, dup));
            }
        }

        // Duplicity a filmy bez metadat jsou hotové hned — uložit, ať jsou v GUI, i když hledání
        // lepších verzí poběží ještě hodinu
        _state.Suggestions.Merge(found, new[] { "identify", "duplicate" });

        // Nejdřív duplicity (ty se hledají vždy, ať je u nich rovnou i lepší verze), pak ostatní;
        // u ostatních jen ty, u kterých se nehledalo posledních X dní.
        var recheck = TimeSpan.FromDays(Math.Max(1, cfg.SuggestRecheckDays));
        toSearch = toSearch
            .OrderByDescending(x => x.Dup != null)
            .ThenBy(x => _state.Suggestions.UpgradeChecked(x.G.Main.Id.ToString("N")) ?? DateTime.MinValue)
            .ToList();
        var rejected = _state.Suggestions.GetAll().Where(s => s.Status != "new").Select(s => s.Id).ToHashSet();

        Status.Total = toSearch.Count;
        _searchPhase = $"Hledám lepší verze na Fastshare ({toSearch.Count} filmů pod {target})";
        Status.Phase = _searchPhase;
        var searched = 0;
        var upgrades = 0;
        var complete = true;
        foreach (var (g, dup) in toSearch)
        {
            ct.ThrowIfCancellationRequested();
            Status.Done++;
            var id = g.Main.Id.ToString("N");
            var checkedAt = _state.Suggestions.UpgradeChecked(id);
            var upId = "up:" + id;
            if (dup == null && rejected.Contains(upId))
            {
                continue;
            }

            if (!force && dup == null && checkedAt != null && DateTime.UtcNow - checkedAt.Value < recheck)
            {
                // nedávno hledáno — návrh (pokud byl) zůstane, jak je
                var old = _state.Suggestions.Get(upId);
                if (old is { Status: "new" })
                {
                    found.Add(old);
                }

                continue;
            }

            if (_state.OutageRemaining() > TimeSpan.Zero)
            {
                Status.Phase = "Fastshare je mimo — hledání lepších verzí přerušeno, dokončí se příště";
                complete = false;
                break;
            }

            var candidate = await FindBetterAsync(g.Main, target, cfg, ct).ConfigureAwait(false);
            searched++;
            _state.Suggestions.MarkUpgradeChecked(id);
            if (dup != null)
            {
                dup.Candidate = candidate;
                _state.Suggestions.Upsert(dup);
            }
            else if (candidate != null)
            {
                upgrades++;
                var up = new Suggestion
                {
                    Id = upId,
                    Kind = "upgrade",
                    ItemId = id,
                    Title = g.Main.Name,
                    OriginalTitle = g.Main.OriginalTitle,
                    Year = g.Main.ProductionYear,
                    Versions = g.Versions.ToList(),
                    Candidate = candidate,
                };
                found.Add(up);
                _state.Suggestions.Upsert(up); // průběžně — přerušená kontrola o nalezené nepřijde
            }

        }

        // Neúplné hledání (výpadek) nesmí smazat dřívější návrhy lepších verzí, na které se nedostalo
        _state.Suggestions.Merge(found, complete ? new[] { "identify", "duplicate", "upgrade" } : new[] { "identify", "duplicate" });
        Status.Phase = null;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{movies.Count} filmů: {found.Count(f => f.Kind == "duplicate")} duplicit, {found.Count(f => f.Kind == "identify")} bez metadat, "
            + $"lepší verze hledána u {searched} (nových návrhů {upgrades})");
        Status.LastResult = summary;
        Status.LastRunUtc = DateTime.UtcNow;
        _state.Suggestions.SetLastRun(summary);
        _logger.LogInformation("Fastshare: kontrola knihovny hotova — {Summary}", summary);
    }

    private string? _searchPhase;

    /// <summary>Stavová hláška při čekání na strop hledání: kdy další dotaz a kolik zbývá.</summary>
    private void WaitNote(DateTime nextUtc)
    {
        var left = Math.Max(0, Status.Total - Status.Done);
        var hours = left * _state.SearchGate.Interval.TotalHours;
        Status.Phase = $"{_searchPhase} — strop {Plugin.Instance?.Configuration.AutoSearchesPerHour}/h, další hledání v "
            + nextUtc.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture)
            + (hours >= 1 ? $", zbývá asi {hours:0} h" : string.Empty);
    }

    /// <summary>Nejlepší soubor v cílové kvalitě: hledá český název + rok, pak původní název.</summary>
    private async Task<SuggestedFile?> FindBetterAsync(BaseItem movie, string target, PluginConfiguration cfg, CancellationToken ct)
    {
        var opts = FsState.SelectorOptions(target);
        opts.MinQuality = target;
        var titles = new[] { movie.Name, movie.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var title in titles)
        {
            var t = new SelectTarget { Kind = MediaKind.Movie, Title = title!, Year = movie.ProductionYear };
            List<FsFile> files;
            try
            {
                files = await _state.Client.SearchAllAsync(t.SearchTerm(), cfg.ShowAdult, 1, ct, _state.SearchGate, WaitNote).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or FastshareException or System.Text.Json.JsonException)
            {
                _logger.LogWarning("Fastshare: hledání lepší verze \"{Title}\" selhalo: {Msg}", title, ex.Message);
                return null;
            }

            var ranked = FileSelector.Rank(files, t, opts);
            if (ranked.Count > 0)
            {
                var f = ranked[0].File;
                return new SuggestedFile
                {
                    FileId = f.Id,
                    FileName = f.FileName,
                    DownloadUrl = f.DownloadUrl,
                    SizeBytes = f.SizeBytes,
                    DurationSec = f.DurationSec,
                    Quality = f.Quality,
                    Language = string.IsNullOrEmpty(f.LanguageLabel) ? null : f.LanguageLabel,
                    Reason = ranked[0].Reason,
                };
            }

        }

        return null;
    }
}
