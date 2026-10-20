namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Společný limit rychlosti pro všechna souběžná stahování (token bucket na dluh):
/// každé čtení si odebere bajty, a když jich je víc, než limit za uplynulý čas dovolí,
/// počká, dokud se dluh nesplatí. Limit se čte při každém volání, takže změna
/// v nastavení platí hned i pro běžící stahování. 0 = bez limitu.
/// </summary>
public sealed class SpeedLimiter
{
    private readonly Func<long> _bytesPerSecond;
    private readonly object _lock = new();
    private double _tokens;
    private DateTime _lastUtc = DateTime.UtcNow;

    public SpeedLimiter(Func<long> bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
    }

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        TimeSpan wait;
        lock (_lock)
        {
            var rate = _bytesPerSecond();
            var now = DateTime.UtcNow;
            if (rate <= 0)
            {
                _tokens = 0;
                _lastUtc = now;
                return;
            }

            // Doplnit podle uplynulého času; zásoba nejvýš na 1 s (žádné nárazy po pauze)
            _tokens = Math.Min(rate, _tokens + ((now - _lastUtc).TotalSeconds * rate));
            _lastUtc = now;
            _tokens -= bytes;
            if (_tokens >= 0)
            {
                return;
            }

            wait = TimeSpan.FromSeconds(Math.Min(2.0, -_tokens / rate));
        }

        if (wait > TimeSpan.FromMilliseconds(5))
        {
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }
}
