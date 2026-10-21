using System.Diagnostics;
using System.Net;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Fastshare vrátil místo souboru prázdnou/textovou odpověď — typicky neplatné přihlášení.</summary>
public sealed class NotAFileException : FastshareException
{
    public NotAFileException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Vlastní stahování jednoho souboru:
///  - navazování přes HTTP Range (soubor .part),
///  - společný limit rychlosti pro všechna souběžná stahování (SpeedLimiter),
///  - kontrola, že přišel opravdu soubor (Fastshare bez přihlášení vrací 200 a 0 B),
///  - kontrola velikosti po dokončení,
///  - průběžný callback progresu.
/// Převzato z pluginu Stream Cinema, upraveno pro Fastshare. Čistý C#.
/// </summary>
public sealed class DownloadEngine
{
    private const int BufferSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public DownloadEngine(HttpClient http, Action<string> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Stáhne URL do partPath (navazuje, pokud .part existuje).
    /// progress(bytesDone, bytesTotal, speedBps) — volá se cca 1× za sekundu.
    /// Vrací počet bajtů stažených V TÉTO relaci (kvůli dennímu počítadlu).
    /// </summary>
    public async Task<long> DownloadAsync(
        string url,
        string partPath,
        Action<HttpRequestMessage> prepare,
        SpeedLimiter limiter,
        long expectedSize,
        Action<long, long, long> progress,
        CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(partPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize > 0 && existing >= expectedSize)
        {
            // .part už je celý (spadlo to až po stažení) — nic nestahovat
            progress(existing, expectedSize, 0);
            return 0;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        prepare(req);
        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (existing > 0 && resp.StatusCode == HttpStatusCode.OK)
        {
            _log("download: server nevrátil 206, začínám od začátku");
            existing = 0;
        }
        else if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _log("download: 416, .part neodpovídá — začínám od začátku");
            File.Delete(partPath);
            return await DownloadAsync(url, partPath, prepare, limiter, expectedSize, progress, ct).ConfigureAwait(false);
        }

        resp.EnsureSuccessStatusCode();

        var contentLength = resp.Content.Headers.ContentLength ?? -1;
        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentLength == 0 || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var text = contentLength == 0 ? string.Empty : await ReadSnippet(resp, ct).ConfigureAwait(false);
            throw new NotAFileException(
                "Fastshare nevrátil soubor (" + (contentLength == 0 ? "prázdná odpověď" : mediaType)
                + (text.Length > 0 ? ": " + text : string.Empty) + ") — přihlášení vypršelo, nebo soubor už není");
        }

        var total = contentLength > 0 ? existing + contentLength : expectedSize;

        var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var sourceDisposer = source;
        await using var target = new FileStream(
            partPath,
            existing > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize);

        // Zrušení: ReadAsync(ct) nemusí přerušit už běžící čtení ze sítě — stream zavřít.
        using var ctReg = ct.Register(() =>
        {
            try
            {
                source.Dispose();
            }
            catch (Exception)
            {
                // ignore — cílem je jen odblokovat čekající read
            }
        });

        var buffer = new byte[BufferSize];
        long sessionBytes = 0;
        var reportWatch = Stopwatch.StartNew();
        long lastReportBytes = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sessionBytes += read;

                await limiter.ThrottleAsync(read, ct).ConfigureAwait(false);

                if (reportWatch.ElapsedMilliseconds >= 1000)
                {
                    var speed = (long)((sessionBytes - lastReportBytes) * 1000.0 / reportWatch.ElapsedMilliseconds);
                    progress(existing + sessionBytes, total, speed);
                    lastReportBytes = sessionBytes;
                    reportWatch.Restart();
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }

        await target.FlushAsync(ct).ConfigureAwait(false);
        var final = existing + sessionBytes;
        progress(final, total, 0);

        // Spojení spadlo uprostřed a server to „ukončil" bez chyby → nedokončené.
        // .part zůstane a příští pokus naváže.
        var expected = total > 0 ? total : expectedSize;
        if (expected > 0 && final < expected)
        {
            throw new FastshareException($"spojení skončilo předčasně ({Dedup.FormatSize(final)} z {Dedup.FormatSize(expected)})");
        }

        return sessionBytes;
    }

    /// <summary>
    /// Velikost souboru dotazem na první bajt (Range 0-0 → Content-Range „bytes 0-0/CELKEM").
    /// Zároveň ověří, že odkaz s přihlášením opravdu vrací soubor. null = nepodařilo se.
    /// </summary>
    public async Task<long?> ProbeSizeAsync(string url, Action<HttpRequestMessage> prepare, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        prepare(req);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        var type = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return resp.Content.Headers.ContentRange?.Length
            ?? (resp.StatusCode == HttpStatusCode.OK ? resp.Content.Headers.ContentLength : null);
    }

    private static async Task<string> ReadSnippet(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var s = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > 120 ? s[..120] + "…" : s;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Volné místo na filesystému, kde leží `path` (longest-prefix match přes mounty).</summary>
    public static long GetFreeSpace(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            DriveInfo? best = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady)
                {
                    continue;
                }

                var root = d.RootDirectory.FullName;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && (best == null || root.Length > best.RootDirectory.FullName.Length))
                {
                    best = d;
                }
            }

            return best?.AvailableFreeSpace ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
