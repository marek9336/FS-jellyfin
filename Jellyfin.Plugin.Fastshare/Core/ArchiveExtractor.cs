using System.Globalization;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Rozbalí stažený archiv (zip/rar/7z, i vícedílný) a roztřídí videa do knihovny.
/// Knihovnu SharpCompress má Jellyfin sám (plugin ji nepřibaluje, viz .csproj).
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public static class ArchiveExtractor
{
    private static readonly Lazy<string?> SupportLazy = new(() =>
    {
        try
        {
            return "SharpCompress " + typeof(ArchiveFactory).Assembly.GetName().Version?.ToString(3);
        }
        catch (Exception)
        {
            return null;
        }
    });

    /// <summary>
    /// Knihovna na archivy (SharpCompress) dodává Jellyfin — tady se ověří, že ji plugin opravdu vidí.
    /// null = rozbalování nefunguje (jiná verze Jellyfinu bez SharpCompress).
    /// </summary>
    public static string? Support => SupportLazy.Value;

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".ass", ".ssa", ".vtt",
    };

    /// <summary>
    /// Rozbalí archiv do `destDir`. `parts` = všechny části v pořadí (u jednodílného jedna).
    /// Cesty uvnitř archivu se zplošťují (jen název souboru) — žádný zápis mimo `destDir`.
    /// Vrací cesty rozbalených souborů.
    /// </summary>
    public static List<string> Extract(IReadOnlyList<string> parts, string destDir, Action<long, long> progress, CancellationToken ct)
    {
        try
        {
            return ExtractCore(parts, destDir, progress, ct);
        }
        catch (SharpCompress.Common.CryptographicException)
        {
            throw new FastsharePermanentException("Archiv je zaheslovaný — rozbal ho ručně");
        }
        catch (SharpCompress.Common.IncompleteArchiveException)
        {
            throw new FastsharePermanentException("Archivu chybí některá část (vícedílný archiv — přidej všechny části)");
        }
        catch (Exception ex) when (ex is SharpCompress.Common.SharpCompressException or InvalidOperationException or InvalidDataException
                                       && ex is not FastshareException)
        {
            throw new FastsharePermanentException("Archiv se nepodařilo rozbalit (poškozený, neúplný nebo nepodporovaný): " + ex.Message);
        }
    }

    private static List<string> ExtractCore(IReadOnlyList<string> parts, string destDir, Action<long, long> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);
        var files = parts.Select(p => new FileInfo(p)).ToList();
        using var archive = files.Count == 1
            ? ArchiveFactory.OpenArchive(files[0], new ReaderOptions())
            : ArchiveFactory.OpenArchive(files, new ReaderOptions());

        if (archive.IsEncrypted || archive.Entries.Any(e => e.IsEncrypted))
        {
            throw new FastsharePermanentException("Archiv je zaheslovaný — rozbal ho ručně");
        }

        if (!archive.IsComplete)
        {
            throw new FastsharePermanentException("Archivu chybí některá část (vícedílný archiv — přidej všechny části)");
        }

        var total = archive.TotalUncompressedSize;
        long done = 0;
        var result = new List<string>();

        void Write(string? key, bool isDirectory, Func<Stream> open)
        {
            ct.ThrowIfCancellationRequested();
            if (isDirectory || string.IsNullOrEmpty(key))
            {
                return;
            }

            var name = MediaOrganizer.Sanitize(Path.GetFileName(key.Replace('\\', '/')));
            if (name.Length == 0 || name is "." or "..")
            {
                return;
            }

            var target = MediaOrganizer.EnsureUniquePath(Path.Combine(destDir, name));
            using (var source = open())
            using (var output = File.Create(target))
            {
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress(done, total);
                }
            }

            result.Add(target);
        }

        // „Solid" RAR a 7z jdou jen sekvenčně (po položkách by se každá rozbalovala od začátku),
        // zip a běžný RAR po položkách.
        if (archive.IsSolid || archive.Type == SharpCompress.Common.ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                Write(entry.Key, entry.IsDirectory, reader.OpenEntryStream);
            }
        }
        else
        {
            foreach (var entry in archive.Entries)
            {
                Write(entry.Key, entry.IsDirectory, entry.OpenEntryStream);
            }
        }

        return result;
    }

    /// <summary>Výsledek roztřídění.</summary>
    public sealed class OrganizeResult
    {
        public List<string> Moved { get; } = new();

        public List<string> Skipped { get; } = new();

        /// <summary>Videa, u kterých se nepodařilo poznat díl (zůstanou ve skryté složce .nerozpoznane — Jellyfin je nenačte jako divný díl).</summary>
        public List<string> Unrecognized { get; } = new();

        public string Summary()
        {
            var parts = new List<string> { $"{Moved.Count} {Word(Moved.Count, "soubor", "soubory", "souborů")} do knihovny" };
            if (Skipped.Count > 0)
            {
                parts.Add($"{Skipped.Count} už v knihovně");
            }

            if (Unrecognized.Count > 0)
            {
                parts.Add($"{Unrecognized.Count} bez označení dílu (skrytá složka .nerozpoznane u seriálu)");
            }

            return string.Join(", ", parts);
        }

        private static string Word(int n, string one, string few, string many) =>
            n == 1 ? one : n is >= 2 and <= 4 ? few : many;
    }

    /// <summary>
    /// Roztřídí rozbalené soubory podle položky fronty (cíl = seriál nebo film):
    /// seriál → každé video podle SxxEyy z názvu (řada chybí → `item.ArchiveSeason`),
    /// film → největší video. Titulky jdou k videu stejného dílu. Ve stejné kvalitě už
    /// stažené díly se přeskočí. Nerozpoznaná videa skončí ve skryté `.nerozpoznane` u seriálu (Jellyfin tečkové složky přeskakuje).
    /// </summary>
    public static OrganizeResult Organize(
        IReadOnlyList<string> extracted, QueueItem item, string moviesPath, string seriesPath, int tolerancePercent, bool overwrite)
    {
        var result = new OrganizeResult();
        var videos = new List<(string Path, FsFile Info)>();
        var subs = new List<(string Path, FsFile Info)>();
        foreach (var path in extracted)
        {
            var info = new FsFile { FileName = Path.GetFileName(path), SizeBytes = new FileInfo(path).Length };
            FileNameParser.Fill(info);
            if (info.IsVideo && !info.IsSample)
            {
                videos.Add((path, info));
            }
            else if (SubtitleExtensions.Contains(Path.GetExtension(path)))
            {
                subs.Add((path, info));
            }
        }

        if (item.MediaType == MediaKind.Movie)
        {
            var main = videos.OrderByDescending(v => v.Info.SizeBytes).FirstOrDefault();
            if (main.Path != null)
            {
                var target = ForFile(item, main.Info, null, null);
                Place(main.Path, target, moviesPath, seriesPath, tolerancePercent, overwrite, result);
                foreach (var s in subs)
                {
                    PlaceSubtitle(s.Path, result.Moved.LastOrDefault(), result);
                }
            }

            return result;
        }

        var placed = new Dictionary<(int, int), string>();
        foreach (var (path, info) in videos.OrderBy(v => v.Info.Season ?? item.ArchiveSeason ?? 1).ThenBy(v => v.Info.Episode))
        {
            if (info.Episode == null)
            {
                var dir = Path.Combine(seriesPath, MediaOrganizer.Sanitize(MediaOrganizer.CleanTitle(item.SeriesTitle ?? item.Title)), ".nerozpoznane");
                Directory.CreateDirectory(dir);
                var dest = MediaOrganizer.EnsureUniquePath(Path.Combine(dir, Path.GetFileName(path)));
                File.Move(path, dest);
                result.Unrecognized.Add(dest);
                continue;
            }

            var season = info.Season ?? item.ArchiveSeason ?? 1;
            var target = ForFile(item, info, season, info.Episode);
            if (Place(path, target, moviesPath, seriesPath, tolerancePercent, overwrite, result))
            {
                placed[(season, info.Episode.Value)] = result.Moved[^1];
            }
        }

        foreach (var (path, info) in subs)
        {
            if (info.Episode != null && placed.TryGetValue((info.Season ?? item.ArchiveSeason ?? 1, info.Episode.Value), out var video))
            {
                PlaceSubtitle(path, video, result);
            }
        }

        return result;
    }

    /// <summary>Položka pro jeden soubor z archivu (kvalita a jazyk z jeho názvu, jinak z archivu).</summary>
    private static QueueItem ForFile(QueueItem archive, FsFile file, int? season, int? episode) => new()
    {
        Title = archive.Title,
        SeriesTitle = archive.SeriesTitle,
        Year = archive.Year,
        MediaType = archive.MediaType,
        Season = season,
        Episode = episode,
        FileName = file.FileName,
        Quality = file.Quality ?? archive.Quality,
        Language = string.IsNullOrEmpty(file.LanguageLabel) ? archive.Language : file.LanguageLabel,
        SizeBytes = file.SizeBytes,
    };

    /// <summary>Přesune video na místo v knihovně. Vrací false, když už tam stejný díl je.</summary>
    private static bool Place(string source, QueueItem target, string moviesPath, string seriesPath, int tolerancePercent, bool overwrite, OrganizeResult result)
    {
        if (!overwrite && MediaOrganizer.FindDuplicate(moviesPath, seriesPath, target, tolerancePercent) is { } dup)
        {
            result.Skipped.Add(dup.Path);
            return false;
        }

        var ext = Path.GetExtension(source).ToLowerInvariant();
        var dest = MediaOrganizer.BuildTargetPath(moviesPath, seriesPath, target, ext);
        if (!overwrite)
        {
            dest = MediaOrganizer.EnsureUniquePath(dest);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(source, dest, overwrite);
        result.Moved.Add(dest);
        return true;
    }

    private static void PlaceSubtitle(string source, string? video, OrganizeResult result)
    {
        if (video == null)
        {
            return;
        }

        var dir = Path.GetDirectoryName(video)!;
        var baseName = Path.GetFileNameWithoutExtension(video);
        var dest = MediaOrganizer.EnsureUniquePath(Path.Combine(dir, baseName + Path.GetExtension(source).ToLower(CultureInfo.InvariantCulture)));
        File.Move(source, dest);
    }
}
