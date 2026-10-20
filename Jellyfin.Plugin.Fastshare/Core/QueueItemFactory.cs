namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Soubor z hledání + co to je (cíl) → položka fronty.</summary>
public static class QueueItemFactory
{
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
