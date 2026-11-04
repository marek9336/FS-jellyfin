using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Fastshare.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Kontrola hudební knihovny: jen soubory na disku a údaje z Jellyfinu — nic se nehledá na internetu.
/// Duplicity (bajtově stejné, stejná skladba), smetí, nízký tok, chybějící tagy, rozbitá diakritika.
/// Opravené tagy se zapisují jen do Jellyfinu (soubory zůstanou netknuté) a položka se zamkne, ať je sken nepřepíše.
/// </summary>
public sealed class MusicAdvisor
{
    private const int HashBlock = 64 * 1024;

    private readonly FsState _state;
    private readonly ILibraryManager _library;
    private readonly ILogger<MusicAdvisor> _logger;
    private int _running;

    public MusicAdvisor(FsState state, ILibraryManager library, ILogger<MusicAdvisor> logger)
    {
        _state = state;
        _library = library;
        _logger = logger;
    }

    public bool Running => _running == 1;

    public string? Phase { get; private set; }

    /// <summary>Kořeny hudebních knihoven Jellyfinu.</summary>
    public List<string> MusicRoots() =>
        _library.GetVirtualFolders()
            .Where(f => f.CollectionType?.ToString()?.Equals("music", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => Path.GetFullPath(l).TrimEnd(Path.DirectorySeparatorChar))
            .ToList();

    /// <summary>Spustí kontrolu na pozadí. false = už běží.</summary>
    public bool Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fastshare: kontrola hudby selhala");
                Phase = null;
            }
            finally
            {
                Phase = null;
                Interlocked.Exchange(ref _running, 0);
            }
        });
        return true;
    }

    private void Run()
    {
        var roots = MusicRoots();
        if (roots.Count == 0)
        {
            _state.Music.Merge(Array.Empty<MusicSuggestion>(), "Jellyfin nemá hudební knihovnu.", 0, 0);
            return;
        }

        Phase = "Načítám skladby z Jellyfinu";
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            IsVirtualItem = false,
        });
        var tracks = items.OfType<Audio>().Where(a => !string.IsNullOrEmpty(a.Path)).Select(ToTrack).ToList();

        // bajtově stejné: otisk jen u souborů, jejichž velikost má i jiný soubor (pár set čtení po 128 kB, ne celá knihovna)
        var sameSize = tracks.Where(t => t.SizeBytes > 0).GroupBy(t => t.SizeBytes).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        Phase = $"Porovnávám obsah {sameSize.Count} souborů stejné velikosti";
        foreach (var t in sameSize)
        {
            t.ContentKey = ContentKey(t.Path, t.SizeBytes);
        }

        Phase = "Hledám duplicity a chybějící tagy";
        var cfg = Plugin.Instance?.Configuration;
        var found = new List<MusicSuggestion>();
        foreach (var root in roots)
        {
            var inRoot = tracks.Where(t => t.Path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList();
            found.AddRange(MusicAnalyzer.Analyze(inRoot, root, new MusicAnalyzer.Options { MinBitrateKbps = cfg?.MusicMinBitrateKbps ?? 128 }));
        }

        var bytes = tracks.Sum(t => t.SizeBytes);
        var result = $"{tracks.Count} skladeb ({Dedup.FormatSize(bytes)}): "
            + $"{found.Count(f => f.Kind is "same" or "song")} duplicit, {found.Where(f => f.Kind == "meta").Sum(f => f.Tracks.Count)} bez tagů, "
            + $"{found.Where(f => f.Kind == "charset").Sum(f => f.Tracks.Count)} s rozbitou diakritikou, {found.Where(f => f.Kind == "low").Sum(f => f.Tracks.Count)} s nízkým tokem";
        _state.Music.Merge(found, result, tracks.Count, bytes);
        _logger.LogInformation("Fastshare: hudba prošlá — {Result}", result);
    }

    private static MusicTrack ToTrack(Audio a)
    {
        var size = a.Size ?? 0;
        var dur = a.RunTimeTicks is > 0 ? (int)(a.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : 0;
        return new MusicTrack
        {
            ItemId = a.Id.ToString("N"),
            Path = a.Path,
            SizeBytes = size,
            Bitrate = a.TotalBitrate is > 0 ? a.TotalBitrate.Value : dur > 0 ? size * 8 / dur : 0,
            DurationSec = dur,
            Title = a.Name ?? string.Empty,
            Artists = a.Artists?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>(),
            AlbumArtist = a.AlbumArtists?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
            Album = a.Album,
            Year = a.ProductionYear,
            TrackNo = a.IndexNumber,
        };
    }

    /// <summary>Velikost + SHA-1 prvních a posledních 64 kB (stejné soubory se liší nejspíš tagy na začátku / konci).</summary>
    private string? ContentKey(string path, long size)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, HashBlock);
            var buf = new byte[HashBlock * 2];
            var n = f.Read(buf, 0, HashBlock);
            if (size > HashBlock * 2)
            {
                f.Seek(-HashBlock, SeekOrigin.End);
                n += f.Read(buf, n, HashBlock);
            }

            return size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(SHA1.HashData(buf.AsSpan(0, n)))[..16];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Fastshare: {Path} nejde přečíst: {Msg}", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Zapíše tagy do Jellyfinu (název, interpret, album, rok, číslo stopy) a položku zamkne — soubor se nemění.
    /// Vrací počet zapsaných.
    /// </summary>
    public async Task<int> ApplyTagsAsync(IReadOnlyList<MusicTags> tags, CancellationToken ct)
    {
        var n = 0;
        foreach (var t in tags)
        {
            if (_library.FindByPath(t.Path, false) is not Audio a)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(t.Title))
            {
                a.Name = t.Title.Trim();
            }

            if (!string.IsNullOrWhiteSpace(t.Artist))
            {
                var artist = t.Artist.Trim();
                a.Artists = new[] { artist };
                if (a.AlbumArtists == null || a.AlbumArtists.Count == 0 || a.AlbumArtists.All(MusicGuess.IsJunkTag) || Mojibake.Fix(a.AlbumArtists[0]) != null)
                {
                    a.AlbumArtists = new[] { artist };
                }
            }

            if (!string.IsNullOrWhiteSpace(t.Album))
            {
                a.Album = t.Album.Trim();
            }

            if (t.Year is >= 1900 and <= 2100)
            {
                a.ProductionYear = t.Year;
            }

            if (t.TrackNo is > 0 and < 1000)
            {
                a.IndexNumber = t.TrackNo;
            }

            a.IsLocked = true; // sken souboru bez tagů by název přepsal zpátky na jméno souboru
            await a.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }
}
