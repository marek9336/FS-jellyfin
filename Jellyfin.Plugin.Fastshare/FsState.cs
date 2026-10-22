using System.Collections.Concurrent;
using Jellyfin.Plugin.Fastshare.Configuration;
using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Most mezi Jellyfinem a Core/ vrstvou. Singleton v DI. Staví klienta Fastshare
/// z aktuální konfigurace (a přestaví ho, když se změní jméno/heslo), drží frontu,
/// hlídané položky, běžící stahování a jejich zrušení.
/// </summary>
public sealed class FsState : IDisposable
{
    private readonly ILogger<FsState> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly object _lock = new();
    private readonly HttpClient _downloadHttp;

    private FastshareClient? _client;
    private string _snapshot = string.Empty;

    // Běžící stahování: id položky → zrušení. Víc najednou (souběžné stahování).
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    private readonly SemaphoreSlim _watchWake = new(0, 1);

    // Přesuny souborů na pozadí: klíč (návrh / soubor / seriál) → stav pro GUI. Klíč drží zámek,
    // dokud přesun běží — druhé kliknutí nevyrobí další kopii.
    private readonly ConcurrentDictionary<string, FileOp> _fileOps = new(StringComparer.Ordinal);

    private readonly object _outageLock = new();
    private DateTime _outageUntilUtc = DateTime.MinValue;
    private string? _outageReason;

    public sealed record FileOp(bool Running, string Text, DateTime AtUtc);

    /// <summary>Zabere klíče pro přesun. Když některý už běží, nezabere nic a vrátí false.</summary>
    public bool TryStartFileOp(IReadOnlyList<string> keys, string text)
    {
        lock (_fileOps)
        {
            if (keys.Any(k => _fileOps.TryGetValue(k, out var op) && op.Running))
            {
                return false;
            }

            foreach (var k in keys)
            {
                _fileOps[k] = new FileOp(true, text, DateTime.UtcNow);
            }

            return true;
        }
    }

    /// <summary>Přesun doběhl: chybu nechá viset pro GUI (u návrhu), úspěch smaže.</summary>
    public void EndFileOp(IReadOnlyList<string> keys, string? error)
    {
        lock (_fileOps)
        {
            foreach (var k in keys)
            {
                if (error != null && (k.StartsWith("sug:", StringComparison.Ordinal) || k.StartsWith("ser:", StringComparison.Ordinal)))
                {
                    _fileOps[k] = new FileOp(false, error, DateTime.UtcNow);
                }
                else
                {
                    _fileOps.TryRemove(k, out _);
                }
            }
        }
    }

