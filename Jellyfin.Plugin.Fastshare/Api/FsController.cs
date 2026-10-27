using Jellyfin.Plugin.Fastshare.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare.Api;

/// <summary>Neošetřená výjimka klienta (⛔ STOP, nepřihlášeno…) → srozumitelná hláška místo HTTP 500.</summary>
public sealed class FastshareErrorFilter : Microsoft.AspNetCore.Mvc.Filters.ExceptionFilterAttribute
{
    public override void OnException(Microsoft.AspNetCore.Mvc.Filters.ExceptionContext context)
    {
        if (context.Exception is FastshareException ex)
        {
            context.Result = new ObjectResult(new { success = false, message = ex.Message }) { StatusCode = 409 };
            context.ExceptionHandled = true;
        }
    }
}

/// <summary>REST API pro stránku pluginu. Jen pro adminy, vše pod /Plugins/Fastshare/...</summary>
[ApiController]
[FastshareErrorFilter]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/Fastshare")]
[Produces("application/json")]
public class FsController : ControllerBase
{
    private readonly FsState _state;
    private readonly ILogger<FsController> _logger;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;

    public FsController(FsState state, ILogger<FsController> logger, MediaBrowser.Controller.Library.ILibraryManager libraryManager)
    {
        _state = state;
        _logger = logger;
        _libraryManager = libraryManager;
    }

    /// <summary>Po smazání souborů nechat Jellyfin knihovnu přenačíst (zmizí smazané položky).</summary>
    private void QueueScan()
    {
        try
        {
            _libraryManager.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Fastshare: sken knihovny se nepodařilo spustit: {Msg}", ex.Message);
        }
    }

    // ── Účet ──────────────────────────────────────────────────────
    [HttpPost("TestLogin")]
    public async Task<ActionResult> TestLogin(CancellationToken ct)
    {
        var client = _state.Client;
        client.InvalidateSession();
        var hash = await client.GetHashAsync(ct, force: true).ConfigureAwait(false);
        if (hash == null)
        {
            return Ok(new { success = false, message = "Přihlášení selhalo — " + (client.LastError ?? "neznámá chyba") });
        }

        _state.ClearOutage();
        var a = client.Account;
        return Ok(new
        {
            success = true,
            unlimited = a?.Unlimited,
            creditMb = a?.CreditMb,
            message = a?.Unlimited == true ? "Přihlášeno — neomezené stahování." : $"Přihlášeno — kredit {a?.CreditMb} MB.",
        });
    }

    // ── Hledání ───────────────────────────────────────────────────
    public sealed class SearchRequest
    {
        public string Term { get; set; } = string.Empty;

        public int Page { get; set; } = 1;
    }

