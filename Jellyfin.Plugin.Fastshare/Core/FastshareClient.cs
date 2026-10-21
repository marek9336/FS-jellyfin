using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Chyba Fastshare, kterou má smysl opakovat (síť, přetížený server).</summary>
public class FastshareException : Exception
{
    public FastshareException(string message)
        : base(message)
    {
    }
}

/// <summary>Trvalá chyba — opakování nepomůže (špatné heslo, málo kreditu, soubor smazán).</summary>
public sealed class FastsharePermanentException : FastshareException
{
    public FastsharePermanentException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Klient API Fastshare — totéž rozhraní, které používá oficiální Kodi doplněk
/// (plugin.video.fastshare 1.0.10): api_kodi.php?process=login|search.
/// Přihlášení vrátí `hash`; stahuje se s cookie FASTSHARE=hash. Bez ní vrací
/// odkaz ke stažení prázdnou odpověď 200 (0 B) — to se musí poznat jako chyba.
/// Čistý C#, žádná závislost na Jellyfin API.
/// </summary>
public sealed class FastshareClient
{
    public const string ApiUrl = "https://fastshare.cz/api/api_kodi.php";

    /// <summary>Jak dlouho věřit jednou získanému hashi, než se přihlásí znovu.</summary>
    private static readonly TimeSpan HashLifetime = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly string _user;
    private readonly string _password;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _loginLock = new(1, 1);

    private string? _hash;
    private DateTime _hashUtc;

    public FastshareClient(HttpClient http, string user, string password, Action<string> log)
    {
        _http = http;
        _user = user ?? string.Empty;
        _password = password ?? string.Empty;
        _log = log;
    }

    public bool HasCredentials => _user.Length > 0 && _password.Length > 0;

    /// <summary>Poslední známý stav účtu (z přihlášení).</summary>
    public FsAccount? Account { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Zahodí hash — další volání se přihlásí znovu (po odmítnutém stahování).</summary>
    public void InvalidateSession() => _hash = null;

    /// <summary>Přihlásí se (nebo vrátí platný hash). Vrací null při neúspěchu, důvod v LastError.</summary>
    public async Task<string?> GetHashAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _hash != null && DateTime.UtcNow - _hashUtc < HashLifetime)
        {
            return _hash;
        }

        await _loginLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && _hash != null && DateTime.UtcNow - _hashUtc < HashLifetime)
            {
                return _hash;
            }

            return await LoginLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<string?> LoginLockedAsync(CancellationToken ct)
    {
        if (!HasCredentials)
        {
            LastError = "chybí jméno nebo heslo k Fastshare";
            return null;
        }

        var url = $"{ApiUrl}?process=login&login={Uri.EscapeDataString(_user)}&password={Uri.EscapeDataString(_password)}";
        string body;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Špatné heslo = HTTP 401 a v těle {"response":"INVALID_LOGIN"} → rozebrat níž
            if (!resp.IsSuccessStatusCode && !body.Contains("INVALID_LOGIN", StringComparison.Ordinal))
            {
                LastError = $"Fastshare odpověděl HTTP {(int)resp.StatusCode}";
                return null;
            }
        }
        catch (HttpRequestException ex)
        {
            LastError = "Fastshare nedostupný: " + ex.Message;
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.String)
            {
                LastError = r.GetString() == "INVALID_LOGIN" ? "špatné jméno nebo heslo" : r.GetString();
                return null;
            }

            if (!root.TryGetProperty("user", out var user) || !user.TryGetProperty("hash", out var h)
                || string.IsNullOrEmpty(h.GetString()))
            {
                LastError = "Fastshare nevrátil přihlašovací klíč";
                return null;
            }

