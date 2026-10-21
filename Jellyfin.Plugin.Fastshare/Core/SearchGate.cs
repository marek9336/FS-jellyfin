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
    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTime _nextUtc = DateTime.MinValue;

    public SearchGate(Func<int> perHour)
    {
        _perHour = perHour;
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
            var now = DateTime.UtcNow;
            if (_nextUtc > now)
            {
                onWait?.Invoke(_nextUtc);
                await Task.Delay(_nextUtc - now, ct).ConfigureAwait(false);
            }

            _nextUtc = DateTime.UtcNow + Interval;
        }
        finally
        {
            _lock.Release();
        }
    }
}
