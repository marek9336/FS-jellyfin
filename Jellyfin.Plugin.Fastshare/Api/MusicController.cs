using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare.Api;

/// <summary>Záložka 🎵 Hudba: jen soubory na disku a tagy v Jellyfinu. ID návrhů obsahují cesty → posílají se v těle, ne v adrese.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/Fastshare/Music")]
[Produces("application/json")]
public class MusicController : ControllerBase
{
    private readonly FsState _state;
    private readonly MusicAdvisor _music;
    private readonly ILibraryManager _library;
    private readonly ILogger<MusicController> _logger;

    public MusicController(FsState state, MusicAdvisor music, ILibraryManager library, ILogger<MusicController> logger)
    {
        _state = state;
        _music = music;
        _library = library;
        _logger = logger;
    }

    [HttpGet]
    public ActionResult Get()
    {
        var last = _state.Music.LastRun;
        var items = _state.Music.GetAll().Where(x => x.Status == "new").ToList();
        return Ok(new
        {
            status = new
            {
                running = _music.Running,
                phase = _music.Phase,
                lastRunUtc = last.LastRunUtc,
                lastResult = last.LastResult,
                tracks = last.Tracks,
                bytes = last.Bytes,
            },
            counts = items.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => new { cards = g.Count(), tracks = g.Sum(x => x.Tracks.Count), savings = g.Sum(x => x.SavingsBytes) }),
            items = items.Select(Dto),
        });
    }

    private static object Dto(MusicSuggestion s) => new
    {
        id = s.Id,
        kind = s.Kind,
        title = s.Title,
        crossAlbum = s.CrossAlbum,
        note = s.Note,
        savings = s.SavingsBytes,
        size = s.Tracks.Sum(t => t.SizeBytes),
        deletePaths = s.DeletePaths,
        tracks = s.Tracks.Select(t => new
        {
            itemId = t.ItemId,
            path = t.Path,
            size = t.SizeBytes,
            bitrate = t.Bitrate,
            duration = t.DurationSec,
            title = t.Title,
            artist = t.Artists.FirstOrDefault() ?? t.AlbumArtist,
            album = t.Album,
            year = t.Year,
            trackNo = t.TrackNo,
        }),
        tags = s.Tags.Select(g => new { path = g.Path, title = g.Title, artist = g.Artist, album = g.Album, year = g.Year, trackNo = g.TrackNo, sure = g.Sure, how = g.How }),
    };

    [HttpPost("Scan")]
    public ActionResult Scan() => Ok(_music.Start()
        ? new { success = true, message = "Procházím hudbu — jen soubory na disku a tagy v Jellyfinu, nic se nehledá na internetu (pár minut)." }
        : new { success = true, message = "Hudba se už prochází." });

    public sealed class DeleteRequest
    {
        public string Id { get; set; } = string.Empty;

        public List<string> Paths { get; set; } = new();
    }

    /// <summary>🗑 Smazat vybrané skladby návrhu. U duplicity musí aspoň jedna zůstat.</summary>
    [HttpPost("Delete")]
    public ActionResult Delete([FromBody] DeleteRequest r)
    {
        var s = _state.Music.Get(r.Id);
        if (s == null)
        {
            return NotFound(new { success = false, message = "Návrh neexistuje — obnov seznam." });
        }

        var known = s.Tracks.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
        var paths = r.Paths.Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (paths.Count == 0)
        {
            return BadRequest(new { success = false, message = "Vyber, co smazat." });
        }

        if (s.Kind is "same" or "song" && paths.Count >= s.Tracks.Count)
        {
            return BadRequest(new { success = false, message = "Aspoň jedna verze skladby musí zůstat." });
        }

        var (deleted, freed, failed) = DeletePaths(s, paths);
        _state.Music.Update(r.Id, x =>
        {
            x.Tracks = x.Tracks.Where(t => !deleted.Contains(t.Path)).ToList();
            x.DeletePaths = x.DeletePaths.Where(p => !deleted.Contains(p)).ToList();
            x.Tags = x.Tags.Where(t => !deleted.Contains(t.Path)).ToList();
            x.Note = $"Smazáno {deleted.Count}, uvolněno {Dedup.FormatSize(freed)}" + (failed.Count > 0 ? "; nesmazáno: " + string.Join(", ", failed) : string.Empty);
            if (x.Kind is "same" or "song" ? x.Tracks.Count <= 1 : x.Tracks.Count == 0)
            {
                x.Status = "done";
                x.DecidedUtc = DateTime.UtcNow;
            }
        });
        QueueScan();
        return Ok(new { success = failed.Count == 0, message = $"Smazáno {deleted.Count} {Plural(deleted.Count, "skladba", "skladby", "skladeb")}, uvolněno {Dedup.FormatSize(freed)}." + (failed.Count > 0 ? " Něco se smazat nepovedlo: " + string.Join("; ", failed.Take(3)) : string.Empty) });
    }

    public sealed class BulkRequest
    {
        /// <summary>"same" (bajtově stejné), "song" (stejná skladba ve stejné složce), "junk" — maže jen předem zaškrtnuté.</summary>
        public List<string> Kinds { get; set; } = new();
    }

    /// <summary>🗑 Hromadně smazat předem zaškrtnuté (bajtově stejné kopie, smetí). Z každé duplicity jedna zůstane.</summary>
    [HttpPost("BulkDelete")]
    public ActionResult BulkDelete([FromBody] BulkRequest r)
    {
        var kinds = r.Kinds.Where(k => k is "same" or "song" or "junk").ToHashSet(StringComparer.Ordinal);
        var list = _state.Music.GetAll().Where(x => x.Status == "new" && kinds.Contains(x.Kind) && x.DeletePaths.Count > 0).ToList();
        var total = 0;
        long freed = 0;
        var failed = new List<string>();
        foreach (var s in list)
        {
            var paths = s.Kind == "junk" ? s.DeletePaths : s.DeletePaths.Where(p => s.Tracks.Count(t => t.Path != p) >= 1).ToList();
            var (deleted, f, fail) = DeletePaths(s, paths);
            total += deleted.Count;
            freed += f;
            failed.AddRange(fail);
            _state.Music.Update(s.Id, x =>
            {
                x.Tracks = x.Tracks.Where(t => !deleted.Contains(t.Path)).ToList();
                x.DeletePaths = x.DeletePaths.Where(p => !deleted.Contains(p)).ToList();
                x.Note = $"Smazáno {deleted.Count} (hromadně)";
                if (x.DeletePaths.Count == 0)
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                }
            });
        }

        if (total > 0)
        {
            QueueScan();
        }

        return Ok(new { success = failed.Count == 0, message = $"Smazáno {total} {Plural(total, "soubor", "soubory", "souborů")}, uvolněno {Dedup.FormatSize(freed)}." + (failed.Count > 0 ? $" {failed.Count}× nepovedlo." : string.Empty) });
    }

    public sealed class ApplyRequest
    {
        public string Id { get; set; } = string.Empty;

        /// <summary>Tagy k zapsání (z GUI, případně upravené). Prázdné = použít navržené jisté.</summary>
        public List<MusicTags>? Tags { get; set; }
    }

    /// <summary>✅ Zapsat tagy do Jellyfinu (jen u skladeb návrhu). Soubory se nemění.</summary>
    [HttpPost("Apply")]
    public async Task<ActionResult> Apply([FromBody] ApplyRequest r, CancellationToken ct)
    {
        var s = _state.Music.Get(r.Id);
        if (s == null)
        {
            return NotFound(new { success = false, message = "Návrh neexistuje — obnov seznam." });
        }

        var known = s.Tracks.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
        var tags = (r.Tags is { Count: > 0 } ? r.Tags : s.Tags.Where(t => t.Sure).ToList())
            .Where(t => known.Contains(t.Path) && (!string.IsNullOrWhiteSpace(t.Title) || !string.IsNullOrWhiteSpace(t.Artist)))
            .ToList();
        if (tags.Count == 0)
        {
            return Ok(new { success = false, message = "Není co zapsat — u skladeb chybí název i interpret." });
        }

        var n = await _music.ApplyTagsAsync(tags, ct).ConfigureAwait(false);
        var done = tags.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
        _state.Music.Update(r.Id, x =>
        {
            x.Tracks = x.Tracks.Where(t => !done.Contains(t.Path)).ToList();
            x.Tags = x.Tags.Where(t => !done.Contains(t.Path)).ToList();
            x.Note = $"Zapsáno {n} skladeb";
            if (x.Tracks.Count == 0)
            {
                x.Status = "done";
                x.DecidedUtc = DateTime.UtcNow;
            }
        });
        _logger.LogInformation("Fastshare: hudba — zapsány tagy {N} skladeb ({Title})", n, s.Title);
        return Ok(new { success = n > 0, message = $"Zapsáno {n} {Plural(n, "skladba", "skladby", "skladeb")} do Jellyfinu (soubory beze změny, položky zamčené proti přepsání skenem)." });
    }

    public sealed class BulkApplyRequest
    {
        /// <summary>"charset" (oprava diakritiky) nebo "meta" (jen jisté odhady).</summary>
        public string Kind { get; set; } = "charset";
    }

    /// <summary>✅ Hromadně: všechny opravy diakritiky / všechny jisté odhady tagů.</summary>
    [HttpPost("BulkApply")]
    public async Task<ActionResult> BulkApply([FromBody] BulkApplyRequest r, CancellationToken ct)
    {
        var kind = r.Kind == "meta" ? "meta" : "charset";
        var list = _state.Music.GetAll().Where(x => x.Status == "new" && x.Kind == kind).ToList();
        var total = 0;
        foreach (var s in list)
        {
            var tags = s.Tags.Where(t => t.Sure).ToList();
            if (tags.Count == 0)
            {
                continue;
            }

            var n = await _music.ApplyTagsAsync(tags, ct).ConfigureAwait(false);
            total += n;
            var done = tags.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
            _state.Music.Update(s.Id, x =>
            {
                x.Tracks = x.Tracks.Where(t => !done.Contains(t.Path)).ToList();
                x.Tags = x.Tags.Where(t => !done.Contains(t.Path)).ToList();
                x.Note = $"Zapsáno {n} (hromadně)";
                if (x.Tracks.Count == 0)
                {
                    x.Status = "done";
                    x.DecidedUtc = DateTime.UtcNow;
                }
            });
        }

        _logger.LogInformation("Fastshare: hudba — hromadně zapsány tagy {N} skladeb ({Kind})", total, kind);
        return Ok(new { success = total > 0, message = total > 0 ? $"Zapsáno {total} {Plural(total, "skladba", "skladby", "skladeb")} do Jellyfinu." : "Nic jistého k zapsání." });
    }

    public sealed class IdRequest
    {
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>✋ Nechat být — při další kontrole se nevrátí.</summary>
    [HttpPost("Reject")]
    public ActionResult Reject([FromBody] IdRequest r)
    {
        _state.Music.Update(r.Id, x =>
        {
            x.Status = "rejected";
            x.DecidedUtc = DateTime.UtcNow;
        });
        return Ok(new { success = true, message = "Nechávám být — při další kontrole se nevrátí." });
    }

    private (HashSet<string> Deleted, long Freed, List<string> Failed) DeletePaths(MusicSuggestion s, IReadOnlyList<string> paths)
    {
        var roots = _music.MusicRoots();
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var failed = new List<string>();
        long freed = 0;
        foreach (var p in paths)
        {
            try
            {
                var size = s.Tracks.FirstOrDefault(t => t.Path == p)?.SizeBytes ?? 0;
                if (LibraryFiles.DeleteAudio(p, roots).Count > 0)
                {
                    freed += size;
                    _logger.LogInformation("Fastshare: hudba — smazáno {Path}", p);
                }

                deleted.Add(p); // i když už neexistoval
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add(Path.GetFileName(p) + ": " + ex.Message);
                _logger.LogWarning("Fastshare: hudba — {Path} nejde smazat: {Msg}", p, ex.Message);
            }
        }

        return (deleted, freed, failed);
    }

    private void QueueScan()
    {
        try
        {
            _library.QueueLibraryScan();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Fastshare: sken knihovny se nepodařilo spustit: {Msg}", ex.Message);
        }
    }

    private static string Plural(int n, string one, string few, string many) => n == 1 ? one : n is > 1 and < 5 ? few : many;
}
