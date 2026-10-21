namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Práce se soubory v knihovně: která verze je lepší a bezpečné mazání. Mazat se smí jen soubor
/// uvnitř některé knihovny Jellyfinu, nikdy složka knihovny sama. Čistý C#, testuje se v Tests/.
/// </summary>
public static class LibraryFiles
{
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".nfo", ".jpg", ".jpeg", ".png", ".txt",
    };

    /// <summary>Kvalita z rozlišení videa (šířka chytá formát 2.40:1, „1920x800" = 1080p).</summary>
    public static string? QualityOf(int width, int height)
    {
        if (width <= 0 && height <= 0)
        {
            return null;
        }

        if (height >= 2000 || width >= 3500)
        {
            return "4K";
        }

        if (height >= 1000 || width >= 1800)
        {
            return "1080p";
        }

        if (height >= 700 || width >= 1200)
        {
            return "720p";
        }

        return "SD";
    }

    public static int Level(string? quality) =>
        quality != null && FileSelector.QualityLevels.TryGetValue(quality, out var l) ? l : 0;

    /// <summary>
    /// Seřadí verze od nejlepší: kvalita (SD &lt; 720p &lt; 1080p &lt; 4K), pak skutečné rozlišení
    /// (404p &gt; 360p), pak datový tok, pak velikost.
    /// </summary>
    public static List<LibraryVersion> BestFirst(IEnumerable<LibraryVersion> versions) =>
        versions
            .OrderByDescending(v => Level(v.Quality))
            .ThenByDescending(v => (long)v.Width * v.Height)
            .ThenByDescending(v => v.Bitrate ?? 0)
            .ThenByDescending(v => v.SizeBytes)
            .ToList();

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "a", "an", "of", "a", "v", "ve", "na", "se", "do", "z", "ze", "i", "u", "o", "cz", "en", "dabing", "dab", "tit", "film",
    };

    /// <summary>Mají dva názvy společné aspoň jedno významné slovo? („Thor Temný svět" × „Thor: Temný svět")</summary>
    public static bool TitlesOverlap(string? a, string? b)
    {
        static HashSet<string> Words(string? s) => FileNameParser.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !Stop.Contains(w) && !w.All(char.IsDigit))
            .ToHashSet();
        var wa = Words(a);
        var wb = Words(b);
        return wa.Count > 0 && wb.Count > 0 && wa.Overlaps(wb);
    }

    /// <summary>Doplní k verzi, co o sobě říká název souboru.</summary>
    public static FsFile Describe(LibraryVersion v)
    {
        var f = new FsFile { FileName = Path.GetFileName(v.Path) };
        FileNameParser.Fill(f);
        v.FileTitle = f.Title;
        v.FileYear = f.Year;
        v.FileSeason = f.Season;
        v.FileEpisode = f.Episode;
        return f;
    }

    /// <summary>
    /// Sedí soubor k filmu? Vrací důvod podezření, nebo null. Díl seriálu ve filmech, jiný rok
    /// (o víc než 1), nebo žádné společné slovo s názvem filmu ani se složkou, ve které leží.
    /// </summary>
    public static string? MovieSuspect(LibraryVersion v, string? name, string? originalTitle, int? year)
    {
        var f = Describe(v);
        if (f.Episode != null)
        {
            return $"soubor je podle názvu díl seriálu ({f.Title} S{f.Season:D2}E{f.Episode:D2})";
        }

        if (year is > 0 && f.Year is > 0 && Math.Abs(year.Value - f.Year.Value) > 1)
        {
            return $"podle názvu souboru je to {f.Title} ({f.Year}), ne film z roku {year}";
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(v.Path) ?? string.Empty);
        if (!TitlesOverlap(f.Title, name) && !TitlesOverlap(f.Title, originalTitle)
            && !TitlesOverlap(folder, name) && !TitlesOverlap(folder, originalTitle))
        {
            return $"název souboru („{f.Title}“) nesedí k filmu";
        }

        return null;
    }

    /// <summary>Sedí soubor k dílu? Název souboru říká jiné SxxEyy → důvod, jinak null.</summary>
    public static string? EpisodeSuspect(LibraryVersion v, int? season, int? episode)
    {
        var f = Describe(v);
        if (f.Episode != null && (f.Episode != episode || (f.Season != null && f.Season != season)))
        {
            return $"podle názvu souboru je to S{f.Season ?? season:D2}E{f.Episode:D2}, Jellyfin ho má jako S{season:D2}E{episode:D2}";
        }

        return null;
    }

    /// <summary>Označí verze, které se délkou liší od nejlepší o víc než 5 minut (prodloužená verze).</summary>
    public static void MarkLengths(List<LibraryVersion> bestFirst)
    {
        var best = bestFirst.FirstOrDefault()?.DurationSec;
        foreach (var v in bestFirst.Skip(1))
        {
            if (best is > 0 && v.DurationSec is > 0 && Math.Abs(v.DurationSec.Value - best.Value) > 300)
            {
                var diff = (v.DurationSec.Value - best.Value) / 60;
                v.LengthNote = diff > 0
                    ? $"o {diff} min delší než nejlepší verze — možná prodloužená verze"
                    : $"o {-diff} min kratší než nejlepší verze";
            }
        }
    }

    /// <summary>Které verze předem zaškrtnout ke smazání: horší, ne podezřelé a ne jiné délky.</summary>
    public static List<string> DefaultDelete(List<LibraryVersion> bestFirst) =>
        bestFirst.Skip(1).Where(v => v.Suspect == null && v.LengthNote == null).Select(v => v.Path).ToList();

    /// <summary>
    /// Přesune video (a jeho doprovodné soubory se stejným základem názvu) do `destDir` pod názvem
    /// `newBaseName` (bez přípony; null = původní). Obojí musí ležet v knihovnách. Vrací novou cestu.
    /// </summary>
    public static string MoveVideo(string path, string destDir, string? newBaseName, IReadOnlyCollection<string> libraryRoots)
    {
        if (!IsInsideLibrary(path, libraryRoots) || !IsInsideLibrary(Path.Combine(destDir, "x"), libraryRoots))
        {
            throw new InvalidOperationException("Přesouvat jde jen v rámci knihoven Jellyfinu: " + path + " → " + destDir);
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Soubor už neexistuje", path);
        }

        Directory.CreateDirectory(destDir);
        var dir = Path.GetDirectoryName(path)!;
        var oldBase = Path.GetFileNameWithoutExtension(path);
        var newBase = MediaOrganizer.Sanitize(string.IsNullOrWhiteSpace(newBaseName) ? oldBase : newBaseName);
        var target = MediaOrganizer.EnsureUniquePath(Path.Combine(destDir, newBase + Path.GetExtension(path)));
        var finalBase = Path.GetFileNameWithoutExtension(target);

        // doprovodné soubory (titulky „Film.cs.srt", „Film.nfo") nejdřív, ať se nepřejmenují pod video
        foreach (var f in Directory.EnumerateFiles(dir).ToList())
        {
            var name = Path.GetFileName(f);
            if (f != path && name.StartsWith(oldBase + ".", StringComparison.Ordinal) && SidecarExtensions.Contains(Path.GetExtension(f)))
            {
                var suffix = name[oldBase.Length..];
                File.Move(f, MediaOrganizer.EnsureUniquePath(Path.Combine(destDir, finalBase + suffix)));
            }
        }

        File.Move(path, target);
        return target;
    }

    /// <summary>Leží cesta uvnitř některé knihovny (a není to knihovna sama)?</summary>
    public static bool IsInsideLibrary(string path, IEnumerable<string> libraryRoots)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in libraryRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(r, StringComparison.Ordinal) && full.Length > r.Length)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Smaže video a jeho doprovodné soubory se stejným základem názvu („Film.cs.srt", „Film.nfo").
    /// Když po tom ve složce filmu (ne v knihovně samé) nezbyde žádné video, smaže i zbytek složky.
    /// Vrací smazané soubory. Mimo knihovnu nesmaže nic (výjimka).
    /// </summary>
    public static List<string> DeleteVideo(string path, IReadOnlyCollection<string> libraryRoots)
    {
        if (!IsInsideLibrary(path, libraryRoots))
        {
            throw new InvalidOperationException("Soubor není v žádné knihovně Jellyfinu, nemažu ho: " + path);
        }

        var deleted = new List<string>();
        if (!File.Exists(path))
        {
            return deleted;
        }

        var dir = Path.GetDirectoryName(path)!;
        var baseName = Path.GetFileNameWithoutExtension(path);
        File.Delete(path);
        deleted.Add(path);

        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith(baseName + ".", StringComparison.Ordinal) && SidecarExtensions.Contains(Path.GetExtension(f)))
            {
                File.Delete(f);
                deleted.Add(f);
            }
        }

        // Složka filmu („Film (2016)/") bez videa už nemá smysl — ale nikdy ne kořen knihovny
        var isRoot = libraryRoots.Any(r => string.Equals(
            Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal));
        if (!isRoot && Directory.Exists(dir) && !Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any(IsVideoFile))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                deleted.Add(f);
            }

            Directory.Delete(dir, true);
        }

        return deleted;
    }

    private static bool IsVideoFile(string path)
    {
        var f = new FsFile { FileName = Path.GetFileName(path) };
        FileNameParser.Fill(f);
        return f.IsVideo;
    }
}
