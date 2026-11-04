using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Skladba z hudební knihovny Jellyfinu (jen to, co hudební kontrola potřebuje).</summary>
public sealed class MusicTrack
{
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>Datový tok v bitech/s (z Jellyfinu, jinak velikost / délka).</summary>
    public long Bitrate { get; set; }

    public int DurationSec { get; set; }

    /// <summary>Název v Jellyfinu — bez tagu je to jméno souboru.</summary>
    public string Title { get; set; } = string.Empty;

    public List<string> Artists { get; set; } = new();

    public string? AlbumArtist { get; set; }

    public string? Album { get; set; }

    public int? Year { get; set; }

    public int? TrackNo { get; set; }

    /// <summary>Otisk obsahu (velikost + začátek a konec souboru) — jen u souborů, které mají stejnou velikost jako jiný.</summary>
    public string? ContentKey { get; set; }
}

/// <summary>Navržené tagy skladby (z cesty a jména souboru, opravené diakritiky, později z MusicBrainz).</summary>
public sealed class MusicTags
{
    public string Path { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Artist { get; set; }

    public string? Album { get; set; }

    public int? Year { get; set; }

    public int? TrackNo { get; set; }

    /// <summary>Odhad je jistý (název i interpret vyčtené jednoznačně) — jde do hromadného použití.</summary>
    public bool Sure { get; set; }

    /// <summary>Odkud odhad je („z názvu souboru a složky“, „oprava diakritiky“).</summary>
    public string? How { get; set; }
}

/// <summary>Návrh v záložce Hudba.</summary>
public sealed class MusicSuggestion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"same" (bajtově stejné soubory), "song" (stejná skladba), "junk" (smetí), "low" (nízký tok), "meta" (bez tagů), "charset" (rozbitá diakritika).</summary>
    public string Kind { get; set; } = "song";

    /// <summary>Nadpis karty („Metallica – Enter Sandman“, „Harlej - University (2008)“).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Skladby, nejlepší první.</summary>
    public List<MusicTrack> Tracks { get; set; } = new();

    /// <summary>Navržené ke smazání (předem zaškrtnuté).</summary>
    public List<string> DeletePaths { get; set; } = new();

    /// <summary>Navržené tagy (meta, charset).</summary>
    public List<MusicTags> Tags { get; set; } = new();

    /// <summary>Stejná skladba na různých albech (výběrovka × původní album) — jen pro informaci, nic se nezaškrtává.</summary>
    public bool CrossAlbum { get; set; }

    /// <summary>"new" | "done" | "rejected".</summary>
    public string Status { get; set; } = "new";

    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedUtc { get; set; }

    /// <summary>Kolik místa uvolní smazání předem zaškrtnutých.</summary>
    public long SavingsBytes => Tracks.Where(t => DeletePaths.Contains(t.Path)).Sum(t => t.SizeBytes);
}

