using System.Globalization;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Co hledáme: film (název + rok), nebo díl seriálu (název + řada + díl).</summary>
public sealed class SelectTarget
{
    public MediaKind Kind { get; set; } = MediaKind.Movie;

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>Výraz pro hledání na Fastshare: „Název 1999" / „Název S01E02".</summary>
    public string SearchTerm() => Kind == MediaKind.Episode
        ? string.Create(CultureInfo.InvariantCulture, $"{Title} S{Season ?? 1:D2}E{Episode ?? 1:D2}")
        : Year is > 0 ? string.Create(CultureInfo.InvariantCulture, $"{Title} {Year}") : Title;
}

/// <summary>Preference pro automatický výběr (z nastavení pluginu).</summary>
public sealed class FileSelectorOptions
{
    public string Lang1 { get; set; } = "CZ";

    public string Lang2 { get; set; } = "SK";

    public string Lang3 { get; set; } = "EN";

    /// <summary>Soubor bez preferovaného jazyka (a bez titulků v něm) vůbec nebrat.</summary>
    public bool SkipWithoutPreferredLang { get; set; }

    /// <summary>Soubor bez údaje o jazyku v názvu brát jako… (body jako jazyk 1–3; 0 = jako cizí).</summary>
    public int UnknownLangScore { get; set; } = 120;

    /// <summary>SD/720p/1080p/4K, „-" = bez limitu.</summary>
    public string MaxQuality { get; set; } = "4K";

    /// <summary>Max. velikost filmu v GB (0 = bez limitu).</summary>
    public int MaxMovieSizeGb { get; set; } = 30;

    /// <summary>Max. velikost dílu seriálu v GB (0 = bez limitu) — zvlášť od filmu.</summary>
    public int MaxEpisodeSizeGb { get; set; } = 5;

    public int MaxBitrateMbps { get; set; }

    public string CodecPreference { get; set; } = "hevc|h264|av1";

    /// <summary>"prefer" | "ignore" | "avoid".</summary>
    public string HdrMode { get; set; } = "ignore";

    public string DvMode { get; set; } = "avoid";

    public bool PreferSubsWhenForeign { get; set; } = true;
}

/// <summary>Jeden kandidát s hodnocením (pro GUI „proč zrovna tenhle").</summary>
public sealed class RankedFile
{
    public FsFile File { get; set; } = new();

    public double Score { get; set; }

    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Vybere z výsledků hledání soubory, které opravdu patří k hledanému filmu/dílu,
/// a seřadí je podle preferencí. Jazyk dominuje (CZ dabing přebije lepší kvalitu),
/// tvrdé limity (kvalita, velikost, bitrate) soubor vyřadí, kodek a HDR jsou jemné
/// preference. Váhy jako v pluginu Stream Cinema.
/// </summary>
public static class FileSelector
{
    public static readonly Dictionary<string, int> QualityLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SD"] = 1,
        ["720p"] = 2,
        ["1080p"] = 3,
        ["4K"] = 4,
        ["8K"] = 5,
    };

    private const int Lang1Weight = 300;
    private const int Lang2Weight = 200;
    private const int Lang3Weight = 100;
    private const int SubsBonus = 70;

    // Menší soubory jsou ukázky, upoutávky nebo rozbité kousky
    private const long MinMovieBytes = 300L * 1024 * 1024;
    private const long MinEpisodeBytes = 40L * 1024 * 1024;

    /// <summary>Patří soubor k cíli? (název, rok, díl, video, ne ukázka)</summary>
    public static bool Matches(FsFile f, SelectTarget t, out string why)
    {
        why = string.Empty;
        if (!f.IsVideo)
        {
            why = "není video";
            return false;
        }

        if (f.IsSample)
        {
            why = "ukázka";
            return false;
        }

        if (!FileNameParser.MatchesTitle(f, t.Title))
        {
            why = "jiný název";
            return false;
        }

        if (t.Kind == MediaKind.Episode)
        {
            if (f.Season != (t.Season ?? 1) || f.Episode != (t.Episode ?? 1))
            {
                why = "jiný díl";
                return false;
            }

            if (f.SizeBytes is > 0 and < MinEpisodeBytes)
            {
                why = "moc malý";
                return false;
            }

            return true;
        }

        if (f.Episode != null)
        {
            why = "díl seriálu";
            return false;
        }

        // Rok: když ho známe na obou stranách, musí sedět (±1 — premiéry na přelomu roku)
        if (t.Year is > 0 && f.Year is > 0 && Math.Abs(t.Year.Value - f.Year.Value) > 1)
        {
            why = "jiný rok";
            return false;
        }

        if (f.SizeBytes is > 0 and < MinMovieBytes)
        {
            why = "moc malý";
            return false;
        }

        return true;
    }

