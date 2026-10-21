using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Odhad řady a dílu u souboru, který Jellyfin neočísloval.</summary>
public sealed record EpisodeGuessResult(int Season, int Episode, string How, string? Name, bool Sure);

/// <summary>
/// Odhad čísla dílu z názvů, kterým Jellyfin nerozumí: „comeback-22-koprovka", „Po stopách duchů XIII (9)",
/// „vteriny-pred-katastrofou-iii-09", „Highschool_DxD_NeW_03", „…#66", „Bylo nás pět (6)", „pitaval z velkého města09".
/// Řada ze souboru (římská číslice), ze složky („2. série", „Season 3"), jinak 1. Když řada neurčená a díl
/// je větší než počet dílů 1. řady, bere se číslo od začátku seriálu („Comeback-45" → S02E19).
/// Odhad je „jistý", jen když TMDb takový díl zná. Čistý C#, testuje se v Tests/.
/// </summary>
public static class EpisodeGuess
{
    private static readonly Regex RomanNumber = new(
        @"(?<![a-z0-9])(?<r>[ivx]{1,6})\s+(?<e>\d{1,3})(?![0-9])", RegexOptions.Compiled);

    private static readonly Regex FolderSeason = new(
        @"(?<![a-z])(?:season|serie|rada|s)\s*0*(?<s>\d{1,2})(?![0-9])|(?<![0-9])(?<s>\d{1,2})\s*(?:\.\s*)?(?:serie|rada|season)(?![a-z])", RegexOptions.Compiled);

    // slova, která v názvu souboru o díle nic neříkají
    private static readonly HashSet<string> Junk = new(StringComparer.Ordinal)
    {
        "cz", "sk", "en", "dabing", "dab", "tit", "titulky", "titl", "dvdrip", "bdrip", "brrip", "webrip", "web", "hdtv", "xvid", "divx",
        "avi", "mkv", "mp4", "hd", "sd", "fullhd", "dvb", "lovok", "prima", "zoom", "nova", "ct", "the", "and", "serie", "season", "dil",
        "epizoda", "cast", "novinka", "x264", "x265", "h264", "h265", "hevc", "aac", "ac3", "mp3", "new",
    };

    // „E8S11" (obráceně)
    private static readonly Regex EpisodeSeason = new(@"(?<![a-z0-9])e(?<e>\d{1,3})\s*s(?<s>\d{1,2})(?![0-9])", RegexOptions.Compiled);

    // „Díl 12. epizoda" / „Episode 5" — TMDb bez skutečného názvu dílu
    private static readonly Regex GenericName = new(@"^\s*(\d+\.\s*(epizoda|dil|díl)|(episode|epizoda|díl|dil)\s*\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> Channels = new(StringComparer.Ordinal) { "prima", "nova", "ct", "joj", "markiza", "zoom" };

    private static readonly HashSet<int> TechNumbers = new() { 264, 265, 360, 480, 576, 720 };

    public static int? Roman(string s)
    {
        var map = new Dictionary<char, int> { ['i'] = 1, ['v'] = 5, ['x'] = 10 };
        if (s.Length == 0 || s.Any(c => !map.ContainsKey(c)))
        {
            return null;
        }

        var total = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (!map.TryGetValue(s[i], out var v))
            {
                return null;
            }

            total += i + 1 < s.Length && map[s[i + 1]] > v ? -v : v;
        }

        // jen kanonický zápis (iiii ani vx není číslo)
        return total is > 0 and <= 39 && ToRoman(total) == s ? total : null;
    }

