using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class QueueCompareTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static Suggestion Sug(params LibraryVersion[] v) => new()
    {
        Id = "up:x",
        Kind = "upgrade",
        Title = "Film",
        Versions = v.ToList(),
        Candidate = new SuggestedFile { FileId = "f", FileName = "Film.1080p.mkv", SizeBytes = 3 * GB, Quality = "1080p", DurationSec = 6000, Language = "CZ" },
    };

    private static LibraryVersion V(string path, long size, string q, int? dur = 6000) =>
        new() { Path = path, SizeBytes = size, Quality = q, DurationSec = dur, AudioLangs = ["CZ"] };

    [Fact]
    public void ReplaceSmallerSavesSpaceAndShowsQuality()
    {
        var item = QueueItemFactory.FromSuggestion(Sug(V("/a.mkv", 5 * GB, "720p"), V("/b.avi", 1 * GB, "SD")), replace: true);
        var c = QueueCompare.Of(item);
        Assert.Equal("replace", c.Kind);
        Assert.Equal(2, c.OldCount);
        Assert.Equal(6 * GB, c.OldBytes); // obě mazané verze dohromady
        Assert.Equal(3 * GB, c.SavedBytes);
        Assert.Equal(50, c.SavedPercent);
        Assert.Equal(1, c.QualityStep); // nejlepší stávající 720p → 1080p
        Assert.Equal("CZ", c.OldLangs);
        Assert.Equal(0, c.DurationDiffSec);
    }

    [Fact]
    public void ReplaceBiggerOrBesideIsNotSaving()
    {
        var bigger = QueueItemFactory.FromSuggestion(Sug(V("/a.mkv", 2 * GB, "720p")), replace: true);
        Assert.Equal(0, QueueCompare.Savings(bigger));
        Assert.Equal(1 * GB, QueueCompare.Of(bigger).DeltaBytes);

        var beside = QueueItemFactory.FromSuggestion(Sug(V("/a.mkv", 5 * GB, "720p")), replace: false);
        var c = QueueCompare.Of(beside);
        Assert.Equal("beside", c.Kind);
        Assert.Equal(0, c.SavedBytes);
        Assert.Equal(3 * GB, c.DeltaBytes);
        Assert.Equal(5 * GB, c.OldBytes); // srovnání s nejlepší stávající verzí

        var fresh = QueueItemFactory.FromSuggestion(Sug(), replace: false);
        Assert.Equal("new", QueueCompare.Of(fresh).Kind);
        Assert.Equal(3 * GB, QueueCompare.Of(fresh).DeltaBytes);
    }

    [Fact]
    public void SortMoviesBySavingsLeavesSeriesAndNonSavers()
    {
        var path = Path.Combine(Path.GetTempPath(), "fs-q-" + Guid.NewGuid() + ".json");
        try
        {
            var q = new DownloadQueue(path, _ => { });
            QueueItem Movie(string id, long oldSize, bool replace)
            {
                var s = Sug(V("/" + id + ".mkv", oldSize, "1080p"));
                s.Title = id;
                s.Candidate!.FileId = id;
                return QueueItemFactory.FromSuggestion(s, replace);
            }

            var newMovie = new QueueItem { FileId = "new", Title = "Nový", SizeBytes = 4 * GB, OldKnown = true };
            var ep = new QueueItem { FileId = "ep", Title = "Seriál", SeriesTitle = "Seriál", MediaType = MediaKind.Episode, Season = 1, Episode = 1 };
            q.Add(newMovie);
            q.Add(Movie("small", 4 * GB, replace: true)); // ušetří 1 GB
            q.Add(ep);
            q.Add(Movie("bigger", 2 * GB, replace: true)); // naroste — bez úspory
            q.Add(Movie("big", 10 * GB, replace: true)); // ušetří 7 GB
            q.Add(Movie("beside", 9 * GB, replace: false)); // nic nemaže

            var (changed, count, saved) = q.SortMoviesBySavings();
            Assert.True(changed);
            Assert.Equal(2, count);
            Assert.Equal(8 * GB, saved);
            var order = QueueOrder.ByPriority(q.GetAll()).Select(i => i.FileId).ToList();
            Assert.Equal(["big", "small", "new", "bigger", "beside", "ep"], order);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
