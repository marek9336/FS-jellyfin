namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Soubor z hledání + co to je (cíl) → položka fronty.</summary>
public static class QueueItemFactory
{
    /// <summary>Návrh s kandidátem → položka fronty (díl jde do složky, kde seriál je; `replace` = smazat staré verze po stažení).</summary>
    public static QueueItem FromSuggestion(Suggestion s, bool replace)
    {
        var c = s.Candidate ?? throw new InvalidOperationException("Návrh nemá kandidáta");
        var episode = s.Kind is "episode" or "missing" || (s.Kind == "smaller" && s.SeriesId != null);
        return new QueueItem
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
            ReplacePaths = replace ? s.Versions.Select(v => v.Path).ToList() : null,
            Overwrite = true, // starší verze ve stejné složce nesmí stažení přeskočit jako „duplicitu"
        };
    }

    public static QueueItem Create(FsFile f, SelectTarget t)
    {
        var episode = t.Kind == MediaKind.Episode;
        return new QueueItem
        {
            Title = episode ? t.Title : t.Title,
            SeriesTitle = episode ? t.Title : null,
            Year = t.Year,
            MediaType = t.Kind,
            Season = episode ? t.Season ?? 1 : null,
            Episode = episode ? t.Episode ?? 1 : null,
            FileId = f.Id,
            FileName = f.FileName,
            DownloadUrl = f.DownloadUrl,
            Quality = f.Quality,
            Language = string.IsNullOrEmpty(f.LanguageLabel) ? null : f.LanguageLabel,
            SizeBytes = f.SizeBytes,
            SizeText = Dedup.FormatSize(f.SizeBytes),
            BytesTotal = f.SizeBytes,
            DurationSec = f.DurationSec,
        };
    }
}