/// <summary>Odhad tagů z cesty a jména souboru. Čistý C#.</summary>
public static class MusicGuess
{
    private static readonly Regex DiscTrack = new(@"^(?<d>\d)\s+(?<t>\d{2})\s*[-._)]*\s+(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex LeadTrack = new(@"^(?<t>\d{1,3})\s*(?:[-._)]+\s*|\s+)(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex GenericKey = new(@"^\d*(?:track|stopa|skladba|audiotrack|titel|piste)(?:no)?\d*[a-z]?$", RegexOptions.Compiled);
    private static readonly Regex YearParen = new(@"\s*[\(\[]\s*(?<y>(?:19|20)\d{2})\s*[\)\]]\s*", RegexOptions.Compiled);
    private static readonly Regex YearLead = new(@"^\s*(?<y>(?:19|20)\d{2})\s*[-_.]?\s+(?<rest>.+)$", RegexOptions.Compiled);
    private static readonly Regex YearTail = new(@"\s*[-–]\s*(?<y>(?:19|20)\d{2})\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberedFolder = new(@"^\d{1,3}\s*\.\s*", RegexOptions.Compiled);
    private static readonly Regex Junk = new(
        @"\s*[\(\[\{](?:official\s*(?:music\s*)?video|official\s*audio|lyrics?|hd|hq|\d{3}\s*kbps|www\.[^\)\]\}]+|by[-_ ][^\)\]\}]+|cel[eé][-_ ]album|full[-_ ]album)[\)\]\}]|!{2,}|\s*-?(?:cel[eé]|full)[-_ ]album\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly CultureInfo Cs = CultureInfo.GetCultureInfo("cs-CZ");

    /// <summary>Tag, který nic neříká (Windows Media Player, prázdné CD).</summary>
    public static bool IsJunkTag(string? s) =>
        string.IsNullOrWhiteSpace(s)
        || Regex.IsMatch(s, @"(?i)^\s*(?:nezn[aá]m[eéý](?:\s+(?:album|interpret|um[eě]lec))?|unknown(?:\s+(?:album|artist))?|no\s+(?:artist|title|album)|various(?:\s+artists)?|track\s*\d*|audiotrack\s*\d*)\b");

    /// <summary>Jméno souboru je jen „Track 04“ / „03 Track“ / „Track No04“ / „Stopa 9“ — název skladby z něj nejde vyčíst.</summary>
    public static bool IsGenericName(string? stem) => stem != null && GenericKey.IsMatch(Norm(stem));

    /// <summary>Nemá skladba tagy? Bez interpreta, nebo název = jméno souboru (Jellyfin ho vezme, když chybí tag).</summary>
    public static bool LacksTags(MusicTrack t) =>
        (t.Artists.All(IsJunkTag) && IsJunkTag(t.AlbumArtist))
        || string.Equals(t.Title.Trim(), System.IO.Path.GetFileNameWithoutExtension(t.Path).Trim(), StringComparison.OrdinalIgnoreCase)
        || IsGenericName(t.Title);

    /// <summary>
    /// Tagy z cesty: „Rock/7. Metallica/Metallica - Kill 'em All/03 - Whiplash.mp3“, „Harlej - University (2008)/01 - Malá nevěrná holka.mp3“,
    /// „MP 3/FLYING STEPS - We Gonna Rock It.mp3“. `root` = kořen hudební knihovny (složky 1. úrovně pod ním jsou žánry/sbírky,
    /// ne interpreti). Ze stávajících tagů se bere jen interpret, album a rok, když dávají smysl.
    /// </summary>
    public static MusicTags Guess(MusicTrack t, string root)
    {
        var path = t.Path;
        var rel = System.IO.Path.GetRelativePath(root, path);
        var parts = rel.Split(System.IO.Path.DirectorySeparatorChar);
        var stem = Clean(System.IO.Path.GetFileNameWithoutExtension(path));
        var dirs = parts.Take(parts.Length - 1).ToList(); // složky pod kořenem
        var g = new MusicTags { Path = path };

        // ── složka: album, rok, případně interpret ──
        string? folderArtist = null;
        var folderPattern = false;
        string? album = null;
        int? year = null;
        if (dirs.Count >= 1 && Regex.IsMatch(dirs[^1], @"(?i)^(?:cd|disc|disk)\s*\d+$") && dirs.Count >= 2)
        {
            dirs = dirs.Take(dirs.Count - 1).ToList(); // „…/Splitting The DNA - 2001/CD1“
        }

        if (dirs.Count >= 1)
        {
            var dir = Clean(dirs[^1]);
            dir = Regex.Replace(dir, @"(?i)[\s_-]*[\(\[]?\s*(?:cd|disc|disk)\s*\d*\s*[\)\]]?\s*$", string.Empty).Trim();
            (dir, year) = TakeYear(dir);
            var dash = SplitDash(dir);
            if (dash.Count >= 2)
            {
                folderPattern = true; // „Interpret - Album“
                folderArtist = NumberedFolder.Replace(dash[0], string.Empty);
                album = string.Join(" - ", dash.Skip(1));
            }
            else
            {
                album = dir;
                // složka 1. úrovně pod kořenem = žánr / sbírka („Rock“, „hudba“, „Kosna“), ne interpret
                if (dirs.Count >= 3)
                {
                    folderArtist = NumberedFolder.Replace(Clean(dirs[^2]), string.Empty).Trim();
                }
            }

            album = NumberedFolder.Replace(album, string.Empty).Trim();
        }

        // ── jméno souboru: disk/číslo, interpret, název ──
        int? track = null;
        var rest = stem;
        var dt = DiscTrack.Match(rest);
        if (dt.Success)
        {
            track = int.Parse(dt.Groups["t"].Value, CultureInfo.InvariantCulture);
            rest = dt.Groups["rest"].Value;
        }
        else if (LeadTrack.Match(rest) is { Success: true } lt)
        {
            var n = int.Parse(lt.Groups["t"].Value, CultureInfo.InvariantCulture);
            if (n is > 0 and < 1000)
            {
                track = n < 200 ? n : n % 100; // „205.Enter Sandman“ = CD2 / 05
                rest = lt.Groups["rest"].Value;
            }
        }

        var segs = SplitDash(rest).Where(x => !Regex.IsMatch(x, @"^\d{1,3}$") || Take(ref track, x)).ToList();
        string? title = null;
        string? fileArtist = null;
        if (segs.Count >= 2 && !(track != null && folderArtist != null && !Same(segs[0], folderArtist) && dirs.Count >= 1 && LooksLikeAlbumTrack(segs)))
        {
            fileArtist = segs[0];
            title = string.Join(" - ", segs.Skip(1));
        }
        else if (segs.Count >= 1)
        {
            title = string.Join(" - ", segs);
        }

        if (title != null && IsGenericName(title))
        {
            track ??= GenericNumber(title);
            title = null;
        }

        if (title != null)
        {
            // „System Of A Down - Track  2“, „Kings Of Metal - 09 - Track 9“: poslední část je jen číslo stopy
            var last = SplitDash(title);
            if (last.Count >= 2 && IsGenericName(last[^1]))
            {
                track ??= GenericNumber(last[^1]);
                title = null;
            }

            if (title != null && Regex.Match(title, @"^(?<t>\d{1,3})\s*(?<rest>\D.*)$") is { Success: true } m && fileArtist != null)
            {
                track ??= int.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture); // „Iné Kafe - 01 Vitaj!“
                title = m.Groups["rest"].Value.Trim(' ', '.', '-');
            }
        }

        var tagArtist = t.Artists.Concat(new[] { t.AlbumArtist }).FirstOrDefault(a => !IsJunkTag(a) && !Same(a!, stem) && (title == null || !Same(a!, title)));
        var artist = tagArtist ?? fileArtist ?? folderArtist;
        g.Artist = Pretty(artist);
        g.Title = Pretty(title);
        g.Album = !IsJunkTag(t.Album) && !Same(t.Album!, stem) ? t.Album : Pretty(album);
        g.Year = year ?? t.Year;
        g.TrackNo = track ?? t.TrackNo;

        // jisté: název ze souboru a interpret z tagu, z „Interpret - Název“ v souboru, nebo souhlasí soubor se složkou
        var agree = fileArtist != null && folderArtist != null && Same(fileArtist, folderArtist);
        g.Sure = g.Title != null && g.Artist != null && (tagArtist != null || fileArtist != null || folderPattern && track != null);
        g.How = g.Title == null
            ? "z názvu souboru nejde vyčíst název skladby — pozná ji až podle zvuku"
            : tagArtist != null ? "název z jména souboru, interpret z tagu"
            : agree ? "interpret v názvu souboru i ve složce"
            : fileArtist != null ? "„Interpret - Název“ v názvu souboru"
            : folderPattern ? "interpret a album ze složky „Interpret - Album“"
            : folderArtist != null ? "interpret jen podle složky — zkontroluj" : "jen název — interpret neznámý";
        return g;
    }

    /// <summary>Číslo stopy z „Track No04“, „Stopa 9“, „Track05!!s“.</summary>
    private static int? GenericNumber(string s) =>
        Regex.Match(s, @"(\d{1,3})\D*$") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>Číselná část mezi pomlčkami („Kryl - 01 - Jde jaro“) je číslo stopy — vyřadit, číslo si vzít.</summary>
    private static bool Take(ref int? track, string seg)
    {
        track ??= int.Parse(seg, CultureInfo.InvariantCulture);
        return false;
    }

    /// <summary>„12 - R.I.P. (Locman - cast III)“ v albu Harleje: pomlčka je součást názvu, ne „Interpret - Název“.</summary>
    private static bool LooksLikeAlbumTrack(List<string> segs) => segs[0].Contains('(') || segs.Count == 2 && segs[1].Length <= 3;

    /// <summary>
    /// Klíč názvu skladby ze jména souboru pro ověření duplicit: bez „Kopie - “, čísla stopy a interpreta; „ (2)“ na konci zvlášť.
    /// „Iné Kafe - 01 Vitaj!“ = „01 - Vitaj!“. Prázdný klíč = nejde poznat (Track 01).
    /// </summary>
    public static (string Key, string Copy) FileTitleKey(string path)
    {
        var stem = Clean(System.IO.Path.GetFileNameWithoutExtension(path));
        stem = Regex.Replace(stem, @"(?i)^(?:kopie|copy of|kopie\s*\(\d+\))\s*[-–]?\s*", string.Empty);
        var copy = string.Empty;
        if (Regex.Match(stem, @"\s*\((?<n>\d{1,3}|kopie|copy)\)\s*$") is { Success: true } cm)
        {
            copy = cm.Groups["n"].Value;
            stem = stem[..cm.Index];
        }

        var segs = SplitDash(stem);
        var last = segs.Count > 0 ? segs[^1] : stem;
        last = Regex.Replace(last, @"^\d{1,3}\s*[-._)]?\s*", string.Empty);
        var key = Norm(last);
        return (IsGenericName(last) || key.Length < 2 ? string.Empty : key, copy);
    }

    /// <summary>
    /// Říkají jména souborů, že jde o různé skladby? Různý název, nebo obě očíslované jinak („GIPSY PRINCE (8)“ × „(9)“ —
    /// „Píseň.mp3“ × „Píseň (2).mp3“ je kopie). Neznámé názvy (Track 01) nic neříkají.
    /// </summary>
    public static bool FilesDiffer(IReadOnlyList<string> paths)
    {
        var k = paths.Select(FileTitleKey).ToList();
        if (k.Any(x => x.Key.Length == 0))
        {
            return false;
        }

        if (k.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return true;
        }

        var numbered = k.Where(x => x.Copy.Length > 0).Select(x => x.Copy).ToList();
        return numbered.Count >= 2 && numbered.Distinct(StringComparer.Ordinal).Count() > 1 && k.All(x => x.Copy.Length > 0);
    }

    /// <summary>„-x-evanescence_-_my_“ → „evanescence - my“; podtržítka, tečky a pomlčky místo mezer, vícenásobné mezery, „(Official Video)“.</summary>
    public static string Clean(string s)
    {
        var x = s.Trim();
        x = Regex.Replace(x, @"^-x-", string.Empty);
        x = Regex.Replace(x, @"(?i)^(?:kopie|copy of)(?:\s*\(\d+\))?\s*[-–]\s*|^copy of\s+", string.Empty); // „Kopie - LORDI - …“ (Windows)
        x = x.Replace("_-_", " - ");
        if (!x.Contains(' ') && x.Contains('_'))
        {
            x = x.Replace('_', ' ');
        }
        else
        {
            x = x.Replace('_', ' ');
        }

        if (!x.Contains(' ') && Regex.Matches(x, @"\.").Count >= 2)
        {
            x = x.Replace('.', ' ');
        }

        if (!x.Contains(' ') && x.Contains('-'))
        {
            // „MAJK-SPIRIT---MLADY-REBEL-(cele-album)“: oddělovač „---“, slova po jedné pomlčce
            x = string.Join(" - ", Regex.Split(x, "-{2,}").Select(p => p.Replace('-', ' ')));
        }

        x = Junk.Replace(x, string.Empty);
        x = Regex.Replace(x, @"\s+", " ").Trim(' ', '-', '.', '+', '§');
        return x;
    }

    /// <summary>Rozdělí podle „ - “ (i „Kryl- 01- Jde“), ale ne uvnitř závorek a ne „Jay-Z“.</summary>
    private static List<string> SplitDash(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (depth == 0 && c is '-' or '–')
            {
                var spaceBefore = i > 0 && s[i - 1] == ' ';
                var j = i;
                while (j + 1 < s.Length && s[j + 1] is '-' or '–')
                {
                    j++;
                }

                var spaceAfter = j + 1 < s.Length && s[j + 1] == ' ';
                if (spaceBefore || spaceAfter || j > i)
                {
                    parts.Add(s[start..i]);
                    start = j + 1;
                }

                i = j;
            }
        }

        parts.Add(s[start..]);
        return parts.Select(p => p.Trim(' ', '-', '–', '.')).Where(p => p.Length > 0).ToList();
    }

    private static (string Name, int? Year) TakeYear(string dir)
    {
        var m = YearParen.Match(dir);
        if (m.Success)
        {
            return (YearParen.Replace(dir, " ").Trim(' ', '-'), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        m = YearLead.Match(dir);
        if (m.Success)
        {
            return (m.Groups["rest"].Value.Trim(' ', '-'), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        m = YearTail.Match(dir);
        return m.Success ? (YearTail.Replace(dir, string.Empty).Trim(), int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture)) : (dir, null);
    }

    private static bool Same(string a, string b) => Norm(a) == Norm(b);

    /// <summary>Porovnávací klíč: bez diakritiky, malá písmena, jen písmena a číslice („AC/DC“ = „AC DC“ = „acdc“).</summary>
    public static string Norm(string? s) =>
        Regex.Replace(FileNameParser.RemoveDiacritics(s ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]", string.Empty);

    /// <summary>
    /// VELKÁ PÍSMENA a „vsechno malym“ → „Jako V Názvu“ — jen když je tam aspoň jedno delší slovo
    /// („VANGELIS“ → „Vangelis“, ale „AC DC“ a „PTK“ zůstanou). Jinak beze změny.
    /// </summary>
    public static string? Pretty(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var x = s.Trim();
        var letters = x.Where(char.IsLetter).ToList();
        var longWord = Regex.IsMatch(x, @"\p{L}{4,}");
        if (letters.Count >= 4 && longWord && (letters.All(char.IsUpper) || letters.All(char.IsLower)))
        {
            x = Cs.TextInfo.ToTitleCase(x.ToLower(Cs));
        }

        return x;
    }
}

/// <summary>
/// Rozbitá diakritika v tagu: text uložený v cp1250 přečtený jako latin-1 („Tøi Sestry“ → „Tři Sestry“, „\x8eízeò“ → „Žízeň“).
/// Opraví se jen, když výsledek dává české znaky a původní text je takhle zakódovat jde. Čistý C#.
/// </summary>
public static class Mojibake
{
    private const string Suspicious = "èøìùòÈØÌÙÒ¹¾»«\u008a\u008e\u009a\u009e\u008d\u009d";
    private const string Czech = "čřěůňšžťďČŘĚŮŇŠŽŤĎ";
    private const string NotCzechSlovak = "űőłśźąęăţşŕŰŐŁŚŹĄĘĂŢŞ˘˛˙˝";

    static Mojibake()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string? Fix(string? s)
    {
        if (string.IsNullOrEmpty(s) || !s.Any(Suspicious.Contains))
        {
            return null;
        }

        // po znacích: co se vejde do bajtu, přečíst jako cp1250 (á, é, í… jsou v obou stejné); znaky mimo latin-1 nechat
        var cp = Encoding.GetEncoding(1250);
        var fixedText = string.Concat(s.Select(c => c <= 0xFF ? cp.GetString(new[] { (byte)c }) : c.ToString()));
        if (fixedText.Any(NotCzechSlovak.Contains))
        {
            return null; // „Crème brûlée“ → „Crčme brűlée“: maďarské / polské znaky = byl to jiný jazyk, ne rozbitá čeština
        }

        var gained = fixedText.Count(Czech.Contains) - s.Count(Czech.Contains);
        return gained > 0 && fixedText != s ? fixedText : null;
    }
}

/// <summary>Rozbor hudební knihovny: duplicity, smetí, nízký tok, chybějící tagy, rozbitá diakritika. Čistý C#.</summary>
public static class MusicAnalyzer
{
    public sealed class Options
    {
        /// <summary>Pod tímhle tokem (kb/s) je skladba „nízká kvalita“. 0 = nehlídat.</summary>
        public int MinBitrateKbps { get; set; } = 128;

        /// <summary>Stejná skladba = délka se liší nejvýš o tolik sekund.</summary>
        public int DurationToleranceSec { get; set; } = 3;
    }

    private static readonly HashSet<string> JunkExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cda", ".m3u", ".pls", ".url", ".lnk", ".ini" };

    /// <summary>Lepší verze první: vyšší tok, má tagy, je v albu (složka s víc skladbami), kratší cesta.</summary>
    public static List<MusicTrack> BestFirst(IEnumerable<MusicTrack> tracks, IReadOnlyDictionary<string, int>? folderCounts = null) =>
        tracks
            .OrderByDescending(t => t.Bitrate / 16000) // ±16 kb/s je pořád „stejný“ tok
            .ThenByDescending(t => MusicGuess.LacksTags(t) ? 0 : 1)
            .ThenByDescending(t => folderCounts != null && folderCounts.TryGetValue(System.IO.Path.GetDirectoryName(t.Path) ?? string.Empty, out var n) ? n : 0)
            .ThenBy(t => t.Path.Length)
            .ThenBy(t => t.Path, StringComparer.Ordinal)
            .ToList();

    public static List<MusicSuggestion> Analyze(IReadOnlyList<MusicTrack> tracks, string root, Options o)
    {
        var found = new List<MusicSuggestion>();
        var folderCounts = tracks.GroupBy(t => System.IO.Path.GetDirectoryName(t.Path) ?? string.Empty).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var junk = tracks.Where(t => JunkExtensions.Contains(System.IO.Path.GetExtension(t.Path)) || t.SizeBytes == 0).ToList();
        var junkPaths = junk.Select(t => t.Path).ToHashSet(StringComparer.Ordinal);
        if (junk.Count > 0)
        {
            found.Add(new MusicSuggestion
            {
                Id = "mjunk",
                Kind = "junk",
                Title = $"Soubory, které nehrají ({junk.Count})",
                Tracks = junk.OrderBy(t => t.Path, StringComparer.Ordinal).ToList(),
                DeletePaths = junk.Select(t => t.Path).ToList(),
                Note = ".cda jsou jen odkazy na stopy z CD (44 B), prázdné soubory a seznamy skladeb",
            });
        }

        var real = tracks.Where(t => !junkPaths.Contains(t.Path)).ToList();
        var inDup = new HashSet<string>(StringComparer.Ordinal);

        // ── bajtově stejné soubory ──
        foreach (var g in real.Where(t => t.ContentKey != null).GroupBy(t => t.ContentKey!).Where(g => g.Count() > 1))
        {
            var best = BestFirst(g, folderCounts);
            found.Add(new MusicSuggestion
            {
                Id = "msame:" + g.Key,
                Kind = "same",
                Title = Display(best[0]),
                Tracks = best,
                DeletePaths = best.Skip(1).Select(t => t.Path).ToList(),
            });
            inDup.UnionWith(best.Select(t => t.Path));
        }

        // ── stejná skladba (interpret + název, délka ± pár s) ──
        var keyed = real.Where(t => !inDup.Contains(t.Path))
            .Select(t => (T: t, K: SongKey(t, root)))
            .Where(x => x.K != null)
            .GroupBy(x => x.K!, StringComparer.Ordinal);
        foreach (var g in keyed)
        {
            foreach (var cluster in ByDuration(g.Select(x => x.T).ToList(), o.DurationToleranceSec).Where(c => c.Count > 1))
            {
                // stejné tagy, ale jména souborů říkají jinou skladbu („GIPSY PRINCE (14)“ × „(15)“ s odpadními tagy) → není to duplicita
                if (MusicGuess.FilesDiffer(cluster.Select(t => t.Path).ToList()))
                {
                    continue;
                }

                var verified = cluster.All(t => MusicGuess.FileTitleKey(t.Path).Key.Length > 0);
                var best = BestFirst(cluster, folderCounts);
                var albums = best.Select(t => System.IO.Path.GetDirectoryName(t.Path)).Distinct(StringComparer.Ordinal).Count();
                var cross = albums > 1;
                found.Add(new MusicSuggestion
                {
                    Id = "msong:" + g.Key + ":" + best.Min(t => t.DurationSec) / 10,
                    Kind = "song",
                    Title = Display(best[0]),
                    Tracks = best,
                    CrossAlbum = cross,
                    // ve stejné složce dvakrát → horší pryč; na různých albech jen ukázat
                    DeletePaths = cross || !verified ? new List<string>() : best.Skip(1).Select(t => t.Path).ToList(),
                    Note = cross ? "na různých albech — smazáním by se album nebo výběrovka rozbila, nic není předem zaškrtnuté"
                        : !verified ? "názvy souborů nic neříkají (Track 01) — ověř poslechem, nic není předem zaškrtnuté" : null,
                });
            }
        }

        // ── nízký datový tok ──
        if (o.MinBitrateKbps > 0)
        {
            var low = real.Where(t => t.Bitrate > 0 && t.Bitrate < o.MinBitrateKbps * 1000L).OrderBy(t => t.Bitrate).ToList();
            if (low.Count > 0)
            {
                found.Add(new MusicSuggestion
                {
                    Id = "mlow",
                    Kind = "low",
                    Title = $"Nízká kvalita — pod {o.MinBitrateKbps} kb/s ({low.Count})",
                    Tracks = low,
                });
            }
        }

        // ── bez tagů: po složkách (album = jedna karta) ──
        foreach (var g in real.Where(MusicGuess.LacksTags).GroupBy(t => System.IO.Path.GetDirectoryName(t.Path) ?? string.Empty, StringComparer.Ordinal))
        {
            var list = g.OrderBy(t => t.Path, StringComparer.Ordinal).ToList();
            found.Add(new MusicSuggestion
            {
                Id = "mmeta:" + g.Key,
                Kind = "meta",
                Title = System.IO.Path.GetRelativePath(root, g.Key),
                Tracks = list,
                Tags = list.Select(t => MusicGuess.Guess(t, root)).ToList(),
            });
        }

        // ── rozbitá diakritika ──
        foreach (var g in real.Where(t => !MusicGuess.LacksTags(t)).Select(t => (T: t, Tags: CharsetFix(t))).Where(x => x.Tags != null)
                     .GroupBy(x => System.IO.Path.GetDirectoryName(x.T.Path) ?? string.Empty, StringComparer.Ordinal))
        {
            var list = g.OrderBy(x => x.T.Path, StringComparer.Ordinal).ToList();
            found.Add(new MusicSuggestion
            {
                Id = "mchar:" + g.Key,
                Kind = "charset",
                Title = System.IO.Path.GetRelativePath(root, g.Key),
                Tracks = list.Select(x => x.T).ToList(),
                Tags = list.Select(x => x.Tags!).ToList(),
            });
        }

        return found;
    }

    /// <summary>Opravené tagy skladby s rozbitou diakritikou, jinak null.</summary>
    public static MusicTags? CharsetFix(MusicTrack t)
    {
        var title = Mojibake.Fix(t.Title);
        var artist = Mojibake.Fix(t.Artists.FirstOrDefault() ?? t.AlbumArtist);
        var album = Mojibake.Fix(t.Album);
        if (title == null && artist == null && album == null)
        {
            return null;
        }

        return new MusicTags
        {
            Path = t.Path,
            Title = (title ?? t.Title).Trim(),
            Artist = (artist ?? t.Artists.FirstOrDefault() ?? t.AlbumArtist)?.Trim(),
            Album = (album ?? t.Album)?.Trim(),
            Year = t.Year,
            TrackNo = t.TrackNo,
            Sure = true,
            How = "oprava diakritiky (cp1250 čtené jako latin-1)",
        };
    }

    /// <summary>Klíč „interpret|název“ — z tagů, u skladeb bez tagů z odhadu; bez interpreta nebo názvu null.</summary>
    public static string? SongKey(MusicTrack t, string root)
    {
        string? artist = t.Artists.FirstOrDefault() ?? t.AlbumArtist;
        string? title = t.Title;
        if (MusicGuess.LacksTags(t))
        {
            var g = MusicGuess.Guess(t, root);
            artist = g.Artist;
            title = g.Title;
        }

        var a = MusicGuess.Norm(Mojibake.Fix(artist) ?? artist);
        var n = MusicGuess.Norm(Regex.Replace(Mojibake.Fix(title) ?? title ?? string.Empty, @"^\d{1,3}\s*[-._)]?\s*", string.Empty));
        return a.Length == 0 || n.Length < 2 ? null : a + "|" + n;
    }

    /// <summary>Rozdělí skladby se stejným klíčem na skupiny podobné délky (živá verze 7:30 není duplicita studiové 4:10).</summary>
    private static List<List<MusicTrack>> ByDuration(List<MusicTrack> tracks, int tolerance)
    {
        var result = new List<List<MusicTrack>>();
        foreach (var t in tracks.OrderBy(t => t.DurationSec))
        {
            var last = result.LastOrDefault();
            if (last != null && t.DurationSec - last[^1].DurationSec <= tolerance && t.DurationSec > 0)
            {
                last.Add(t);
            }
            else
            {
                result.Add(new List<MusicTrack> { t });
            }
        }

        return result;
    }

    private static string Display(MusicTrack t)
    {
        var a = t.Artists.FirstOrDefault() ?? t.AlbumArtist;
        return (string.IsNullOrWhiteSpace(a) ? string.Empty : (Mojibake.Fix(a) ?? a).Trim() + " – ") + (Mojibake.Fix(t.Title) ?? t.Title).Trim();
    }
}