    /// <summary>Seřazení kandidáti (nejlepší první). Vyřazené nevrací.</summary>
    public static List<RankedFile> Rank(IEnumerable<FsFile> files, SelectTarget t, FileSelectorOptions o)
    {
        var maxLevel = QualityLevels.TryGetValue(o.MaxQuality ?? string.Empty, out var ml) ? ml : int.MaxValue;
        var maxGb = t.Kind == MediaKind.Episode ? o.MaxEpisodeSizeGb : o.MaxMovieSizeGb;
        var maxBytes = maxGb > 0 ? (long)maxGb * 1024 * 1024 * 1024 : long.MaxValue;
        var codecPrefs = (o.CodecPreference ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => c.ToLowerInvariant())
            .ToList();

        var result = new List<RankedFile>();
        foreach (var f in files)
        {
            if (!Matches(f, t, out _))
            {
                continue;
            }

            var level = QualityLevels.TryGetValue(f.Quality ?? string.Empty, out var l) ? l : 0;
            if (level > maxLevel || (f.SizeBytes > 0 && f.SizeBytes > maxBytes))
            {
                continue;
            }

            if (o.MaxBitrateMbps > 0 && f.Bitrate is > 0 && f.Bitrate.Value > (long)o.MaxBitrateMbps * 1_000_000)
            {
                continue;
            }

            var langScore = LangScore(f, o, out var langWhy);
            var subs = o.PreferSubsWhenForeign && langScore < Lang1Weight && HasSubs(f, o.Lang1);
            if (langScore == 0 && !subs && o.SkipWithoutPreferredLang)
            {
                continue;
            }

            double score = langScore + (subs ? SubsBonus : 0) + (level * 10);
            if (f.Bitrate is > 0)
            {
                score += Math.Min(f.Bitrate.Value / 1e6, 60) / 6;
            }

            if (f.Codec != null)
            {
                var idx = codecPrefs.IndexOf(f.Codec);
                if (idx >= 0)
                {
                    score += Math.Max(1, 5 - idx);
                }
            }

            score += ModeScore(o.HdrMode, f.Hdr) + ModeScore(o.DvMode, f.Dv);

            // Přesný rok je jistější než ±1 nebo žádný
            if (t.Year is > 0 && f.Year == t.Year)
            {
                score += 5;
            }

            var parts = new List<string> { langWhy };
            if (subs)
            {
                parts.Add(o.Lang1 + " titulky");
            }

            parts.Add(f.Quality ?? "kvalita ?");
            parts.Add(Dedup.FormatSize(f.SizeBytes));
            if (f.Codec != null)
            {
                parts.Add(f.Codec.ToUpperInvariant());
            }

            if (f.Hdr)
            {
                parts.Add("HDR");
            }

            result.Add(new RankedFile
            {
                File = f,
                Score = score,
                Reason = string.Join(" · ", parts) + string.Create(CultureInfo.InvariantCulture, $" (skóre {score:F0})"),
            });
        }

        // Při shodě skóre větší soubor (lepší encode), pak novější nahrání
        return result
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.File.SizeBytes)
            .ThenByDescending(r => r.File.UploadedDate, StringComparer.Ordinal)
            .ToList();
    }

    private static int LangScore(FsFile f, FileSelectorOptions o, out string why)
    {
        if (f.AudioLangs.Count == 0)
        {
            why = "jazyk neuveden";
            return Math.Max(0, o.UnknownLangScore);
        }

        why = string.Join(",", f.AudioLangs);
        if (Has(f, o.Lang1))
        {
            return Lang1Weight;
        }

        if (Has(f, o.Lang2))
        {
            return Lang2Weight;
        }

        if (Has(f, o.Lang3))
        {
            return Lang3Weight;
        }

        return 0;

        static bool Has(FsFile f, string lang) =>
            !string.IsNullOrWhiteSpace(lang) && f.AudioLangs.Contains(Norm(lang));
    }

    private static bool HasSubs(FsFile f, string lang) =>
        !string.IsNullOrWhiteSpace(lang) && f.SubtitleLangs.Contains(Norm(lang));

    private static string Norm(string lang)
    {
        var l = lang.Trim().ToUpperInvariant();
        return l == "CS" ? "CZ" : l;
    }

    private static double ModeScore(string mode, bool has) => mode switch
    {
        "prefer" when has => 15,
        "avoid" when has => -40,
        _ => 0,
    };
}
