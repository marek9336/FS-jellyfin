using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Stav položky ve frontě stahování.</summary>
public enum QueueItemStatus
{
    Queued,
    Downloading,
    Done,
    Error,
    Skipped
}

/// <summary>Typ média — určuje cílovou složku.</summary>
public enum MediaKind
{
    Movie,
    Episode
}

/// <summary>
/// Jeden soubor z hledání Fastshare (api_kodi.php?process=search) + co se dalo vyčíst
/// z jeho názvu. Fastshare nemá katalog filmů — jen soubory, které nahráli uživatelé,
/// takže název, rok, díl, jazyk a kvalita se odhadují z názvu souboru.
/// </summary>
public class FsFile
{
    /// <summary>ID souboru na Fastshare (klíč pro rozpoznání „tenhle soubor už mám ve frontě").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Původní název souboru, jak ho vrátil Fastshare.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Odkaz ke stažení (bez cookie nevrací nic — viz FastshareClient).</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? DurationSec { get; set; }

    /// <summary>Rozlišení podle Fastshare („1080p", „x px" = neznámé).</summary>
    public string? Resolution { get; set; }

    public string? Thumbnail { get; set; }

    public string? UploadedDate { get; set; }

    // ── Vyčteno z názvu (FileNameParser) ──────────────────────────
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>Normalizovaná kvalita: SD / 720p / 1080p / 4K (null = neví se).</summary>
    public string? Quality { get; set; }

    /// <summary>Jazyky zvuku (dabing) — „CZ", „SK", „EN"… v pořadí, jak jsou v názvu.</summary>
    public List<string> AudioLangs { get; set; } = new();

    /// <summary>Jazyky titulků — „CZ" u „cz titulky", „EN" u „en subs".</summary>
    public List<string> SubtitleLangs { get; set; } = new();

    public string? Codec { get; set; }

    public bool Hdr { get; set; }

    public bool Dv { get; set; }

    public bool Atmos { get; set; }

    /// <summary>Ukázka/trailer — při automatickém výběru se nikdy nevezme.</summary>
    public bool IsSample { get; set; }

    /// <summary>Přípona souboru včetně tečky („.mkv").</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>Video soubor (mkv/mp4/avi…) — archivy a obrázky se do knihovny nehodí.</summary>
    public bool IsVideo { get; set; }

    /// <summary>Archiv (zip/rar/7z, i díl vícedílného) — po stažení se rozbalí.</summary>
    public bool IsArchive { get; set; }

    /// <summary>Číslo dílu vícedílného archivu („.part2.rar" = 2, „.r00" = 2, „.001" = 1), jinak null.</summary>
    public int? ArchivePart { get; set; }

    /// <summary>Řada celého balíku bez dílu („Teorie velkého třesku 9. série.rar" = 9).</summary>
    public int? PackSeason { get; set; }

    /// <summary>Společný základ názvu dílů archivu („Pratele.S01.part" pro „Pratele.S01.part1.rar").</summary>
    public string? ArchiveBase { get; set; }

    /// <summary>Přibližný datový tok v bitech/s (velikost / délka), když je délka známá.</summary>
    [JsonIgnore]
    public long? Bitrate => DurationSec is > 0 && SizeBytes > 0 ? SizeBytes * 8 / DurationSec.Value : null;

    /// <summary>Jazykový popisek pro frontu a název souboru: „CZ", „CZ,EN", „EN+tit".</summary>
    [JsonIgnore]
    public string LanguageLabel
    {
        get
        {
            var parts = new List<string>(AudioLangs);
            foreach (var sub in SubtitleLangs)
            {
                parts.Add(sub + "+tit");
            }

            return string.Join(",", parts);
        }
    }
}

