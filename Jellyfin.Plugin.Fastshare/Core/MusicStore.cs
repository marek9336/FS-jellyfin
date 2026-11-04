using System.Text.Json;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Uložené návrhy záložky Hudba (music.json). Odmítnuté a vyřízené se při další kontrole nevrátí.</summary>
public sealed class MusicStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _file;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private State _state = new();

    public sealed class State
    {
        public List<MusicSuggestion> Items { get; set; } = new();

        public DateTime? LastRunUtc { get; set; }

        public string? LastResult { get; set; }

        /// <summary>Kolik skladeb knihovna měla při poslední kontrole a kolik zabírala.</summary>
        public int Tracks { get; set; }

        public long Bytes { get; set; }
    }

    public MusicStore(string file, Action<string> log)
    {
        _file = file;
        _log = log;
        try
        {
            if (File.Exists(file))
            {
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(file)) ?? new State();
            }
        }
        catch (Exception ex)
        {
            _log($"hudba: stav se nepodařilo načíst ({ex.Message}), začínám znovu");
        }
    }

    public (DateTime? LastRunUtc, string? LastResult, int Tracks, long Bytes) LastRun
    {
        get
        {
            lock (_lock)
            {
                return (_state.LastRunUtc, _state.LastResult, _state.Tracks, _state.Bytes);
            }
        }
    }

    public List<MusicSuggestion> GetAll()
    {
        lock (_lock)
        {
            return _state.Items.Select(Clone).ToList();
        }
    }

    public MusicSuggestion? Get(string id)
    {
        lock (_lock)
        {
            var s = _state.Items.FirstOrDefault(i => i.Id == id);
            return s == null ? null : Clone(s);
        }
    }

    /// <summary>
    /// Výsledek kontroly: nové návrhy přidat, „new" přepsat aktuálním stavem, rozhodnuté nechat; „new", které už
    /// nejsou (soubor smazán, tagy opravené), zmizí. Vyřízené dávkové karty (smetí, nízký tok) se vrátí, když přibylo něco nového.
    /// </summary>
    public void Merge(IReadOnlyCollection<MusicSuggestion> found, string result, int tracks, long bytes)
    {
        lock (_lock)
        {
            var byId = _state.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
            foreach (var s in found)
            {
                if (!byId.TryGetValue(s.Id, out var old))
                {
                    _state.Items.Add(s);
                    continue;
                }

                var newPaths = s.Tracks.Select(t => t.Path).Except(old.Tracks.Select(t => t.Path), StringComparer.Ordinal).Any();
                if (old.Status == "new" || (old.Status == "done" && newPaths))
                {
                    s.CreatedUtc = old.CreatedUtc;
                    _state.Items[_state.Items.IndexOf(old)] = s;
                }
            }

            var ids = found.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            _state.Items.RemoveAll(i => i.Status == "new" && !ids.Contains(i.Id));
            _state.LastRunUtc = DateTime.UtcNow;
            _state.LastResult = result;
            _state.Tracks = tracks;
            _state.Bytes = bytes;
            SaveLocked();
        }
    }

    public void Update(string id, Action<MusicSuggestion> mutate)
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

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOpts));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex)
        {
            _log($"hudba: uložení selhalo ({ex.Message})");
        }
    }

    private static MusicSuggestion Clone(MusicSuggestion s) =>
        JsonSerializer.Deserialize<MusicSuggestion>(JsonSerializer.Serialize(s))!;
}
