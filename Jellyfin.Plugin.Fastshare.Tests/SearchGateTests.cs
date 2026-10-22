using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class SearchGateTests
{
    private static DateTime At(int h, int m = 0) => new(2026, 10, 22, h, m, 0, DateTimeKind.Local);

    [Theory]
    [InlineData("1-7", 0, false)]
    [InlineData("1-7", 1, true)]
    [InlineData("1-7", 6, true)]
    [InlineData("1-7", 7, false)]
    [InlineData("22-3", 23, true)]
    [InlineData("22-3", 2, true)]
    [InlineData("22-3", 12, false)]
    [InlineData("1-7,13-15", 14, true)]
    [InlineData("", 12, true)]
    public void Window(string spec, int hour, bool open) =>
        Assert.Equal(open, SearchGate.InWindow(Schedule.ParseRanges(spec), At(hour, 30)));

    [Fact]
    public void NextWindowStart()
    {
        var r = Schedule.ParseRanges("1-7,13-15");
        Assert.Equal(At(13), SearchGate.NextWindow(r, At(9, 15)));
        Assert.Equal(At(1).AddDays(1), SearchGate.NextWindow(r, At(16, 40)));
        Assert.Equal(At(2, 10), SearchGate.NextWindow(r, At(2, 10)));
    }

    [Fact]
    public async Task WaitsForWindowAndSpacesQueries()
    {
        // okno celý den → první dotaz hned, další s rozestupem 3600/n ± 25 % (n nejvýš 60)
        var gate = new SearchGate(() => 60, () => "5-5");
        await gate.WaitAsync(CancellationToken.None);
        var gap = gate.NextUtc - DateTime.UtcNow;
        Assert.InRange(gap.TotalSeconds, 44, 76);

        // mimo okno → čeká (zrušení během čekání)
        var closed = new SearchGate(() => 3600, () => $"{(DateTime.Now.Hour + 2) % 24}-{(DateTime.Now.Hour + 3) % 24}");
        DateTime? waitUntil = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closed.WaitAsync(cts.Token, t => waitUntil = t));
        Assert.NotNull(waitUntil);
        Assert.True(waitUntil > DateTime.UtcNow.AddMinutes(30));
    }
}
