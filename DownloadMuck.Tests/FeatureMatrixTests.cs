using System.IO.Compression;
using DownloadMuck;
using Xunit;

namespace DownloadMuck.Tests;

public class FeatureMatrixTests
{
    [Fact]
    public void Metalink_parses_v4_urls_and_ignores_non_http()
    {
        string xml = """
            <?xml version="1.0"?>
            <metalink xmlns="urn:ietf:params:xml:ns:metalink">
              <file name="app.zip">
                <size>1234</size>
                <url>https://a.example/app.zip</url>
                <url>https://b.example/app.zip</url>
                <url>magnet:?xt=urn:btih:x</url>
              </file>
            </metalink>
            """;
        var parsed = MetalinkParser.Parse(xml);
        Assert.NotNull(parsed);
        Assert.Equal("app.zip", parsed!.FileName);
        Assert.Equal(1234, parsed.Size);
        Assert.Equal(2, parsed.Urls.Count);
        Assert.DoesNotContain(parsed.Urls, u => u.StartsWith("magnet:"));
    }

    [Fact]
    public void Metalink_parses_hash_when_present()
    {
        string xml = """
            <?xml version="1.0"?>
            <metalink xmlns="urn:ietf:params:xml:ns:metalink">
              <file name="app.zip">
                <hash type="sha-256">aabb</hash>
                <url>https://a.example/app.zip</url>
              </file>
            </metalink>
            """;
        var parsed = MetalinkParser.Parse(xml);
        Assert.Equal("sha-256", parsed!.HashType);
        Assert.Equal("aabb", parsed.HashValue);
    }

    [Fact]
    public void Scheduler_overnight_window()
    {
        Assert.True(SchedulerGate.IsInsideWindow(new DateTime(2026, 1, 1, 23, 0, 0), 22, 6));
        Assert.True(SchedulerGate.IsInsideWindow(new DateTime(2026, 1, 1, 2, 0, 0), 22, 6));
        Assert.False(SchedulerGate.IsInsideWindow(new DateTime(2026, 1, 1, 12, 0, 0), 22, 6));
        Assert.True(SchedulerGate.IsInsideWindow(DateTime.Now, 0, 0));
    }

    [Fact]
    public void Ftp_and_sftp_endpoints_parse_userinfo()
    {
        Assert.True(RemoteEndpointParser.TryParse("ftp://u:p@host:2121/dir/a.bin", "ftp", 21, out var ftp));
        Assert.Equal("host", ftp.Host);
        Assert.Equal(2121, ftp.Port);
        Assert.Equal("u", ftp.User);
        Assert.Equal("p", ftp.Password);
        Assert.Equal("/dir/a.bin", ftp.RemotePath);

        Assert.True(RemoteEndpointParser.TryParse("sftp://root@192.168.1.8/data/x.rar", "sftp", 22, out var sftp));
        Assert.Equal("192.168.1.8", sftp.Host);
        Assert.Equal(22, sftp.Port);
        Assert.Equal("root", sftp.User);
        Assert.Equal("/data/x.rar", sftp.RemotePath);
    }

    [Fact]
    public void Split_slowest_chunk_covers_same_bytes()
    {
        var chunks = SegmentPlanner.BuildChunks(8 * 1024 * 1024, 2);
        long size = 8L * 1024 * 1024;
        chunks[0].CurrentOffset = chunks[0].Start;
        var stolen = SegmentPlanner.TrySplitSlowest(chunks, SegmentPlanner.MinChunkBytes);
        Assert.NotNull(stolen);
        chunks.Add(stolen!);
        Assert.True(SegmentPlanner.CoversExactly(chunks, size));
    }

    [Fact]
    public void Archive_zip_roundtrip()
    {
        string root = Path.Combine(Path.GetTempPath(), "mdm-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string inner = Path.Combine(root, "hello.txt");
        File.WriteAllText(inner, "mdm");
        string zip = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".zip");
        ZipFile.CreateFromDirectory(root, zip);
        try
        {
            string outDir = ArchiveExtractor.Extract(zip);
            Assert.True(Directory.Exists(outDir));
            Assert.Equal("mdm", File.ReadAllText(Path.Combine(outDir, "hello.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
            try { File.Delete(zip); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Cli_parses_add_and_grab()
    {
        Assert.True(CliArgs.TryParseAdd(new[] { "--add", "https://x/a.zip" }, out string url));
        Assert.Equal("https://x/a.zip", url);
        Assert.True(CliArgs.IsGrab(new[] { "--grab", "https://x/page" }));
        Assert.False(CliArgs.TryParseAdd(new[] { "--background" }, out _));
    }

    [Fact]
    public void Plugin_registry_isolates_create()
    {
        PluginRegistry.ClearForTests();
        PluginRegistry.Register(new FakePlugin());
        Assert.Contains("fake", PluginRegistry.LoadedNames);
        var backend = PluginRegistry.TryCreate("fake://x", "c:\\t.bin", 2);
        Assert.NotNull(backend);
        PluginRegistry.ClearForTests();
    }

    [Fact]
    public void Video_probe_reads_og_video()
    {
        string html = """<meta property="og:video" content="https://cdn.example/v.mp4">""";
        var urls = VideoProbe.ExtractFromHtml(html, "https://site.example/watch");
        Assert.Contains("https://cdn.example/v.mp4", urls);
    }

    [Fact]
    public void LooksLikeFileUrl_zip_not_html()
    {
        Assert.True(LinkGrabberService.LooksLikeFileUrl("https://cdn.example/a.zip"));
        Assert.False(LinkGrabberService.LooksLikeFileUrl("https://cdn.example/index.html"));
    }

    private sealed class FakePlugin : IMdmPlugin
    {
        public string Name => "fake";
        public ITransferBackend? TryCreate(string url, string savePath, int threadCount)
            => url.StartsWith("fake:", StringComparison.OrdinalIgnoreCase)
                ? new DownloadEngine("http://127.0.0.1/missing", savePath, 1)
                : null;
    }
}
