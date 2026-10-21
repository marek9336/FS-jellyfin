using System.Text.Json;
using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>
/// Ruční kontrola čtečky na vzorku skutečných odpovědí (samples/*.json).
/// Spustí se jen s FS_DUMP=cesta_k_výstupu, jinak nic nedělá.
/// </summary>
public class SampleDump
{
    [Fact]
    public void Dump()
    {
        var output = Environment.GetEnvironmentVariable("FS_DUMP");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        var dir = Path.Combine(AppContext.BaseDirectory, "../../../../samples");
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.json").Order())
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var f in FastshareClient.ParseFiles(doc.RootElement))
            {
                lines.Add(string.Join(" | ", f.FileName, f.Title, f.Year, f.Season is null ? "" : $"S{f.Season:D2}E{f.Episode:D2}",
                    f.PackSeason is null ? "" : "PACK" + f.PackSeason, f.Quality, string.Join(",", f.AudioLangs), string.Join(",", f.SubtitleLangs), f.Codec, f.IsVideo ? "" : "NEVIDEO"));
            }
        }

        File.WriteAllLines(output, lines);
    }
}
