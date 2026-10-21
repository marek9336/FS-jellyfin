using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>RAR v různých podobách (testovací archivy SharpCompress) — rozbalení a pořadí částí.</summary>
public sealed class RarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-rar-" + Guid.NewGuid());

    private static string F(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "rar", name);

    /// <summary>Seřadí části tak, jak to dělá plugin (podle čísla dílu z názvu).</summary>
    private static List<string> Ordered(params string[] names) =>
        names
            .Select(n => { var f = new FsFile { FileName = n }; FileNameParser.Fill(f); return (n, f); })
            .OrderBy(x => x.f.ArchivePart ?? 1)
            .Select(x => F(x.n))
            .ToList();

    [Theory]
    [InlineData("Rar.rar")]
    [InlineData("Rar.solid.rar")]
    [InlineData("Rar5.rar")]
    public void SingleRar(string name)
    {
        var files = ArchiveExtractor.Extract([F(name)], _dir, (_, _) => { }, CancellationToken.None);
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length > 0));
    }

    [Fact]
    public void MultiPartRar()
    {
        // schválně zamíchané — plugin je seřadí podle .partNN
        var parts = Ordered("Rar.multi.part03.rar", "Rar.multi.part01.rar", "Rar.multi.part06.rar",
            "Rar.multi.part02.rar", "Rar.multi.part05.rar", "Rar.multi.part04.rar");
        Assert.EndsWith("part01.rar", parts[0], StringComparison.Ordinal);
        var files = ArchiveExtractor.Extract(parts, _dir, (_, _) => { }, CancellationToken.None);
        Assert.NotEmpty(files);
    }

    [Fact]
    public void OldStyleMultiRar()
    {
        var parts = Ordered("Rar2.multi.r02", "Rar2.multi.rar", "Rar2.multi.r00", "Rar2.multi.r05",
            "Rar2.multi.r01", "Rar2.multi.r03", "Rar2.multi.r04");
        Assert.EndsWith("Rar2.multi.rar", parts[0], StringComparison.Ordinal);
        Assert.EndsWith("Rar2.multi.r05", parts[^1], StringComparison.Ordinal);
        var files = ArchiveExtractor.Extract(parts, _dir, (_, _) => { }, CancellationToken.None);
        Assert.NotEmpty(files);
    }

    [Fact]
    public void EncryptedRarIsPermanentError() =>
        Assert.Throws<FastsharePermanentException>(() =>
            ArchiveExtractor.Extract([F("Rar.Encrypted.rar")], _dir, (_, _) => { }, CancellationToken.None));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (Exception)
        {
        }
    }
}
