using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Fastshare.Configuration;

/// <summary>
/// Nastavení pluginu. POZOR: Jellyfin ukládá jako plaintext XML
/// v /config/plugins/configurations/ — heslo k Fastshare tam leží čitelně.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── Účet Fastshare ────────────────────────────────────────────
    public string FastshareUsername { get; set; } = string.Empty;

    public string FastsharePassword { get; set; } = string.Empty;

    /// <summary>Zobrazovat ve výsledcích i obsah pro dospělé (parametr API `adult`).</summary>
    public bool ShowAdult { get; set; }

    // ── Automatický výběr souboru (⚡ Stáhnout nejlepší, Hlídač) ───
    public string PreferredLang1 { get; set; } = "CZ";

    public string PreferredLang2 { get; set; } = "SK";

    public string PreferredLang3 { get; set; } = "EN";

    /// <summary>Soubor bez preferovaného jazyka nestahovat.</summary>
    public bool SkipWithoutPreferredLang { get; set; }

    /// <summary>
    /// Jak brát soubor, u kterého název jazyk neuvádí (na Fastshare častý — často je to
    /// český dabing, ale jistota to není): "lang2" (jako 2. jazyk), "lang3", "foreign".
    /// </summary>
    public string UnknownLangAs { get; set; } = "lang3";

    public bool PreferSubsWhenForeign { get; set; } = true;

    /// <summary>SD/720p/1080p/4K, "-" = bez limitu.</summary>
    public string MaxQuality { get; set; } = "4K";

    /// <summary>Maximální velikost filmu v GB (0 = bez limitu).</summary>
    public int MaxMovieSizeGb { get; set; } = 30;

    /// <summary>
    /// Maximální velikost jednoho dílu seriálu v GB (0 = bez limitu). Zvlášť od filmu —
    /// společný limit v pluginu Stream Cinema pouštěl 20GB díly, nebo škrtal filmy.
    /// </summary>
    public int MaxEpisodeSizeGb { get; set; } = 5;

    /// <summary>Maximální datový tok v Mbit/s (0 = bez limitu) — pro sledování na dálku.</summary>
    public int MaxBitrateMbps { get; set; }

    public string CodecPreference { get; set; } = "hevc|h264|av1";

    public string HdrMode { get; set; } = "ignore";

    public string DvMode { get; set; } = "avoid";

    // ── Cílové cesty (uvnitř Jellyfin kontejneru!) ────────────────
    public string MoviesPath { get; set; } = "/media/movies";

    public string SeriesPath { get; set; } = "/media/tvshows";

    // ── Stahování ─────────────────────────────────────────────────
    /// <summary>Kolik souborů stahovat najednou (1–8).</summary>
    public int MaxParallelDownloads { get; set; } = 2;

    /// <summary>Společný limit rychlosti všech stahování v Mbit/s. 0 = bez limitu.</summary>
    public int SpeedLimitMbps { get; set; }

    /// <summary>Pauza mezi soubory v sekundách (0 = hned další).</summary>
    public int PauseBetweenSeconds { get; set; }

    /// <summary>Denní strop stažených dat v GB. 0 = vypnuto.</summary>
    public int DailyCapGb { get; set; }

    /// <summary>Minimální volné místo v GB — pod tuto hodnotu se nezačne další stahování.</summary>
    public int MinFreeSpaceGb { get; set; } = 50;

    /// <summary>Kolikrát po pádu zkusit navázat, než položka spadne do „Problémů".</summary>
    public int RetryAttempts { get; set; } = 3;

    public int DuplicateSizeTolerancePercent { get; set; } = 5;

    public bool TriggerLibraryScan { get; set; } = true;

    // ── Časová okna (stejný zápis jako v pluginu Stream Cinema) ───
    /// <summary>Stahovat jen v okně (0–23). From == To = okno vypnuté (celý den).</summary>
    public int WindowFromHour { get; set; }

    public int WindowToHour { get; set; }

    /// <summary>Další okna ke globálnímu od–do, např. "22-3,12-13".</summary>
    public string WindowExtraRanges { get; set; } = string.Empty;

    public bool UseWeeklyWindow { get; set; }

    /// <summary>Rozvrh po dnech: "1:8-16,22-3;2:0-0;6:off" (viz Core/Schedule.cs).</summary>
    public string WeeklyWindow { get; set; } = string.Empty;

    /// <summary>
    /// Mimo okno přerušit i běžící stahování (stažená část zůstane a naváže se).
    /// Vypnuto = rozjeté soubory se dostáhnou, jen nové nezačnou.
    /// </summary>
    public bool StopAtWindowEnd { get; set; }

    // ── Hlídač ────────────────────────────────────────────────────
    public int WatchDefaultIntervalDays { get; set; } = 1;

    /// <summary>Kolik stránek výsledků hledání projít při kontrole (200 souborů na stránku).</summary>
    public int WatchSearchPages { get; set; } = 3;
}
