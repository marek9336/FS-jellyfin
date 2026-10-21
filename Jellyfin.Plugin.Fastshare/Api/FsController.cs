using Jellyfin.Plugin.Fastshare.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare.Api;

/// <summary>REST API pro stránku pluginu. Jen pro adminy, vše pod /Plugins/Fastshare/...</summary>
[ApiController]
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
        return Ok(new
        {
            status = StatusDto(),
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

    [HttpPost("Queue/GroupSeries")]
    public ActionResult GroupSeries() => Ok(new { success = _state.Queue.GroupSeries() });

    /// <summary>⏹ Zastavit jedno stahování (vrátí se do fronty, stažená část zůstane).</summary>
    [HttpPost("Queue/{id}/Stop")]
    public ActionResult Stop([FromRoute] Guid id) => Ok(new { success = _state.CancelDownload(id) > 0 });

    [HttpPost("Worker/{action}")]
    public ActionResult Worker([FromRoute] string action)
    {
        switch (action)
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
            default:
                return BadRequest();
        }

        return Ok(new { paused = _state.Queue.WorkerPaused });
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
    public ActionResult Scan([FromBody] ScanRequest? r) =>
        Ok(new { started = Advisor.Start(r?.Force == true), message = Advisor.Status.Running ? "Kontrola už běží." : "Kontrola spuštěna." });

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
        var item = new QueueItem
        {
            Title = s.Title,
            Year = s.Year,
            MediaType = MediaKind.Movie,
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

    /// <summary>🗑 Duplicita: smazat horší verze hned (nejlepší zůstane).</summary>
    [HttpPost("Suggestions/{id}/DeleteWorse")]
    public ActionResult DeleteWorse([FromRoute] string id)
    {
        var s = _state.Suggestions.Get(id);
        if (s is not { Kind: "duplicate" } || s.DeletePaths.Count == 0)
        {
            return NotFound(new { success = false, message = "Není co mazat." });
        }

        var (deleted, failed) = Delete(s.DeletePaths);
        var kept = s.Versions.Where(v => !s.DeletePaths.Contains(v.Path)).ToList();
        _state.Suggestions.Update(id, x =>
        {
            x.DeletePaths = failed.Select(f => f.Path).ToList();
            x.Versions = kept.Concat(x.Versions.Where(v => failed.Any(f => f.Path == v.Path))).ToList();
            x.DecidedUtc = DateTime.UtcNow;
            x.Note = $"Smazáno horších verzí: {deleted}" + (failed.Count > 0 ? "; nesmazáno: " + string.Join(", ", failed.Select(f => f.Why)) : string.Empty);
            if (failed.Count == 0)
            {
                // Zbyla jedna verze: když je pod cílem a máme lepší, návrh žije dál jako „lepší verze"
                if (x.Candidate != null)
                {
                    x.Kind = "upgrade";
                    x.Id = "up:" + x.ItemId;
                }
                else
                {
                    x.Status = "done";
                }
            }
        });
        QueueScan();
        return Ok(new { success = failed.Count == 0, message = $"Smazáno {deleted} {(deleted == 1 ? "verze" : "verzí")}." + (failed.Count > 0 ? " Něco se smazat nepovedlo." : string.Empty) });
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
