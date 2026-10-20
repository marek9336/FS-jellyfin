using System.Text.Json;
using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>Výběr nad skutečnými výsledky hledání (samples/*.json z 20. 10. 2026).</summary>
public class SelectorTests
{
    private static List<FsFile> Sample(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "../../../../samples", name);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return FastshareClient.ParseFiles(doc.RootElement);
    }

    [Fact]
    public void EpisodePrefersCzechDub1080p()
    {
        var t = new SelectTarget { Kind = MediaKind.Episode, Title = "Breaking Bad", Season = 2, Episode = 3 };
        var ranked = FileSelector.Rank(Sample("breaking_bad_s02e03.json"), t, new FileSelectorOptions());
        Assert.NotEmpty(ranked);
        var best = ranked[0].File;
        Assert.Contains("CZ", best.AudioLangs);
        Assert.Equal("1080p", best.Quality);
    }

    [Fact]
    public void EpisodeOfOtherSeriesIsRejected()
    {
        var t = new SelectTarget { Kind = MediaKind.Episode, Title = "Přátelé", Season = 1, Episode = 1 };
        var ranked = FileSelector.Rank(Sample("pratele_s01e01.json"), t, new FileSelectorOptions());
        Assert.NotEmpty(ranked);
        Assert.DoesNotContain(ranked, r => r.File.FileName.Contains("mušketýři", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ranked, r => r.File.FileName.Contains("Lassie", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MovieYearFiltersSequel()
    {
        var t = new SelectTarget { Kind = MediaKind.Movie, Title = "Spider-Man Paralelní světy", Year = 2018 };
        var ranked = FileSelector.Rank(Sample("spider-man_paralelni_svety.json"), t, new FileSelectorOptions());
        Assert.NotEmpty(ranked);
        Assert.All(ranked, r => Assert.True(r.File.Year is null or 2018 or 2019 or 2017));
        Assert.Contains("CZ", ranked[0].File.AudioLangs);
    }

    [Fact]
    public void EpisodeAndMovieSizeLimitsAreSeparate()
    {
        var big = new FsFile { Id = "1", FileName = "Breaking Bad S02E03 CZ dabing 1080p.mkv", DownloadUrl = "x", SizeBytes = 8L << 30 };
        var small = new FsFile { Id = "2", FileName = "Breaking Bad S02E03 CZ dabing 720p.mkv", DownloadUrl = "x", SizeBytes = 1L << 30 };
        FileNameParser.Fill(big);
        FileNameParser.Fill(small);
        var ep = new SelectTarget { Kind = MediaKind.Episode, Title = "Breaking Bad", Season = 2, Episode = 3 };
        var o = new FileSelectorOptions { MaxEpisodeSizeGb = 5, MaxMovieSizeGb = 30 };
        var ranked = FileSelector.Rank(new[] { big, small }, ep, o);
        Assert.Single(ranked);
        Assert.Equal("2", ranked[0].File.Id);

        var movie = new FsFile { Id = "3", FileName = "Dune 2021 CZ 2160p.mkv", DownloadUrl = "x", SizeBytes = 20L << 30 };
        FileNameParser.Fill(movie);
        var mv = new SelectTarget { Kind = MediaKind.Movie, Title = "Dune", Year = 2021 };
        Assert.Single(FileSelector.Rank(new[] { movie }, mv, o));
    }

    [Fact]
    public void MaxQualityExcludes4K()
    {
        var t = new SelectTarget { Kind = MediaKind.Movie, Title = "Matrix", Year = 1999 };
        var ranked = FileSelector.Rank(Sample("matrix_1999.json"), t, new FileSelectorOptions { MaxQuality = "1080p" });
        Assert.NotEmpty(ranked);
        Assert.DoesNotContain(ranked, r => r.File.Quality == "4K");
    }
}
