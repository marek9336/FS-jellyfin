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
