using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Vyčte z názvu souboru, co to je: název, rok, řadu a díl, jazyk zvuku a titulků,
/// kvalitu a kodek. Fastshare žádná metadata nemá a soubory pojmenovávají uživatelé
/// po svém („Avatar 2--CZ-dab. 2022.mp4", „Teorie velkého třesku_11x07.avi",
/// „BREAKING-BAD-HD-1080P-CZ-DABING+TITULKY-S02E03-BODNUTÍ-MRTVÉ-VČELY.mkv"),
/// takže všechno je odhad — proto se v GUI dá před zařazením opravit.
/// Čistý C#, žádná závislost na Jellyfin API (testuje se v Tests/).
/// </summary>
public static class FileNameParser
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z",
    };

    // Vícedílné archivy: „x.part2.rar", „x.r00" (= 2. díl po x.rar), „x.7z.001", „x.zip.001", „x.001"
    private static readonly Regex PartRar = new(@"^(?<base>.+\.part)(?<n>\d{1,3})\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OldRar = new(@"^(?<base>.+)\.r(?<n>\d{2})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^(?<base>.+?)(?:\.(?:7z|zip|rar))?\.(?<n>\d{3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".wmv", ".mov", ".mpg", ".mpeg", ".webm", ".flv", ".divx", ".ogm",
    };

    // S01E02, s1e2, S01 E02, S01.E02, S01E02E03 (vezme se první díl)
    private static readonly Regex SxxEyy = new(
        @"(?<![a-z0-9])s(?<s>\d{1,2})[ ._-]?e(?<e>\d{1,3})(?![0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 11x07, 1x02 (ne rozlišení 1920x1080 — díl max. 3 číslice a řada max. 2)
    private static readonly Regex NxNN = new(
        @"(?<![a-z0-9])(?<s>\d{1,2})x(?<e>\d{2,3})(?![0-9a-z])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Celá řada bez dílu: „9. série", „5 serie", „1.serie", „Season 3", „S02" (bez E)
    private static readonly Regex PackSeasonRe = new(
        @"(?<![a-z0-9])(?<s>\d{1,2})\s*\.?\s*(?:serie|series|season|sezona|rada)(?![a-z])"
        + @"|(?<![a-z0-9])(?:serie|series|season|sezona|rada)\s*\.?\s*(?<s>\d{1,2})(?![0-9])"
        + @"|(?<![a-z0-9])s(?<s>\d{1,2})(?![0-9]|\s*e\d|x\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Rok v závorce má přednost: „Matrix 1 (1999)"
    private static readonly Regex YearInParens = new(@"[\(\[](?<y>(19|20)\d{2})[\)\],]", RegexOptions.Compiled);

    private static readonly Regex YearToken = new(@"(?<![0-9])(?<y>(19|20)\d{2})(?![0-9p])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Resolution = new(@"(?<w>\d{3,4})\s*x\s*(?<h>\d{3,4})", RegexOptions.Compiled);

    private static readonly Regex Brackets = new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);

    private static readonly Regex Separators = new(@"[._\-+,;:!&@⭐]+", RegexOptions.Compiled);

    // Název s mezerami: pomlčky („Spider-Man", „Duna - Dune") zůstanou, zdvojené
    // („Avatar 2--CZ") a ostatní interpunkce jsou oddělovače.
    private static readonly Regex SeparatorsKeepHyphen = new(
        @"[._+,;:!&@⭐]+|-{2,}", RegexOptions.Compiled);

    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    // Tokeny, za kterými už nezačíná název, ale technické údaje
    private static readonly HashSet<string> MarkerTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "cz", "cze", "czech", "cesky", "czdab", "czdabing", "cztit", "cztitulky", "czsub", "czsubs",
        "sk", "skdab", "skdabing", "sktit", "slovensky", "en", "eng", "english", "entit",
        "dab", "dabing", "dabbing", "titulky", "tit", "subs", "sub",
        "2160p", "1080p", "720p", "576p", "480p", "4k", "uhd", "fullhd", "fhd", "hd", "sd", "hdr", "hdr10", "dv",
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "xvid", "divx",
        "bluray", "bdrip", "brrip", "brip", "webrip", "webdl", "web", "dvdrip", "hdtv", "hdrip", "remux", "tvrip",
        "aac", "ac3", "dts", "eac3", "atmos", "10bit", "8bit", "up",
    };

    /// <summary>Rozebere soubor z hledání Fastshare. `apiResolution` je pole „resolution" z API.</summary>
    public static void Fill(FsFile f)
    {
        var name = f.FileName ?? string.Empty;
        var ext = Path.GetExtension(name);
        if (ext.Length > 6 || ext.Contains(' '))
        {
            ext = string.Empty;
        }

        f.Extension = ext.ToLowerInvariant();
        f.IsVideo = VideoExtensions.Contains(f.Extension);
        DetectArchive(f, name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        var plain = RemoveDiacritics(stem).ToLowerInvariant();
        var tokens = Tokens(plain);
        var tokenSet = new HashSet<string>(tokens);

        // ── Díl ───────────────────────────────────────────────────
        var markerIndex = -1;
        var m = SxxEyy.Match(plain);
        if (!m.Success)
        {
            m = NxNN.Match(plain);
        }

        if (m.Success)
        {
            f.Season = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
            f.Episode = int.Parse(m.Groups["e"].Value, CultureInfo.InvariantCulture);
            markerIndex = m.Index;
        }
        else if (PackSeasonRe.Match(plain) is { Success: true } pm)
        {
            f.PackSeason = int.Parse(pm.Groups["s"].Value, CultureInfo.InvariantCulture);
            markerIndex = pm.Index;
        }

        // ── Rok ───────────────────────────────────────────────────
        var maxYear = DateTime.UtcNow.Year + 1;
        var ym = YearInParens.Match(plain);
        int? year = ym.Success ? int.Parse(ym.Groups["y"].Value, CultureInfo.InvariantCulture) : null;
        var yearIndex = ym.Success ? ym.Index : -1;
        if (year == null)
        {
            // poslední rok v názvu („Avatar 2 2022", „2023 - Spider-Man…" má jen jeden)
            foreach (Match y in YearToken.Matches(plain))
            {
                var v = int.Parse(y.Groups["y"].Value, CultureInfo.InvariantCulture);
                if (v is >= 1930 && v <= maxYear && !IsInsideResolution(plain, y.Index))
                {
                    year = v;
                    yearIndex = y.Index;
                }
            }
        }

        if (year is >= 1930 && year <= maxYear)
        {
            f.Year = year;
        }
        else
        {
            yearIndex = -1;
        }

        // ── Kvalita: rozlišení z API je změřené, název je až druhý zdroj ──
        f.Quality = QualityFromResolution(f.Resolution) ?? QualityFromTokens(tokenSet, plain);

        // ── Kodek a HDR ───────────────────────────────────────────
        if (tokenSet.Overlaps(["x265", "h265", "hevc"]))
        {
            f.Codec = "hevc";
        }
        else if (tokenSet.Overlaps(["x264", "h264", "avc"]))
        {
            f.Codec = "h264";
        }
        else if (tokenSet.Contains("av1"))
        {
            f.Codec = "av1";
        }

        f.Hdr = tokenSet.Overlaps(["hdr", "hdr10", "hdr10plus"]) || plain.Contains("hdr10+", StringComparison.Ordinal);
        f.Dv = tokenSet.Overlaps(["dv", "dovi"]) || plain.Contains("dolby vision", StringComparison.Ordinal);
        f.Atmos = tokenSet.Contains("atmos");

        // ── Jazyk ─────────────────────────────────────────────────
        ParseLanguages(tokens, f);

        // ── Ukázka ────────────────────────────────────────────────
        f.IsSample = tokenSet.Overlaps(["sample", "trailer", "ukazka", "teaser"]);

        // ── Název: text před prvním technickým údajem ─────────────
        f.Title = ExtractTitle(stem, plain, markerIndex, yearIndex);
    }

    /// <summary>Rozpozná archiv a díl vícedílného archivu.</summary>
    private static void DetectArchive(FsFile f, string name)
    {
        Match m;
        if ((m = PartRar.Match(name)).Success)
        {
            f.IsArchive = true;
            f.ArchivePart = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            f.ArchiveBase = m.Groups["base"].Value;
        }
        else if ((m = OldRar.Match(name)).Success)
        {
            // starý styl: x.rar je 1. díl, x.r00 druhý, x.r01 třetí…
            f.IsArchive = true;
            f.ArchivePart = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) + 2;
            f.ArchiveBase = m.Groups["base"].Value + ".rar";
        }
        else if ((m = Numbered.Match(name)).Success)
        {
            f.IsArchive = true;
            f.ArchivePart = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            f.ArchiveBase = m.Groups["base"].Value;
        }
        else if (ArchiveExtensions.Contains(Path.GetExtension(name)))
        {
            f.IsArchive = true;
            f.ArchiveBase = name;
        }
    }

    /// <summary>Normalizace pro porovnávání názvů: bez diakritiky, malá písmena, jen slova.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var plain = RemoveDiacritics(s).ToLowerInvariant();
        return string.Join(' ', Tokens(plain));
    }

    /// <summary>
    /// Patří soubor k hledanému titulu? Slova titulu musí v názvu souboru stát vedle sebe,
    /// u dílu PŘED označením SxxEyy — jinak „Tři mušketýři S01E01 Přátelé a nepřátelé"
    /// projde jako „Přátelé" a „S01E01 Lassie a přátelé" taky.
    /// </summary>
    public static bool MatchesTitle(FsFile f, string title)
    {
        var want = Normalize(title);
        if (want.Length == 0)
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(f.FileName ?? string.Empty);
        var plain = RemoveDiacritics(stem).ToLowerInvariant();
        if (f.Episode != null)
        {
            var m = SxxEyy.Match(plain);
            if (!m.Success)
            {
                m = NxNN.Match(plain);
            }

            if (m.Success)
            {
                plain = plain[..m.Index];
            }
        }

        // Název musí ZAČÍNAT některý úsek („Přátelé (Friends)", „Friends - Přátelé",
        // „Pernikovy.tata-Breaking.Bad"), ne stát uprostřed věty („Lassie a přátelé").
        // Pomlčka bez mezer („Spider-Man") úsek nedělí, pokud název mezery má.
        var splitter = plain.Contains(' ', StringComparison.Ordinal)
            ? @"\s-+\s|-{2,}|[\(\)\[\]\{\}/|,_]"
            : @"[-\(\)\[\]\{\}/|,_]";
        var wantTokens = want.Split(' ');
        foreach (var segment in Regex.Split(plain, splitter))
        {
            var tokens = Tokens(segment);
            var start = 0;

            // „1999 Pelíšky", „The Matrix" × „Matrix"
            while (start < tokens.Count && (YearToken.IsMatch(tokens[start]) && tokens[start].Length == 4
                       || tokens[start] is "the" or "a" or "an") && tokens[start] != wantTokens[0])
            {
                start++;
            }

            if (tokens.Count - start >= wantTokens.Length
                && tokens.Skip(start).Take(wantTokens.Length).SequenceEqual(wantTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static void ParseLanguages(List<string> tokens, FsFile f)
    {
        var audio = new List<string>();
        var subs = new List<string>();

        void AddAudio(string l)
        {
            if (!audio.Contains(l))
            {
                audio.Add(l);
            }
        }

        void AddSub(string l)
        {
            if (!subs.Contains(l))
            {
                subs.Add(l);
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var next = i + 1 < tokens.Count ? tokens[i + 1] : string.Empty;
            var nextIsSubs = next is "tit" or "titulky" or "sub" or "subs" or "titles";
            var nextIsDab = next is "dab" or "dabing" or "dabbing" or "audio";

            switch (t)
            {
                case "cz" or "cze" or "czech" or "cesky" or "cs":
                    if (nextIsSubs)
                    {
                        AddSub("CZ");
                        i++;
                    }
                    else
                    {
                        AddAudio("CZ");
                        if (nextIsDab)
                        {
                            i++;
                        }
                    }

                    break;
                case "czdab" or "czdabing" or "czaudio":
                    AddAudio("CZ");
                    break;
                case "cztit" or "cztitulky" or "czsub" or "czsubs" or "cztitles":
                    AddSub("CZ");
                    break;
                case "sk" or "slovensky" or "slo" or "svk":
                    if (nextIsSubs)
                    {
                        AddSub("SK");
                        i++;
                    }
                    else
                    {
                        AddAudio("SK");
                        if (nextIsDab)
                        {
                            i++;
                        }
                    }

                    break;
                case "skdab" or "skdabing":
                    AddAudio("SK");
                    break;
                case "sktit" or "sktitulky":
                    AddSub("SK");
                    break;
                case "en" or "eng" or "english" or "anglicky":
                    if (next is "tit" or "titulky")
                    {
                        // „Eng+titulky" = anglický zvuk s (českými) titulky
                        AddAudio("EN");
                        AddSub("CZ");
                        i++;
                    }
                    else if (nextIsSubs)
                    {
                        AddSub("EN");
                        i++;
                    }
                    else
                    {
                        AddAudio("EN");
                    }

                    break;
                case "entit" or "engsub" or "ensub" or "ensubs":
                    AddSub("EN");
                    break;

                // Samotný „dabing" na českém úložišti = český dabing
                case "dab" or "dabing" or "dabbing":
                    if (audio.Count == 0)
                    {
                        AddAudio("CZ");
                    }

                    break;

                // Samotné „titulky" = české titulky
                case "titulky" or "tit":
                    AddSub("CZ");
                    break;
            }
        }

        f.AudioLangs = audio;

        // Titulky ke stejnému jazyku, jako je dabing, nic nepřidávají („CZ dabing + titulky")
        f.SubtitleLangs = subs.Where(s => !audio.Contains(s) || audio.Count > 1).ToList();
    }

    private static string? QualityFromResolution(string? res)
    {
        if (string.IsNullOrWhiteSpace(res))
        {
            return null;
        }

        var r = res.Trim().ToLowerInvariant();
        if (r.EndsWith('p') && int.TryParse(r[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
        {
            return FromHeight(p, 0);
        }

        var m = Resolution.Match(r);
        if (m.Success)
        {
            var w = int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture);
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            return FromHeight(h, w);
        }

        return null;
    }

    /// <summary>Kvalita z výšky; šířka chytá filmy ve formátu 2.40:1 („1920x800" = 1080p).</summary>
    private static string? FromHeight(int h, int w)
    {
        if (h <= 0 && w <= 0)
        {
            return null;
        }

        if (h >= 2000 || w >= 3500)
        {
            return "4K";
        }

        if (h >= 1000 || w >= 1800)
        {
            return "1080p";
        }

        if (h >= 700 || w >= 1200)
        {
            return "720p";
        }

        return "SD";
    }

    private static string? QualityFromTokens(HashSet<string> t, string plain)
    {
        if (t.Overlaps(["2160p", "4k", "uhd"]))
        {
            return "4K";
        }

        if (t.Overlaps(["1080p", "1080i", "1080", "fullhd", "fhd"]) || plain.Contains("full hd", StringComparison.Ordinal))
        {
            return "1080p";
        }

        if (t.Overlaps(["720p", "720", "hd"]))
        {
            return "720p";
        }

        if (t.Overlaps(["480p", "576p", "sd", "dvdrip", "xvid", "divx", "tvrip"]))
        {
            return "SD";
        }

        return null;
    }

    private static string ExtractTitle(string stem, string plain, int markerIndex, int yearIndex)
    {
        // Konec názvu = nejdřívější z: označení dílu, rok, první technický token
        var cut = plain.Length;
        if (markerIndex >= 0)
        {
            cut = Math.Min(cut, markerIndex);
        }

        if (yearIndex >= 0)
        {
            // rok v závorce je kus za názvem; rok na úplném začátku („2023 - Spider-Man") ne
            if (yearIndex > 0)
            {
                cut = Math.Min(cut, yearIndex);
            }
        }

        // Technické tokeny PŘED prvním slovem názvu („(UHD) Matrix I.") se přeskočí
        var lead = 0;
        var seenWord = false;
        foreach (Match tm in Regex.Matches(plain, @"[a-z0-9]+"))
        {
            if (tm.Index >= cut)
            {
                break;
            }

            if (MarkerTokens.Contains(tm.Value))
            {
                if (seenWord)
                {
                    cut = tm.Index;
                    break;
                }

                lead = tm.Index + tm.Length;
                continue;
            }

            seenWord = true;
        }

        if (lead > 0 && lead < cut)
        {
            stem = new string(' ', lead) + stem[lead..];
        }

        // `plain` má stejnou délku jako `stem` (odstranění diakritiky zachová počet znaků),
        // takže řezat jde původní název s diakritikou.
        var raw = cut <= stem.Length ? stem[..cut] : stem;
        if (yearIndex == 0 && raw.Length < 6)
        {
            raw = stem;
        }

        raw = Brackets.Replace(raw, " ");
        raw = Regex.Replace(raw, @"[\(\)\[\]\{\}]", " ");

        // Název s mezerami má pomlčky skutečné („Spider-Man"); název bez mezer
        // („BREAKING-BAD-HD", „Pernikovy.tata-Breaking.Bad") je má jako oddělovače.
        raw = stem.Contains(' ', StringComparison.Ordinal)
            ? SeparatorsKeepHyphen.Replace(raw, " ")
            : Separators.Replace(raw, " ");
        raw = MultiSpace.Replace(raw, " ").Trim().Trim('-', '=', ' ');

        // dvojitá přípona („Fontána-pre-Zuzanu-3.avi.avi") → „… avi" z konce pryč
        raw = Regex.Replace(raw, @"\s+(avi|mkv|mp4|m4v|wmv|mpg|mpeg|divx)$", string.Empty, RegexOptions.IgnoreCase);

        // „2023 Spider Man Napric…": rok na začátku z názvu pryč
        raw = Regex.Replace(raw, @"^(19|20)\d{2}\s+", string.Empty).Trim('-', '=', ' ');
        if (raw.Length == 0)
        {
            raw = MultiSpace.Replace(Separators.Replace(stem, " "), " ").Trim();
        }

        return TitleCase(raw);
    }

    /// <summary>„BREAKING BAD" → „Breaking Bad"; smíšená velikost písmen zůstane, jak je.</summary>
    private static string TitleCase(string s)
    {
        if (s.Any(char.IsLower))
        {
            return char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];
        }

        return CultureInfo.GetCultureInfo("cs-CZ").TextInfo.ToTitleCase(s.ToLower(CultureInfo.GetCultureInfo("cs-CZ")));
    }

    private static bool IsInsideResolution(string plain, int index)
    {
        foreach (Match r in Resolution.Matches(plain))
        {
            if (index >= r.Index && index < r.Index + r.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> Tokens(string plain) =>
        Regex.Matches(plain, @"[a-z0-9]+").Select(x => x.Value).ToList();

    /// <summary>Odstraní diakritiku, ale zachová délku řetězce (á → a, ř → r).</summary>
    public static string RemoveDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var d = c.ToString().Normalize(NormalizationForm.FormD);
            var baseChar = d.FirstOrDefault(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);
            sb.Append(d.Length > 0 && baseChar != default ? baseChar : c);
        }

        return sb.ToString();
    }
}
