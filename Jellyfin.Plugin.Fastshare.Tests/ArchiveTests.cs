using System.IO.Compression;
using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>Rozbalení a roztřídění archivu se seriálem (zip vytvořený v testu).</summary>
public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fs-arch-" + Guid.NewGuid());

    private string Movies => Path.Combine(_root, "Filmy");

    private string Series => Path.Combine(_root, "Serialy");

    private string MakeZip(params (string Name, int Size)[] entries)
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "Pratele 1. serie.zip");
        using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, size) in entries)
        {
            using var s = z.CreateEntry(name).Open();
            s.Write(Enumerable.Repeat((byte)7, size).ToArray());
        }

        return zip;
    }

    private static QueueItem SeriesItem() => new()
    {
        Title = "Přátelé", SeriesTitle = "Přátelé", MediaType = MediaKind.Episode, IsArchive = true, ArchiveSeason = 1, Quality = "720p", Language = "CZ",
    };

    [Fact]
    public void ExtractsAndSortsEpisodes()
    {
        var zip = MakeZip(
            ("Pratele 1. serie/Pratele S01E01 CZ 1080p.mkv", 5000),
            ("Pratele 1. serie/Pratele S01E01 CZ 1080p.srt", 100),
            ("Pratele 1. serie/Pratele 1x02.avi", 4000),
            ("Pratele 1. serie/03 - Jak Ross.mkv", 3000),
            ("../../zlo.mkv", 10),
            ("info.txt", 10));
        var extracted = ArchiveExtractor.Extract([zip], Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None);
        Assert.All(extracted, p => Assert.StartsWith(Path.Combine(_root, "tmp"), p)); // nic mimo pracovní složku

        var r = ArchiveExtractor.Organize(extracted, SeriesItem(), Movies, Series, 5, overwrite: false);

        var s01 = Path.Combine(Series, "Přátelé", "Season 01");
        Assert.True(File.Exists(Path.Combine(s01, "Přátelé - S01E01 - [1080p CZ].mkv")));
        Assert.True(File.Exists(Path.Combine(s01, "Přátelé - S01E01 - [1080p CZ].srt")));
        Assert.True(File.Exists(Path.Combine(s01, "Přátelé - S01E02 - [720p CZ].avi"))); // kvalita a jazyk z archivu
        Assert.Equal(2, r.Moved.Count);
        Assert.Contains(r.Unrecognized, p => p.EndsWith("03 - Jak Ross.mkv", StringComparison.Ordinal));
        Assert.Contains(r.Unrecognized, p => p.Contains(".nerozpoznane", StringComparison.Ordinal));
    }

    [Fact]
    public void SkipsEpisodeAlreadyInLibrary()
    {
        var s01 = Path.Combine(Series, "Přátelé", "Season 01");
        Directory.CreateDirectory(s01);
        File.WriteAllBytes(Path.Combine(s01, "Přátelé - S01E01 - [1080p CZ].mkv"), new byte[5000]);

        var zip = MakeZip(("Pratele S01E01 CZ 1080p.mkv", 5000), ("Pratele S01E02 CZ 1080p.mkv", 5000));
        var extracted = ArchiveExtractor.Extract([zip], Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None);
        var r = ArchiveExtractor.Organize(extracted, SeriesItem(), Movies, Series, 5, overwrite: false);

        Assert.Single(r.Skipped);
        Assert.Single(r.Moved);
        Assert.EndsWith("S01E02 - [1080p CZ].mkv", r.Moved[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MovieArchiveTakesLargestVideo()
    {
        var zip = MakeZip(("Pelisky.1999.CZ.1080p.mkv", 9000), ("sample.mkv", 500), ("Pelisky.srt", 50));
        var item = new QueueItem { Title = "Pelíšky", Year = 1999, MediaType = MediaKind.Movie, IsArchive = true };
        var extracted = ArchiveExtractor.Extract([zip], Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None);
        var r = ArchiveExtractor.Organize(extracted, item, Movies, Series, 5, overwrite: false);

        var dir = Path.Combine(Movies, "Pelíšky (1999)");
        Assert.True(File.Exists(Path.Combine(dir, "Pelíšky (1999) - [1080p CZ].mkv")));
        Assert.True(File.Exists(Path.Combine(dir, "Pelíšky (1999) - [1080p CZ].srt")));
        Assert.Single(r.Moved);
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void Solid7z()
    {
        var extracted = ArchiveExtractor.Extract([Fixture("tbbt9.solid.7z")], Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None);
        Assert.Equal(3, extracted.Count);
        var item = new QueueItem { Title = "Teorie velkého třesku", SeriesTitle = "Teorie velkého třesku", MediaType = MediaKind.Episode, IsArchive = true };
        var r = ArchiveExtractor.Organize(extracted, item, Movies, Series, 5, overwrite: false);
        Assert.Equal(3, r.Moved.Count);
        Assert.True(File.Exists(Path.Combine(Series, "Teorie velkého třesku", "Season 09", "Teorie velkého třesku - S09E03 - [720p CZ].mkv")));
    }

    [Fact]
    public void MultiVolume7z()
    {
        var parts = new[] { "tbbt9.7z.001", "tbbt9.7z.002", "tbbt9.7z.003" }.Select(Fixture).ToList();
        var extracted = ArchiveExtractor.Extract(parts, Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None);
        Assert.Equal(3, extracted.Count);
        Assert.All(extracted, p => Assert.Equal(30000, new FileInfo(p).Length));
    }

    [Fact]
    public void MissingVolumeIsReported()
    {
        var parts = new[] { "tbbt9.7z.001", "tbbt9.7z.002" }.Select(Fixture).ToList();
        Assert.ThrowsAny<Exception>(() =>
            ArchiveExtractor.Extract(parts, Path.Combine(_root, "tmp"), (_, _) => { }, CancellationToken.None));
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
