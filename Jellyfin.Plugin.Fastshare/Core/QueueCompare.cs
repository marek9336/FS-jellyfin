namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Co položka fronty udělá s knihovnou: kolik místa ušetří / zabere, jak se změní kvalita.</summary>
public sealed class QueueComparison
{
    /// <summary>"replace" (nahradí stávající soubory), "beside" (přibude vedle stávající verze), "new" (titul v knihovně není).</summary>
    public string Kind { get; set; } = "new";

    public int OldCount { get; set; }

    public long OldBytes { get; set; }

    public long NewBytes { get; set; }

    /// <summary>O kolik knihovna po stažení naroste (záporné = místo se uvolní).</summary>
    public long DeltaBytes { get; set; }

    /// <summary>Úspora místa — jen u náhrady menším souborem, jinak 0.</summary>
    public long SavedBytes { get; set; }

    /// <summary>Úspora v procentech stávající velikosti (jen u náhrady).</summary>
    public int? SavedPercent { get; set; }

    public string? OldQuality { get; set; }

    public string? NewQuality { get; set; }

    /// <summary>Rozdíl úrovní kvality (SD → 1080p = +2); null = některá kvalita neznámá.</summary>
    public int? QualityStep { get; set; }

    public long? OldBitrate { get; set; }

    public long? NewBitrate { get; set; }

    /// <summary>Délka nové verze minus stávající (s); null = neznámá.</summary>
    public int? DurationDiffSec { get; set; }

    public string? OldLangs { get; set; }

    public string? NewLangs { get; set; }
}

/// <summary>Srovnání položky fronty se stávající verzí v knihovně. Čistý C#.</summary>
public static class QueueCompare
{
    /// <summary>
    /// Zapíše do položky, co je teď v knihovně: u náhrady mazané soubory (součet velikostí, nejlepší z nich pro kvalitu),
    /// jinak nejlepší stávající verzi titulu. Bez verzí = nový titul.
    /// </summary>
    public static void Snapshot(QueueItem item, IReadOnlyList<LibraryVersion> versions)
    {
        item.OldKnown = true;
        var replace = item.ReplacePaths is { Count: > 0 }
            ? versions.Where(v => item.ReplacePaths.Contains(v.Path, StringComparer.Ordinal)).ToList()
            : new List<LibraryVersion>();
        var pool = replace.Count > 0 ? replace : versions.ToList();
        if (pool.Count == 0)
        {
            item.OldFileCount = 0;
            return;
        }

        var best = LibraryFiles.BestFirst(pool)[0];
        item.OldFileCount = pool.Count;
        item.OldSizeBytes = replace.Count > 0 ? replace.Sum(v => v.SizeBytes) : best.SizeBytes;
        item.OldQuality = best.Quality;
        item.OldHeight = best.Height;
        item.OldBitrate = best.Bitrate;
        item.OldDurationSec = best.DurationSec;
        item.OldLangs = LangsText(best.AudioLangs, best.SubLangs);
    }

    /// <summary>„CZ, EN + CZ tit."; nic neznámého → null.</summary>
    public static string? LangsText(IReadOnlyCollection<string> audio, IReadOnlyCollection<string> subs)
    {
        if (audio.Count == 0 && subs.Count == 0)
        {
            return null;
        }

        var a = audio.Count > 0 ? string.Join(", ", audio) : "?";
        return subs.Count > 0 ? $"{a} + {string.Join(", ", subs)} tit." : a;
    }

    public static QueueComparison Of(QueueItem i)
    {
        var newBytes = i.SizeBytes > 0 ? i.SizeBytes : i.BytesTotal;
        var replace = i.ReplacePaths is { Count: > 0 };
        var c = new QueueComparison
        {
            Kind = replace ? "replace" : i.OldFileCount > 0 ? "beside" : "new",
            OldCount = i.OldFileCount,
            OldBytes = i.OldSizeBytes,
            NewBytes = newBytes,
            NewQuality = i.Quality,
            NewLangs = string.IsNullOrWhiteSpace(i.Language) ? null : i.Language,
            NewBitrate = newBytes > 0 && i.DurationSec > 0 ? newBytes * 8 / i.DurationSec : null,
        };
        if (c.Kind == "new")
        {
            c.DeltaBytes = newBytes;
            return c;
        }

        c.OldQuality = i.OldQuality;
        c.OldLangs = i.OldLangs;
        c.OldBitrate = i.OldBitrate;
        var ol = LibraryFiles.Level(i.OldQuality);
        var nl = LibraryFiles.Level(i.Quality);
        c.QualityStep = ol > 0 && nl > 0 ? nl - ol : null;
        c.DurationDiffSec = i.DurationSec > 0 && i.OldDurationSec > 0 ? i.DurationSec - i.OldDurationSec : null;
        if (replace && i.OldSizeBytes > 0 && newBytes > 0)
        {
            c.DeltaBytes = newBytes - i.OldSizeBytes;
            c.SavedBytes = Math.Max(0, -c.DeltaBytes);
            c.SavedPercent = (int)Math.Round(c.SavedBytes * 100.0 / i.OldSizeBytes);
        }
        else
        {
            c.DeltaBytes = newBytes; // vedle stávající verze (nebo neznámá velikost mazaných) — přibude celý soubor
        }

        return c;
    }

    /// <summary>Kolik položka ušetří — jen náhrada menším souborem, jinak 0 (nové soubory a zvýšení kvality s nárůstem se nepočítají).</summary>
    public static long Savings(QueueItem i) => Of(i).SavedBytes;
}
