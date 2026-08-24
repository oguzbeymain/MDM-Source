using System.Net;
using System.Security.Cryptography;
using DownloadMuck;
using MonoTorrent;
using MonoTorrent.Client;
using Xunit;

namespace DownloadMuck.Tests;

[Collection("Torrent")]
public class TorrentTransferTests
{
    [Fact]
    public async Task Local_seeder_delivers_payload_via_torrent_file()
    {
        await RunSwarmAsync(useMagnet: false);
    }

    [Fact]
    public async Task Local_seeder_delivers_payload_via_magnet()
    {
        await RunSwarmAsync(useMagnet: true);
    }

    private static async Task RunSwarmAsync(bool useMagnet)
    {
        string root = Path.Combine(Path.GetTempPath(), "mdm-tor-" + Guid.NewGuid().ToString("N"));
        string seedDir = Path.Combine(root, "seed");
        string leechDir = Path.Combine(root, "leech");
        string seedCache = Path.Combine(root, "seed-cache");
        string leechCache = Path.Combine(root, "leech-cache");
        Directory.CreateDirectory(seedDir);
        Directory.CreateDirectory(leechDir);

        byte[] payload = new byte[128 * 1024];
        RandomNumberGenerator.Fill(payload);
        string payloadPath = Path.Combine(seedDir, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, payload);

        var creator = new TorrentCreator { CreatedBy = "MDM-test" };
        var fileSource = new TorrentFileSource(payloadPath);
        var dict = await creator.CreateAsync(fileSource);
        var torrent = Torrent.Load(dict);
        string torrentPath = Path.Combine(root, "payload.torrent");
        await File.WriteAllBytesAsync(torrentPath, dict.Encode());

        int seedPort = FreePort();
        int leechPort = FreePort();
        using var seeder = TorrentEngineHost.CreateIsolated(seedCache, seedPort, loopback: true, dht: false, lpd: false);
        using var leecherEngine = TorrentEngineHost.CreateIsolated(leechCache, leechPort, loopback: true, dht: false, lpd: false);

        try
        {
            var seedManager = await seeder.AddAsync(torrent, seedDir);
            await seedManager.StartAsync();

            string source = useMagnet
                ? new MagnetLink(torrent.InfoHashes.V1 ?? torrent.InfoHashes.V2!, torrent.Name).ToV1String()
                : torrentPath;
            string destHint = Path.Combine(leechDir, "payload.bin");
            var backend = new TorrentTransferBackend(source, destHint, leecherEngine);

            var started = backend.StartOrResumeDownloadAsync();
            for (int i = 0; i < 50 && backend.ManagerForTests == null; i++)
                await Task.Delay(100);

            Assert.NotNull(backend.ManagerForTests);
            await backend.AddPeerForTestsAsync(new Uri($"ipv4://127.0.0.1:{seedPort}"));

            await started.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(backend.CompletedSuccessfully, $"state={backend.ManagerForTests?.State}");

            string downloaded = Directory.GetFiles(leechDir, "*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f) == "payload.bin")
                ?? throw new FileNotFoundException("Leecher did not write payload.bin");
            byte[] got = await File.ReadAllBytesAsync(downloaded);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), Convert.ToHexString(SHA256.HashData(got)));
        }
        finally
        {
            try { await seeder.StopAllAsync(); } catch { /* ignore */ }
            try { await leecherEngine.StopAllAsync(); } catch { /* ignore */ }
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public void Engine_settings_honor_listen_port_and_dht()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mdm-eng-" + Guid.NewGuid().ToString("N"));
        var settings = TorrentEngineHost.BuildSettings(dir, 17011, loopback: true, dht: false, lpd: false, portForward: true);
        Assert.Equal(17011, settings.ListenEndPoints["ipv4"].Port);
        Assert.Null(settings.DhtEndPoint);
        Assert.True(settings.AllowPortForwarding);
        Assert.False(settings.AllowLocalPeerDiscovery);
    }

