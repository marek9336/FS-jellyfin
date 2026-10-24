using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Fastshare.Configuration;
using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
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

    private volatile bool _restartRequested;
    private volatile bool _restartForce;

    // Seriály „🔎 Hledat teď": jejich díly jdou v příští kontrole na začátek fronty a hledají se i nedávno
    // prohledané. Po zpracování ze seznamu zmizí.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _prioritySeries = new(StringComparer.Ordinal);

    public void Prioritize(string seriesId)
    {
        _prioritySeries[seriesId] = 0;
        Restart();
    }

    private bool IsPriority(SearchJob j) => j.NetKey != null && _prioritySeries.ContainsKey(j.NetKey);

    /// <summary>
    /// Spustit kontrolu znovu hned (změna nastavení seriálu). Běžící se zastaví — nic se neztratí:
    /// návrhy se ukládají průběžně a prohledané položky se přeskočí.
    /// </summary>
    public void Restart(bool force = false)
    {
        if (!Status.Running)
        {
            Start(force);
            return;
        }

        _restartForce = force; // _forceNext by vynuloval finally přerušené kontroly
        _restartRequested = true;
        Stop();
    }

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

            // Nedokončená kontrola (restart uprostřed dvoudenního hledání) pokračuje — už prohledané
            // filmy přeskočí (mají čas poslední kontroly), takže se nic nehledá dvakrát
            if (manual || due || _state.Suggestions.RunPending)
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

            if (_restartRequested)
            {
                _restartRequested = false;
                _forceNext = _restartForce;
                _restartForce = false;
                manual = true;
                continue;
            }

            try
            {
                // nedokončená kontrola (výpadek Fastshare) → zkusit po pauze, jinak co 6 h ověřit termín
                var wait = _state.Suggestions.RunPending
                    ? TimeSpan.FromMinutes(Math.Max(5, _state.OutageRemaining().TotalMinutes + 1))
                    : TimeSpan.FromHours(6);
                manual = await _trigger.WaitAsync(wait, ct).ConfigureAwait(false);
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
        _state.Suggestions.RunPending = true;
        _seriesSearch.Clear();
        _seriesFetched.Clear();
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
        var misplaced = new List<(BaseItem Item, FsFile File)>();
        foreach (var item in movies)
        {
            ct.ThrowIfCancellationRequested();

            // Díl seriálu, který se dostal do Filmů („/Filmy/Chernobyl.S01E03….avi") → návrh přesunu
            var asFile = new FsFile { FileName = Path.GetFileName(item.Path ?? string.Empty) };
            FileNameParser.Fill(asFile);
            if (asFile.Episode != null && asFile.IsVideo)
            {
                misplaced.Add((item, asFile));
                continue;
            }

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

            g.Versions.AddRange(VersionsOf(item, seenPaths));
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

        // ── Duplicity a lepší verze filmů ──
        var rejected = _state.Suggestions.GetAll().Where(s => s.Status != "new").Select(s => s.Id).ToHashSet();
        var jobs = new List<SearchJob>();
        foreach (var g in groups.Values.Where(g => g.Versions.Count > 0))
        {
            var best = LibraryFiles.BestFirst(g.Versions);
            var id = g.Main.Id.ToString("N");
            foreach (var v in best)
            {
                v.Suspect = LibraryFiles.MovieSuspect(v, g.Main.Name, g.Main.OriginalTitle, g.Main.ProductionYear);
            }

            LibraryFiles.MarkSequelMismatch(best, g.Main.Name, g.Main.OriginalTitle);

            LibraryFiles.MarkLengths(best);
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
                    DeletePaths = LibraryFiles.DefaultDelete(best),
                };
                found.Add(dup);
            }

            // Nejlepší verze pod cílem (neznámé rozlišení se nebere — nejde posoudit)
            if (best[0].Quality == null || LibraryFiles.Level(best[0].Quality) >= targetLevel)
            {
                continue;
            }

            var upId = "up:" + id;
            var main = g.Main;
            var d = dup;
            jobs.Add(new SearchJob
            {
                Key = id,
                Priority = d != null ? 0 : 2,
                Skip = rejected.Contains(d?.Id ?? upId),
                Find = c => FindBetterAsync(main, target, cfg, c),
                Apply = cand =>
                {
                    if (d != null)
                    {
                        d.Candidate = cand;
                        _state.Suggestions.Upsert(d);
                    }
                    else if (cand != null)
                    {
                        var up = new Suggestion
                        {
                            Id = upId, Kind = "upgrade", ItemId = id, Title = main.Name, OriginalTitle = main.OriginalTitle,
                            Year = main.ProductionYear, Versions = best, Candidate = cand,
                        };
                        found.Add(up);
                        _state.Suggestions.Upsert(up); // průběžně — přerušená kontrola o nalezené nepřijde
                    }
                },
                KeepOld = () =>
                {
                    // nedávno hledáno: dřívější návrh lepší verze zůstává (duplicita nese kandidáta přes Merge)
                    if (d == null && _state.Suggestions.Get(upId) is { Status: "new" } old)
                    {
                        found.Add(old);
                    }
                },
            });
        }

        // ── Seriály: sjednotit díly na převažující kvalitu ──
        Status.Phase = "Načítám seriály z knihovny";
        jobs.AddRange(CollectSeries(cfg, target, found, rejected, ct));
        CollectMisplaced(misplaced, cfg, found);

        // Duplicity, filmy bez metadat a díly jsou hotové hned — uložit, ať jsou v GUI, i když
        // hledání lepších verzí poběží ještě dny
        _state.Suggestions.Merge(found, new[] { "identify", "duplicate", "episode", "missing", "misplaced", "tomovies" });

        // Pořadí: duplicity filmů → díly seriálů → lepší verze filmů; v rámci skupiny nejdéle nehledané
        var recheck = TimeSpan.FromDays(Math.Max(1, cfg.SuggestRecheckDays));
        // Skupiny (duplicity filmů, díly seriálů, lepší verze filmů) se střídají — dřív šly za sebou
        // a na filmy bez duplicit se při 20–60 hledáních za hodinu nedostalo celé dny
        var queues = jobs
            .Where(j => !j.Skip)
            .GroupBy(j => j.Priority)
            .OrderBy(g => g.Key)
            .Select(g => new Queue<SearchJob>(g.OrderBy(j => _state.Suggestions.UpgradeChecked(j.Key) ?? DateTime.MinValue)))
            .ToList();
        jobs = new List<SearchJob>();
        while (queues.Any(q => q.Count > 0))
        {
            foreach (var q in queues.Where(q => q.Count > 0))
            {
                jobs.Add(q.Dequeue());
            }
        }

        // „Hledat teď" → díly těch seriálů na začátek
        var prioritized = jobs.Where(IsPriority).Select(j => j.NetKey!).ToHashSet(StringComparer.Ordinal);
        var askedAtStart = _prioritySeries.Keys.ToList(); // i seriály, u kterých nebylo co hledat
        if (askedAtStart.Count > 0)
        {
            _logger.LogInformation("Fastshare: přednostně {Series} — {Jobs} položek", string.Join(", ", askedAtStart), jobs.Count(IsPriority));
        }

        jobs = jobs.Where(IsPriority).Concat(jobs.Where(j => !IsPriority(j))).ToList();

        Status.Total = jobs.Count;
        Status.Done = 0;
        _jobs = jobs;
        _recheck = recheck;
        _force = force;
        _searchPhase = $"Hledám lepší verze na Fastshare ({jobs.Count(j => j.Priority != 1)} filmů, {jobs.Count(j => j.Priority == 1)} dílů z {jobs.Where(j => j.NetKey != null).Select(j => j.NetKey).Distinct().Count()} seriálů)";
        Status.Phase = _searchPhase;
        var searched = 0;
        var complete = true;
        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            Status.Done++;
            if (prioritized.Count > 0 && !IsPriority(job))
            {
                // přednostní seriály hotové
                foreach (var p in prioritized)
                {
                    _prioritySeries.TryRemove(p, out _);
                }

                prioritized.Clear();
            }

            var checkedAt = _state.Suggestions.UpgradeChecked(job.Key);
            if (!force && !IsPriority(job) && checkedAt != null && DateTime.UtcNow - checkedAt.Value < recheck)
            {
                job.KeepOld?.Invoke();
                continue;
            }

            if (_state.OutageRemaining() > TimeSpan.Zero)
            {
                Status.Phase = "Fastshare je mimo — hledání přerušeno, dokončí se příště";
                complete = false;
                break;
            }

            SuggestedFile? candidate;
            try
            {
                candidate = await job.Find(ct).ConfigureAwait(false);
            }
            catch (FastshareException)
            {
                Status.Phase = _state.OutageReason ?? "Hledání přerušeno";
                complete = false;
                break; // neoznačí se jako prohledané — příště na to dojde znovu
            }

            searched++;
            _state.Suggestions.MarkUpgradeChecked(job.Key);
            job.Apply(candidate);
        }

        foreach (var p in askedAtStart)
        {
            _prioritySeries.TryRemove(p, out _);
        }

        // Neúplné hledání (výpadek) nesmí smazat dřívější návrhy lepších verzí, na které se nedostalo
        _state.Suggestions.Merge(found, complete
            ? new[] { "identify", "duplicate", "episode", "missing", "misplaced", "tomovies", "upgrade" }
            : new[] { "identify", "duplicate", "episode", "missing", "misplaced", "tomovies" });
        Status.Phase = null;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{movies.Count} filmů: {found.Count(f => f.Kind == "duplicate")} duplicit, {found.Count(f => f.Kind == "identify")} bez metadat; "
            + $"{_state.Suggestions.Series.Count} seriálů: {found.Count(f => f.Kind == "episode")} dílů k sjednocení; "
            + $"hledáno {searched}× (návrhů lepší verze {found.Count(f => f.Kind == "upgrade")})");
        Status.LastResult = summary;
        Status.LastRunUtc = DateTime.UtcNow;
        _state.Suggestions.SetLastRun(summary);
        if (!complete)
        {
            // výpadek Fastshare: na zbytek nezapomenout, pokračovat po pauze (ne až za týden)
            _state.Suggestions.RunPending = true;
            var at = DateTime.Now + _state.OutageRemaining();
            Status.LastResult = summary + $" — přerušeno ({_state.OutageReason ?? "Fastshare mimo"}), pokračuje v {at:H:mm}";
        }
        _logger.LogInformation("Fastshare: kontrola knihovny hotova — {Summary}", summary);
    }

    /// <summary>Jedno hledání lepší verze (film nebo díl) ve společné frontě.</summary>
    private sealed class SearchJob
    {
        /// <summary>ID položky — pod ním se pamatuje, kdy se naposledy hledalo.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>0 = duplicita filmu, 1 = díl seriálu, 2 = lepší verze filmu.</summary>
        public int Priority { get; set; }

        /// <summary>Díly jednoho seriálu sdílejí jedno hledání (ID seriálu) — pro odhad zbývajícího času.</summary>
        public string? NetKey { get; set; }

        /// <summary>Návrh odmítnutý / vyřízený — nehledat.</summary>
        public bool Skip { get; set; }

        public Func<CancellationToken, Task<SuggestedFile?>> Find { get; set; } = null!;

        public Action<SuggestedFile?> Apply { get; set; } = null!;

        public Action? KeepOld { get; set; }
    }

    /// <summary>Verze (soubory) položky — sloučené verze i jednotlivé zdroje. `seen` brání dvojímu započtení.</summary>
    private List<LibraryVersion> VersionsOf(BaseItem item, HashSet<string> seen)
    {
        var list = new List<LibraryVersion>();
        foreach (var src in _mediaSources.GetStaticMediaSources(item, false, null))
        {
            if (string.IsNullOrEmpty(src.Path) || !seen.Add(src.Path))
            {
                continue;
            }

            var video = src.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var w = video?.Width ?? 0;
            var h = video?.Height ?? 0;
            var size = src.Size ?? (File.Exists(src.Path) ? new FileInfo(src.Path).Length : 0);
            var ticks = src.RunTimeTicks is > 0 ? src.RunTimeTicks : item.RunTimeTicks;
            var seconds = ticks is > 0 ? ticks.Value / TimeSpan.TicksPerSecond : 0;
            list.Add(new LibraryVersion
            {
                DurationSec = seconds > 0 ? (int)seconds : null,
                ItemId = Guid.TryParse(src.Id, out var sid) ? sid.ToString("N") : item.Id.ToString("N"),
                OwnerId = item.Id.ToString("N"),
                Path = src.Path,
                SizeBytes = size,
                Width = w,
                Height = h,
                Quality = LibraryFiles.QualityOf(w, h),
                Bitrate = seconds > 0 && size > 0 ? size * 8 / seconds : null,
            });
        }

        return list;
    }

    /// <summary>
    /// Seriály: pro každý spočítá kvalitu dílů, cíl = převažující kvalita (při shodě vyšší, nejvýš
    /// cíl z nastavení; ručně zvolený má přednost). Díl pod cílem nebo ve víc verzích = návrh.
    /// Vrací hledání lepších verzí dílů pod cílem.
    /// </summary>
    private List<SearchJob> CollectSeries(PluginConfiguration cfg, string maxTarget, List<Suggestion> found, HashSet<string> rejected, CancellationToken ct)
    {
        var jobs = new List<SearchJob>();
        _seriesEps.Clear();
        var episodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>().ToList();

        // Chybějící díly: virtuální položky, které vytváří TMDb plugin Jellyfinu („Importovat chybějící
        // epizody" pro knihovnu). Bez něj žádné nejsou → počet chybějících neznámý (null).
        var virtualBySeries = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = true,
        }).OfType<Episode>().GroupBy(e => e.SeriesId).ToDictionary(g => g.Key, g => g.ToList());
        var missingKnown = virtualBySeries.Count > 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var summaries = new List<SeriesSummary>();
        foreach (var series in episodes.Where(e => e.SeriesId != Guid.Empty).GroupBy(e => e.SeriesId))
        {
            ct.ThrowIfCancellationRequested();
            var seriesId = series.Key.ToString("N");
            var eps = series
                .Select(e => (Ep: e, Versions: LibraryFiles.BestFirst(VersionsOf(e, seen))))
                .Where(x => x.Versions.Count > 0)
                .ToList();
            if (eps.Count == 0)
            {
                continue;
            }

            var dist = eps.Where(x => x.Versions[0].Quality != null)
                .GroupBy(x => x.Versions[0].Quality!)
                .ToDictionary(g => g.Key, g => g.Count());
            var manual = _state.Suggestions.SeriesTarget(seriesId);
            var mode = dist.OrderByDescending(kv => kv.Value).ThenByDescending(kv => LibraryFiles.Level(kv.Key)).Select(kv => kv.Key).FirstOrDefault() ?? maxTarget;
            var target = manual ?? (LibraryFiles.Level(mode) > LibraryFiles.Level(maxTarget) ? maxTarget : mode);
            var targetLevel = LibraryFiles.Level(target);
            var name = series.First().SeriesName ?? "?";
            var summary = new SeriesSummary
            {
                SeriesId = seriesId,
                Name = name,
                Episodes = eps.Count,
                Qualities = dist,
                Target = target,
                TargetIsManual = manual != null,
                MultiVersion = eps.Count(x => x.Versions.Count > 1),
                SavingsBytes = eps.Sum(x => x.Versions.Skip(1).Sum(v => v.SizeBytes)),
                FindMissing = _state.Suggestions.FindMissing(seriesId),
                Folders = eps.Select(x => SeriesRoot(x.Versions[0].Path)).Distinct(StringComparer.Ordinal).ToList(),
            };
            summaries.Add(summary);

            BaseItem? seriesItem = _library.GetItemById(series.Key);
            summary.TmdbId = seriesItem?.GetProviderId(MetadataProvider.Tmdb);
            summary.SeriesStatus = (seriesItem as MediaBrowser.Controller.Entities.TV.Series)?.Status?.ToString();
            summary.OriginalTitle = seriesItem?.OriginalTitle;
            _seriesEps[seriesId] = eps.Select(x => (x.Ep, x.Versions)).ToList();
            var titles = new[] { name, seriesItem?.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            jobs.AddRange(CollectMissing(series.Key, seriesId, name, titles!, seriesItem, eps.Select(x => (x.Ep, x.Versions)).ToList(),
                virtualBySeries, missingKnown, target, summary, found, rejected, cfg));
            // díly, které TMDb / knihovna zná (řada, díl) → název — pro odhad neočíslovaných dílů
            var known = new Dictionary<(int Season, int Episode), string?>();
            foreach (var e in eps.Select(x => x.Ep).Concat(virtualBySeries.TryGetValue(series.Key, out var virt) ? virt : new List<Episode>()))
            {
                if (e.ParentIndexNumber is int ks && e.IndexNumber is int ke)
                {
                    known.TryAdd((ks, ke), e.Name);
                }
            }

            foreach (var (ep, versions) in eps)
            {
                foreach (var v in versions)
                {
                    v.Suspect = LibraryFiles.EpisodeSuspect(v, ep.ParentIndexNumber, ep.IndexNumber);
                    if (ep.IndexNumber == null)
                    {
                        var g = EpisodeGuess.Guess(v.Path, titles!, known);
                        v.FileSeason = g?.Season;
                        v.FileEpisode = g?.Episode;
                        v.FileEpisodeName = g?.Name;
                        v.FileGuessSure = g?.Sure == true;
                        v.Suspect = g == null
                            ? "Jellyfin díl neočísloval a z názvu souboru ho nejde poznat — zařaď ho ručně"
                            : string.Create(CultureInfo.InvariantCulture, $"Jellyfin díl neočísloval — odhad S{g.Season:D2}E{g.Episode:D2}")
                              + (g.Name != null ? $" „{g.Name}“" : " (TMDb takový díl nezná)") + $" {g.How}" + (g.Sure ? string.Empty : " — nejisté, zkontroluj");
                    }
                }

                LibraryFiles.MarkLengths(versions);
                var epId = ep.Id.ToString("N");

                // Neočíslovaný „díl", který je ve skutečnosti film → návrh přesunu do Filmů (místo dílu)
                if (ep.IndexNumber == null && versions.All(v => !v.FileGuessSure)
                    && versions.Select(v => (v, why: LibraryFiles.MovieInSeries(v, titles!))).ToList() is var mv && mv.All(x => x.why != null))
                {
                    var f0 = LibraryFiles.Describe(versions[0]);
                    var movieName = string.IsNullOrWhiteSpace(f0.Title) ? Path.GetFileNameWithoutExtension(versions[0].Path) : f0.Title;
                    found.Add(new Suggestion
                    {
                        Id = "tomov:" + epId,
                        Kind = "tomovies",
                        ItemId = epId,
                        Title = movieName,
                        Year = f0.Year,
                        SeriesId = seriesId,
                        SeriesName = name,
                        Note = mv[0].why,
                        TargetDir = cfg.MoviesPath,
                        Versions = versions,
                    });
                    continue;
                }

                var below = versions[0].Quality != null && LibraryFiles.Level(versions[0].Quality) < targetLevel;
                if (!below && versions.Count < 2 && versions.All(v => v.Suspect == null))
                {
                    continue;
                }

                var sug = new Suggestion
                {
                    Id = "ep:" + epId,
                    Kind = "episode",
                    ItemId = epId,
                    Title = name,
                    SeriesId = seriesId,
                    SeriesName = name,
                    Season = ep.ParentIndexNumber,
                    Episode = ep.IndexNumber,
                    TargetQuality = target,
                    Versions = versions,
                    DeletePaths = LibraryFiles.DefaultDelete(versions),
                };
                found.Add(sug);

                if (!below || ep.ParentIndexNumber == null || ep.IndexNumber == null)
                {
                    continue;
                }

                var s = sug;
                jobs.Add(new SearchJob
                {
                    Key = epId,
                    Priority = 1,
                    NetKey = seriesId,
                    Skip = rejected.Contains(s.Id),
                    Find = c => FindEpisodeAsync(seriesId, titles!, ep.ParentIndexNumber.Value, ep.IndexNumber.Value, target, cfg, c),
                    Apply = cand =>
                    {
                        s.Candidate = cand;
                        _state.Suggestions.Upsert(s);
                    },
                });
            }
        }

        _state.Suggestions.Series = summaries.OrderBy(x => x.Name, StringComparer.CurrentCulture).ToList();
        _seriesItems = summaries;
        return jobs;
    }

    private readonly Dictionary<string, List<(Episode Ep, List<LibraryVersion> Versions)>> _seriesEps = new();
    private List<SeriesSummary> _seriesItems = new();

    /// <summary>Složka seriálu z cesty dílu (nad „Season 01" / „Série 1", jinak složka dílu).</summary>
    public static string SeriesRoot(string episodePath)
    {
        var dir = Path.GetDirectoryName(episodePath) ?? string.Empty;
        var name = Path.GetFileName(dir);
        return name.StartsWith("Season", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Série", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Serie", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Řada", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dir) ?? dir
            : dir;
    }

    /// <summary>Díly seriálů ve Filmech: najít seriál v knihovně (název nebo původní název) a cílovou složku řady.</summary>
    private void CollectMisplaced(List<(BaseItem Item, FsFile File)> misplaced, PluginConfiguration cfg, List<Suggestion> found)
    {
        foreach (var (item, file) in misplaced)
        {
            var series = _seriesItems.FirstOrDefault(sm =>
                FileNameParser.Normalize(sm.Name) == FileNameParser.Normalize(file.Title)
                || FileNameParser.Normalize(sm.OriginalTitle) == FileNameParser.Normalize(file.Title)
                || LibraryFiles.TitlesOverlap(sm.OriginalTitle, file.Title) && LibraryFiles.TitlesOverlap(sm.Name, file.Title))
                ?? _seriesItems.FirstOrDefault(sm => LibraryFiles.TitlesOverlap(sm.Name, file.Title) || LibraryFiles.TitlesOverlap(sm.OriginalTitle, file.Title));
            var season = file.Season ?? 1;
            string? dir;
            if (series != null && _seriesEps.TryGetValue(series.SeriesId, out var eps))
            {
                dir = SeasonDir(eps, season, _library.GetItemById(Guid.Parse(series.SeriesId)));
            }
            else
            {
                dir = Path.Combine(cfg.SeriesPath, MediaOrganizer.Sanitize(file.Title), string.Create(CultureInfo.InvariantCulture, $"Season {season:D2}"));
            }

            var v = new LibraryVersion { ItemId = item.Id.ToString("N"), Path = item.Path!, SizeBytes = File.Exists(item.Path) ? new FileInfo(item.Path!).Length : 0 };
            found.Add(new Suggestion
            {
                Id = "mov:" + item.Id.ToString("N"),
                Kind = "misplaced",
                ItemId = item.Id.ToString("N"),
                Title = series?.Name ?? file.Title,
                SeriesId = series?.SeriesId,
                SeriesName = series?.Name ?? file.Title,
                Season = season,
                Episode = file.Episode,
                TargetDir = dir,
                Versions = new() { v },
                Note = series != null ? $"patří k seriálu „{series.Name}“ v knihovně" : "seriál v knihovně zatím není — vznikne nová složka",
            });
        }
    }

    /// <summary>Všechny soubory dílů seriálu (i víc verzí) — pro smazání a sloučení seriálu.</summary>
    public List<(Episode Ep, LibraryVersion Version)> SeriesFiles(string seriesId)
    {
        var guid = Guid.Parse(seriesId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Episode], Recursive = true, IsVirtualItem = false })
            .OfType<Episode>()
            .Where(e => e.SeriesId == guid)
            .SelectMany(e => VersionsOf(e, seen).Select(v => (e, v)))
            .ToList();
    }

    /// <summary>Složka řady seriálu pro přesun dílu (jako u chybějících dílů).</summary>
    public string? SeasonDirFor(string seriesId, int season)
    {
        var files = SeriesFiles(seriesId);
        var eps = files.GroupBy(f => f.Ep.Id).Select(g => (g.First().Ep, g.Select(x => x.Version).ToList())).ToList();
        return SeasonDir(eps, season, _library.GetItemById(Guid.Parse(seriesId)));
    }

    /// <summary>
    /// Chybějící odvysílané díly seriálu (podle virtuálních položek z TMDb). Návrhy a hledání jen
    /// u seriálů, kde to uživatel zapnul — Simpsonovi by jinak znamenali stovky dotazů.
    /// </summary>
    private List<SearchJob> CollectMissing(
        Guid seriesGuid, string seriesId, string name, IReadOnlyList<string> titles, BaseItem? seriesItem,
        List<(Episode Ep, List<LibraryVersion> Versions)> eps, Dictionary<Guid, List<Episode>> virtualBySeries, bool missingKnown,
        string target, SeriesSummary summary, List<Suggestion> found, HashSet<string> rejected, PluginConfiguration cfg)
    {
        var jobs = new List<SearchJob>();
        var have = new HashSet<(int, int)>();
        foreach (var (ep, _) in eps)
        {
            if (ep.ParentIndexNumber is int sn && ep.IndexNumber is int en)
            {
                for (var k = en; k <= (ep.IndexNumberEnd ?? en); k++)
                {
                    have.Add((sn, k)); // dvojdíl „S01E01-E02" pokrývá oba
                }
            }
        }

        var now = DateTime.UtcNow;
        var missing = (virtualBySeries.TryGetValue(seriesGuid, out var v) ? v : new List<Episode>())
            .Where(e => e.ParentIndexNumber is > 0 && e.IndexNumber != null && e.PremiereDate != null && e.PremiereDate <= now)
            .Where(e => !have.Contains((e.ParentIndexNumber!.Value, e.IndexNumber!.Value)))
            .GroupBy(e => (e.ParentIndexNumber!.Value, e.IndexNumber!.Value))
            .Select(g => g.First())
            .OrderBy(e => e.ParentIndexNumber)
            .ThenBy(e => e.IndexNumber)
            .ToList();
        summary.Missing = missingKnown ? missing.Count : null;
        if (!summary.FindMissing)
        {
            return jobs;
        }

        foreach (var m in missing)
        {
            var season = m.ParentIndexNumber!.Value;
            var episode = m.IndexNumber!.Value;
            var id = string.Create(CultureInfo.InvariantCulture, $"miss:{seriesId}:S{season:D2}E{episode:D2}");
            var sug = new Suggestion
            {
                Id = id,
                Kind = "missing",
                ItemId = m.Id.ToString("N"),
                Title = name,
                SeriesId = seriesId,
                SeriesName = name,
                Season = season,
                Episode = episode,
                EpisodeName = m.Name,
                PremiereDate = m.PremiereDate,
                TargetQuality = target,
                TargetDir = SeasonDir(eps, season, seriesItem),
            };
            found.Add(sug);
            var s = sug;
            jobs.Add(new SearchJob
            {
                Key = id,
                Priority = 1,
                NetKey = seriesId,
                Skip = rejected.Contains(id),
                Find = c => FindEpisodeAsync(seriesId, titles, season, episode, target, cfg, c, allowLower: true),
                Apply = cand =>
                {
                    s.Candidate = cand;
                    _state.Suggestions.Upsert(s);
                },
                KeepOld = () =>
                {
                    if (_state.Suggestions.Get(id) is { Status: "new", Candidate: not null } old)
                    {
                        s.Candidate = old.Candidate;
                    }
                },
            });
        }

        return jobs;
    }

    /// <summary>Složka řady v knihovně: kde leží jiné díly téže řady, jinak vedle ostatních řad, jinak složka seriálu.</summary>
    private static string? SeasonDir(List<(Episode Ep, List<LibraryVersion> Versions)> eps, int season, BaseItem? seriesItem)
    {
        var same = eps.FirstOrDefault(x => x.Ep.ParentIndexNumber == season && x.Versions.Count > 0);
        if (same.Ep != null)
        {
            return Path.GetDirectoryName(same.Versions[0].Path);
        }

        var seasonName = string.Create(CultureInfo.InvariantCulture, $"Season {season:D2}");
        var any = eps.FirstOrDefault(x => x.Versions.Count > 0);
        if (any.Ep != null)
        {
            var dir = Path.GetDirectoryName(any.Versions[0].Path)!;
            var parent = Path.GetFileName(dir).StartsWith("Season", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(dir).StartsWith("Série", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir)!
                : dir;
            return Path.Combine(parent, seasonName);
        }

        return string.IsNullOrEmpty(seriesItem?.Path) ? null : Path.Combine(seriesItem.Path, seasonName);
    }

    /// <summary>Díl v cílové kvalitě: „Název S01E02" (český, pak původní název seriálu).</summary>
    // Hledání po seriálech: „Simpsonovi" jednou (pár stránek po 200), z výsledků se vyberou všechny díly —
    // místo dotazu na každý díl („Simpsonovi S05E01"). Při stropu 20/h je to rozdíl mezi dny a hodinami
    // a pro Fastshare to vypadá jako běžné hledání, ne jako procházení botem.
    private const int SeriesSearchPages = 5;
    private readonly Dictionary<string, (List<FsFile> Files, bool Truncated)> _seriesSearch = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seriesFetched = new(StringComparer.Ordinal);

    private async Task<SuggestedFile?> FindEpisodeAsync(string seriesId, IReadOnlyList<string> titles, int season, int episode, string target, PluginConfiguration cfg, CancellationToken ct, bool allowLower = false)
    {
        var opts = FsState.SelectorOptions(target);
        opts.MinQuality = target;

        // Chybějící díl: když není v cílové kvalitě, lepší nižší než žádný (bez dalšího dotazu)
        var fallback = allowLower ? FsState.SelectorOptions(target) : null;
        foreach (var title in titles)
        {
            var t = new SelectTarget { Kind = MediaKind.Episode, Title = title, Season = season, Episode = episode };
            if (!_seriesSearch.TryGetValue(title, out var cached))
            {
                var total = 0;
                var files = await SearchFilesAsync(FastshareClient.SearchTerm(title), SeriesSearchPages, cfg, ct, n => total = n).ConfigureAwait(false);
                cached = (files ?? new List<FsFile>(), files != null && total > SeriesSearchPages);
                if (files != null)
                {
                    _seriesSearch[title] = cached;
                }
            }

            _seriesFetched.Add(seriesId);
            var c = Pick(cached.Files, t, opts, fallback);
            if (c == null && cached.Truncated)
            {
                // obří seriál (víc než 1000 souborů) — na díl, který se do stránek nevešel, zvlášť
                c = await SearchBestAsync(t, opts, cfg, ct, fallback).ConfigureAwait(false);
            }

            if (c != null)
            {
                return c;
            }
        }

        return null;
    }

    private string? _searchPhase;

    private List<SearchJob> _jobs = new();
    private TimeSpan _recheck;
    private bool _force;

    /// <summary>Hledá se (nebylo prohledáno v posledních N dnech, nebo „hledat znovu vše")?</summary>
    private bool NeedsSearch(SearchJob j)
    {
        var at = _state.Suggestions.UpgradeChecked(j.Key);
        return _force || IsPriority(j) || at == null || DateTime.UtcNow - at.Value >= _recheck;
    }

    /// <summary>Stavová hláška při čekání na strop hledání: kdy další dotaz a kolik zbývá.</summary>
    private void WaitNote(DateTime nextUtc)
    {
        // dotazy, které ještě zbývají: film = 1, seriál = ~2 stránky (jeho díly už pak žádný dotaz nepotřebují)
        var rest = _jobs.Skip(Math.Max(0, Status.Done - 1)).Where(NeedsSearch).ToList();
        var left = rest.Count(j => j.NetKey == null)
            + (2 * rest.Where(j => j.NetKey != null && !_seriesFetched.Contains(j.NetKey)).Select(j => j.NetKey).Distinct().Count());
        if (_state.SearchGate.Paused)
        {
            Status.Phase = _state.Queue.FastshareStopped
                ? $"{_searchPhase} — ⛔ STOP, hledání stojí, dokud Fastshare nepovolíš"
                : $"{_searchPhase} — automatické hledání vypnuto (strop 0 v Nastavení)";
            return;
        }

        var hours = left * _state.SearchGate.Interval.TotalHours / _state.SearchGate.WindowFraction;
        var win = _state.SearchGate.WindowText;
        Status.Phase = $"{_searchPhase} — strop {Plugin.Instance?.Configuration.AutoSearchesPerHour}/h{(win != null ? " jen " + win : string.Empty)}, další hledání v "
            + nextUtc.ToLocalTime().ToString("H:mm", CultureInfo.InvariantCulture)
            + (hours >= 1 ? $", zbývá asi {hours:0} h" : string.Empty);
    }

    /// <summary>Nejlepší soubor filmu v cílové kvalitě: český název + rok, pak původní název.</summary>
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
            var c = await SearchBestAsync(t, opts, cfg, ct).ConfigureAwait(false);
            if (c != null)
            {
                return c;
            }
        }

        return null;
    }

    /// <summary>
    /// Jedno hledání přes strop dotazů. Přibrzdění (429/403 z Cloudflare) nebo výpadek (5xx) →
    /// hodinová pauza a <see cref="FastshareException"/> (smyčka kontroly skončí, položka se
    /// neoznačí jako prohledaná). Jiná chyba = nic nenalezeno.
    /// </summary>
    private async Task<SuggestedFile?> SearchBestAsync(SelectTarget t, FileSelectorOptions opts, PluginConfiguration cfg, CancellationToken ct, FileSelectorOptions? fallback = null)
    {
        var files = await SearchFilesAsync(t.SearchTerm(), 1, cfg, ct).ConfigureAwait(false);
        return files == null ? null : Pick(files, t, opts, fallback);
    }

    /// <summary>Hledání přes strop dotazů (každá stránka = jeden dotaz). null = chyba (nic nenalezeno).</summary>
    private async Task<List<FsFile>?> SearchFilesAsync(string term, int pages, PluginConfiguration cfg, CancellationToken ct, Action<int>? onTotalPages = null)
    {
        try
        {
            var got = 0;
            var files = await _state.Client.SearchAllAsync(term, cfg.ShowAdult, pages, ct, _state.SearchGate, WaitNote, n =>
            {
                got++;
                onTotalPages?.Invoke(n);
            }).ConfigureAwait(false);
            _logger.LogInformation("Fastshare: kontrola hledala „{Term}“ — {Pages} {Word}, {Count} souborů", term, got, got == 1 ? "dotaz" : "dotazy", files.Count);
            return files;
        }
        catch (Exception ex) when (ex is HttpRequestException or FastshareException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Fastshare: hledání \"{Term}\" selhalo: {Msg}", term, ex.Message);
            var code = (int?)(ex as HttpRequestException)?.StatusCode;
            if (ex is HttpRequestException && code is null or 429 or 403 or >= 500)
            {
                _state.MarkOutage(TimeSpan.FromHours(1), code is 429 or 403
                    ? $"Fastshare omezil hledání (HTTP {code}) — pauza hodinu, zvaž nižší strop v Nastavení"
                    : "Fastshare nedostupný při hledání (" + ex.Message + ")");
                throw new FastshareException("přerušeno: " + ex.Message);
            }

            return null;
        }
    }

    private static SuggestedFile? Pick(List<FsFile> files, SelectTarget t, FileSelectorOptions opts, FileSelectorOptions? fallback)
    {
        var ranked = FileSelector.Rank(files, t, opts);
        if (ranked.Count == 0 && fallback != null)
        {
            ranked = FileSelector.Rank(files, t, fallback);
        }

        if (ranked.Count == 0)
        {
            return null;
        }

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