/// <summary>Jedna položka fronty stahování. Serializuje se do JSON stavového souboru.</summary>
public class QueueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Název filmu / seriálu (bez roku).</summary>
    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    public MediaKind MediaType { get; set; } = MediaKind.Movie;

    /// <summary>Jen pro epizody.</summary>
    public string? SeriesTitle { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    /// <summary>ID souboru na Fastshare.</summary>
    public string FileId { get; set; } = string.Empty;

    /// <summary>Původní název souboru (pro zobrazení a příponu).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Odkaz ke stažení z hledání.</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    // Popisné údaje — pro zobrazení a pojmenování souboru.
    public string? Quality { get; set; }

    /// <summary>„CZ", „CZ,EN", „EN+tit" — jde do tagu v názvu souboru.</summary>
    public string? Language { get; set; }

    public string? SizeText { get; set; }

    public long SizeBytes { get; set; }

    public QueueItemStatus Status { get; set; } = QueueItemStatus.Queued;

    public string? ErrorMessage { get; set; }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedUtc { get; set; }

    public long BytesTotal { get; set; }

    public long BytesDone { get; set; }

    /// <summary>Výsledná cesta po dokončení.</summary>
    public string? TargetPath { get; set; }

    /// <summary>Počet neúspěšných pokusů (pro automatický retry s odstupem).</summary>
    public int FailCount { get; set; }

    /// <summary>Na kolika procentech se stahování naposledy přerušilo.</summary>
    public int StoppedPercent { get; set; }

    /// <summary>„Stáhnout teď" — položka má přednost a obejde časové okno.</summary>
    public bool ForceNow { get; set; }

    public int? DurationSec { get; set; }

    /// <summary>Ruční pořadí ve frontě (nižší = dřív).</summary>
    public long SortIndex { get; set; }

    /// <summary>„Stáhnout znovu": nepřeskakovat kvůli existujícímu souboru a přepsat ho.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Položku zařadil Hlídač (pro zobrazení ve frontě).</summary>
    public bool FromWatcher { get; set; }

    /// <summary>
    /// Po chybě: další pokus nejdřív v tuhle chvíli (backoff). Ostatní položky se mezitím
    /// stahují dál — při souběžném stahování nečeká kvůli jedné chybě celý worker.
    /// </summary>
    public DateTime? RetryAfterUtc { get; set; }

    // ── Archiv (zip/rar/7z) ───────────────────────────────────────
    /// <summary>Položka je archiv: stáhnout všechny části, rozbalit, videa roztřídit do knihovny.</summary>
    public bool IsArchive { get; set; }

    /// <summary>Části archivu v pořadí (u jednodílného jedna). Hlavní soubor je i ve FileId/DownloadUrl.</summary>
    public List<ArchivePart>? Parts { get; set; }

    /// <summary>Řada pro soubory v archivu, které ji v názvu nemají (seriál po řadách: „Přátelé 3. série.rar").</summary>
    public int? ArchiveSeason { get; set; }

    /// <summary>Výsledek rozbalení pro GUI („24 dílů, 1 přeskočen, 2 nerozpoznané").</summary>
    public string? ArchiveResult { get; set; }

    // ── Náhrada (záložka Navržené) ────────────────────────────────
    /// <summary>
    /// Po úspěšném stažení smazat tyhle soubory (horší verze téhož filmu). Maže se jen uvnitř
    /// knihoven Jellyfinu a jen když nový soubor opravdu existuje.
    /// </summary>
    public List<string>? ReplacePaths { get; set; }

    /// <summary>Ze kterého návrhu položka vznikla (po dokončení se návrh označí).</summary>
    public string? SuggestionId { get; set; }

    /// <summary>
    /// Uložit do téhle složky (místo standardní cesty) — u dílů seriálu tam, kde seriál v knihovně
    /// už je, ať Jellyfin nevytvoří druhý seriál vedle stávající složky („Mentalista" vs. „Mentalista (seriál)…").
    /// </summary>
    public string? TargetDir { get; set; }

    /// <summary>Doplňující výsledek pro GUI („nahrazeno — smazány 2 starší verze").</summary>
    public string? ResultNote { get; set; }
}

/// <summary>Jeden soubor archivu (u vícedílného jich je víc, stahují se postupně).</summary>
public class ArchivePart
{
    public string FileId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
}

/// <summary>
/// Hlídaný seriál nebo film. Hlídač ho periodicky hledá na Fastshare a zařadí,
/// co chybí: u seriálu díly, které ještě nejsou staženy ani ve frontě.
/// </summary>
public class WatchItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"series" nebo "movie".</summary>
    public string Type { get; set; } = "series";

    public string Title { get; set; } = string.Empty;

    public int? Year { get; set; }

    /// <summary>Vlastní hledaný výraz (prázdné = název). Hodí se, když se soubory jmenují jinak.</summary>
    public string? SearchTerm { get; set; }

    /// <summary>Od které řady hlídat (seriál). null = všechny.</summary>
    public int? FromSeason { get; set; }

    public int IntervalDays { get; set; } = 1;

    /// <summary>Vlastní max. kvalita jen pro tuhle položku. Prázdné = globální nastavení.</summary>
    public string? MaxQuality { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Klíče zařazených epizod („S01E03").</summary>
    public List<string> Grabbed { get; set; } = new();

    public bool MovieGrabbed { get; set; }

    public DateTime? LastCheckedUtc { get; set; }

    /// <summary>Vynucená kontrola „hned" (tlačítko ⚡).</summary>
    public bool ForceCheck { get; set; }

    /// <summary>Film stažen → položka je hotová.</summary>
    public bool Completed { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Info o účtu Fastshare z přihlášení.</summary>
public class FsAccount
{
    /// <summary>Předplatné „neomezené stahování".</summary>
    public bool Unlimited { get; set; }

    /// <summary>Zbývající kredit v MB (u neomezeného tarifu bez významu).</summary>
    public long CreditMb { get; set; }
}

/// <summary>Persistentní stav pluginu (fronta + denní počítadlo). Jeden JSON soubor.</summary>
public class PluginState
{
    public List<QueueItem> Items { get; set; } = new();

    /// <summary>Datum (UTC, yyyy-MM-dd), ke kterému platí DailyBytes.</summary>
    public string? DailyDate { get; set; }

    public long DailyBytes { get; set; }

    public bool WorkerPaused { get; set; }

    /// <summary>„⚡ Stahovat teď": celá fronta mimo časové okno a denní strop, dokud se nevyprázdní.</summary>
    public bool IgnoreWindow { get; set; }

    /// <summary>⛔ STOP: žádné spojení na Fastshare (stahování, hledání, přihlášení), dokud ho uživatel nepovolí.</summary>
    public bool FastshareStopped { get; set; }

    /// <summary>Klouzavý průměr rychlosti stahování (B/s).</summary>
    public long AvgSpeedBps { get; set; }
}

/// <summary>Jedno právě běžící stahování (při souběžném stahování jich je víc).</summary>
public class ActiveDownload
{
    public Guid ItemId { get; set; }

    public string Title { get; set; } = string.Empty;

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public long SpeedBps { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Snapshot pro /status endpoint.</summary>
public class WorkerStatus
{
    public bool Paused { get; set; }

    public List<ActiveDownload> Active { get; set; } = new();

    public DateTime? NextActionUtc { get; set; }

    public string? LastMessage { get; set; }

    public long DailyBytes { get; set; }

    public long FreeSpaceBytes { get; set; }

    public FsAccount? Account { get; set; }
}