    [Fact]
    public async Task Peek_reads_name_and_size_from_torrent_file()
    {
        string root = Path.Combine(Path.GetTempPath(), "mdm-peek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payloadPath = Path.Combine(root, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, new byte[4096]);
        var creator = new TorrentCreator { CreatedBy = "MDM-test" };
        var dict = await creator.CreateAsync(new TorrentFileSource(payloadPath));
        string torrentPath = Path.Combine(root, "payload.torrent");
        await File.WriteAllBytesAsync(torrentPath, dict.Encode());
        try
        {
            var peek = await TorrentPeek.TryDescribeAsync(torrentPath);
            Assert.NotNull(peek);
            Assert.False(string.IsNullOrWhiteSpace(peek!.Name));
            Assert.Equal(4096, peek.Size);
            Assert.Equal(1, peek.FileCount);

            var magnetPeek = await TorrentPeek.TryDescribeAsync("magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&dn=Ubuntu%20ISO");
            Assert.Equal("Ubuntu ISO", magnetPeek!.Name);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Pause_stops_before_complete()
    {
        string root = Path.Combine(Path.GetTempPath(), "mdm-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string payloadPath = Path.Combine(root, "payload.bin");
        await File.WriteAllBytesAsync(payloadPath, new byte[8192]);
        var creator = new TorrentCreator { CreatedBy = "MDM-test" };
        var dict = await creator.CreateAsync(new TorrentFileSource(payloadPath));
        string torrentPath = Path.Combine(root, "payload.torrent");
        await File.WriteAllBytesAsync(torrentPath, dict.Encode());
        string cache = Path.Combine(root, "cache");
        string dest = Path.Combine(root, "leech", "payload.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var engine = TorrentEngineHost.CreateIsolated(cache, FreePort(), loopback: true, dht: false, lpd: false);
        var backend = new TorrentTransferBackend(torrentPath, dest, engine);
        var started = backend.StartOrResumeDownloadAsync();
        for (int i = 0; i < 50 && backend.ManagerForTests == null; i++)
            await Task.Delay(100);
        Assert.NotNull(backend.ManagerForTests);
        backend.Pause();
        await started.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(backend.IsPaused);
        Assert.False(backend.CompletedSuccessfully);
        try { await engine.StopAllAsync(); } catch { /* ignore */ }
        try { Directory.Delete(root, true); } catch { /* ignore */ }
    }

    [Fact]
    public void Error_reason_maps_for_status_text()
    {
        Assert.Equal("yazma hatası", TorrentTransferBackend.MapErrorReason("WriteFailure"));
        Assert.Equal("okuma hatası", TorrentTransferBackend.MapErrorReason("ReadFailure"));
        Assert.Equal("parça doğrulama", TorrentTransferBackend.MapErrorReason("HashFailed"));
        Assert.False(TorrentTransferBackend.IsNetworkReason("WriteFailure"));
        Assert.True(TorrentTransferBackend.IsNetworkReason("ConnectionFailed"));
        Assert.Contains("kaldığı yerden", TorrentTransferBackend.FormatFatalStatus(new InvalidOperationException("")), StringComparison.Ordinal);
        Assert.StartsWith("Torrent hatası —", TorrentTransferBackend.FormatFatalStatus(new IOException("disk full")));
    }

    [Fact]
    public void List_error_status_stays_resumable()
    {
        var item = new DownloadItem { Status = "Torrent hatası — yazma hatası" };
        Assert.True(item.IsErrorState);
        Assert.True(item.CanPauseResume);
        Assert.False(item.IsDownloading);
    }
}

public class TorrentClassifierTests
{
    [Fact]
    public void Http_torrent_path_is_torrent_kind()
    {
        Assert.Equal(TransferKind.Torrent, UrlClassifier.Classify("https://cdn.example/ubuntu.torrent"));
        Assert.Equal(TransferKind.Http, UrlClassifier.Classify("https://x.com/dl?name=a.torrent"));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Torrent));
        Assert.True(UrlClassifier.CanDownloadNow(TransferKind.Magnet));
    }

    [Fact]
    public void Extract_includes_magnet()
    {
        var urls = UrlClassifier.ExtractDownloadUrls("see magnet:?xt=urn:btih:abc123&dn=pack and https://cdn.example/a.zip");
        Assert.Contains(urls, u => u.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("https://cdn.example/a.zip", urls, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Magnet_display_name_from_dn()
    {
        Assert.Equal("Ubuntu ISO", FileNameHelper.TryFileNameFromUrl("magnet:?xt=urn:btih:abc&dn=Ubuntu%20ISO"));
    }

    [Fact]
    public void Factory_creates_torrent_backend()
    {
        var backend = TransferFactory.Create("magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(Path.GetTempPath(), "t.bin"));
        Assert.IsType<TorrentTransferBackend>(backend);
    }
}
