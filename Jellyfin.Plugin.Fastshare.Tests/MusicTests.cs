using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

public sealed class MusicTests
{
    private static MusicTrack T(string rel, string? title = null, string? artist = null, string? album = null, int dur = 200, long size = 5_000_000, long br = 192_000, string? key = null) => new()
    {
        Path = "/Hudba/" + rel,
        Title = title ?? Path.GetFileNameWithoutExtension(rel),
        Artists = artist == null ? new List<string>() : new List<string> { artist },
        Album = album,
        DurationSec = dur,
        SizeBytes = size,
        Bitrate = br,
        ContentKey = key,
    };

    [Theory]
    [InlineData("hudba/Harlej - University (2008)/01 - Malá nevěrná holka.mp3", "Harlej", "University", 2008, 1, "Malá nevěrná holka", true)]
    [InlineData("MP 3/FLYING STEPS - We Gonna Rock It.mp3", "Flying Steps", "MP 3", null, null, "We Gonna Rock It", true)]
    [InlineData("Rock/7. Metallica/Metallica - Kill 'em All/Metallica - (Anesthesia) Pulling Teeth.mp3", "Metallica", "Kill 'em All", null, null, "(Anesthesia) Pulling Teeth", true)]
    [InlineData("Rock/6. Kryl/1992 - Dvě půle lunety/Karel Kryl- 01- Jde Jaro Do Léta.MP3", "Karel Kryl", "Dvě půle lunety", 1992, 1, "Jde Jaro Do Léta", true)]
    [InlineData("hudba/Harlej - Harlejova kometa/12 - R.I.P. (Locman - cast III).mp3", "Harlej", "Harlejova kometa", null, 12, "R.I.P. (Locman - cast III)", true)]
    [InlineData("hudba/AC DC - Platium/12- ThunderStruck.mp3", "AC DC", "Platium", null, 12, "ThunderStruck", true)]
    [InlineData("Rock/Linkin Park - Splitting The DNA - 2001/CD1/10 - She Shines.mp3", "Linkin Park", "Splitting The DNA", 2001, 10, "She Shines", true)]
    [InlineData("Kosna/Zahraniční Mp3/-x-evanescence_-_my_.mp3", "Evanescence", "Zahraniční Mp3", null, null, "my", true)]
    public void GuessFromPath(string rel, string? artist, string? album, int? year, int? track, string? title, bool sure)
    {
        var g = MusicGuess.Guess(T(rel), "/Hudba");
        Assert.Equal(artist, g.Artist);
        Assert.Equal(album, g.Album);
        Assert.Equal(year, g.Year);
        Assert.Equal(track, g.TrackNo);
        Assert.Equal(title, g.Title);
        Assert.Equal(sure, g.Sure);
    }

    [Theory]
    [InlineData("Rock/21.Team/TEAM - Best of/Track 07.mp3")]
    [InlineData("KOLEDY/Vánoční hvězdy  1997/Track No04.mp3")]
    [InlineData("Rock/5. Kabát/Kabát - Go Satane go/06 Stopa 6.mp3")]
    [InlineData("Rock/16. Offspring/The Offsprig - Ignition/Track05!!s.mp3")]
    [InlineData("Rock/20. System of A Down/System Of A Down - Toxicity/System Of A Down - Track  2.mp3")]
    [InlineData("Rock/25. Manowar/Kings Of Metal/Manowar - Kings Of Metal - 09 - Track  9.mp3")]
    public void GenericNamesHaveNoTitle(string rel)
    {
        var g = MusicGuess.Guess(T(rel), "/Hudba");
        Assert.Null(g.Title);
        Assert.False(g.Sure);
        Assert.NotNull(g.TrackNo);
    }

    [Fact]
    public void TagsWinAndJunkTagsDoNot()
    {
        var g = MusicGuess.Guess(T("Rock/48. Nostradameus/2003 The Third Prophecy/09 In Harmony.Mp3", artist: "Nostradameus", album: "The Third Prophecy"), "/Hudba");
        Assert.Equal(("Nostradameus", "In Harmony", 9, 2003), (g.Artist, g.Title, g.TrackNo, g.Year));
        Assert.True(g.Sure);
        var j = MusicGuess.Guess(T("Rock/System of a Down/Toxicity/05 Aerials.mp3", artist: "No Artist", album: "Neznámé album (6.11.2009 0:22:15)"), "/Hudba");
        Assert.Equal(("System of a Down", "Toxicity", "Aerials"), (j.Artist, j.Album, j.Title));
        Assert.Equal("AC DC", MusicGuess.Pretty("AC DC"));
        Assert.Equal("Vangelis", MusicGuess.Pretty("VANGELIS"));
    }

