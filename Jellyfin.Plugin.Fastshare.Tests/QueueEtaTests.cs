using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class QueueEtaTests
{
    private const long Mb = 1024 * 1024;

    private static QueueItem Movie(string t, long mb, int idx, bool force = false) =>
        new() { Title = t, MediaType = MediaKind.Movie, SizeBytes = mb * Mb, SortIndex = idx, ForceNow = force, AddedUtc = DateTime.UtcNow.AddMinutes(idx) };

    private static readonly DateTime Noon = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void TwoSlotsInWindow()
    {
        // 1 MB/s, 2 souběžně, okno celý den: 2× 600 MB hned (10 min), třetí po prvním
        var q = new List<QueueItem> { Movie("A", 600, 1), Movie("B", 600, 2), Movie("C", 600, 3) };
        var s = new EtaSettings { WindowFromHour = 0, WindowToHour = 0, Slots = 2, SpeedBps = Mb };
        var r = QueueEta.Estimate(q, [], s, Noon, 0);
        Assert.Equal(Noon, r.StartUtc[q[0].Id].ToLocalTime());
        Assert.Equal(Noon, r.StartUtc[q[1].Id].ToLocalTime());
        Assert.Equal(Noon.AddMinutes(10), r.StartUtc[q[2].Id].ToLocalTime());
        Assert.Equal(Noon.AddMinutes(20), r.AllDoneUtc!.Value.ToLocalTime());
    }

    [Fact]
    public void WaitsForWindowUnlessForced()
    {
        // okno 0–17 h, teď 18:00 → začne o půlnoci; ⚡ hned; „stahovat mimo okno" celé hned
        var now = new DateTime(2026, 10, 30, 18, 0, 0, DateTimeKind.Local);
        var q = new List<QueueItem> { Movie("A", 60, 1), Movie("B", 60, 2, force: true) };
        var s = new EtaSettings { WindowFromHour = 0, WindowToHour = 17, Slots = 1, SpeedBps = Mb };
        var r = QueueEta.Estimate(q, [], s, now, 0);
        Assert.Equal(now, r.StartUtc[q[1].Id].ToLocalTime()); // ⚡ jde první a hned
        Assert.Equal(now.Date.AddDays(1), r.StartUtc[q[0].Id].ToLocalTime());

        s.IgnoreWindow = true;
        r = QueueEta.Estimate(q, [], s, now, 0);
        Assert.Equal(now.AddMinutes(1), r.StartUtc[q[0].Id].ToLocalTime());
    }

    [Fact]
    public void ActiveDownloadOccupiesSlotAndReorderMatters()
    {
        var q = new List<QueueItem> { Movie("A", 600, 1), Movie("B", 60, 2) };
        var active = new List<EtaActive> { new() { Id = Guid.NewGuid(), BytesDone = 300 * Mb, BytesTotal = 600 * Mb, SpeedBps = Mb } };
        var s = new EtaSettings { WindowFromHour = 0, WindowToHour = 0, Slots = 1, SpeedBps = Mb };
        var r = QueueEta.Estimate(q, active, s, Noon, 0);
        Assert.Equal(Noon.AddMinutes(5), r.StartUtc[q[0].Id].ToLocalTime()); // po doběhnutí rozjetého
        Assert.Equal(Noon.AddMinutes(15), r.StartUtc[q[1].Id].ToLocalTime());

        // předběhnutí (▲ / ⚡) → B hned po rozjetém
        q[1].SortIndex = 0;
        r = QueueEta.Estimate(q, active, s, Noon, 0);
        Assert.Equal(Noon.AddMinutes(5), r.StartUtc[q[1].Id].ToLocalTime());
        Assert.Equal(Noon.AddMinutes(6), r.StartUtc[q[0].Id].ToLocalTime());
    }
}