    private static string ToRoman(int n)
    {
        var parts = new[] { (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") };
        var sb = new System.Text.StringBuilder();
        foreach (var (v, r) in parts)
        {
            while (n >= v)
            {
                sb.Append(r);
                n -= v;
            }
        }

        return sb.ToString();
    }

    /// <summary>Text bez diakritiky, malými písmeny, oddělovače jako mezery; `split`: „mesta09" → „mesta 09".</summary>
    private static string Plain(string s, bool split = true)
    {
        var p = FileNameParser.RemoveDiacritics(s).ToLowerInvariant();
        p = Regex.Replace(p, @"[^a-z0-9]+", " ");
        if (split)
        {
            p = Regex.Replace(p, @"(?<=[a-z])(?=\d)|(?<=\d)(?=[a-z])", " ");
        }

        return Regex.Replace(p, @"\s+", " ").Trim();
    }

    private static HashSet<string> Words(string s, HashSet<string> exclude) =>
        Plain(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !w.All(char.IsDigit) && !Junk.Contains(w) && !exclude.Contains(w) && Roman(w) == null)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Díl podle názvu („S Homerem přijde zákon.avi", „comeback-22-koprovka"): slova názvu dílu z TMDb musí být
    /// v názvu souboru (aspoň 60 %, u jednoslovných celé). Víc stejně dobrých shod = nic.
    /// </summary>
    private static ((int Season, int Episode) Key, double Score)? ByName(HashSet<string> fileWords, IReadOnlyDictionary<(int Season, int Episode), string?> known, HashSet<string> titleWords)
    {
        if (fileWords.Count == 0)
        {
            return null;
        }

        var scored = known
            .Where(k => k.Key.Season > 0 && !string.IsNullOrWhiteSpace(k.Value) && !GenericName.IsMatch(k.Value!))
            .Select(k =>
            {
                var w = Words(k.Value!, titleWords);
                return (k.Key, Score: w.Count == 0 ? 0 : (double)w.Count(fileWords.Contains) / w.Count);
            })
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ToList();
        if (scored.Count == 0 || (scored.Count > 1 && scored[1].Score >= scored[0].Score))
        {
            return null;
        }

        return (scored[0].Key, scored[0].Score);
    }

    /// <summary>Řada podle složky („Highschool DxD New - 2. série", „Season 03", „Série 1"), jinak null.</summary>
    public static int? SeasonFromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var m = FolderSeason.Match(Plain(folder));
        return m.Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <param name="path">Cesta k souboru.</param>
    /// <param name="titles">Název seriálu (a původní název) — odstraní se, ať se jejich čísla nepletou („Havaj 5-0").</param>
    /// <param name="known">Díly, které TMDb / knihovna zná: (řada, díl) → název dílu.</param>
    public static EpisodeGuessResult? Guess(string path, IEnumerable<string?> titles, IReadOnlyDictionary<(int Season, int Episode), string?> known)
    {
        var fileName = Path.GetFileName(path);
        var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
        var folderSeason = SeasonFromFolder(folder);
        int? season = null;
        int? episode = null;
        string how;

        var f = new FsFile { FileName = fileName };
        FileNameParser.Fill(f);
        // délka „71'" (minuty) není číslo dílu
        var rawStem = Regex.Replace(Path.GetFileNameWithoutExtension(fileName), @"\d+\s*'", " ");
        var stem = Plain(rawStem);
        var es = EpisodeSeason.Match(Plain(rawStem, split: false));
        if (f.Episode != null)
        {
            season = f.Season;
            episode = f.Episode;
            how = "z označení řady a dílu v názvu";
        }
        else if (es.Success)
        {
            season = int.Parse(es.Groups["s"].Value, CultureInfo.InvariantCulture);
            episode = int.Parse(es.Groups["e"].Value, CultureInfo.InvariantCulture);
            how = $"z „{es.Value}“";
        }
        else if (RomanNumber.Matches(Plain(rawStem, split: false)).Select(m => (m, r: Roman(m.Groups["r"].Value))).FirstOrDefault(x => x.r != null) is { m: not null } rm)
        {
            season = rm.r;
            episode = int.Parse(rm.m.Groups["e"].Value, CultureInfo.InvariantCulture);
            how = $"z „{rm.m.Value}“ (řada římsky)";
        }
        else if (FileNameParser.LoneEpisode(fileName) is { } lone)
        {
            episode = lone;
            how = "z čísla dílu v názvu";
        }
        else
        {
            // první samostatné číslo po odstranění názvu seriálu
            var titleWords = titles.Where(t => !string.IsNullOrWhiteSpace(t)).SelectMany(t => Plain(t!).Split(' ')).ToHashSet(StringComparer.Ordinal);
            var words = stem.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var number = words
                .Select((w, i) => (w, next: i + 1 < words.Length ? words[i + 1] : string.Empty))
                .Where(x => !titleWords.Contains(x.w) && x.w.Length <= 3 && x.w.All(char.IsDigit) && !Channels.Contains(x.next)) // „[9 Prima ZOOM]"
                .Select(x => int.Parse(x.w, CultureInfo.InvariantCulture))
                .FirstOrDefault(n => n is > 0 and <= 999 && !TechNumbers.Contains(n));
            episode = number == 0 ? null : number;
            how = $"z čísla {number} v názvu";
        }

        var tWords = titles.Where(t => !string.IsNullOrWhiteSpace(t)).SelectMany(t => Plain(t!).Split(' ')).ToHashSet(StringComparer.Ordinal);
        var fileWords = Words(Path.GetFileNameWithoutExtension(fileName), tWords);
        var byName = ByName(fileWords, known, tWords);
        if (episode is null or <= 0)
        {
            return byName is { } bn
                ? new EpisodeGuessResult(bn.Key.Season, bn.Key.Episode, "podle názvu dílu", known[bn.Key], bn.Score >= 0.99)
                : null;
        }

        season ??= folderSeason;
        if (season == null)
        {
            // řada neurčená: číslo dílu v 1. řadě, nebo pořadí od začátku seriálu (Comeback-45)
            var first = known.Keys.Count(k => k.Season == 1);
            var ordered = known.Keys.Where(k => k.Season > 0).OrderBy(k => k.Season).ThenBy(k => k.Episode).ToList();
            if (first > 0 && episode > first && episode <= ordered.Count)
            {
                var abs = ordered[episode.Value - 1];
                how += $", počítáno od začátku seriálu ({episode}. díl)";
                season = abs.Season;
                episode = abs.Episode;
            }
            else
            {
                season = 1;
            }
        }

        var key = (season.Value, episode.Value);
        if (byName is { } m && m.Key != key)
        {
            // číslo a název si odporují („Chlapec, který věděl příliš mnoho #66" je S05E20) → věřit názvu
            return new EpisodeGuessResult(m.Key.Season, m.Key.Episode, $"podle názvu dílu (číslo v souboru ukazovalo na S{season:D2}E{episode:D2})", known[m.Key], false);
        }

        var exists = known.TryGetValue(key, out var name);
        if (name != null && GenericName.IsMatch(name))
        {
            name = null; // „10. epizoda" není název, nemůže odporovat
        }

        // jistý: TMDb díl zná a název souboru mu neodporuje (buď sedí, nebo v souboru žádný název dílu není)
        var sure = exists && (byName != null || fileWords.Count == 0 || string.IsNullOrWhiteSpace(name));
        return new EpisodeGuessResult(season.Value, episode.Value, how + (byName != null ? " a sedí název dílu" : string.Empty), name, sure);
    }
}
