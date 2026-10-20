using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.Fastshare.Core;
using Xunit;

namespace Jellyfin.Plugin.Fastshare.Tests;

/// <summary>Stahování proti lokálnímu serveru, který se chová jako Fastshare.</summary>
public sealed class DownloadEngineTests : IDisposable
{
    private static readonly byte[] Payload = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();

    private readonly HttpListener _listener = new();
    private readonly string _base;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fs-test-" + Guid.NewGuid());
    private readonly CancellationTokenSource _cts = new();

    public DownloadEngineTests()
    {
        var port = FreePort();
        _base = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        Directory.CreateDirectory(_dir);
        _ = Task.Run(ServeAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var res = ctx.Response;
            var cookie = ctx.Request.Headers["Cookie"] ?? string.Empty;
            if (!cookie.Contains("FASTSHARE=ok", StringComparison.Ordinal))
            {
                // Jako Fastshare bez přihlášení: 200, text/html, 0 B
                res.ContentType = "text/html; charset=UTF-8";
                res.ContentLength64 = 0;
                res.Close();
                continue;
            }

            long from = 0;
            var range = ctx.Request.Headers["Range"];
            if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                from = long.Parse(range[6..].TrimEnd('-'));
                res.StatusCode = 206;
                res.AddHeader("Content-Range", $"bytes {from}-{Payload.Length - 1}/{Payload.Length}");
            }

            res.ContentType = "application/octet-stream";
            res.ContentLength64 = Payload.Length - from;

            // ?cut=N: spojení „skončí" po N bajtech (jako spadlá síť)
            var cut = ctx.Request.QueryString["cut"];
            var len = cut != null ? Math.Min(int.Parse(cut), Payload.Length - (int)from) : Payload.Length - (int)from;
            try
            {
                await res.OutputStream.WriteAsync(Payload.AsMemory((int)from, len));
                if (cut != null)
                {
                    res.Abort();
                    continue;
                }

                res.Close();
            }
            catch (Exception)
            {
                // klient zavřel spojení
            }
        }
    }

    private static DownloadEngine Engine() =>
        new(new HttpClient(new SocketsHttpHandler { UseCookies = false }), _ => { });

    private static void Ok(HttpRequestMessage r) => r.Headers.Add("Cookie", "FASTSHARE=ok");

    [Fact]
    public async Task EmptyResponseWithoutLoginIsNotAFile()
    {
        var part = Path.Combine(_dir, "a.mkv.part");
        await Assert.ThrowsAsync<NotAFileException>(() => Engine().DownloadAsync(
            _base + "file", part, _ => { }, new SpeedLimiter(() => 0), Payload.Length, (_, _, _) => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ResumesAfterDroppedConnection()
    {
        var part = Path.Combine(_dir, "b.mkv.part");
        var engine = Engine();
        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAsync(
            _base + "file?cut=1000000", part, Ok, new SpeedLimiter(() => 0), Payload.Length, (_, _, _) => { }, CancellationToken.None));
        var partial = new FileInfo(part).Length;
        Assert.InRange(partial, 1, Payload.Length - 1);

        var session = await engine.DownloadAsync(
            _base + "file", part, Ok, new SpeedLimiter(() => 0), Payload.Length, (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(Payload.Length - partial, session);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(part));
    }

    [Fact]
    public async Task SharedSpeedLimitAppliesToParallelDownloads()
    {
        // 2 souběžné × 3 MB při společném limitu 3 MB/s → aspoň ~1,5 s (bez limitu pár ms)
        var limiter = new SpeedLimiter(() => 3L * 1024 * 1024);
        var engine = Engine();
        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            engine.DownloadAsync(_base + "file", Path.Combine(_dir, "c1.part"), Ok, limiter, Payload.Length, (_, _, _) => { }, CancellationToken.None),
            engine.DownloadAsync(_base + "file", Path.Combine(_dir, "c2.part"), Ok, limiter, Payload.Length, (_, _, _) => { }, CancellationToken.None));
        Assert.True(sw.Elapsed > TimeSpan.FromSeconds(1.4), $"trvalo jen {sw.Elapsed}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (Exception)
        {
        }
    }
}
