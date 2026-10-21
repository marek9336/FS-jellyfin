using System.Text.Json;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Jeden soubor filmu v knihovně (u filmu ve víc verzích jich je víc).</summary>
public class LibraryVersion
{
    /// <summary>ID položky Jellyfinu, ke které soubor patří (u sloučených verzí každá svoje).</summary>
    public string ItemId { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    /// <summary>SD / 720p / 1080p / 4K (podle rozlišení videa), null = neznámé.</summary>
    public string? Quality { get; set; }

    /// <summary>Datový tok v bitech/s (velikost / délka), když je délka známá.</summary>
    public long? Bitrate { get; set; }
}

/// <summary>Kandidát ke stažení z Fastshare.</summary>
public class SuggestedFile
{
    public string FileId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public int? DurationSec { get; set; }

    public string? Quality { get; set; }

    public string? Language { get; set; }

    /// <summary>Proč zrovna tenhle („CZ · 1080p · 2,1 GB · HEVC (skóre 345)").</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Návrh ze záložky Navržené. Klíč (`Id`) je stabilní — „up:&lt;film&gt;", „dup:&lt;film&gt;", „id:&lt;položka&gt;" —
/// takže odmítnutý návrh se při další kontrole knihovny nevrátí.
/// </summary>
public class Suggestion
{
    public string Id { get; set; } = string.Empty;

    /// <summary>"upgrade" (lepší verze), "duplicate" (víc verzí), "identify" (bez metadat).</summary>
    public string Kind { get; set; } = "upgrade";

    /// <summary>Hlavní položka filmu v Jellyfinu.</summary>
    public string ItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? OriginalTitle { get; set; }

    public int? Year { get; set; }

    /// <summary>Verze v knihovně, nejlepší první.</summary>
    public List<LibraryVersion> Versions { get; set; } = new();

    /// <summary>Duplicita: horší verze, které navrhuji smazat (cesty z <see cref="Versions"/>).</summary>
    public List<string> DeletePaths { get; set; } = new();

    /// <summary>Lepší verze na Fastshare (upgrade, nebo duplicita pod 1080p). null = nic nenašel.</summary>
    public SuggestedFile? Candidate { get; set; }

    /// <summary>Bez metadat: co se vyčetlo z názvu souboru (název a rok pro hledání v TMDb).</summary>
    public string? GuessTitle { get; set; }

    public int? GuessYear { get; set; }

    /// <summary>"new" | "done" | "rejected".</summary>
    public string Status { get; set; } = "new";

    /// <summary>Co se s návrhem stalo („zařazeno ke stažení, starý soubor se po stažení smaže").</summary>
    public string? Note { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? DecidedUtc { get; set; }
}

/// <summary>Stav kontroly knihovny pro GUI.</summary>
public class AdvisorStatus
{
    public bool Running { get; set; }

    public int Done { get; set; }

    public int Total { get; set; }

    public string? Phase { get; set; }

    public DateTime? LastRunUtc { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Uložené návrhy + kdy se u filmu naposledy hledala lepší verze (ať se 880 filmů nehledá pokaždé).</summary>
public class SuggestionState
{
    public List<Suggestion> Items { get; set; } = new();

    /// <summary>ID filmu → kdy se naposledy hledala lepší verze (i když nic nenašel).</summary>
    public Dictionary<string, DateTime> UpgradeCheckedUtc { get; set; } = new();

    public DateTime? LastRunUtc { get; set; }

    public string? LastResult { get; set; }
}

/// <summary>Persistentní návrhy (JSON, atomický zápis jako fronta).</summary>
public sealed class SuggestionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _file;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private SuggestionState _state = new();

    public SuggestionStore(string file, Action<string> log)
    {
        _file = file;
        _log = log;
        try
        {
            if (File.Exists(file))
            {
                _state = JsonSerializer.Deserialize<SuggestionState>(File.ReadAllText(file)) ?? new SuggestionState();
            }
        }
        catch (Exception ex)
        {
            _log($"návrhy: stav se nepodařilo načíst ({ex.Message}), začínám znovu");
        }
    }

    public List<Suggestion> GetAll()
    {
        lock (_lock)
        {
            return _state.Items.Select(Clone).ToList();
        }
    }

    public Suggestion? Get(string id)
    {
        lock (_lock)
        {
            var s = _state.Items.FirstOrDefault(i => i.Id == id);
            return s == null ? null : Clone(s);
        }
    }

    /// <summary>
    /// Výsledek kontroly: nové návrhy přidá, existující „new" aktualizuje (kandidát, verze), rozhodnuté
    /// (done/rejected) nechá být. „new" návrhy, které už neplatí (duplicita smazána ručně, film
    /// identifikován), z kontrolovaných druhů odebere.
    /// </summary>
    public void Merge(IReadOnlyCollection<Suggestion> found, IReadOnlyCollection<string> kindsChecked)
    {
        lock (_lock)
        {
            var byId = _state.Items.ToDictionary(i => i.Id);
            foreach (var s in found)
            {
                if (byId.TryGetValue(s.Id, out var old))
                {
                    if (old.Status == "new")
                    {
                        s.CreatedUtc = old.CreatedUtc;
                        s.Candidate ??= old.Candidate; // kandidát z dřívějška, když se teď nehledalo
                        _state.Items[_state.Items.IndexOf(old)] = s;
                    }
                }
                else
                {
                    _state.Items.Add(s);
                }
            }

            var foundIds = found.Select(f => f.Id).ToHashSet();
            _state.Items.RemoveAll(i => i.Status == "new" && kindsChecked.Contains(i.Kind) && !foundIds.Contains(i.Id));
            SaveLocked();
        }
    }

    public void Update(string id, Action<Suggestion> mutate)
    {
        lock (_lock)
        {
            var s = _state.Items.FirstOrDefault(i => i.Id == id);
            if (s != null)
            {
                mutate(s);
                SaveLocked();
            }
        }
    }

    public DateTime? UpgradeChecked(string movieId)
    {
        lock (_lock)
        {
            return _state.UpgradeCheckedUtc.TryGetValue(movieId, out var t) ? t : null;
        }
    }

    public void MarkUpgradeChecked(string movieId)
    {
        lock (_lock)
        {
            _state.UpgradeCheckedUtc[movieId] = DateTime.UtcNow;
            SaveLocked();
        }
    }

    public (DateTime? LastRunUtc, string? LastResult) LastRun
    {
        get { lock (_lock) { return (_state.LastRunUtc, _state.LastResult); } }
    }

    public void SetLastRun(string result)
    {
        lock (_lock)
        {
            _state.LastRunUtc = DateTime.UtcNow;
            _state.LastResult = result;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex)
        {
            _log($"návrhy: uložení selhalo: {ex.Message}");
        }
    }

    private static Suggestion Clone(Suggestion s) =>
        JsonSerializer.Deserialize<Suggestion>(JsonSerializer.Serialize(s))!;
}