            var account = new FsAccount
            {
                Unlimited = string.Equals(Str(user, "unlimited"), "true", StringComparison.OrdinalIgnoreCase),
            };
            if (user.TryGetProperty("data", out var data) && long.TryParse(
                    Str(data, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
            {
                account.CreditMb = mb;
            }

            Account = account;
            _hash = h.GetString();
            _hashUtc = DateTime.UtcNow;
            LastError = null;
            _log(account.Unlimited ? "fastshare: přihlášeno (neomezené stahování)" : $"fastshare: přihlášeno, kredit {account.CreditMb} MB");
            return _hash;
        }
        catch (JsonException)
        {
            LastError = "nečitelná odpověď Fastshare na přihlášení";
            return null;
        }
    }

    /// <summary>Výsledek hledání: soubory + kolik jich Fastshare celkem zná.</summary>
    public sealed class SearchResult
    {
        public List<FsFile> Files { get; set; } = new();

        public int Total { get; set; }

        public int TotalPages { get; set; }
    }

    /// <summary>
    /// Hledání podle výrazu (diakritiku Fastshare ignoruje). Jedna stránka; server
    /// vrací méně souborů, než je `perPage` (část odfiltruje), stránkuje se `page`.
    /// </summary>
    public async Task<SearchResult> SearchAsync(string term, int page, bool adult, CancellationToken ct, int perPage = 200)
    {
        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{ApiUrl}?process=search&pagination={perPage}&page={Math.Max(1, page)}&term={Uri.EscapeDataString(SearchTerm(term))}&adult={(adult ? 1 : 0)}");
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new FastshareException($"hledání: HTTP {(int)resp.StatusCode}");
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var result = new SearchResult { Files = ParseFiles(doc.RootElement) };
        if (doc.RootElement.TryGetProperty("search", out var s))
        {
            result.Total = Int(s, "total");
            result.TotalPages = Int(s, "total_pages");
        }

        return result;
    }

    /// <summary>
    /// Výraz pro API: bez diakritiky. Fastshare s ní skoro nic nenajde (20. 10. 2026:
    /// „Teorie velkého třesku" = 0 souborů, „Teorie velkeho tresku" = 464), bez ní
    /// najde i soubory pojmenované s diakritikou.
    /// </summary>
    public static string SearchTerm(string term) =>
        FileNameParser.RemoveDiacritics(term ?? string.Empty).Trim();

    /// <summary>
    /// Hledání přes víc stránek (Hlídač, automatický výběr). Končí, když další stránka
    /// nic nepřinese nebo dojde `maxPages`.
    /// </summary>
    public async Task<List<FsFile>> SearchAllAsync(string term, bool adult, int maxPages, CancellationToken ct)
    {
        var all = new List<FsFile>();
        var seen = new HashSet<string>();
        for (var page = 1; page <= maxPages; page++)
        {
            var r = await SearchAsync(term, page, adult, ct).ConfigureAwait(false);
            var added = 0;
            foreach (var f in r.Files)
            {
                if (seen.Add(f.Id))
                {
                    all.Add(f);
                    added++;
                }
            }

            if (added == 0 || page >= r.TotalPages)
            {
                break;
            }
        }

        return all;
    }

    private static readonly System.Text.RegularExpressions.Regex LinkRe = new(
        @"^https?://(?:www\.)?fastshare\.(?:cloud|cz)/(?<id>\d+)(?:/[^\s?#]*)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex TitleRe = new(
        @"<title>\s*(?<name>.+?)\s+(?:online ke zhlédnutí a stažení|ke stažení)\s*\|", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly System.Text.RegularExpressions.Regex DataLinkRe = new(
        @"(?:https?:)?//(?<host>data\d+\.fastshare\.(?:cloud|cz))/download\.php\?id=(?<id>\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Je to odkaz na soubor na webu Fastshare? Vrací ID souboru.</summary>
    public static string? FileIdFromLink(string link)
    {
        var m = LinkRe.Match((link ?? string.Empty).Trim());
        return m.Success ? m.Groups["id"].Value : null;
    }

    /// <summary>
    /// Soubor podle odkazu z webu Fastshare (https://fastshare.cloud/ID/nazev). Hledání přes API
    /// vrací jen videa — archivy (rar/zip/7z) a cokoli dalšího jde do pluginu dostat jen takhle.
    /// Název se bere z titulku stránky, odkaz ke stažení z jejího obsahu. Velikost doplní volající.
    /// </summary>
    public async Task<FsFile> ResolveLinkAsync(string link, CancellationToken ct)
    {
        var id = FileIdFromLink(link) ?? throw new FastsharePermanentException("Tohle není odkaz na soubor na Fastshare (https://fastshare.cloud/číslo/název).");
        using var req = new HttpRequestMessage(HttpMethod.Get, link.Trim());
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Jellyfin-Fastshare)");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new FastshareException($"stránka souboru: HTTP {(int)resp.StatusCode}");
        }

        if (html.Contains("Tento soubor již není dostupný", StringComparison.Ordinal))
        {
            throw new FastsharePermanentException("Soubor už na Fastshare není (nebo je odkaz neúplný — zkopíruj ho celý i s názvem).");
        }

        var name = TitleRe.Match(html) is { Success: true } t ? System.Net.WebUtility.HtmlDecode(t.Groups["name"].Value.Trim()) : null;
        var data = DataLinkRe.Matches(html).FirstOrDefault(m => m.Groups["id"].Value == id);
        if (string.IsNullOrEmpty(name) || data == null)
        {
            throw new FastshareException("Ze stránky souboru se nepodařilo vyčíst název nebo odkaz ke stažení (změnil se web Fastshare?).");
        }

        var f = new FsFile
        {
            Id = id,
            FileName = name,
            DownloadUrl = $"https://{data.Groups["host"].Value}/download.php?id={id}",
        };
        FileNameParser.Fill(f);
        return f;
    }

    /// <summary>
    /// Připraví požadavek na stažení: cookie s přihlašovacím klíčem. Fastshare odkazy
    /// přesměrovávají na datový server, cookie se proto přidává ke každému požadavku
    /// (HttpClient bez CookieContaineru ji při přesměrování nese dál jen jako hlavičku).
    /// </summary>
    public static void PrepareDownload(HttpRequestMessage req, string hash)
    {
        req.Headers.Add("Cookie", "FASTSHARE=" + hash);
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Fastshare", "1.0"));
    }

    /// <summary>Rozebere `search.file[]` z odpovědi API (čísla chodí jako řetězce).</summary>
    public static List<FsFile> ParseFiles(JsonElement root)
    {
        var list = new List<FsFile>();
        if (!root.TryGetProperty("search", out var s) || !s.TryGetProperty("file", out var files)
            || files.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var x in files.EnumerateArray())
        {
            var f = new FsFile
            {
                Id = Str(x, "id") ?? string.Empty,
                FileName = Str(x, "filename") ?? string.Empty,
                DownloadUrl = Str(x, "download_url") ?? string.Empty,
                Resolution = Str(x, "resolution"),
                Thumbnail = Str(x, "thumbnail"),
                UploadedDate = Str(x, "uploaded_date"),
            };
            if (x.TryGetProperty("data", out var d)
                && long.TryParse(Str(d, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                f.SizeBytes = size;
            }

            if (x.TryGetProperty("duration", out var du)
                && int.TryParse(Str(du, "value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec > 0)
            {
                f.DurationSec = sec;
            }

            if (f.Id.Length == 0 || f.DownloadUrl.Length == 0)
            {
                continue;
            }

            FileNameParser.Fill(f);
            list.Add(f);
        }

        return list;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int Int(JsonElement e, string name) =>
        int.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
