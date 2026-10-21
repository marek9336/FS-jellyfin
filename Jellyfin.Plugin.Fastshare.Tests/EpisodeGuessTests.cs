using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>Skutečné neočíslované díly z knihovny (22. 10. 2026) a díly z TMDb (české názvy).</summary>
public sealed class EpisodeGuessTests
{
    private static Dictionary<(int Season, int Episode), string?> Seasons(params int[] counts)
    {
        var d = new Dictionary<(int Season, int Episode), string?>();
        for (var s = 0; s < counts.Length; s++)
        {
            for (var e = 1; e <= counts[s]; e++)
            {
                d[(s + 1, e)] = null;
            }
        }

        return d;
    }

    private static EpisodeGuessResult? G(string path, string[] titles, Dictionary<(int Season, int Episode), string?> known) =>
        EpisodeGuess.Guess(path, titles, known);

    [Fact]
    public void ComebackNumberAndName()
    {
        var k = Seasons(26, 26);
        k[(1, 22)] = "Koprovka";
        k[(2, 19)] = "Šulc";
        var a = G("/Serialy/Comeback/comeback-22-koprovka.avi", ["Comeback"], k)!;
        Assert.Equal((1, 22, true), (a.Season, a.Episode, a.Sure));

        // 45. díl od začátku seriálu = S02E19
        var b = G("/Serialy/comeback  2/Comeback-45-Sulc-(martens)-CZ.avi", ["Comeback"], k)!;
        Assert.Equal((2, 19, true), (b.Season, b.Episode, b.Sure));
    }

    [Theory]
    [InlineData("/Serialy/Po stopách duchů/Po stopách duchů XIII (9).avi", 13, 9)]
    [InlineData("/Serialy/Po stopách duchů/19800113_081836_[9 Prima ZOOM]_[Po stopách duchů XV (9) -W -HD].ts", 15, 9)]
    [InlineData("/Serialy/Vteřiny před katastrofou/vteriny-pred-katastrofou-iii-09-havarie-letadla-v-queensu.avi", 3, 9)]
    [InlineData("/Serialy/Vteřiny před katastrofou/vteriny-pred-katastrofou-i-02-peklo-v-tunelu.avi", 1, 2)]
    [InlineData("/Serialy/Highschool DxD New - 2. série/Highschool_DxD_NeW_03.mp4", 2, 3)]
    [InlineData("/Serialy/Plechva kavalerie/Plechová kavalerie-7.avi", 1, 7)]
    [InlineData("/Serialy/malý pitaval z velkého města/Malý pitaval z velkého města09 - Kuchařinka.avi", 1, 9)]
    [InlineData("/Serialy/Můj malý Pony - Přátelství je magické - série 1/Můj malý Pony-Přátelství je magické-20-Svět módy.mkv", 1, 20)]
    [InlineData("/Serialy/Černobyl/Černobyl E04 CZ dabing.avi", 1, 4)]
    [InlineData("/Serialy/Po stopách duchů/Po stopách duchů E8S11- Strašidelný dum.avi", 11, 8)]
    [InlineData("/Serialy/Po stopách duchů/Po stopách duchů IX (12.Díl) - Hořící bible.CZ Dabing SUPER DOKUMENT!!!.avi", 9, 12)]
    [InlineData("/Serialy/Po stopách duchů/_MSTPVR/20190519_112547_[9 Prima ZOOM]_[Americké řeky (4) -W -HD].ts", 1, 4)]
    [InlineData("/Serialy/Okupace/(okupace)-05-srpen--15'--DVBT-(romin).mkv", 1, 5)]
    public void NumberGuesses(string path, int season, int episode)
    {
        var titles = new[] { "Po stopách duchů", "Vteřiny před katastrofou", "Plechová kavalérie", "Malý pitaval z velkého města", "Můj malý Pony: Přátelství je magické", "Černobyl", "High School DxD" };
        var g = G(path, titles, Seasons(30, 30, 30))!;
        Assert.Equal((season, episode), (g.Season, g.Episode));
    }

    [Fact]
    public void BrokenX264IsNotRomanTen()
    {
        var k = Seasons(10);
        k[(1, 6)] = "Cesta do Indie a zpět";
        var g = G("/Serialy/Bylo nás 5/Bylo nás pět (6) - Cesta do Indie a zpět.Bylo nas pet (6) - Cesta do Indie a zpet.Lovok.DVB-T.x26.mp4", ["Bylo nás 5"], k)!;
        Assert.Equal((1, 6, true), (g.Season, g.Episode, g.Sure));
    }

    [Fact]
    public void NameBeatsWrongNumber()
    {
        var k = Seasons(13, 22, 24, 22, 22);
        k[(5, 20)] = "Chlapec, který věděl příliš mnoho";
        k[(4, 7)] = "Marge v řetězech";
        var g = G("/Serialy/the simpsons/Chlapec_ktery_vedel_prilis_mnoho#66.mpg", ["Simpsonovi", "The Simpsons"], k)!;
        Assert.Equal((5, 20), (g.Season, g.Episode));
        Assert.False(g.Sure); // číslo a název si odporují → jen ručně

        k[(4, 15)] = "S Homerem přijde zákon";
        var h = G("/Serialy/the simpsons/S Homerem prijde zakon.avi", ["Simpsonovi", "The Simpsons"], k)!;
        Assert.Equal((4, 15, true), (h.Season, h.Episode, h.Sure));
    }

    [Fact]
    public void MidsomerRomanAndName()
    {
        var k = Seasons(5, 4, 5, 4, 5, 5, 6);
        k[(7, 3)] = "Špatné zprávy";
        var g = G("/Serialy/Vraždy v Midsomeru všechny díly/Vrazdy v Midsomeru VII-30 Spatne zpravy.avi", ["Vraždy v Midsomeru", "Midsomer Murders"], k)!;
        Assert.Equal((7, 3), (g.Season, g.Episode));
    }

    [Fact]
    public void GenericTmdbNameDoesNotBlockSure()
    {
        var k = Seasons(12, 13, 12);
        k[(3, 10)] = "10. epizoda";
        var g = G("/Serialy/Highschool DxD BorN - 3. série/Himitsu-Highschool_DxD_BorN_10_CZ.mp4", ["ハイスクールD×D", "High School DxD"], k)!;
        Assert.Equal((3, 10, true), (g.Season, g.Episode, g.Sure));
        Assert.Null(G("/Serialy/Stare ceske kriminalky/vim,ze-jsi-vrah...-71'-DVBT_CZ-(romin).avi", ["Staré české kriminálky"], k));
    }

    [Theory]
    [InlineData("/Serialy/BBC_Sexualita/Vlasy.avi")]
    [InlineData("/Serialy/Dora/Dora-pruzkumnice_Trikralova-oslava.avi")]
    [InlineData("/Serialy/the simpsons/Simpsons XIII - Jsem cholerik ale lecim se.avi")]
    public void NothingToGuess(string path) =>
        Assert.Null(G(path, ["BBC Sexualita", "Dora", "Simpsonovi", "The Simpsons"], new()));

    [Theory]
    [InlineData("Highschool DxD New - 2. série", 2)]
    [InlineData("Season 03", 3)]
    [InlineData("6. serie", 6)]
    [InlineData("Simpsons 2", null)]
    [InlineData("comeback  2", null)]
    public void FolderSeason(string folder, int? season) =>
        Assert.Equal(season, EpisodeGuess.SeasonFromFolder(folder));
}
