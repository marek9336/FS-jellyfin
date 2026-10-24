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

public sealed class QueueOrderTests
{
    private static QueueItem Ep(string series, int s, int e, int idx, bool force = false) =>
        new() { Title = series, SeriesTitle = series, MediaType = MediaKind.Episode, Season = s, Episode = e, SortIndex = idx, ForceNow = force, AddedUtc = DateTime.UtcNow.AddMinutes(idx) };

    private static QueueItem Movie(string title, int idx, bool force = false) =>
        new() { Title = title, MediaType = MediaKind.Movie, SortIndex = idx, ForceNow = force, AddedUtc = DateTime.UtcNow.AddMinutes(idx) };

    [Fact]
    public void MoviesFirstSeriesGroupedForceWins()
    {
        var items = new List<QueueItem>
        {
            Ep("Dexter", 1, 2, 1), Ep("Mentalista", 1, 1, 2), Ep("Dexter", 1, 1, 3), Movie("Pelíšky", 4), Ep("Mentalista", 1, 2, 5), Movie("Kytice", 6),
        };
        var order = QueueOrder.ByPriority(items).Select(i => i.MediaType == MediaKind.Movie ? i.Title : $"{i.Title} {i.Episode}").ToList();
        Assert.Equal(["Pelíšky", "Kytice", "Dexter 1", "Dexter 2", "Mentalista 1", "Mentalista 2"], order);

        // ⚡ u seriálu → před filmy
        items.Add(Ep("Černobyl", 1, 1, 7, force: true));
        Assert.Equal("Černobyl", QueueOrder.ByPriority(items).First().Title);
    }
}