    [Fact]
    public void MojibakeFixesCzechOnly()
    {
        Assert.Equal("Tři Sestry", Mojibake.Fix("Tøi Sestry"));
        Assert.Equal("Žízeň", Mojibake.Fix("\u008eízeò"));
        Assert.Equal("Přátelé stárnou", Mojibake.Fix("Pøátelé stárnou"));
        Assert.Null(Mojibake.Fix("Tři Sestry"));
        Assert.Equal("Průša se vrací", Mojibake.Fix("Prùša se vrací")); // část znaků už správně
        Assert.Equal(("Lordi", "Good to be bad"), (MusicGuess.Guess(T("Rock/Lordi/X/Kopie - LORDI - Good to be bad.mp3"), "/Hudba").Artist, MusicGuess.Guess(T("Rock/Lordi/X/Kopie - LORDI - Good to be bad.mp3"), "/Hudba").Title));
        Assert.Null(Mojibake.Fix("Crème brûlée")); // francouzština zůstane
        Assert.Null(Mojibake.Fix("Hello"));
    }

    [Fact]
    public void DuplicatesNeedFileNamesToAgree()
    {
        var tracks = new List<MusicTrack>
        {
            // stejné odpadní tagy, jiné skladby → žádná duplicita
            T("DUO BAND/GIPSY PRINCE (14).mp3", "GIPSY PRINCE", "WWW.JARO81.PICZO.COM", dur: 240),
            T("DUO BAND/GIPSY PRINCE (15).mp3", "GIPSY PRINCE", "WWW.JARO81.PICZO.COM", dur: 241),
            // kopie ve stejné složce → horší předem zaškrtnutá
            T("Rock/Lordi/The Apockalypse/LORDI - Good to be bad.mp3", "Good to be bad", "LORDI", br: 320_000),
            T("Rock/Lordi/The Apockalypse/Kopie - LORDI - Good to be bad.mp3", "Good to be bad", "LORDI", br: 320_000),
            // výběrovka × album → jen ukázat
            T("hudba/Metallica - Metallica/01 - Enter Sandman.mp3", "Enter Sandman", "Metallica", dur: 331),
            T("hudba/Metallica - Best of the Best cd3/205.Enter Sandman.mp3", "Enter Sandman", "Metallica", dur: 332),
            // živá verze není duplicita
            T("hudba/Metallica - S & M/2 09 Enter Sandman.mp3", "Enter Sandman", "Metallica", dur: 460),
            // bajtově stejné
            T("hudba/Kabát - Žízeň/11 Máš to už za sebou.mp3", "Máš to už za sebou", "Kabát", key: "k1"),
            T("Flash hudba 2020/11 Máš to už za sebou.mp3", "Máš to už za sebou", "Kabát", key: "k1"),
            T("CD/Track01.cda", size: 44, br: 0),
        };
        var res = MusicAnalyzer.Analyze(tracks, "/Hudba", new MusicAnalyzer.Options());
        Assert.DoesNotContain(res, r => r.Title.Contains("GIPSY", StringComparison.Ordinal));
        var lordi = Assert.Single(res, r => r.Kind == "song" && r.Title.Contains("Good to be bad", StringComparison.Ordinal));
        Assert.Equal(new[] { "/Hudba/Rock/Lordi/The Apockalypse/Kopie - LORDI - Good to be bad.mp3" }, lordi.DeletePaths);
        var sandman = Assert.Single(res, r => r.Kind == "song" && r.Title.Contains("Enter Sandman", StringComparison.Ordinal));
        Assert.True(sandman.CrossAlbum);
        Assert.Empty(sandman.DeletePaths);
        Assert.Equal(2, sandman.Tracks.Count);
        var same = Assert.Single(res, r => r.Kind == "same");
        Assert.Single(same.DeletePaths);
        Assert.Contains(res, r => r.Kind == "junk" && r.DeletePaths.Single().EndsWith(".cda", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteAudioKeepsAlbumFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "fs-mus-" + Guid.NewGuid());
        var album = Path.Combine(root, "Kabát - Žízeň");
        Directory.CreateDirectory(album);
        try
        {
            File.WriteAllText(Path.Combine(album, "01.mp3"), "a");
            File.WriteAllText(Path.Combine(album, "02.mp3"), "b");
            File.WriteAllText(Path.Combine(album, "02.lrc"), "l");
            File.WriteAllText(Path.Combine(album, "folder.jpg"), "j");
            LibraryFiles.DeleteAudio(Path.Combine(album, "02.mp3"), [root]);
            Assert.True(File.Exists(Path.Combine(album, "01.mp3")));
            Assert.False(File.Exists(Path.Combine(album, "02.lrc")));
            Assert.True(Directory.Exists(album));
            Assert.Throws<InvalidOperationException>(() => LibraryFiles.DeleteAudio("/etc/hosts", [root]));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
