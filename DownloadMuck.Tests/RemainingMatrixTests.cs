using System.Security.Cryptography;
using System.Text;
using DownloadMuck;
using Xunit;

namespace DownloadMuck.Tests;

public class RemainingMatrixTests
{
    [Theory]
    [InlineData(1000, 10, 1, 0, 1)]
    [InlineData(8L * 1024 * 1024, 20, 1, 0, 4)]
    [InlineData(200L * 1024 * 1024, 20, 1, 0, 8)]
    [InlineData(200L * 1024 * 1024, 300, 1, 0, 4)]
    [InlineData(200L * 1024 * 1024, 20, 8, 0, 4)]
    public void Channel_budget_scales_with_size_rtt_and_load(long bytes, int rtt, int jobs, int host, int expectedMax)
    {
        int n = ChannelBudget.ForJob(bytes, rtt, jobs, host);
        Assert.InRange(n, 1, expectedMax);
        Assert.True(n <= ChannelBudget.MaxPerJob);
    }

    [Fact]
    public void Host_load_add_remove_balances()
    {
        string host = "budget-test-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, HostLoad.Active(host));
        HostLoad.Add(host, 4);
        Assert.Equal(4, HostLoad.Active(host));
        HostLoad.Remove(host, 4);
        Assert.Equal(0, HostLoad.Active(host));
    }

    [Fact]
    public void Smart_rules_skip_extension_and_rename()
    {
        var settings = new AppSettings
        {
            SkipExtensions = "exe, scr",
            SkipUrlContains = "tracker",
            SkipDuplicateUrls = true,
            RenamePattern = "{name}_{date}{ext}"
        };
        Assert.True(SmartRules.ShouldSkip("https://a/x.exe", "x.exe", settings, Array.Empty<string>(), out _));
        Assert.True(SmartRules.ShouldSkip("https://a/tracker/a.zip", "a.zip", settings, Array.Empty<string>(), out _));
        Assert.True(SmartRules.ShouldSkip("https://a/a.zip", "a.zip", settings, new[] { "https://a/a.zip" }, out _));
        Assert.False(SmartRules.ShouldSkip("https://a/a.zip", "a.zip", settings, Array.Empty<string>(), out _));

        string renamed = SmartRules.ApplyRename("pack.zip", "https://cdn.example/pack.zip", new DateTime(2026, 8, 24), settings.RenamePattern);
        Assert.Equal("pack_20260824.zip", renamed);
        Assert.Equal("pack.zip", SmartRules.ApplyRename("pack.zip", "https://x/y", DateTime.Now, "{name}{ext}"));
    }

    [Fact]
    public void Smart_rules_domain_regex_mime_and_size()
    {
        var settings = new AppSettings
        {
            SkipDomains = "ads.example.com",
            SkipUrlRegex = @"/tracker/\d+",
            SkipMimeContains = "video/",
            SkipMinSizeMb = 2,
            SkipMaxSizeMb = 100
        };
        Assert.True(SmartRules.HostMatches("https://cdn.ads.example.com/a.zip", settings.SkipDomains));
        Assert.True(SmartRules.RegexMatches("https://x/tracker/12/a.zip", settings.SkipUrlRegex));
        Assert.False(SmartRules.RegexMatches("https://x/ok.zip", settings.SkipUrlRegex));
        Assert.Equal("video/mp4", SmartRules.GuessMime("clip.mkv"));
        Assert.True(SmartRules.ShouldSkip("https://cdn.example/a.mp4", "a.mp4", settings, Array.Empty<string>(), out _));
        Assert.True(SmartRules.ShouldSkip("https://cdn.example/a.bin", "a.bin", settings, Array.Empty<string>(), out _, fileSizeBytes: 500_000));
        Assert.True(SmartRules.ShouldSkip("https://cdn.example/a.bin", "a.bin", settings, Array.Empty<string>(), out _, fileSizeBytes: 200L * 1024 * 1024));
        Assert.False(SmartRules.ShouldSkip("https://cdn.example/a.bin", "a.bin", settings, Array.Empty<string>(), out _, fileSizeBytes: 10L * 1024 * 1024));
    }

    [Fact]
    public void Queue_and_speed_limit_defaults()
    {
        Assert.False(DownloadQueue.IsFull(2, 0));
        Assert.True(DownloadQueue.IsFull(2, 2));
        Assert.False(DownloadQueue.IsFull(1, 2));
        Assert.Equal(ChannelBudget.MaxPerJob, DownloadQueue.HttpChannels(new AppSettings { HttpMaxChannels = 0 }));
        Assert.Equal(4, DownloadQueue.HttpChannels(new AppSettings { HttpMaxChannels = 4 }));
        SpeedLimiter.ResetForTests();
        SpeedLimiter.OverrideBytesPerSecond = 0;
        Assert.Equal(0, SpeedLimiter.LimitBytesPerSecond);
        SpeedLimiter.ResetForTests();
    }