    /// <summary>Stav přesunů po návrzích / seriálech (bez předpony „sug:" / „ser:").</summary>
    public Dictionary<string, FileOp> FileOps(string prefix) =>
        _fileOps.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.Ordinal);

    public FsState(IApplicationPaths applicationPaths, IHttpClientFactory httpClientFactory, ILogger<FsState> logger)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;

        var dataDir = Path.Combine(applicationPaths.DataPath, "fastshare");
        Queue = new DownloadQueue(Path.Combine(dataDir, "state.json"), Log);
        Watch = new WatchStore(Path.Combine(dataDir, "watchlist.json"), Log);
        Suggestions = new SuggestionStore(Path.Combine(dataDir, "suggestions.json"), Log);

        // Vlastní handler: bez správy cookies (jinak by HttpClient hlavičku Cookie
        // s přihlašovacím klíčem zahodil) a s přesměrováním na datový server.
        _downloadHttp = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        Engine = new DownloadEngine(_downloadHttp, Log);

        // Uložené nastavení (nové heslo, okno, místo…) platí hned, ne až po dalším kole workeru
        if (Plugin.Instance != null)
        {
            Plugin.Instance.ConfigurationChanged += (_, _) =>
            {
                ClearOutage();
                Queue.Wake();
            };
        }

        SearchGate = new SearchGate(() => Config.AutoSearchesPerHour, () => Config.SearchHours);

        Limiter = new SpeedLimiter(() =>
        {
            var mbps = Config.SpeedLimitMbps;
            return mbps > 0 ? (long)mbps * 1000 * 1000 / 8 : 0;
        });
    }

    public DownloadQueue Queue { get; }

    public WatchStore Watch { get; }

    /// <summary>Návrhy ze záložky Navržené (lepší verze, duplicity, bez metadat).</summary>
    public SuggestionStore Suggestions { get; }

    public DownloadEngine Engine { get; }

    /// <summary>Strop automatických hledání za hodinu (kontrola knihovny + hlídač).</summary>
    public SearchGate SearchGate { get; }

    /// <summary>Společný limit rychlosti všech souběžných stahování.</summary>
    public SpeedLimiter Limiter { get; }

    /// <summary>Runtime stav pro /status (aktualizuje worker).</summary>
    public WorkerStatus Status { get; } = new();

    /// <summary>Probíhající přenosy pro GUI (id → průběh). Plní worker.</summary>
    public ConcurrentDictionary<Guid, ActiveDownload> Active { get; } = new();

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Klient Fastshare odpovídající aktuální konfiguraci.</summary>
    public FastshareClient Client
    {
        get
        {
            var cfg = Config;
            var snapshot = cfg.FastshareUsername + "\n" + cfg.FastsharePassword;
            lock (_lock)
            {
                if (_client == null || snapshot != _snapshot)
                {
                    _snapshot = snapshot;
                    var http = _httpClientFactory.CreateClient("Fastshare");
                    http.Timeout = TimeSpan.FromSeconds(30);
                    _client = new FastshareClient(http, cfg.FastshareUsername, cfg.FastsharePassword, Log);
                }

                return _client;
            }
        }
    }

    /// <summary>Preference automatického výběru z aktuální konfigurace.</summary>
    public static FileSelectorOptions SelectorOptions(string? maxQualityOverride = null)
    {
        var c = Config;
        return new FileSelectorOptions
        {
            Lang1 = c.PreferredLang1,
            Lang2 = c.PreferredLang2,
            Lang3 = c.PreferredLang3,
            SkipWithoutPreferredLang = c.SkipWithoutPreferredLang,
            UnknownLangScore = c.UnknownLangAs switch
            {
                "lang2" => 200,
                "lang3" => 100,
                _ => 0,
            },
            PreferSubsWhenForeign = c.PreferSubsWhenForeign,
            MaxQuality = string.IsNullOrWhiteSpace(maxQualityOverride) ? c.MaxQuality : maxQualityOverride,
            MaxMovieSizeGb = c.MaxMovieSizeGb,
            MaxEpisodeSizeGb = c.MaxEpisodeSizeGb,
            MaxBitrateMbps = c.MaxBitrateMbps,
            CodecPreference = c.CodecPreference,
            HdrMode = c.HdrMode,
            DvMode = c.DvMode,
        };
    }

    // ── Běžící stahování ──────────────────────────────────────────
    public CancellationToken BeginDownload(Guid id, CancellationToken outer)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _running[id] = cts;
        return cts.Token;
    }

    public void EndDownload(Guid id)
    {
        if (_running.TryRemove(id, out var cts))
        {
            cts.Dispose();
        }

        Active.TryRemove(id, out _);
    }

    /// <summary>Zruší jedno stahování (id), nebo všechna (null). Vrací, kolik jich zrušilo.</summary>
    public int CancelDownload(Guid? id = null)
    {
        var n = 0;
        foreach (var (key, cts) in _running)
        {
            if (id != null && key != id)
            {
                continue;
            }

            try
            {
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    n++;
                }
            }
            catch (ObjectDisposedException)
            {
                // mezitím skončilo
            }
        }

        return n;
    }

    public int RunningCount => _running.Count;

    // ── Výpadek služby ────────────────────────────────────────────
    public void MarkOutage(TimeSpan duration, string reason)
    {
        lock (_outageLock)
        {
            var until = DateTime.UtcNow.Add(duration);
            if (until > _outageUntilUtc)
            {
                _outageUntilUtc = until;
                _outageReason = reason;
            }
        }
    }

    public TimeSpan OutageRemaining()
    {
        lock (_outageLock)
        {
            var rest = _outageUntilUtc - DateTime.UtcNow;
            return rest > TimeSpan.Zero ? rest : TimeSpan.Zero;
        }
    }

    public string? OutageReason
    {
        get { lock (_outageLock) { return OutageRemaining() > TimeSpan.Zero ? _outageReason : null; } }
    }

    public void ClearOutage()
    {
        lock (_outageLock)
        {
            _outageUntilUtc = DateTime.MinValue;
            _outageReason = null;
        }
    }

    // ── Hlídač ────────────────────────────────────────────────────
    public async Task WaitWatcherAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _watchWake.WaitAsync(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void WakeWatcher()
    {
        try
        {
            _watchWake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public void Log(string message) => _logger.LogInformation("Fastshare: {Message}", message);

    public void Dispose()
    {
        _downloadHttp.Dispose();
    }
}
