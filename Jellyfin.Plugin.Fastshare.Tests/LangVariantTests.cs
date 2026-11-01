using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class LangVariantTests
{
    [Theory]
    [InlineData("cze", "CZ")]
    [InlineData("ces", "CZ")]
    [InlineData("eng", "EN")]
    [InlineData("slk", "SK")]
    [InlineData("und", null)]
    [InlineData("", null)]
    public void NormalizeIso(string iso, string? expected) => Assert.Equal(expected, LangVariant.Normalize(iso));

    [Fact]
    public void ParseAndLabel()
    {
        Assert.Equal(("EN", "CZ"), LangVariant.Parse("EN+CZtit"));
        Assert.Equal(("CZ", (string?)null), LangVariant.Parse("CZ"));
        Assert.Equal("EN + CZ titulky", LangVariant.Label("EN+CZtit"));
        Assert.True(LangVariant.IsValid("EN+CZtit"));
        Assert.False(LangVariant.IsValid("english"));
    }

    [Fact]
    public void MatchesLibraryFile()
    {
        var dab = new LibraryVersion { AudioLangs = ["CZ"] };
        var enTit = new LibraryVersion { AudioLangs = ["EN"], SubLangs = ["CZ"] };
        var unknown = new LibraryVersion();
        Assert.True(LangVariant.Matches(dab, "CZ", "CZ"));
        Assert.False(LangVariant.Matches(dab, "EN+CZtit", "CZ"));
        Assert.True(LangVariant.Matches(enTit, "EN+CZtit", "CZ"));
        Assert.True(LangVariant.Matches(unknown, "CZ", "CZ"));       // starý soubor bez údaje = výchozí jazyk
        Assert.False(LangVariant.Matches(unknown, "EN+CZtit", "CZ")); // …ale ne druhá verze
    }

    [Fact]
    public void EnglishWithCzechSubsOnly()
    {
        FsFile F(string id, string name) { var f = new FsFile { Id = id, FileName = name, DownloadUrl = "x", SizeBytes = 900L << 20, Resolution = "1080p" }; FileNameParser.Fill(f); return f; }
        var cz = F("cz", "Super drbna S01E01 CZ dabing 1080p.mkv");
        var en = F("en", "Gossip Girl S01E01 EN 1080p.mkv");
        var entit = F("entit", "Gossip Girl S01E01 EN CZ titulky 1080p.mkv");
        var t = new SelectTarget { Kind = MediaKind.Episode, Title = "Super drbna", Season = 1, Episode = 1 };
        var t2 = new SelectTarget { Kind = MediaKind.Episode, Title = "Gossip Girl", Season = 1, Episode = 1 };
        var o = LangVariant.Options(new FileSelectorOptions { MaxQuality = "1080p" }, "EN+CZtit", primary: false);
        Assert.Equal(["entit"], FileSelector.Rank([cz, en, entit], t2, o).Select(r => r.File.Id));
        var oCz = LangVariant.Options(new FileSelectorOptions { MaxQuality = "1080p" }, "CZ", primary: true);
        Assert.Equal("cz", FileSelector.Rank([cz], t, oCz)[0].File.Id);
    }

    [Fact]
    public void OtherLanguageIsNotQueueDuplicate()
    {
        var path = Path.Combine(Path.GetTempPath(), "fs-q-" + Guid.NewGuid() + ".json");
        try
        {
            var q = new DownloadQueue(path, _ => { });
            QueueItem Ep(string fid, string lang) => new() { FileId = fid, Title = "Super drbna", SeriesTitle = "Super drbna", MediaType = MediaKind.Episode, Season = 1, Episode = 1, Quality = "1080p", Language = lang };
            Assert.Equal(AddOutcome.Added, q.Add(Ep("a", "CZ")));
            Assert.Equal(AddOutcome.Added, q.Add(Ep("b", "EN+CZ tit")));
            Assert.NotEqual(AddOutcome.Added, q.Add(Ep("c", "CZ")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
