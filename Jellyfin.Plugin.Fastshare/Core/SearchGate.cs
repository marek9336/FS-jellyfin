namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Strop automatických dotazů na hledání Fastshare (kontrola knihovny, hlídač): nejvýš N za hodinu,
/// rovnoměrně rozložených. Fastshare limit nezveřejňuje — výchozích 20/h je opatrnost, ať se API
/// nezahltí (kontrola 1 100 filmů pak trvá zhruba dva a půl dne, ale nikoho neruší).
/// Ruční hledání v GUI strop nemá. Limit se čte při každém volání (změna v nastavení platí hned).
/// </summary>
public sealed class SearchGate
{
    private readonly Func<int> _perHour;
    private readonly Func<string?> _hours;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _nextUtc = DateTime.MinValue;

    public SearchGate(Func<int> perHour, Func<string?>? hours = null)
    {
        _perHour = perHour;
        _hours = hours ?? (() => null);
    }

    /// <summary>Je místní čas v okně hledání? Prázdné okno = kdykoli; „22-3" přes půlnoc; „5-5" celý den.</summary>
    public static bool InWindow(IReadOnlyList<Schedule.Range> ranges, DateTime local) =>
        ranges.Count == 0 || ranges.Any(r => r.AllDay
            || (r.From < r.To ? local.Hour >= r.From && local.Hour < r.To : local.Hour >= r.From || local.Hour < r.To));

    /// <summary>Začátek nejbližšího okna od `local` (místní čas). V okně = `local`.</summary>
    public static DateTime NextWindow(IReadOnlyList<Schedule.Range> ranges, DateTime local)
    {
        if (InWindow(ranges, local))
        {
            return local;
        }

        var t = local.Date.AddHours(local.Hour + 1);
        for (var i = 0; i < 48 && !InWindow(ranges, t); i++)
        {
            t = t.AddHours(1);
        }

        return t;
    }

    /// <summary>Kolik ze dne je okno hledání (pro odhad zbývajícího času).</summary>
    public double WindowFraction
    {
        get
        {
            var r = Schedule.ParseRanges(_hours());
            return r.Count == 0 ? 1 : Math.Max(1, Enumerable.Range(0, 24).Count(h => InWindow(r, DateTime.Today.AddHours(h)))) / 24.0;
        }
    }

    /// <summary>Popis okna do hlášek („1–7 h"), null = kdykoli.</summary>
    public string? WindowText
    {
        get
        {
            var r = Schedule.ParseRanges(_hours());
            return r.Count == 0 ? null : string.Join(", ", r.Select(x => $"{x.From}–{x.To} h"));
        }
    }

    /// <summary>Kdy nejdřív smí jít další dotaz.</summary>
    public DateTime NextUtc => _nextUtc;

    public TimeSpan Interval
    {
        get
        {
            var n = _perHour();
            return n <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(3600.0 / n);
        }
    }

    /// <summary>Počká na svůj slot. `onWait` dostane čas, kdy dotaz půjde (pro stavovou hlášku).</summary>
    public async Task WaitAsync(CancellationToken ct, Action<DateTime>? onWait = null)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                // nejdřív smí jít po rozestupu, a jen v okně hledání (začátek okna + pár minut náhodně)
                var now = DateTime.UtcNow;
                var at = _nextUtc > now ? _nextUtc : now;
                var ranges = Schedule.ParseRanges(_hours());
                if (!InWindow(ranges, at.ToLocalTime()))
                {
                    at = NextWindow(ranges, at.ToLocalTime()).ToUniversalTime() + TimeSpan.FromMinutes(Random.Shared.Next(1, 20));
                    _nextUtc = at;
                }

                if (at <= now)
                {
                    break;
                }

                onWait?.Invoke(at);
                // po kouscích — změna okna / stropu v nastavení platí hned
                await Task.Delay(at - now < TimeSpan.FromMinutes(10) ? at - now : TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            }

            // rozestup s náhodou ±25 %, ať dotazy nechodí jako hodinky
            _nextUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Interval.TotalSeconds * (0.75 + (Random.Shared.NextDouble() * 0.5)));
        }
        finally
        {
            _lock.Release();
        }
    }
}
