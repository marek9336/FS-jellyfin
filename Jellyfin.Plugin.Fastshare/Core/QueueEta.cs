namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>Vstupy odhadu — nastavení, která ovlivňují, kdy se co stáhne.</summary>
public sealed class EtaSettings
{
    public bool UseWeeklyWindow { get; set; }

    public string? WeeklyWindow { get; set; }

    public int WindowFromHour { get; set; }

    public int WindowToHour { get; set; }

    /// <summary>Další okna ("22-3,12-13").</summary>
    public string? WindowExtraRanges { get; set; }

    /// <summary>„⚡ Stahovat teď (mimo okno)" — celá fronta bez okna a denního stropu.</summary>
    public bool IgnoreWindow { get; set; }

    /// <summary>Souběžná stahování.</summary>
    public int Slots { get; set; } = 1;

    public int PauseBetweenSeconds { get; set; }

    /// <summary>Denní strop v bajtech (0 = bez stropu), po dnech UTC jako ve workeru.</summary>
    public long DailyCapBytes { get; set; }

    /// <summary>Předpokládaná rychlost jednoho stahování (B/s).</summary>
    public long SpeedBps { get; set; }
}

/// <summary>Probíhající stahování (zabírá jeden souběžný slot).</summary>
public sealed class EtaActive
{
    public Guid Id { get; set; }

    public long BytesDone { get; set; }

    public long BytesTotal { get; set; }

    public long SpeedBps { get; set; }
}

/// <summary>Výsledek odhadu. Časy v UTC.</summary>
public sealed class EtaResult
{
    public Dictionary<Guid, DateTime> StartUtc { get; } = new();

    public Dictionary<Guid, DateTime> FinishUtc { get; } = new();

    /// <summary>Kdy bude staženo všechno (null = nejde spočítat).</summary>
    public DateTime? AllDoneUtc { get; set; }

    /// <summary>Rozvrh nemá v příštím týdnu žádné okno → zbytek fronty nemá odhad.</summary>
    public bool NoWindow { get; set; }

    /// <summary>Odhad přesáhl horizont — u zbytku fronty se nepočítá.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Odhad, kdy se která položka fronty stáhne. Přehraje worker nanečisto: pořadí (QueueOrder.PickNext — ⚡, ⏫,
/// filmy, seriály po dílech), souběžná stahování, časové okno / rozvrh po dnech, ⚡ a „stahovat mimo okno",
/// pauza mezi soubory, další pokus po chybě, denní strop. Rychlost = průměr posledních stažení.
/// Nepočítá s výpadky, chybami ani s přerušením na konci okna — je to odhad, ne slib. Čistý C#.
/// </summary>
public static class QueueEta
{
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(120);

    private const long FallbackEpisodeBytes = 1L * 1024 * 1024 * 1024;

    private const long FallbackMovieBytes = 4L * 1024 * 1024 * 1024;

    public static EtaResult Estimate(
        IReadOnlyList<QueueItem> queued,
        IReadOnlyList<EtaActive> active,
        EtaSettings s,
        DateTime nowLocal,
        long dailyBytesToday)
    {
        var result = new EtaResult();
        var speed = Math.Max(1, s.SpeedBps);
        var slots = Math.Max(1, s.Slots);
        var horizon = nowLocal.Add(Horizon);
        var pause = TimeSpan.FromSeconds(Math.Max(0, s.PauseBetweenSeconds));
        var capDay = nowLocal.ToUniversalTime().Date;
        var dayBytes = dailyBytesToday;

        // kdy se který slot uvolní
        var free = new List<DateTime>();
        foreach (var a in active.Take(slots))
        {
            var remaining = Math.Max(0, (a.BytesTotal > 0 ? a.BytesTotal : FallbackMovieBytes) - a.BytesDone);
            var finish = nowLocal.AddSeconds((double)remaining / (a.SpeedBps > 0 ? a.SpeedBps : speed));
            result.FinishUtc[a.Id] = finish.ToUniversalTime();
            free.Add(finish.Add(pause));
        }

        while (free.Count < slots)
        {
            free.Add(nowLocal);
        }

        var pending = queued.ToList();
        var guard = 0;
        while (pending.Count > 0 && guard++ < 20_000)
        {
            var next = QueueOrder.PickNext(pending)!;
            var slot = free.IndexOf(free.Min());
            var t = free[slot];
            if (next.RetryAfterUtc is { } retry && retry.ToLocalTime() > t && !next.ForceNow)
            {
                t = retry.ToLocalTime();
            }

            if (t > horizon)
            {
                result.Truncated = true;
                break;
            }

            var bypass = next.ForceNow || s.IgnoreWindow;
            if (!bypass)
            {
                for (var w = 0; w < 30 && !IsOpen(s, t); w++)
                {
                    var open = Schedule.NextOpen(s.UseWeeklyWindow, s.WeeklyWindow, s.WindowFromHour, s.WindowToHour, t, s.WindowExtraRanges);
                    if (open == null)
                    {
                        break;
                    }

                    t = open.Value;
                }

                if (!IsOpen(s, t))
                {
                    result.NoWindow = true;
                    break;
                }

                // denní strop (dny UTC): vyčerpaný → půlnoc UTC a znovu na okno
                if (s.DailyCapBytes > 0)
                {
                    var day = t.ToUniversalTime().Date;
                    if (day != capDay)
                    {
                        capDay = day;
                        dayBytes = 0;
                    }

                    if (dayBytes >= s.DailyCapBytes)
                    {
                        free[slot] = DateTime.SpecifyKind(capDay.AddDays(1), DateTimeKind.Utc).ToLocalTime();
                        continue;
                    }
                }
            }

            var size = RemainingOf(next, queued);
            var end = t.AddSeconds((double)size / speed);
            result.StartUtc[next.Id] = t.ToUniversalTime();
            result.FinishUtc[next.Id] = end.ToUniversalTime();
            CountBytes(end, size, ref capDay, ref dayBytes);
            free[slot] = end.Add(pause);
            pending.Remove(next);
        }

        if (pending.Count == 0)
        {
            result.AllDoneUtc = result.FinishUtc.Count > 0 ? result.FinishUtc.Values.Max() : null;
        }

        return result;
    }

    private static bool IsOpen(EtaSettings s, DateTime t) =>
        Schedule.IsOpen(s.UseWeeklyWindow, s.WeeklyWindow, s.WindowFromHour, s.WindowToHour, 0, t, s.WindowExtraRanges);

    private static void CountBytes(DateTime atLocal, long bytes, ref DateTime capDay, ref long dayBytes)
    {
        var day = atLocal.ToUniversalTime().Date;
        if (day != capDay)
        {
            capDay = day;
            dayBytes = 0;
        }

        dayBytes += bytes;
    }

    /// <summary>Kolik zbývá — u přerušeného stahování se navazuje (HTTP Range).</summary>
    private static long RemainingOf(QueueItem item, IReadOnlyList<QueueItem> all)
    {
        if (item.BytesTotal > 0 && item.BytesDone > 0 && item.BytesDone < item.BytesTotal)
        {
            return item.BytesTotal - item.BytesDone;
        }

        if (item.SizeBytes > 0)
        {
            return item.SizeBytes;
        }

        if (item.BytesTotal > 0)
        {
            return item.BytesTotal;
        }

        var known = all.Where(i => i.MediaType == item.MediaType && i.SizeBytes > 0).ToList();
        return known.Count > 0
            ? (long)known.Average(i => i.SizeBytes)
            : item.MediaType == MediaKind.Episode ? FallbackEpisodeBytes : FallbackMovieBytes;
    }
}