    [Fact]
    public void Cli_accepts_download_alias_and_bare_url()
    {
        Assert.True(CliArgs.TryParseVerb(new[] { "download", "https://x/a.zip" }, out string verb, out string value));
        Assert.Equal("add", verb);
        Assert.Equal("https://x/a.zip", value);
        Assert.True(CliArgs.TryParseAdd(new[] { "--download", "https://x/b.bin" }, out string url));
        Assert.Equal("https://x/b.bin", url);
        Assert.True(CliArgs.TryParseAdd(new[] { "https://x/c.bin" }, out url));
        Assert.Equal("https://x/c.bin", url);
        Assert.True(CliArgs.LooksLikeDownloadTarget("magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Fact]
    public void Metalink_parses_sha256_hash()
    {
        string xml = """
            <?xml version="1.0"?>
            <metalink xmlns="urn:ietf:params:xml:ns:metalink">
              <file name="app.bin">
                <size>4</size>
                <hash type="sha-256">9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08</hash>
                <url>https://a.example/app.bin</url>
              </file>
            </metalink>
            """;
        var parsed = MetalinkParser.Parse(xml);
        Assert.NotNull(parsed);
        Assert.Equal("sha-256", parsed!.HashType);
        Assert.Equal(64, parsed.HashValue!.Length);
    }

    [Fact]
    public void Checksum_verifies_sha256()
    {
        string path = Path.Combine(Path.GetTempPath(), "mdm-hash-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("test"));
        try
        {
            string hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("test")));
            Assert.True(ChecksumUtil.TryVerifyFile(path, "sha-256", hex));
            Assert.False(ChecksumUtil.TryVerifyFile(path, "sha-256", "00"));
            Assert.True(ChecksumUtil.TryVerifyFile(path, null, null));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Robots_disallow_and_allow_longest_match()
    {
        string robots = """
            User-agent: *
            Disallow: /private
            Allow: /private/ok
            """;
        Assert.False(RobotsTxt.IsAllowed(robots, "MDM", "/private/secret.zip"));
        Assert.True(RobotsTxt.IsAllowed(robots, "MDM", "/private/ok/a.zip"));
        Assert.True(RobotsTxt.IsAllowed(robots, "MDM", "/public/a.zip"));
        Assert.True(RobotsTxt.IsAllowed("", "MDM", "/x"));
    }

    [Fact]
    public void Hls_picks_highest_bandwidth()
    {
        string pl = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=800000
            low.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000
            high.m3u8
            """;
        string? best = HlsPlaylist.PickBestVariant(pl, "https://cdn.example/v/master.m3u8");
        Assert.Equal("https://cdn.example/v/high.m3u8", best);
    }

    [Fact]
    public void Video_probe_reads_video_tag_and_jsonld()
    {
        string html = """
            <video src="https://cdn.example/clip.mp4"></video>
            <script type="application/ld+json">{"@type":"VideoObject","contentUrl":"https://cdn.example/ld.webm"}</script>
            """;
        var urls = VideoProbe.ExtractFromHtml(html, "https://site.example/watch");
        Assert.Contains("https://cdn.example/clip.mp4", urls);
        Assert.Contains("https://cdn.example/ld.webm", urls);
    }

    [Fact]
    public void Remote_router_lists_jobs_and_requires_lan_token()
    {
        var host = new FakeJobs();
        host.Jobs.Add(new RemoteJobDto { Id = "abc", Url = "https://x/a.zip", FileName = "a.zip", Status = "İndiriliyor" });

        var list = RemoteApiRouter.Route("GET", "/jobs", "", true, "", false, "", (_, _, _) => { }, host);
        Assert.Equal(200, list.Status);
        Assert.Contains("abc", list.Body);

        var denied = RemoteApiRouter.Route("GET", "/jobs", "", false, "", true, "secret", (_, _, _) => { }, host);
        Assert.Equal(401, denied.Status);

        var ok = RemoteApiRouter.Route("GET", "/jobs", "", false, "Bearer secret", true, "secret", (_, _, _) => { }, host);
        Assert.Equal(200, ok.Status);

        var paused = RemoteApiRouter.Route("POST", "/jobs/abc/pause", "", true, "", false, "", (_, _, _) => { }, host);
        Assert.Equal(200, paused.Status);
        Assert.Contains("abc", host.Paused);

        string? captured = null;
        var cap = RemoteApiRouter.Route("POST", "/capture", """{"url":"https://x/b.bin","filename":"b.bin"}""", true, "", false, "",
            (u, _, _) => captured = u, host);
        Assert.Equal(200, cap.Status);
        Assert.Equal("https://x/b.bin", captured);
    }

    [Fact]
    public async Task Capture_server_serves_jobs_json()
    {
        var host = new FakeJobs();
        host.Jobs.Add(new RemoteJobDto { Id = "j1", FileName = "a.bin", Status = "Hazır", Url = "https://x/a.bin" });
        using var server = new BrowserCaptureServer((_, _, _) => { }, lan: false, token: null, jobs: host);
        server.Start();
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var resp = await client.GetAsync($"http://127.0.0.1:{server.ActivePort}/jobs");
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("j1", body);
            Assert.Contains("a.bin", body);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public void Cli_parses_list_and_pause()
    {
        Assert.True(CliArgs.TryParseVerb(new[] { "--list" }, out string verb, out string value));
        Assert.Equal("list", verb);
        Assert.True(CliArgs.IsRemoteQuery(verb));
        Assert.True(CliArgs.TryParseVerb(new[] { "--pause", "abc" }, out verb, out value));
        Assert.Equal("pause", verb);
        Assert.Equal("abc", value);
    }

    private sealed class FakeJobs : IRemoteJobHost
    {
        public List<RemoteJobDto> Jobs { get; } = new();
        public List<string> Paused { get; } = new();

        public IReadOnlyList<RemoteJobDto> ListJobs() => Jobs;
        public RemoteJobDto? GetJob(string id) => Jobs.FirstOrDefault(j => j.Id == id);
        public string? AddJob(string url, string? filename)
        {
            var dto = new RemoteJobDto { Id = "new", Url = url, FileName = filename ?? "" };
            Jobs.Add(dto);
            return dto.Id;
        }
        public bool Pause(string id)
        {
            if (GetJob(id) == null) return false;
            Paused.Add(id);
            return true;
        }
        public bool Resume(string id) => GetJob(id) != null;
        public bool Cancel(string id) => GetJob(id) != null;
    }
}