    [HttpPost("Search")]
    public async Task<ActionResult> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Term))
        {
            return BadRequest(new { message = "Zadej, co hledat." });
        }

        try
        {
            var cfg = Plugin.Instance!.Configuration;
            var r = await _state.Client.SearchAsync(request.Term.Trim(), request.Page, cfg.ShowAdult, ct).ConfigureAwait(false);
            return Ok(new
            {
                total = r.Total,
                totalPages = r.TotalPages,
                page = request.Page,
                files = r.Files.Select(ToDto),
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or FastshareException or System.Text.Json.JsonException)
        {
            _logger.LogWarning("Fastshare: hledání selhalo — {Msg}", ex.Message);
            return StatusCode(502, new { message = "Hledání na Fastshare selhalo: " + ex.Message });
        }
    }

    private static object ToDto(FsFile f) => new
    {
        id = f.Id,
        fileName = f.FileName,
        downloadUrl = f.DownloadUrl,
        sizeBytes = f.SizeBytes,
        sizeText = Dedup.FormatSize(f.SizeBytes),
        durationSec = f.DurationSec,
        resolution = f.Resolution,
        quality = f.Quality,
        audioLangs = f.AudioLangs,
        subtitleLangs = f.SubtitleLangs,
        language = f.LanguageLabel,
        codec = f.Codec,
        hdr = f.Hdr,
        dv = f.Dv,
        title = f.Title,
        year = f.Year,
        season = f.Season,
        episode = f.Episode,
        isVideo = f.IsVideo,
        isArchive = f.IsArchive,
        archivePart = f.ArchivePart,
        archiveBase = f.ArchiveBase,
        packSeason = f.PackSeason,
        isSample = f.IsSample,
        thumbnail = f.Thumbnail,
        uploaded = f.UploadedDate,
    };

    // ── Odkazy z webu Fastshare (archivy a vše, co API hledání nevrací) ──
    public sealed class LinksRequest
    {
        /// <summary>Jeden nebo víc odkazů (každý na řádek, nebo oddělené mezerou).</summary>
        public string Text { get; set; } = string.Empty;
    }

    [HttpPost("Links")]
    public async Task<ActionResult> ResolveLinks([FromBody] LinksRequest r, CancellationToken ct)
    {
        var links = System.Text.RegularExpressions.Regex.Matches(r.Text ?? string.Empty, @"https?://\S+")
            .Select(m => m.Value.TrimEnd(',', ';', ')'))
            .Distinct()
            .Take(50)
            .ToList();
        if (links.Count == 0)
        {
            return BadRequest(new { message = "Vlož odkaz na soubor z webu Fastshare (https://fastshare.cloud/číslo/název)." });
        }

        var hash = await _state.Client.GetHashAsync(ct).ConfigureAwait(false);
        var files = new List<object>();
        var errors = new List<string>();
        foreach (var link in links)
        {
            try
            {
                var f = await _state.Client.ResolveLinkAsync(link, ct).ConfigureAwait(false);
                if (hash != null)
                {
                    // Velikost (stránka ji neuvádí) a zároveň kontrola, že odkaz s přihlášením opravdu vrací soubor
                    f.SizeBytes = await _state.Engine.ProbeSizeAsync(f.DownloadUrl, q => FastshareClient.PrepareDownload(q, hash), ct)
                        .ConfigureAwait(false) ?? 0;
                }

                files.Add(ToDto(f));
            }
            catch (Exception ex) when (ex is HttpRequestException or FastshareException or TaskCanceledException)
            {
                errors.Add(link + " — " + ex.Message);
            }
        }

        return Ok(new { files, errors, loggedIn = hash != null });
    }

    public sealed class ArchiveRequest
    {
        public List<ArchivePart> Parts { get; set; } = new();

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        /// <summary>"series" | "movie".</summary>
        public string MediaType { get; set; } = "series";

        /// <summary>Řada pro soubory, které ji v názvu nemají.</summary>
        public int? Season { get; set; }

        public string? Quality { get; set; }

        public string? Language { get; set; }

        public bool ForceNow { get; set; }
    }

    /// <summary>Zařadí archiv (jedno- i vícedílný): po stažení se rozbalí a videa roztřídí do knihovny.</summary>
    [HttpPost("QueueArchive")]
    public ActionResult QueueArchive([FromBody] ArchiveRequest r)
    {
        if (r.Parts.Count == 0 || string.IsNullOrWhiteSpace(r.Title))
        {
            return BadRequest(new { success = false, message = "Chybí části archivu nebo název." });
        }

        foreach (var p in r.Parts)
        {
            if (!Uri.TryCreate(p.DownloadUrl, UriKind.Absolute, out var u) || u.Scheme != "https"
                || !u.Host.EndsWith(".fastshare.cloud", StringComparison.OrdinalIgnoreCase) && !u.Host.EndsWith(".fastshare.cz", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { success = false, message = "Odkaz nevede na Fastshare: " + p.FileName });
            }
        }

        var parts = r.Parts
            .Select(p => new { Part = p, Info = Parsed(p.FileName) })
            .OrderBy(x => x.Info.ArchivePart ?? 1)
            .Select(x => x.Part)
            .ToList();
        var series = r.MediaType != "movie";
        var item = new QueueItem
        {
            Title = r.Title.Trim(),
            SeriesTitle = series ? r.Title.Trim() : null,
            Year = r.Year is > 1800 ? r.Year : null,
            MediaType = series ? MediaKind.Episode : MediaKind.Movie,
            IsArchive = true,
            Parts = parts,
            ArchiveSeason = r.Season is > 0 ? r.Season : null,
            FileId = parts[0].FileId,
            FileName = parts[0].FileName,
            DownloadUrl = parts[0].DownloadUrl,
            SizeBytes = parts.Sum(p => p.SizeBytes),
            BytesTotal = parts.Sum(p => p.SizeBytes),
            SizeText = Dedup.FormatSize(parts.Sum(p => p.SizeBytes)),
            Quality = string.IsNullOrWhiteSpace(r.Quality) ? null : r.Quality,
            Language = string.IsNullOrWhiteSpace(r.Language) ? null : r.Language,
            ForceNow = r.ForceNow,
        };

        var outcome = _state.Queue.Add(item);
        _state.Queue.Wake();
        return Ok(outcome == AddOutcome.Added
            ? new { success = true, message = $"Archiv zařazen ({parts.Count} {(parts.Count == 1 ? "část" : "části")}, {Dedup.FormatSize(item.SizeBytes)}). Po stažení se rozbalí." }
            : new { success = false, message = "Tenhle archiv už ve frontě je." });
    }

    private static FsFile Parsed(string name)
    {
        var f = new FsFile { FileName = name };
        FileNameParser.Fill(f);
        return f;
    }

    // ── Zařazení ──────────────────────────────────────────────────
    public sealed class QueueRequest
    {
        public string FileId { get; set; } = string.Empty;

        public string FileName { get; set; } = string.Empty;

        public string DownloadUrl { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public int? DurationSec { get; set; }

        public string? Quality { get; set; }

        public string? Language { get; set; }

        /// <summary>Název v knihovně (film nebo seriál).</summary>
        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        /// <summary>"movie" | "episode".</summary>
        public string MediaType { get; set; } = "movie";

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public bool ForceNow { get; set; }
    }

    [HttpPost("Queue")]
    public ActionResult AddToQueue([FromBody] QueueRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FileId) || string.IsNullOrWhiteSpace(r.DownloadUrl)
            || string.IsNullOrWhiteSpace(r.Title))
        {
            return BadRequest(new { success = false, message = "Chybí soubor nebo název." });
        }

        if (!r.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !new Uri(r.DownloadUrl).Host.Contains("fastshare", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Odkaz nevede na Fastshare." });
        }

        var episode = string.Equals(r.MediaType, "episode", StringComparison.OrdinalIgnoreCase);
        if (episode && (r.Season is null or < 0 || r.Episode is null or < 0))
        {
            return BadRequest(new { success = false, message = "U dílu seriálu vyplň řadu a díl." });
        }

        var item = new QueueItem
        {
            Title = r.Title.Trim(),
            SeriesTitle = episode ? r.Title.Trim() : null,
            Year = r.Year is > 1800 ? r.Year : null,
            MediaType = episode ? MediaKind.Episode : MediaKind.Movie,
            Season = episode ? r.Season : null,
            Episode = episode ? r.Episode : null,
            FileId = r.FileId,
            FileName = r.FileName,
            DownloadUrl = r.DownloadUrl,
            SizeBytes = r.SizeBytes,
            BytesTotal = r.SizeBytes,
            SizeText = Dedup.FormatSize(r.SizeBytes),
            DurationSec = r.DurationSec,
            Quality = string.IsNullOrWhiteSpace(r.Quality) ? null : r.Quality,
            Language = string.IsNullOrWhiteSpace(r.Language) ? null : r.Language,
            ForceNow = r.ForceNow,
        };

        var outcome = _state.Queue.Add(item);
        _state.Queue.Wake();
        return Ok(outcome switch
        {
            AddOutcome.Added => new { success = true, message = "Zařazeno do fronty." },
            AddOutcome.DuplicateStream => new { success = false, message = "Tenhle soubor už ve frontě je." },
            _ => (object)new { success = false, message = "Tentýž film/díl ve stejné kvalitě už ve frontě čeká." },
        });
    }

    public sealed class QueueBestRequest
    {
        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string MediaType { get; set; } = "movie";

        public int? Season { get; set; }

        public int? Episode { get; set; }

        /// <summary>true = jen ukázat, co by se vybralo (nic nezařadit).</summary>
        public bool DryRun { get; set; }
    }

    /// <summary>⚡ Stáhnout nejlepší: vyhledá a zařadí nejlepší soubor podle preferencí.</summary>
    [HttpPost("QueueBest")]
    public async Task<ActionResult> QueueBest([FromBody] QueueBestRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Title))
        {
            return BadRequest(new { success = false, message = "Chybí název." });
        }

        var t = new SelectTarget
        {
            Kind = string.Equals(r.MediaType, "episode", StringComparison.OrdinalIgnoreCase) ? MediaKind.Episode : MediaKind.Movie,
            Title = r.Title.Trim(),
            Year = r.Year is > 1800 ? r.Year : null,
            Season = r.Season,
            Episode = r.Episode,
        };

        List<FsFile> files;
        try
        {
            files = await _state.Client.SearchAllAsync(t.SearchTerm(), Plugin.Instance!.Configuration.ShowAdult, 2, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or FastshareException or System.Text.Json.JsonException)
        {
            return StatusCode(502, new { success = false, message = "Hledání selhalo: " + ex.Message });
        }

        var ranked = FileSelector.Rank(files, t, FsState.SelectorOptions());
        var top = ranked.Take(5).Select(x => new { reason = x.Reason, file = ToDto(x.File) }).ToList();
        if (ranked.Count == 0)
        {
            return Ok(new
            {
                success = false,
                message = $"Žádný z {files.Count} nalezených souborů nesplnil pravidla (název/rok/díl, jazyk, kvalita, velikost).",
                top,
            });
        }

        if (r.DryRun)
        {
            return Ok(new { success = true, message = "Vybral bych: " + ranked[0].Reason, top });
        }

        var outcome = _state.Queue.Add(QueueItemFactory.Create(ranked[0].File, t));
        _state.Queue.Wake();
        return Ok(new
        {
            success = outcome == AddOutcome.Added,
            message = outcome == AddOutcome.Added ? "Zařazeno: " + ranked[0].Reason : "Už ve frontě je.",
            top,
        });
    }

    // ── Fronta ────────────────────────────────────────────────────
    [HttpGet("Queue")]
    public ActionResult GetQueue()
    {
        var items = _state.Queue.GetAll();
        var ordered = QueueOrder.ByPriority(items.Where(i => i.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading))
            .OrderBy(i => i.Status == QueueItemStatus.Downloading ? 0 : 1)
            .ToList();
        // Seriály ve frontě: kolik dílů se vylepšuje (náhrada horší verze z Navržených), kolik chybějících
        var boosted = _state.Queue.BoostedSeries;
        var series = ordered.Where(i => i.MediaType == MediaKind.Episode)
            .GroupBy(i => QueueOrder.SeriesKey(i)!)
            .Select(g => new
            {
                name = g.First().SeriesTitle ?? g.First().Title,
                total = g.Count(),
                upgrading = g.Count(i => i.ReplacePaths is { Count: > 0 } || (i.SuggestionId?.StartsWith("ep:", StringComparison.Ordinal) ?? false)),
                missing = g.Count(i => i.SuggestionId?.StartsWith("miss:", StringComparison.Ordinal) ?? false),
                downloading = g.Count(i => i.Status == QueueItemStatus.Downloading),
                quality = g.GroupBy(i => i.Quality ?? "?").OrderByDescending(q => q.Count()).First().Key,
                forced = g.Any(i => i.ForceNow),
                boosted = boosted.Contains(g.Key),
            })
            .ToList();
        return Ok(new
        {
            status = StatusDto(),
            series,
            queue = ordered.Select(ItemDto),
            problems = items.Where(i => i.Status == QueueItemStatus.Error).OrderByDescending(i => i.AddedUtc).Select(ItemDto),
            done = items.Where(i => i.Status is QueueItemStatus.Done or QueueItemStatus.Skipped)
                .OrderByDescending(i => i.CompletedUtc).Take(200).Select(ItemDto),
        });
    }

    private object ItemDto(QueueItem i)
    {
        var active = _state.Active.TryGetValue(i.Id, out var a) ? a : null;
        return new
        {
            id = i.Id,
            title = DownloadWorkerService.DisplayTitle(i),
            mediaType = i.MediaType == MediaKind.Episode ? "episode" : "movie",
            seriesTitle = i.SeriesTitle,
            season = i.Season,
            episode = i.Episode,
            year = i.Year,
            fileName = i.FileName,
            quality = i.Quality,
            language = i.Language,
            sizeBytes = i.SizeBytes,
            status = i.Status.ToString(),
            error = i.ErrorMessage,
            forceNow = i.ForceNow,
            boost = i.Boost > 0,
            fromWatcher = i.FromWatcher,
            failCount = i.FailCount,
            stoppedPercent = i.StoppedPercent,
            retryAfterUtc = i.RetryAfterUtc,
            bytesDone = active?.BytesDone ?? i.BytesDone,
            bytesTotal = active?.BytesTotal > 0 ? active.BytesTotal : i.BytesTotal,
            speedBps = active?.SpeedBps ?? 0,
            targetPath = i.TargetPath,
            isArchive = i.IsArchive,
            parts = i.Parts?.Count ?? 0,
            archiveSeason = i.ArchiveSeason,
            archiveResult = i.ArchiveResult,
            resultNote = i.ResultNote,
            addedUtc = i.AddedUtc,
            completedUtc = i.CompletedUtc,
        };
    }

    [HttpGet("Status")]
    public ActionResult GetStatus() => Ok(StatusDto());

    private object StatusDto()
    {
        var s = _state.Status;
        var active = _state.Active.Values.ToList();
        return new
        {
            paused = _state.Queue.WorkerPaused,
            forced = _state.Queue.IgnoreWindow,
            stopped = _state.Queue.FastshareStopped,
            active = active.Select(a => new
            {
                itemId = a.ItemId,
                title = a.Title,
                bytesDone = a.BytesDone,
                bytesTotal = a.BytesTotal,
                speedBps = a.SpeedBps,
            }),
            totalSpeedBps = active.Sum(a => a.SpeedBps),
            message = s.LastMessage,
            nextActionUtc = s.NextActionUtc,
            dailyBytes = _state.Queue.GetDailyBytes(),
            freeSpaceBytes = s.FreeSpaceBytes,
            avgSpeedBps = _state.Queue.AvgSpeedBps,
            account = _state.Client.Account,
            outage = _state.OutageReason,
            archiveSupport = ArchiveExtractor.Support,
        };
    }

    [HttpDelete("Queue/{id}")]
    public ActionResult RemoveFromQueue([FromRoute] Guid id)
    {
        _state.CancelDownload(id);
        return Ok(new { success = _state.Queue.Remove(id, force: true) });
    }

    [HttpPost("Queue/ClearCompleted")]
    public ActionResult ClearCompleted() => Ok(new { removed = _state.Queue.ClearCompleted() });

    [HttpPost("Queue/RetryAll")]
    public ActionResult RetryAll()
    {
        _state.ClearOutage();
        return Ok(new { count = _state.Queue.RetryAll() });
    }

    [HttpPost("Queue/{id}/Retry")]
    public ActionResult Retry([FromRoute] Guid id)
    {
        _state.Queue.Update(id, i =>
        {
            i.RetryAfterUtc = null;
            i.FailCount = 0;
        });
        var ok = _state.Queue.Retry(id);
        _state.Queue.Wake();
        return Ok(new { success = ok });
    }

    [HttpPost("Queue/{id}/Now")]
    public ActionResult Now([FromRoute] Guid id)
    {
        _state.Queue.Update(id, i => i.RetryAfterUtc = null);
        return Ok(new { success = _state.Queue.ForceNow(id) });
    }

    [HttpPost("Queue/{id}/Unforce")]
    public ActionResult Unforce([FromRoute] Guid id) => Ok(new { success = _state.Queue.Unforce(id) });

    [HttpPost("Queue/{id}/Redownload")]
    public ActionResult Redownload([FromRoute] Guid id) => Ok(new { success = _state.Queue.Redownload(id) });

    [HttpPost("Queue/{id}/Move/{direction}")]
    public ActionResult Move([FromRoute] Guid id, [FromRoute] string direction) =>
        Ok(new { success = _state.Queue.Move(id, direction == "up") });

    public sealed class ReorderRequest
    {
        public List<Guid> Ids { get; set; } = new();
    }

    [HttpPost("Queue/Reorder")]
    public ActionResult Reorder([FromBody] ReorderRequest r) => Ok(new { success = _state.Queue.Reorder(r.Ids) });

    public sealed class BulkRequest
    {
        public List<Guid> Ids { get; set; } = new();

        /// <summary>now (⚡) | unforce | top | bottom | remove</summary>
        public string Op { get; set; } = string.Empty;
    }

    /// <summary>Hromadná akce nad vybranými položkami fronty (výběr s Ctrl/Shift v GUI).</summary>
    [HttpPost("Queue/Bulk")]
    public ActionResult Bulk([FromBody] BulkRequest r)
    {
        if (r.Ids.Count == 0)
        {
            return BadRequest(new { success = false, message = "Nic není vybráno." });
        }

        var n = 0;
        switch (r.Op)
        {
            case "now":
                foreach (var id in r.Ids)
                {
                    _state.Queue.Update(id, i => i.RetryAfterUtc = null);
                    n += _state.Queue.ForceNow(id) ? 1 : 0;
                }

                return Ok(new { success = true, message = $"⚡ Přednost u {n} položek — stahují se hned, i mimo okno." });
            case "unforce":
                n = r.Ids.Count(id => _state.Queue.Unforce(id));
                return Ok(new { success = true, message = $"Přednost zrušena u {n} položek." });
            case "top":
                _state.Queue.Reorder(r.Ids);
                return Ok(new { success = true, message = $"{r.Ids.Count} položek přesunuto nahoru (v rámci filmů / seriálů)." });
            case "bottom":
                var queued = QueueOrder.ByPriority(_state.Queue.GetAll().Where(i => i.Status == QueueItemStatus.Queued)).Select(i => i.Id).ToList();
                _state.Queue.Reorder(queued.Where(id => !r.Ids.Contains(id)).Concat(r.Ids).ToList());
                return Ok(new { success = true, message = $"{r.Ids.Count} položek přesunuto na konec." });
            case "remove":
                foreach (var id in r.Ids)
                {
                    _state.CancelDownload(id);
                    n += _state.Queue.Remove(id, force: true) ? 1 : 0;
                }

                return Ok(new { success = true, message = $"Odebráno {n} položek z fronty." });
            default:
                return BadRequest(new { success = false, message = "Neznámá akce." });
        }
    }

    public sealed class BoostRequest
    {
        public string SeriesTitle { get; set; } = string.Empty;

        public bool On { get; set; } = true;
    }

    /// <summary>
    /// ⏫ Upřednostnit seriál (ten, na který se právě kouká): jeho díly jdou před filmy i ostatní seriály, v časovém
    /// okně (na rozdíl od ⚡). Platí i pro díly, které hlídač přidá později, dokud se nezruší.
    /// </summary>
    [HttpPost("Queue/BoostSeries")]
    public ActionResult BoostSeries([FromBody] BoostRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.SeriesTitle))
        {
            return BadRequest(new { success = false, message = "Chybí seriál." });
        }

        var n = _state.Queue.BoostSeries(QueueOrder.SeriesKey(r.SeriesTitle), r.On);
        _state.Queue.Wake();
        return Ok(new
        {
            success = true,
            message = r.On
                ? $"⏫ {r.SeriesTitle} má přednost před filmy i ostatními seriály ({n} {(n == 1 ? "díl" : n is > 1 and < 5 ? "díly" : "dílů")} ve frontě, nové díly ji dostanou taky). Stahuje se v časovém okně — hned jen s ⚡."
                : $"{r.SeriesTitle} už není upřednostněný.",
        });
    }

    [HttpPost("Queue/GroupSeries")]
    public ActionResult GroupSeries() => Ok(new { success = _state.Queue.GroupSeries() });

    /// <summary>⏹ Zastavit jedno stahování (vrátí se do fronty, stažená část zůstane).</summary>
    [HttpPost("Queue/{id}/Stop")]
    public ActionResult Stop([FromRoute] Guid id) => Ok(new { success = _state.CancelDownload(id) > 0 });

    public sealed class StopRequest
    {
        public bool On { get; set; } = true;
    }

    /// <summary>
    /// ⛔ STOP: okamžitě zastaví všechna stahování a jakékoli spojení na Fastshare (kontrola, hlídač, ruční hledání,
    /// přihlášení), dokud uživatel nepovolí. Pro jistotu při podezření na ban. Stažené části zůstanou a navážou se.
    /// </summary>
    [HttpPost("Stop")]
    public ActionResult FastshareStop([FromBody] StopRequest r)
    {
        _state.Queue.FastshareStopped = r.On;
        if (r.On)
        {
            _state.CancelDownload();
            _logger.LogWarning("Fastshare: ⛔ STOP — všechna komunikace s Fastshare zastavena uživatelem");
            return Ok(new { success = true, stopped = true, message = "⛔ Fastshare zastaven: stahování přerušeno (stažené části zůstanou), hledání stojí, žádné spojení na Fastshare. Povolíš tlačítkem ▶ Povolit Fastshare." });
        }

        _state.Queue.Wake();
        _logger.LogInformation("Fastshare: ▶ STOP zrušen — komunikace s Fastshare povolena");
        return Ok(new { success = true, stopped = false, message = "▶ Fastshare povolen — stahování a hledání pokračují podle oken a stropu." });
    }

    // pozor: {action} je v ASP.NET rezervované (název akce) — s ním se Worker/pause nikdy netrefil
    [HttpPost("Worker/{op}")]
    public ActionResult Worker([FromRoute] string op)
    {
        switch (op)
        {
            case "pause":
                _state.Queue.WorkerPaused = true;
                _state.CancelDownload();
                break;
            case "resume":
                _state.Queue.WorkerPaused = false;
                _state.ClearOutage();
                _state.Queue.Wake();
                break;
            case "force":
                // ⚡ celá fronta hned — mimo časové okno i denní strop (a zruší pozastavení)
                _state.Queue.IgnoreWindow = true;
                _state.Queue.WorkerPaused = false;
                _state.Queue.Wake();
                return Ok(new { success = true, paused = false, forced = true, message = "⚡ Stahuji frontu hned, mimo časové okno. Až se fronta vyprázdní, vrátí se běžné okno samo." });
            case "unforce":
                _state.Queue.IgnoreWindow = false;
                return Ok(new { success = true, paused = _state.Queue.WorkerPaused, forced = false, message = "Zpět na časové okno — rozjetá stahování doběhnou, nová počkají na okno." });
            default:
                return BadRequest();
        }

        return Ok(new { success = true, paused = _state.Queue.WorkerPaused, message = _state.Queue.WorkerPaused ? "Stahování pozastaveno." : "Stahování pokračuje (v časovém okně)." });
    }

    // ── Navržené (kontrola knihovny) ──────────────────────────────
    private LibraryAdvisor Advisor => LibraryAdvisor.Instance ?? throw new InvalidOperationException("Kontrola knihovny neběží");

    [HttpGet("Suggestions")]
    public ActionResult GetSuggestions()
    {
        var st = Advisor.Status;
        var last = _state.Suggestions.LastRun;
        return Ok(new
        {
            status = new
            {
                running = st.Running,
                done = st.Done,
                total = st.Total,
                phase = st.Phase,
                lastRunUtc = st.LastRunUtc ?? last.LastRunUtc,
                lastResult = st.LastResult ?? last.LastResult,
            },
            series = _state.Suggestions.Series,
            // kandidáti návrhů, kteří už jsou ve frontě / stažení: FileId → stav
            inQueue = _state.Queue.GetAll()
                .Where(q => !string.IsNullOrEmpty(q.FileId) && q.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading or QueueItemStatus.Done)
                .GroupBy(q => q.FileId)
                .ToDictionary(g => g.Key, g => new
                {
                    status = g.First().Status.ToString(),
                    percent = g.First().BytesTotal > 0 ? (int)(g.First().BytesDone * 100 / g.First().BytesTotal) : 0,
                }),
            fileOps = new { items = _state.FileOps("sug:"), series = _state.FileOps("ser:") },
            items = _state.Suggestions.GetAll()
                .OrderBy(x => x.Status == "new" ? 0 : 1)
                .ThenBy(x => x.Kind)
                .ThenBy(x => x.Title, StringComparer.CurrentCulture),
        });
    }

    public sealed class ScanRequest
    {
        /// <summary>Hledat lepší verze i u filmů kontrolovaných nedávno.</summary>
        public bool Force { get; set; }
    }

    [HttpPost("Suggestions/Scan")]
    public ActionResult Scan([FromBody] ScanRequest? r)
    {
        var force = r?.Force == true;
        var cfg = Plugin.Instance?.Configuration;
        var win = _state.SearchGate.WindowText;
        var pace = _state.SearchGate.Paused
            ? "automatické hledání je vypnuté (strop 0)"
            : $"hledání na Fastshare jde po stropu {cfg?.AutoSearchesPerHour}/h{(win != null ? " v okně " + win : string.Empty)}";
        if (Advisor.Start(force))
        {
            return Ok(new { success = true, started = true, message = $"Kontrola spuštěna — nejdřív projde knihovnu (pár minut), pak {pace}." });
        }

        // už běží (často jen čeká na okno / strop hledání) → znovu načíst knihovnu, prohledané se nehledá znovu
        Advisor.Restart(force);
        return Ok(new { success = true, started = true, message = $"Kontrola už běžela — načítám knihovnu znovu (nové a smazané soubory), hledání pokračuje, kde skončilo; {pace}." });
    }

    [HttpPost("Suggestions/Stop")]
    public ActionResult StopScan()
    {
        Advisor.Stop();
        return Ok(new { success = true });
    }

    public sealed class SuggestionDownloadRequest
    {
        /// <summary>Po stažení smazat všechny dosavadní verze filmu.</summary>
        public bool Replace { get; set; }
    }

    /// <summary>⬇️ Stáhnout navrženou lepší verzi (volitelně s náhradou starých).</summary>
    [HttpPost("Suggestions/{id}/Download")]
    public ActionResult DownloadSuggestion([FromRoute] string id, [FromBody] SuggestionDownloadRequest r)
    {
        var s = _state.Suggestions.Get(id);
        if (s?.Candidate == null)
        {
            return NotFound(new { success = false, message = "Návrh nemá soubor ke stažení." });
        }

        var c = s.Candidate;
        var episode = s.Kind is "episode" or "missing";
        var item = new QueueItem
        {
            Title = s.Title,
            Year = s.Year,
            MediaType = episode ? MediaKind.Episode : MediaKind.Movie,
            SeriesTitle = episode ? s.SeriesName : null,
            Season = episode ? s.Season : null,
            Episode = episode ? s.Episode : null,
            TargetDir = !episode ? null : s.TargetDir ?? (s.Versions.Count > 0 ? Path.GetDirectoryName(s.Versions[0].Path) : null),
            FileId = c.FileId,
            FileName = c.FileName,
            DownloadUrl = c.DownloadUrl,
            SizeBytes = c.SizeBytes,
            BytesTotal = c.SizeBytes,
            SizeText = Dedup.FormatSize(c.SizeBytes),
            DurationSec = c.DurationSec,
            Quality = c.Quality,
            Language = c.Language,
            SuggestionId = s.Id,
            ReplacePaths = r.Replace ? s.Versions.Select(v => v.Path).ToList() : null,
            Overwrite = true, // starší verze ve stejné složce nesmí stažení přeskočit jako „duplicitu"
        };
        var outcome = _state.Queue.Add(item);
        _state.Queue.Wake();
        if (outcome != AddOutcome.Added)
        {
            // Už ve frontě (dřívější kliknutí, hlídač) → připojit k ní tenhle návrh, ať se po stažení
            // staré verze smažou (nahradit) a návrh se vyřídí
            var dup = _state.Queue.FindDuplicate(item);
            if (dup is { Status: QueueItemStatus.Queued or QueueItemStatus.Downloading })
            {
                _state.Queue.Update(dup.Id, q =>
                {
                    q.SuggestionId ??= s.Id;
                    if (r.Replace)
                    {
                        q.ReplacePaths = (q.ReplacePaths ?? new List<string>()).Union(s.Versions.Select(v => v.Path)).ToList();
                    }
                });
                _state.Suggestions.Update(id, x =>
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                    x.Note = "Už bylo ve frontě — připojeno" + (r.Replace ? ", staré verze se smažou po stažení" : string.Empty);
                });
                var where = dup.Status == QueueItemStatus.Downloading ? "právě se stahuje" : "čeká ve frontě";
                return Ok(new { success = true, message = $"„{dup.FileName}“ už {where} — připojeno k návrhu" + (r.Replace ? ", staré verze se po stažení smažou." : ".") });
            }

            if (dup is { Status: QueueItemStatus.Done })
            {
                _state.Suggestions.Update(id, x =>
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                    x.Note = "Už staženo " + dup.CompletedUtc?.ToLocalTime().ToString("d. M. H:mm", System.Globalization.CultureInfo.InvariantCulture) + " → " + (dup.TargetPath ?? dup.FileName);
                });
                QueueScan();
                return Ok(new { success = true, message = $"Tenhle soubor už je stažený ({dup.TargetPath ?? dup.FileName}). Návrh vyřízen; až Jellyfin knihovnu načte, ukáže se film ve víc verzích v Duplicitách — horší tam smažeš." });
            }

            return Ok(new { success = false, message = "Tenhle soubor už ve frontě je." });
        }

        _state.Suggestions.Update(id, x =>
        {
            x.Status = "done";
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = r.Replace ? "Zařazeno ke stažení — staré verze se smažou, až se nová stáhne" : "Zařazeno ke stažení";
        });
        return Ok(new { success = true, message = r.Replace ? "Zařazeno. Staré verze se smažou až po úspěšném stažení." : "Zařazeno do fronty." });
    }

    public sealed class DeleteVersionsRequest
    {
        /// <summary>Které verze smazat (cesty z návrhu). Aspoň jedna musí zůstat.</summary>
        public List<string> Paths { get; set; } = new();
    }

    /// <summary>
    /// 🗑 Smazat vybrané verze filmu nebo dílu (u Harryho Pottera třeba jen SD, 4K a Full HD nechat).
    /// Nikdy nesmaže všechny — aspoň jedna verze musí zůstat.
    /// </summary>
    [HttpPost("Suggestions/{id}/DeleteVersions")]
    public ActionResult DeleteVersions([FromRoute] string id, [FromBody] DeleteVersionsRequest r)
    {
        var s = _state.Suggestions.Get(id);
        if (s == null)
        {
            return NotFound(new { success = false, message = "Návrh neexistuje." });
        }

        var known = s.Versions.Select(v => v.Path).ToHashSet(StringComparer.Ordinal);
        var paths = r.Paths.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            return BadRequest(new { success = false, message = "Vyber, kterou verzi smazat." });
        }

        if (paths.Count >= s.Versions.Count)
        {
            return BadRequest(new { success = false, message = "Aspoň jedna verze musí zůstat (celý film smaže „Smazat film“)." });
        }

        var (deleted, failed) = Delete(paths);
        var gone = paths.Where(p => failed.All(f => f.Path != p)).ToHashSet(StringComparer.Ordinal);
        _state.Suggestions.Update(id, x =>
        {
            x.Versions = x.Versions.Where(v => !gone.Contains(v.Path)).ToList();
            x.DeletePaths = x.Versions.Skip(1).Select(v => v.Path).ToList();
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = $"Smazáno verzí: {deleted}" + (failed.Count > 0 ? "; nesmazáno: " + string.Join(", ", failed.Select(f => f.Why)) : string.Empty);
            if (x.Versions.Count > 1)
            {
                // Nechal sis víc verzí schválně (Harry Potter: 4K + Full HD) — rozhodnuto, znovu nenabízet
                x.Status = "done";
                x.Note += $"; ponecháno {x.Versions.Count} verzí";
                return;
            }

            // Zbyla jedna verze. Pod cílem s nalezenou lepší → žije dál jako „lepší verze", jinak vyřízeno.
            var target = x.TargetQuality ?? Plugin.Instance?.Configuration.SuggestTargetQuality ?? "1080p";
            var below = LibraryFiles.Level(x.Versions[0].Quality) < LibraryFiles.Level(target);
            if (x.Kind == "duplicate")
            {
                if (below && x.Candidate != null)
                {
                    x.Kind = "upgrade";
                    x.Id = "up:" + x.ItemId;
                }
                else
                {
                    x.Status = "done";
                }
            }
            else if (x.Kind == "episode" && !below)
            {
                x.Status = "done";
            }
        });
        QueueScan();
        return Ok(new { success = failed.Count == 0, message = $"Smazáno {deleted} {(deleted == 1 ? "verze" : "verzí")}." + (failed.Count > 0 ? " Něco se smazat nepovedlo." : string.Empty) });
    }

    /// <summary>
    /// Přesun souborů na pozadí. Mezi Filmy a Seriály se kopíruje (jiné připojení) a 4 GB trvají minuty —
    /// požadavek by visel a další kliknutí by pouštěla další kopie („S01E02 (23).mp4"). Klíče (návrh,
    /// zdrojové soubory, seriál) drží zámek, dokud přesun běží; GUI stav čte z GET Suggestions (fileOps).
    /// </summary>
    private ActionResult RunFileOp(IEnumerable<string> keys, string text, Func<string> work, Action<string>? onSuccess = null)
    {
        var k = keys.Distinct(StringComparer.Ordinal).ToList();
        var sources = k.Where(x => x.StartsWith("path:", StringComparison.Ordinal)).Select(x => x[5..]).ToList();
        if (sources.Count > 0 && sources.All(x => !System.IO.File.Exists(x)))
        {
            return Ok(new { success = false, message = "Soubor už na původním místě není — nejspíš je přesunutý. Klikni na „Obnovit seznam“." });
        }

        if (!_state.TryStartFileOp(k, text))
        {
            return Ok(new { success = false, message = "Tenhle přesun už běží — počkej, až doběhne." });
        }

        _ = Task.Run(() =>
        {
            string? error = null;
            try
            {
                var msg = work();
                onSuccess?.Invoke(msg);
            }
            catch (Exception ex)
            {
                error = "⚠️ Přesun selhal: " + ex.Message;
                _logger.LogWarning("Fastshare: přesun selhal: {Msg}", ex.Message);
            }
            finally
            {
                _state.EndFileOp(k, error);
                QueueScan();
            }
        });
        return Ok(new { success = true, background = true, message = text });
    }

    /// <summary>
    /// 🔀 Jiné verze k návrhu: ruční hledání na Fastshare (1 stránka na název, jako hledání v GUI — mimo strop),
    /// seřazené podle preferencí, s velikostí a délkou proti dnešnímu souboru. Výběr pak „Vybrat" (SetCandidate).
    /// </summary>
    [HttpPost("Suggestions/{id}/Alternatives")]
    public async Task<ActionResult> Alternatives([FromRoute] string id, CancellationToken ct)
    {
        var s = _state.Suggestions.Get(id);
        if (s == null)
        {
            return NotFound(new { success = false, message = "Návrh neexistuje." });
        }

        var episode = s.Kind is "episode" or "missing";
        var cfg = Plugin.Instance!.Configuration;
        var target = s.TargetQuality ?? cfg.SuggestTargetQuality;
        var opts = FsState.SelectorOptions(target);
        var titles = new[] { episode ? s.SeriesName ?? s.Title : s.Title, s.OriginalTitle }
            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var all = new List<RankedFile>();
        foreach (var title in titles)
        {
            var t = new SelectTarget { Kind = episode ? MediaKind.Episode : MediaKind.Movie, Title = title!, Year = episode ? null : s.Year, Season = s.Season, Episode = s.Episode };
            var files = await _state.Client.SearchAllAsync(t.SearchTerm(), cfg.ShowAdult, 1, ct).ConfigureAwait(false);
            all.AddRange(FileSelector.Rank(files, t, opts));
        }

        var current = s.Versions.Count > 0 ? s.Versions.Max(v => v.SizeBytes) : 0;
        var currentDur = s.Versions.Select(v => v.DurationSec).FirstOrDefault(d => d is > 0);
        var queued = _state.Queue.GetAll().Where(q => q.Status is QueueItemStatus.Queued or QueueItemStatus.Downloading or QueueItemStatus.Done)
            .Select(q => q.FileId).ToHashSet(StringComparer.Ordinal);
        var list = all.GroupBy(x => x.File.Id).Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(x => LibraryFiles.Level(x.File.Quality)).ThenByDescending(x => x.Score)
            .Take(25)
            .Select(x => new
            {
                fileId = x.File.Id,
                fileName = x.File.FileName,
                downloadUrl = x.File.DownloadUrl,
                sizeBytes = x.File.SizeBytes,
                durationSec = x.File.DurationSec,
                quality = x.File.Quality,
                language = string.IsNullOrEmpty(x.File.LanguageLabel) ? null : x.File.LanguageLabel,
                reason = x.Reason,
                smaller = current > 0 && x.File.SizeBytes > 0 && x.File.SizeBytes < current,
                lengthDiffMin = currentDur is > 0 && x.File.DurationSec is > 0 ? (x.File.DurationSec.Value - currentDur.Value) / 60 : (int?)null,
                current = s.Candidate?.FileId == x.File.Id,
                inQueue = queued.Contains(x.File.Id),
            })
            .ToList();
        return Ok(new { success = true, currentBytes = current, currentDurationSec = currentDur, items = list, message = list.Count == 0 ? "Žádná jiná verze nenalezena." : $"Nalezeno {list.Count} verzí." });
    }

    /// <summary>Vybrat jinou verzi jako kandidáta návrhu (z Jiné verze).</summary>
    [HttpPost("Suggestions/{id}/SetCandidate")]
    public ActionResult SetCandidate([FromRoute] string id, [FromBody] SuggestedFile c)
    {
        if (string.IsNullOrWhiteSpace(c.FileId) || !Uri.TryCreate(c.DownloadUrl, UriKind.Absolute, out var u)
            || u.Scheme != "https" || !u.Host.Contains("fastshare", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Odkaz nevede na Fastshare." });
        }

        if (_state.Suggestions.Get(id) == null)
        {
            return NotFound(new { success = false, message = "Návrh neexistuje." });
        }

        _state.Suggestions.Update(id, x =>
        {
            x.Candidate = c;
            x.Status = "new";
        });
        return Ok(new { success = true, message = $"Vybráno: {c.FileName} — teď můžeš dát ⬇️ Stáhnout." });
    }

    public sealed class EpisodeTarget
    {
        /// <summary>Soubor (verze) v návrhu.</summary>
        public string? Path { get; set; }

        /// <summary>Ručně zadaná řada a díl (jinak z návrhu / názvu souboru).</summary>
        public int? Season { get; set; }

        public int? Episode { get; set; }
    }

    private static string EpName(string series, int season, int episode) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{series} - S{season:D2}E{episode:D2}");

    /// <summary>🚚 Díl seriálu ve Filmech → přesunout do složky řady seriálu (Jellyfin ho pak zařadí sám).</summary>
    [HttpPost("Suggestions/{id}/Move")]
    public ActionResult MoveMisplaced([FromRoute] string id, [FromBody] EpisodeTarget? r)
    {
        var s = _state.Suggestions.Get(id);
        if (s is not { Kind: "misplaced" } || s.Versions.Count == 0 || string.IsNullOrEmpty(s.TargetDir))
        {
            return NotFound(new { success = false, message = "Návrh nemá kam přesunout." });
        }

        var season = r?.Season is > 0 ? r.Season.Value : s.Season ?? 1;
        var episode = r?.Episode is > 0 ? r.Episode.Value : s.Episode ?? 1;
        var dir = s.TargetDir;
        if (season != (s.Season ?? 1) && s.SeriesId != null)
        {
            // jiná řada, než plugin odhadl → složka té řady
            dir = Advisor.SeasonDirFor(s.SeriesId, season) ?? dir;
        }
        else if (season != (s.Season ?? 1))
        {
            dir = Path.Combine(Path.GetDirectoryName(dir)!, $"Season {season:D2}");
        }

        var src = s.Versions[0].Path;
        var name = EpName(s.SeriesName ?? s.Title, season, episode);
        return RunFileOp(
            new[] { "sug:" + id, "path:" + src },
            $"⏳ Přesouvám do {dir} jako „{name}“ — velký soubor se mezi Filmy a Seriály kopíruje, může to trvat i pár minut.",
            () => LibraryFiles.MoveVideo(src, dir, name, Advisor.LibraryRoots()),
            moved =>
            {
                _logger.LogInformation("Fastshare: přesunuto {From} → {To}", src, moved);
                _state.Suggestions.Update(id, x =>
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                    x.Note = "Přesunuto do " + moved;
                });
            });
    }

    public sealed class PathRequest
    {
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>
    /// 🔧 Díl má špatné číslo („05x01" jako S01E02, „Černobyl E01" bez čísla) → přesunout do správné řady
    /// pod jednoznačným názvem „Seriál - S05E01", ať ho Jellyfin zařadí správně. Řada a díl z názvu
    /// souboru, nebo ručně z GUI.
    /// </summary>
    [HttpPost("Suggestions/{id}/FixEpisode")]
    public ActionResult FixEpisode([FromRoute] string id, [FromBody] EpisodeTarget r)
    {
        var s = _state.Suggestions.Get(id);
        var v = s?.Versions.FirstOrDefault(x => x.Path == r.Path);
        var episode = r.Episode is > 0 ? r.Episode : v?.FileEpisode;
        if (s?.SeriesId == null || v == null || episode == null)
        {
            return NotFound(new { success = false, message = "Soubor v návrhu není, nebo chybí číslo dílu — zadej ho ručně." });
        }

        var season = r.Season is >= 0 ? r.Season.Value : v.FileSeason ?? s.Season ?? 1;
        var dir = Advisor.SeasonDirFor(s.SeriesId, season) ?? Path.Combine(LibraryAdvisor.SeriesRoot(v.Path), $"Season {season:D2}");
        var name = EpName(s.SeriesName ?? s.Title, season, episode.Value);
        var path = v.Path;
        return RunFileOp(
            new[] { "sug:" + id, "path:" + path },
            $"⏳ Přesouvám jako „{name}“…",
            () => LibraryFiles.MoveVideo(path, dir, name, Advisor.LibraryRoots()),
            moved => _state.Suggestions.Update(id, x =>
            {
                x.Versions = x.Versions.Where(y => y.Path != path).ToList();
                x.DeletePaths = x.DeletePaths.Where(p => p != path).ToList();
                x.Note = "Přesunuto jako " + Path.GetFileName(moved) + " — Jellyfin ho po skenu zařadí jako " + name[(name.LastIndexOf(" - ", StringComparison.Ordinal) + 3)..];
                if (x.Versions.Count == 0 || (x.Versions.Count == 1 && x.Versions.All(y => y.Suspect == null)))
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                }
            }));
    }

    /// <summary>
    /// 🎬 Film ve Seriálech („Hospoda/Hunger Games…avi") → do knihovny filmů jako „Název (rok)/Název (rok).avi".
    /// Funguje pro návrh „tomovies" i pro jakýkoli díl (ruční „Je to film").
    /// </summary>
    [HttpPost("Suggestions/{id}/ToMovies")]
    public ActionResult ToMovies([FromRoute] string id, [FromBody] OwnFolderRequest r)
    {
        var s = _state.Suggestions.Get(id);
        var v = s?.Versions.FirstOrDefault(x => x.Path == r.Path);
        var title = MediaOrganizer.Sanitize((r.Name ?? string.Empty).Trim());
        var movies = Plugin.Instance?.Configuration.MoviesPath;
        if (s == null || v == null || string.IsNullOrWhiteSpace(title))
        {
            return NotFound(new { success = false, message = "Soubor v návrhu není, nebo chybí název filmu." });
        }

        var roots = Advisor.LibraryRoots();
        if (string.IsNullOrWhiteSpace(movies) || !LibraryFiles.IsInsideLibrary(Path.Combine(movies, "x"), roots))
        {
            return BadRequest(new { success = false, message = "Cesta k filmům v Nastavení (" + movies + ") není knihovna Jellyfinu." });
        }

        var name = r.Year is >= 1900 and <= 2100
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{title} ({r.Year})")
            : title;
        var dir = Path.Combine(movies, name);
        var path = v.Path;
        var oldDir = Path.GetDirectoryName(path)!;
        return RunFileOp(
            new[] { "sug:" + id, "path:" + path },
            $"⏳ Přesouvám do {dir} — mezi Seriály a Filmy se soubor kopíruje, může to trvat i pár minut.",
            () => LibraryFiles.MoveVideo(path, dir, name, roots),
            moved =>
            {
                _logger.LogInformation("Fastshare: film ze Seriálů {From} → {To}", path, moved);
                _state.Suggestions.Update(id, x =>
                {
                    x.Versions = x.Versions.Where(y => y.Path != path).ToList();
                    x.DeletePaths = x.DeletePaths.Where(p => p != path).ToList();
                    x.Note = "Přesunuto do Filmů: " + moved;
                    if (x.Versions.Count == 0)
                    {
                        x.Status = "done";
                        x.DecidedUtc = DateTime.UtcNow;
                    }
                });
                RemoveEmptyFolders(new List<string> { oldDir }, roots);
            });
    }

    public sealed class OwnFolderRequest
    {
        public string Path { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public int? Year { get; set; }
    }

    /// <summary>
    /// 🔧 Je to jiný film, ale Jellyfin ho má sloučený s jiným (stejná složka „star wars/", „Sloučit verze") →
    /// přesunout do vlastní složky „Název (rok)/Název (rok).avi". Po skenu ho Jellyfin vezme jako samostatný
    /// film a podle názvu složky identifikuje.
    /// </summary>
    [HttpPost("Suggestions/{id}/OwnFolder")]
    public ActionResult OwnFolder([FromRoute] string id, [FromBody] OwnFolderRequest r)
    {
        var s = _state.Suggestions.Get(id);
        var v = s?.Versions.FirstOrDefault(x => x.Path == r.Path);
        var title = MediaOrganizer.Sanitize((r.Name ?? string.Empty).Trim());
        if (s == null || v == null || string.IsNullOrWhiteSpace(title))
        {
            return NotFound(new { success = false, message = "Soubor v návrhu není, nebo chybí název filmu." });
        }

        var roots = Advisor.LibraryRoots();
        var root = roots.Where(x => LibraryFiles.IsInsideLibrary(v.Path, new[] { x }))
            .OrderByDescending(x => x.Length).FirstOrDefault();
        if (root == null)
        {
            return BadRequest(new { success = false, message = "Soubor není v žádné knihovně." });
        }

        var name = r.Year is >= 1900 and <= 2100
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{title} ({r.Year})")
            : title;
        var dir = Path.Combine(root, name);
        var path = v.Path;
        return RunFileOp(
            new[] { "sug:" + id, "path:" + path },
            $"⏳ Přesouvám do vlastní složky {dir}…",
            () => LibraryFiles.MoveVideo(path, dir, name, roots),
            moved => _state.Suggestions.Update(id, x =>
            {
                x.Versions = x.Versions.Where(y => y.Path != path).ToList();
                x.DeletePaths = x.DeletePaths.Where(p => p != path).ToList();
                x.Note = "Odděleno do " + moved + " — po skenu knihovny bude samostatně";
                if (x.Versions.Count <= 1)
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                }
            }));
    }

    /// <summary>Verze do návrhu nepatří (jiný film, opravený v Jellyfinu) → jen ji z návrhu vyřadit, nic nemazat.</summary>
    [HttpPost("Suggestions/{id}/DropVersion")]
    public ActionResult DropVersion([FromRoute] string id, [FromBody] PathRequest r)
    {
        _state.Suggestions.Update(id, x =>
        {
            x.Versions = x.Versions.Where(v => v.Path != r.Path).ToList();
            x.DeletePaths = x.DeletePaths.Where(p => p != r.Path).ToList();
            x.Note = "Vyřazeno: " + Path.GetFileName(r.Path);
            if (x.Versions.Count <= 1)
            {
                x.Status = "done";
            }
        });
        return Ok(new { success = true, message = "Vyřazeno z návrhu." });
    }

    // ── Seriály ───────────────────────────────────────────────────
    /// <summary>🗑 Smazat celý seriál (všechny díly a verze). GUI se předem ptá s počtem a velikostí.</summary>
    [HttpPost("Suggestions/Series/{seriesId}/Delete")]
    public ActionResult DeleteSeries([FromRoute] string seriesId)
    {
        var files = Advisor.SeriesFiles(seriesId);
        if (files.Count == 0)
        {
            return NotFound(new { success = false, message = "Seriál nemá žádné soubory." });
        }

        var folders = files.Select(f => LibraryAdvisor.SeriesRoot(f.Version.Path)).Distinct(StringComparer.Ordinal).ToList();
        var (deleted, failed) = Delete(files.Select(f => f.Version.Path).ToList());
        var roots = Advisor.LibraryRoots();
        foreach (var dir in folders.Where(d => Directory.Exists(d) && LibraryFiles.IsInsideLibrary(Path.Combine(d, "x"), roots)
                                               && !roots.Any(r => string.Equals(Path.GetFullPath(r).TrimEnd('/'), Path.GetFullPath(d).TrimEnd('/'), StringComparison.Ordinal))))
        {
            try
            {
                // složka seriálu bez videí (zbyly plakáty, nfo) → pryč
                if (!Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => Parsed(Path.GetFileName(f)).IsVideo))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning("Fastshare: složku {Dir} se nepodařilo smazat: {Msg}", dir, ex.Message);
            }
        }

        foreach (var x in _state.Suggestions.GetAll().Where(x => x.SeriesId == seriesId && x.Status == "new"))
        {
            _state.Suggestions.Update(x.Id, y =>
            {
                y.Status = "done";
                y.Note = "Seriál smazán";
            });
        }

        QueueScan();
        return Ok(new { success = failed.Count == 0, message = $"Smazáno {deleted} souborů seriálu." + (failed.Count > 0 ? $" {failed.Count} se smazat nepovedlo." : string.Empty) });
    }

    public sealed class MergeSeriesRequest
    {
        /// <summary>Seriál, jehož díly se přesunou…</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>…do složek tohoto seriálu.</summary>
        public string To { get; set; } = string.Empty;
    }

    /// <summary>👯 Stejný seriál ve dvou složkách → přesunout díly jednoho do složek řad druhého.</summary>
    [HttpPost("Suggestions/Series/Merge")]
    public ActionResult MergeSeries([FromBody] MergeSeriesRequest r)
    {
        if (string.IsNullOrEmpty(r.From) || string.IsNullOrEmpty(r.To) || r.From == r.To)
        {
            return BadRequest(new { success = false, message = "Vyber dva různé seriály." });
        }

        var roots = Advisor.LibraryRoots();
        var files = Advisor.SeriesFiles(r.From);
        var oldFolders = files.Select(f => LibraryAdvisor.SeriesRoot(f.Version.Path)).Distinct(StringComparer.Ordinal).ToList();
        var dirs = new Dictionary<int, string?>();
        foreach (var season in files.Select(f => f.Ep.ParentIndexNumber ?? 1).Distinct())
        {
            dirs[season] = Advisor.SeasonDirFor(r.To, season);
        }

        var keys = new[] { "ser:" + r.From, "ser:" + r.To }.Concat(files.Select(f => "path:" + f.Version.Path));
        return RunFileOp(keys, $"⏳ Přesouvám {files.Count} souborů…", () =>
        {
            var moved = 0;
            var failed = new List<string>();
            foreach (var (ep, v) in files)
            {
                var dir = dirs[ep.ParentIndexNumber ?? 1];
                if (dir == null)
                {
                    failed.Add(Path.GetFileName(v.Path) + ": cílový seriál nemá složku");
                    continue;
                }

                try
                {
                    LibraryFiles.MoveVideo(v.Path, dir, null, roots);
                    moved++;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    failed.Add(Path.GetFileName(v.Path) + ": " + ex.Message);
                }
            }

            RemoveEmptyFolders(oldFolders, roots);
            _logger.LogInformation("Fastshare: sloučení seriálů — přesunuto {Moved}, chyby {Failed}", moved, failed.Count);
            if (failed.Count > 0)
            {
                throw new IOException($"přesunuto {moved}, nepovedlo se: " + string.Join("; ", failed.Take(5)));
            }

            return string.Empty;
        });
    }

    private static void RemoveEmptyFolders(List<string> oldFolders, IReadOnlyCollection<string> roots)
    {
        // prázdné složky po přesunu (zbyly plakáty, nfo) pryč
        foreach (var dir in oldFolders.Where(Directory.Exists))
        {
            if (!roots.Any(x => string.Equals(Path.GetFullPath(x).TrimEnd('/'), Path.GetFullPath(dir).TrimEnd('/'), StringComparison.Ordinal))
                && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(f => Parsed(Path.GetFileName(f)).IsVideo))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 🔧 Seriál: všechny neočíslované díly s jistým odhadem (TMDb díl zná a název souboru mu neodporuje)
    /// přesunout a přejmenovat na „Seriál - SxxEyy". Nejisté odhady zůstanou na ruční kontrolu.
    /// </summary>
    [HttpPost("Suggestions/Series/{seriesId}/FixGuessed")]
    public ActionResult FixGuessed([FromRoute] string seriesId)
    {
        var todo = _state.Suggestions.GetAll()
            .Where(x => x.SeriesId == seriesId && x.Kind == "episode" && x.Status == "new" && x.Episode == null)
            .SelectMany(x => x.Versions.Where(v => v.FileGuessSure && v.FileEpisode != null && System.IO.File.Exists(v.Path))
                .Select(v => (Sug: x, Path: v.Path, Season: v.FileSeason ?? 1, Episode: v.FileEpisode!.Value)))
            .ToList();
        if (todo.Count == 0)
        {
            return Ok(new { success = false, message = "Žádný díl s jistým odhadem." });
        }

        var series = todo[0].Sug.SeriesName ?? todo[0].Sug.Title;
        var dirs = todo.Select(t => t.Season).Distinct().ToDictionary(n => n, n => Advisor.SeasonDirFor(seriesId, n));
        var roots = Advisor.LibraryRoots();
        var keys = new[] { "ser:" + seriesId }.Concat(todo.Select(t => "sug:" + t.Sug.Id)).Concat(todo.Select(t => "path:" + t.Path));
        return RunFileOp(keys, $"⏳ Zařazuji {todo.Count} dílů seriálu {series}…", () =>
        {
            var moved = 0;
            var failed = new List<string>();
            foreach (var t in todo)
            {
                var dir = dirs[t.Season] ?? Path.Combine(LibraryAdvisor.SeriesRoot(t.Path), $"Season {t.Season:D2}");
                try
                {
                    var to = LibraryFiles.MoveVideo(t.Path, dir, EpName(series, t.Season, t.Episode), roots);
                    moved++;
                    _state.Suggestions.Update(t.Sug.Id, x =>
                    {
                        x.Versions = x.Versions.Where(y => y.Path != t.Path).ToList();
                        x.DeletePaths = x.DeletePaths.Where(p => p != t.Path).ToList();
                        x.Note = "Zařazeno jako " + Path.GetFileName(to);
                        if (x.Versions.Count == 0)
                        {
                            x.Status = "done";
                            x.DecidedUtc = DateTime.UtcNow;
                        }
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    failed.Add(Path.GetFileName(t.Path) + ": " + ex.Message);
                }
            }

            _logger.LogInformation("Fastshare: {Series} — zařazeno {Moved} odhadnutých dílů, chyby {Failed}", series, moved, failed.Count);
            if (failed.Count > 0)
            {
                throw new IOException($"zařazeno {moved}, nepovedlo se: " + string.Join("; ", failed.Take(5)));
            }

            return string.Empty;
        });
    }

    public sealed class SeriesBulkRequest
    {
        public bool Replace { get; set; } = true;

        /// <summary>"episode" (lepší verze dílů) nebo "missing" (chybějící díly).</summary>
        public string Kind { get; set; } = "episode";
    }

    /// <summary>⬇️ U seriálu zařadit všechny nalezené lepší verze dílů.</summary>
    [HttpPost("Suggestions/Series/{seriesId}/DownloadAll")]
    public ActionResult SeriesDownloadAll([FromRoute] string seriesId, [FromBody] SeriesBulkRequest r)
    {
        var kind = r.Kind == "missing" ? "missing" : "episode";
        var list = _state.Suggestions.GetAll().Where(x => x.Kind == kind && x.Status == "new" && x.SeriesId == seriesId && x.Candidate != null).ToList();
        var n = 0;
        foreach (var x in list)
        {
            if (DownloadSuggestion(x.Id, new SuggestionDownloadRequest { Replace = r.Replace }) is OkObjectResult)
            {
                n++;
            }
        }

        _state.Queue.GroupSeries();
        return Ok(new { success = n > 0, message = $"Zařazeno {n} dílů" + (r.Replace ? " (staré verze se smažou po stažení)." : ".") });
    }

    /// <summary>✋ Odmítnout všechny návrhy dílů seriálu (kvalita stačí).</summary>
    [HttpPost("Suggestions/Series/{seriesId}/Reject")]
    public ActionResult SeriesReject([FromRoute] string seriesId)
    {
        var list = _state.Suggestions.GetAll().Where(x => x.Kind is "episode" or "missing" && x.Status == "new" && x.SeriesId == seriesId).ToList();
        foreach (var x in list)
        {
            _state.Suggestions.Update(x.Id, y =>
            {
                y.Status = "rejected";
                y.DecidedUtc = DateTime.UtcNow;
                y.Note = "Odmítnuto celým seriálem";
            });
        }

        return Ok(new { success = true, message = $"Odmítnuto {list.Count} dílů." });
    }

    public sealed class FindMissingRequest
    {
        public bool Enable { get; set; } = true;
    }

    /// <summary>🔍 Zapnout/vypnout dohledávání chybějících dílů seriálu na Fastshare.</summary>
    [HttpPost("Suggestions/Series/{seriesId}/FindMissing")]
    public ActionResult FindMissing([FromRoute] string seriesId, [FromBody] FindMissingRequest r)
    {
        _state.Suggestions.SetFindMissing(seriesId, r.Enable);
        if (!r.Enable)
        {
            return Ok(new { success = true, message = "Chybějící díly se u seriálu už nehledají." });
        }

        Advisor.Prioritize(seriesId);
        return Ok(new { success = true, message = "Chybějící díly se hledají — seriál jde na začátek fronty. " + NextSearchNote() });
    }

    /// <summary>🔎 Seriál hned na řadu: lepší verze dílů (a chybějící, je-li zapnuto) i když se nedávno hledalo.</summary>
    [HttpPost("Suggestions/Series/{seriesId}/SearchNow")]
    public ActionResult SearchSeriesNow([FromRoute] string seriesId)
    {
        Advisor.Prioritize(seriesId);
        return Ok(new { success = true, message = "Seriál jde na začátek fronty hledání. " + NextSearchNote() });
    }

    /// <summary>Kdy půjde další dotaz (strop hledání za hodinu) — ať je jasné, proč se hned nic neděje.</summary>
    private string NextSearchNote()
    {
        var next = _state.SearchGate.NextUtc;
        var perHour = Plugin.Instance?.Configuration.AutoSearchesPerHour ?? 20;
        return next > DateTime.UtcNow
            ? $"Další dotaz na Fastshare v {next.ToLocalTime():H:mm} (strop {perHour}/h), seriál potřebuje 1–2 dotazy na název. Pak klikni na „Obnovit seznam“."
            : "Za chvilku klikni na „Obnovit seznam“.";
    }

    public sealed class SeriesTargetRequest
    {
        /// <summary>SD/720p/1080p, prázdné = převažující kvalita.</summary>
        public string? Quality { get; set; }
    }

    /// <summary>Ručně zvolit, na jakou kvalitu se seriál sjednocuje. Projeví se další kontrolou (spustí se hned).</summary>
    [HttpPost("Suggestions/Series/{seriesId}/Target")]
    public ActionResult SeriesTarget([FromRoute] string seriesId, [FromBody] SeriesTargetRequest r)
    {
        var q = string.IsNullOrWhiteSpace(r.Quality) ? null : r.Quality;
        if (q != null && !FileSelector.QualityLevels.ContainsKey(q))
        {
            return BadRequest(new { success = false, message = "Neznámá kvalita." });
        }

        var episodes = _state.Suggestions.GetAll().Where(x => x.Kind == "episode" && x.SeriesId == seriesId).Select(x => x.ItemId);
        _state.Suggestions.SetSeriesTarget(seriesId, q, episodes);
        Advisor.Prioritize(seriesId); // díly pod novým cílem se hledají přednostně
        return Ok(new { success = true, message = (q == null ? "Zpět na převažující kvalitu." : $"Sjednotit na {q} — díly pod {q} se objeví k vyřízení a hledají se na Fastshare přednostně.")
            + " Kontrola teď znovu načítá knihovnu (pár minut). " + NextSearchNote() });
    }

    /// <summary>🗑 Smazat celý film (dostal se do knihovny omylem). GUI se předem ptá.</summary>
    [HttpPost("Suggestions/{id}/DeleteMovie")]
    public ActionResult DeleteMovie([FromRoute] string id)
    {
        var s = _state.Suggestions.Get(id);
        if (s == null || s.Versions.Count == 0)
        {
            return NotFound(new { success = false, message = "Návrh nemá soubory." });
        }

        var (deleted, failed) = Delete(s.Versions.Select(v => v.Path).ToList());
        _state.Suggestions.Update(id, x =>
        {
            x.Status = failed.Count == 0 ? "done" : x.Status;
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = $"Film smazán ({deleted} souborů)" + (failed.Count > 0 ? "; nesmazáno: " + string.Join(", ", failed.Select(f => f.Why)) : string.Empty);
        });
        QueueScan();
        return Ok(new { success = failed.Count == 0, message = failed.Count == 0 ? "Film smazán." : "Něco se smazat nepovedlo." });
    }

    public sealed class DecideRequest
    {
        public string? Note { get; set; }
    }

    /// <summary>✋ Odmítnout (kvalita stačí / film nechci řešit). Při další kontrole se nevrátí.</summary>
    [HttpPost("Suggestions/{id}/Reject")]
    public ActionResult Reject([FromRoute] string id, [FromBody] DecideRequest? r)
    {
        _state.Suggestions.Update(id, x =>
        {
            x.Status = "rejected";
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = string.IsNullOrWhiteSpace(r?.Note) ? "Odmítnuto" : r!.Note;
        });
        return Ok(new { success = true });
    }

    /// <summary>Film byl identifikován (GUI to udělalo přes Jellyfin Items/RemoteSearch/Apply).</summary>
    [HttpPost("Suggestions/{id}/Identified")]
    public ActionResult Identified([FromRoute] string id, [FromBody] DecideRequest? r)
    {
        _state.Suggestions.Update(id, x =>
        {
            x.Status = "done";
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = string.IsNullOrWhiteSpace(r?.Note) ? "Identifikováno" : r!.Note;
        });
        return Ok(new { success = true });
    }

    [HttpPost("Suggestions/{id}/Reopen")]
    public ActionResult Reopen([FromRoute] string id)
    {
        _state.Suggestions.Update(id, x =>
        {
            x.Status = "new";
            x.Note = null;
            x.DecidedUtc = null;
        });
        return Ok(new { success = true });
    }

    private (int Deleted, List<(string Path, string Why)> Failed) Delete(IReadOnlyList<string> paths)
    {
        var roots = Advisor.LibraryRoots();
        var deleted = 0;
        var failed = new List<(string, string)>();
        foreach (var p in paths)
        {
            try
            {
                if (LibraryFiles.DeleteVideo(p, roots).Count > 0)
                {
                    deleted++;
                    _logger.LogInformation("Fastshare: smazáno z knihovny {Path}", p);
                }
            }
            catch (Exception ex)
            {
                failed.Add((p, Path.GetFileName(p) + ": " + ex.Message));
                _logger.LogWarning("Fastshare: {Path} se nepodařilo smazat: {Msg}", p, ex.Message);
            }
        }

        return (deleted, failed);
    }

    // ── Hlídač ────────────────────────────────────────────────────
    [HttpGet("Watch")]
    public ActionResult GetWatch() => Ok(_state.Watch.GetAll());

    public sealed class WatchRequest
    {
        public string Type { get; set; } = "series";

        public string Title { get; set; } = string.Empty;

        public int? Year { get; set; }

        public string? SearchTerm { get; set; }

        public int? FromSeason { get; set; }

        public int? IntervalDays { get; set; }

        public string? MaxQuality { get; set; }

        public bool? Enabled { get; set; }
    }

    [HttpPost("Watch")]
    public ActionResult AddWatch([FromBody] WatchRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title))
        {
            return BadRequest(new { success = false, message = "Chybí název." });
        }

        var id = _state.Watch.Add(new WatchItem
        {
            Type = r.Type == "movie" ? "movie" : "series",
            Title = r.Title.Trim(),
            Year = r.Year is > 1800 ? r.Year : null,
            SearchTerm = string.IsNullOrWhiteSpace(r.SearchTerm) ? null : r.SearchTerm.Trim(),
            FromSeason = r.FromSeason is > 0 ? r.FromSeason : null,
            IntervalDays = r.IntervalDays is > 0 ? r.IntervalDays.Value : Plugin.Instance!.Configuration.WatchDefaultIntervalDays,
            MaxQuality = string.IsNullOrWhiteSpace(r.MaxQuality) ? null : r.MaxQuality,
            ForceCheck = true,
        });
        if (id == null)
        {
            return Ok(new { success = false, message = "Už ho hlídáš." });
        }

        _state.WakeWatcher();
        return Ok(new { success = true, id, message = "Přidáno — první kontrola proběhne hned." });
    }

    [HttpPost("Watch/{id}")]
    public ActionResult UpdateWatch([FromRoute] Guid id, [FromBody] WatchRequest r)
    {
        _state.Watch.Update(id, w =>
        {
            if (r.Enabled != null)
            {
                w.Enabled = r.Enabled.Value;
            }

            if (r.IntervalDays is > 0)
            {
                w.IntervalDays = r.IntervalDays.Value;
            }

            if (r.MaxQuality != null)
            {
                w.MaxQuality = r.MaxQuality.Length == 0 ? null : r.MaxQuality;
            }

            if (r.SearchTerm != null)
            {
                w.SearchTerm = r.SearchTerm.Trim().Length == 0 ? null : r.SearchTerm.Trim();
            }

            if (r.FromSeason != null)
            {
                w.FromSeason = r.FromSeason > 0 ? r.FromSeason : null;
            }
        });
        return Ok(new { success = true });
    }

    [HttpPost("Watch/{id}/CheckNow")]
    public ActionResult CheckNow([FromRoute] Guid id)
    {
        _state.Watch.Update(id, w => w.ForceCheck = true);
        _state.WakeWatcher();
        return Ok(new { success = true });
    }

    /// <summary>Zapomenout, co už bylo zařazeno (další kontrola projde všechny díly znovu).</summary>
    [HttpPost("Watch/{id}/Reset")]
    public ActionResult ResetWatch([FromRoute] Guid id)
    {
        _state.Watch.Update(id, w =>
        {
            w.Grabbed.Clear();
            w.MovieGrabbed = false;
            w.Completed = false;
            w.ForceCheck = true;
        });
        _state.WakeWatcher();
        return Ok(new { success = true });
    }

    [HttpDelete("Watch/{id}")]
    public ActionResult RemoveWatch([FromRoute] Guid id) => Ok(new { success = _state.Watch.Remove(id) });
}
