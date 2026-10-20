using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>Skutečné názvy z Fastshare (hledání 20. 10. 2026).</summary>
public class ParserTests
{
    private static FsFile P(string name, string? res = null)
    {
        var f = new FsFile { FileName = name, Resolution = res };
        FileNameParser.Fill(f);
        return f;
    }

    [Theory]
    [InlineData("Breaking Bad S02E03 CZ dab 720p.mp4", "Breaking Bad", 2, 3, "CZ", "")]
    [InlineData("Breaking Bad S02E03 CzTit.mp4", "Breaking Bad", 2, 3, "", "CZ")]
    [InlineData("BREAKING-BAD-HD-1080P-CZ-DABING+TITULKY-S02E03-BODNUTÍ-MRTVÉ-VČELY.mkv", "Breaking Bad", 2, 3, "CZ", "")]
    [InlineData("Teorie velkého třesku_11x07.avi", "Teorie velkého třesku", 11, 7, "", "")]
    [InlineData("Pratele.S01e01.avi", "Pratele", 1, 1, "", "")]
    [InlineData("Přátelé S01E01 CZ DABING .mp4", "Přátelé", 1, 1, "CZ", "")]
    [InlineData("Pernikovy.tata-Breaking.Bad.S02E03.1080p.8bit.BluRay.AAC.x265.HEVC.CZ.EN.mkv", "Pernikovy Tata Breaking Bad", 2, 3, "CZ,EN", "")]
    public void Episodes(string name, string title, int season, int episode, string audio, string subs)
    {
        var f = P(name);
        Assert.Equal(title, f.Title, ignoreCase: true);
        Assert.Equal(season, f.Season);
        Assert.Equal(episode, f.Episode);
        Assert.Equal(audio, string.Join(",", f.AudioLangs));
        Assert.Equal(subs, string.Join(",", f.SubtitleLangs));
        Assert.True(f.IsVideo);
    }

    [Theory]
    [InlineData("Avatar 2--CZ-dab. 2022.mp4", "Avatar 2", 2022, "CZ")]
    [InlineData("Avatar - Cesta vody (CZ)(2022).mp4", "Avatar - Cesta vody", 2022, "CZ")]
    [InlineData("Matrix 1 (1999) CZ dabing.mkv", "Matrix 1", 1999, "CZ")]
    [InlineData("The Matrix 1999 (Matrix 1)Brip,Cz,Eng+titulky.mkv", "The Matrix", 1999, "CZ,EN")]
    [InlineData("Duna - Dune 2021 HEVC 1080p EN+CZ MarcelCu.mkv", "Duna - Dune", 2021, "EN,CZ")]
    [InlineData("2023 - Spider-Man Napric paralelnimi svety CZ Dabing - Cez paralelné svety.mkv", "Spider-Man Napric paralelnimi svety", 2023, "CZ")]
    [InlineData("Spider-Man Paralelní světy 2018 SK CZ.mkv", "Spider-Man Paralelní světy", 2018, "SK,CZ")]
    [InlineData("Pelíšky (1999) UP UHD cz.mkv", "Pelíšky", 1999, "CZ")]
    [InlineData("Pelíšky.mkv", "Pelíšky", null, "")]
    public void Movies(string name, string title, int? year, string audio)
    {
        var f = P(name);
        Assert.Equal(title, f.Title);
        Assert.Equal(year, f.Year);
        Assert.Null(f.Episode);
        Assert.Equal(audio, string.Join(",", f.AudioLangs));
    }

    [Theory]
    [InlineData("1080p", "1080p")]
    [InlineData("1920x800", "1080p")]
    [InlineData("608x256", "SD")]
    [InlineData("2160p", "4K")]
    [InlineData("720x540", "SD")]
    [InlineData("1918x1080", "1080p")]
    public void QualityFromApi(string res, string quality) =>
        Assert.Equal(quality, P("Film.mkv", res).Quality);

    [Fact]
    public void QualityFromNameWhenApiEmpty() =>
        Assert.Equal("1080p", P("Breaking Bad S02E03 CZ Dabing Full HD.mkv", string.Empty).Quality);

    [Fact]
    public void ResolutionIsNotYear()
    {
        var f = P("Film 1920x1080.mkv");
        Assert.Null(f.Year);
    }

    [Fact]
    public void SearchTermWithoutDiacritics() =>
        Assert.Equal("Teorie velkeho tresku S01", FastshareClient.SearchTerm(" Teorie velkého třesku S01 "));

    [Theory]
    [InlineData("Přátelé (Friends) S01E01.mkv", "Přátelé", true)]
    [InlineData("Tři Mušketýři-S01E01-Přátelé a nepřátelé_2014.mkv", "Přátelé", false)]
    [InlineData("S01E01   Lassie a přátelé  .mp4", "Přátelé", false)]
    [InlineData("Pernikovy.tata-Breaking.Bad.S02E03.1080p.mkv", "Breaking Bad", true)]
    [InlineData("Spider-Man Paralelní světy 2018 SK CZ.mkv", "Spider-Man: Paralelní světy", true)]
    [InlineData("Matrix Reloaded 2003.mkv", "Matrix", true)]
    [InlineData("Lassie a přátelé S01E01 - Záchrana Zuliho.mkv", "Přátelé", false)]
    [InlineData("Friends - Přátelé S01E01 The One Where It All Began.mkv", "Přátelé", true)]
    [InlineData("The Matrix 1999 (Matrix 1)Brip,Cz,Eng+titulky.mkv", "Matrix", true)]
    [InlineData("1999 Pelíšky.mkv", "Pelíšky", true)]
    [InlineData("Duna - Dune 2021 HEVC 1080p EN+CZ MarcelCu.mkv", "Dune", true)]
    [InlineData("Batman: Duše draka (2021).mp4", "Dune", false)]
    public void TitleMatch(string name, string title, bool expected) =>
        Assert.Equal(expected, FileNameParser.MatchesTitle(P(name), title));
}
