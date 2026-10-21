using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-lib-" + Guid.NewGuid());

    private string Lib => Path.Combine(_root, "Filmy");

    [Fact]
    public void BestVersionAlenka404Over360()
    {
        // skutečné verze z knihovny (21. 10. 2026)
        var v360 = new LibraryVersion { Path = "a.avi", Width = 640, Height = 340, SizeBytes = 1080L << 20, Quality = LibraryFiles.QualityOf(640, 340) };
        var v404 = new LibraryVersion { Path = "b.avi", Width = 720, Height = 384, SizeBytes = 1431L << 20, Quality = LibraryFiles.QualityOf(720, 384) };
        Assert.Equal("SD", v360.Quality);
        Assert.Equal("SD", v404.Quality);
        Assert.Equal("b.avi", LibraryFiles.BestFirst([v360, v404])[0].Path);
    }

    [Fact]
    public void QualityBeatsResolutionAndSize()
    {
        var hd = new LibraryVersion { Path = "hd", Width = 1920, Height = 800, SizeBytes = 1L << 30, Quality = LibraryFiles.QualityOf(1920, 800) };
        var sd = new LibraryVersion { Path = "sd", Width = 720, Height = 576, SizeBytes = 4L << 30, Quality = LibraryFiles.QualityOf(720, 576) };
        Assert.Equal("1080p", hd.Quality);
        Assert.Equal("hd", LibraryFiles.BestFirst([sd, hd])[0].Path);
    }

    [Fact]
    public void DeleteVideoWithSidecarsAndEmptyFolder()
    {
        var dir = Path.Combine(Lib, "Alenka (2016)");
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "Alenka 360p.avi");
        var b = Path.Combine(dir, "Alenka 404p.avi");
        File.WriteAllText(a, "x");
        File.WriteAllText(Path.Combine(dir, "Alenka 360p.cs.srt"), "x");
        File.WriteAllText(b, "x");
        File.WriteAllText(Path.Combine(dir, "Alenka 404p.srt"), "x");
        File.WriteAllText(Path.Combine(dir, "poster.jpg"), "x");

        var deleted = LibraryFiles.DeleteVideo(a, [Lib]);
        Assert.Equal(2, deleted.Count); // video + jeho titulky
        Assert.True(File.Exists(b));
        Assert.True(File.Exists(Path.Combine(dir, "Alenka 404p.srt")));
        Assert.True(File.Exists(Path.Combine(dir, "poster.jpg")));

        // poslední video ve složce → pryč i složka filmu (plakát apod.)
        LibraryFiles.DeleteVideo(b, [Lib]);
        Assert.False(Directory.Exists(dir));
        Assert.True(Directory.Exists(Lib)); // knihovna sama zůstane
    }

    [Fact]
    public void NeverDeletesLibraryRootOrOutside()
    {
        Directory.CreateDirectory(Lib);
        var inRoot = Path.Combine(Lib, "Film.avi");
        File.WriteAllText(inRoot, "x");
        LibraryFiles.DeleteVideo(inRoot, [Lib]);
        Assert.True(Directory.Exists(Lib)); // video přímo v knihovně: složka knihovny zůstane

        var outside = Path.Combine(_root, "jinde.avi");
        File.WriteAllText(outside, "x");
        Assert.Throws<InvalidOperationException>(() => LibraryFiles.DeleteVideo(outside, [Lib]));
        Assert.Throws<InvalidOperationException>(() => LibraryFiles.DeleteVideo(Path.Combine(Lib, "..", "jinde.avi"), [Lib]));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void RejectedSuggestionSurvivesRescan()
    {
        var store = new SuggestionStore(Path.Combine(_root, "s.json"), _ => { });
        store.Merge([new Suggestion { Id = "up:1", Kind = "upgrade", Title = "A" }, new Suggestion { Id = "dup:2", Kind = "duplicate", Title = "B" }], ["upgrade", "duplicate"]);
        store.Update("up:1", s => s.Status = "rejected");

        // další kontrola najde obojí znovu + duplicita B už neexistuje
        store.Merge([new Suggestion { Id = "up:1", Kind = "upgrade", Title = "A2" }], ["upgrade", "duplicate"]);
        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal("rejected", all[0].Status);
        Assert.Equal("A", all[0].Title); // rozhodnutý návrh se nepřepíše

        // a přežije restart (soubor)
        var again = new SuggestionStore(Path.Combine(_root, "s.json"), _ => { });
        Assert.Equal("rejected", again.Get("up:1")!.Status);
    }

    [Fact]
    public void UpsertKeepsDecisionAndSurvivesInterruptedRun()
    {
        var store = new SuggestionStore(Path.Combine(_root, "u.json"), _ => { });
        store.Upsert(new Suggestion { Id = "up:1", Kind = "upgrade", Title = "A" });
        store.Update("up:1", s => s.Status = "rejected");
        store.Upsert(new Suggestion { Id = "up:1", Kind = "upgrade", Title = "A2" });
        store.Upsert(new Suggestion { Id = "up:2", Kind = "upgrade", Title = "B" });

        // přerušená kontrola (výpadek) slučuje jen identify+duplicate → návrhy lepších verzí zůstanou
        store.Merge([], ["identify", "duplicate"]);
        var all = store.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("rejected", all.Single(x => x.Id == "up:1").Status);
        Assert.Equal("A", all.Single(x => x.Id == "up:1").Title);
    }

    [Fact]
    public async Task SearchGateSpacesQueries()
    {
        var gate = new SearchGate(() => 7200); // 2 za sekundu → odstup 0,5 s
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        Assert.True(sw.Elapsed >= TimeSpan.FromSeconds(0.9), $"jen {sw.Elapsed}");
        Assert.Equal(TimeSpan.FromSeconds(180), new SearchGate(() => 20).Interval);
        Assert.Equal(TimeSpan.Zero, new SearchGate(() => 0).Interval);
    }

    [Fact]
    public void DoubleExtensionNotInTitle()
    {
        var f = new FsFile { FileName = "Fontána-pre-Zuzanu-3.avi.avi" };
        FileNameParser.Fill(f);
        Assert.Equal("Fontána pre Zuzanu 3", f.Title);
    }

    // Skutečné případy z knihovny (22. 10. 2026)
    [Theory]
    [InlineData("/Filmy/Marvel/THOR/08. Thor The Dark World (2013).mkv", false)]
    [InlineData("/Filmy/Thor Temný svět.mkv", false)]
    [InlineData("/Filmy/Titanic (Katastrofický, Drama, USA, 1997).avi", true)]
    [InlineData("/Filmy/Chernobyl.S01E03.Open.Wide.O.Earth.CZ.avi", true)]
    public void MovieSuspectThor(string path, bool suspect)
    {
        var v = new LibraryVersion { Path = path };
        var why = LibraryFiles.MovieSuspect(v, "Thor: Temný svět", "Thor: The Dark World", 2013);
        Assert.Equal(suspect, why != null);
    }

    [Theory]
    [InlineData("/Serialy/červený trpaslík/Červený-trpaslík-05x01-Hololoď-(Holoship)-.Title2.avi", true)]
    [InlineData("/Serialy/červený trpaslík/Červený-trpaslík-01x02-Ozvěny-budoucnosti-(Future-Echoes).avi", false)]
    [InlineData("/Serialy/červený trpaslík/Season 01/Epizoda bez čísla.avi", false)]
    public void EpisodeSuspectRedDwarf(string path, bool suspect)
    {
        var v = new LibraryVersion { Path = path };
        Assert.Equal(suspect, LibraryFiles.EpisodeSuspect(v, 1, 2) != null);
        if (suspect)
        {
            Assert.Equal(5, v.FileSeason);
            Assert.Equal(1, v.FileEpisode);
        }
    }

    [Fact]
    public void SuspectAndLongerVersionsNotPreselected()
    {
        var best = new LibraryVersion { Path = "a", DurationSec = 7000 };
        var worseSame = new LibraryVersion { Path = "b", DurationSec = 7050 };
        var extended = new LibraryVersion { Path = "c", DurationSec = 8200 };
        var wrong = new LibraryVersion { Path = "d", DurationSec = 7000, Suspect = "jiný film" };
        var list = new List<LibraryVersion> { best, worseSame, extended, wrong };
        LibraryFiles.MarkLengths(list);
        Assert.Null(worseSame.LengthNote);
        Assert.Contains("delší", extended.LengthNote);
        Assert.Equal(["b"], LibraryFiles.DefaultDelete(list));
    }

    [Fact]
    public void MoveEpisodeFromMoviesWithSubtitles()
    {
        var movies = Path.Combine(_root, "Filmy");
        var series = Path.Combine(_root, "Serialy");
        Directory.CreateDirectory(movies);
        var src = Path.Combine(movies, "Chernobyl.S01E03.Open.Wide.O.Earth.CZ.avi");
        File.WriteAllText(src, "x");
        File.WriteAllText(Path.Combine(movies, "Chernobyl.S01E03.Open.Wide.O.Earth.CZ.cs.srt"), "x");
        File.WriteAllText(Path.Combine(movies, "Jiny film.avi"), "x");

        var dest = Path.Combine(series, "Černobyl", "Season 01");
        var moved = LibraryFiles.MoveVideo(src, dest, "Černobyl - S01E03", [movies, series]);
        Assert.Equal(Path.Combine(dest, "Černobyl - S01E03.avi"), moved);
        Assert.True(File.Exists(Path.Combine(dest, "Černobyl - S01E03.cs.srt")));
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(Path.Combine(movies, "Jiny film.avi")));
        Assert.Throws<InvalidOperationException>(() => LibraryFiles.MoveVideo(moved, Path.Combine(_root, "jinde"), null, [movies, series]));
    }

    [Fact]
    public void MinQualityFiltersCandidates()
    {
        var sd = new FsFile { Id = "1", FileName = "Pelisky 1999 CZ.avi", DownloadUrl = "x", SizeBytes = 700L << 20, Resolution = "608x400" };
        var hd = new FsFile { Id = "2", FileName = "Pelisky 1999 CZ.mkv", DownloadUrl = "x", SizeBytes = 2L << 30, Resolution = "1080p" };
        var uhd = new FsFile { Id = "3", FileName = "Pelisky 1999 CZ UHD.mkv", DownloadUrl = "x", SizeBytes = 8L << 30, Resolution = "2160p" };
        foreach (var f in new[] { sd, hd, uhd }) FileNameParser.Fill(f);
        var t = new SelectTarget { Kind = MediaKind.Movie, Title = "Pelíšky", Year = 1999 };
        var ranked = FileSelector.Rank([sd, hd, uhd], t, new FileSelectorOptions { MinQuality = "1080p", MaxQuality = "1080p" });
        Assert.Single(ranked);
        Assert.Equal("2", ranked[0].File.Id);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (Exception)
        {
        }
    }
}
