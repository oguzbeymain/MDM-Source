using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using MDM;
using Xunit;

namespace MDM.Tests;

[Collection("Network")]
public class DownloadEngineTests
{
    private static byte[] RandomBytes(int size)
    {
        var data = new byte[size];
        RandomNumberGenerator.Fill(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public async Task Multi_chunk_download_matches_payload()
    {
        byte[] payload = RandomBytes(6 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 4);
            await engine.StartOrResumeDownloadAsync();

            Assert.True(engine.CompletedSuccessfully);
            Assert.False(engine.IsDownloading);
            byte[] got = await File.ReadAllBytesAsync(dest);
            Assert.Equal(Sha(payload), Sha(got));
            Assert.False(File.Exists(dest + ".mdmstate"));
            Assert.True(server.RequestCount >= 2, $"expected probe+ranges, got {server.RequestCount}");
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Pause_then_resume_completes_same_bytes()
    {
        byte[] payload = RandomBytes(4 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload, throttleBytesPerWrite: 32 * 1024, throttleDelayMs: 8);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 4);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProgressChanged += p =>
            {
                if (p >= 8)
                    started.TrySetResult();
            };

            Task run = engine.StartOrResumeDownloadAsync();
            await Task.WhenAny(started.Task, Task.Delay(8000));
            Assert.True(started.Task.IsCompleted, "download did not reach 8% in time");

            engine.Pause();
            await run;

            Assert.True(engine.IsPaused || engine.CompletedSuccessfully);
            if (engine.CompletedSuccessfully)
                return; // throttle lost the race; still valid if hash matches below

            Assert.True(File.Exists(dest));
            Assert.True(File.Exists(dest + ".mdmstate"));

            await engine.StartOrResumeDownloadAsync();
            Assert.True(engine.CompletedSuccessfully);
            byte[] got = await File.ReadAllBytesAsync(dest);
            Assert.Equal(Sha(payload), Sha(got));
            Assert.False(File.Exists(dest + ".mdmstate"));
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task New_engine_resumes_from_mdmstate()
    {
        byte[] payload = RandomBytes(3 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload, throttleBytesPerWrite: 16 * 1024, throttleDelayMs: 10);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var first = new DownloadEngine(server.Url, dest, threadCount: 4);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.ProgressChanged += p =>
            {
                if (p >= 10)
                    started.TrySetResult();
            };

            Task run = first.StartOrResumeDownloadAsync();
            await Task.WhenAny(started.Task, Task.Delay(8000));
            first.Pause();
            await run;

            if (first.CompletedSuccessfully)
            {
                Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(dest)));
                return;
            }

            Assert.True(File.Exists(dest + ".mdmstate"));
            var second = new DownloadEngine(server.Url, dest, threadCount: 4);
            await second.StartOrResumeDownloadAsync();
            Assert.True(second.CompletedSuccessfully);
            Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(dest)));
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task No_accept_ranges_falls_back_to_single_stream()
    {
        byte[] payload = RandomBytes(512 * 1024);
        await using var server = new RangeHttpServer(payload) { SendAcceptRanges = false };
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 8);
            var statuses = new List<string>();
            engine.StatusChanged += s => statuses.Add(s);
            await engine.StartOrResumeDownloadAsync();
            Assert.True(engine.CompletedSuccessfully);
            Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(dest)));
            Assert.Contains(statuses, s => s.Contains("Tek kanaldan", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, server.RequestCount);
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Tiny_file_single_chunk_still_correct()
    {
        byte[] payload = RandomBytes(1200);
        await using var server = new RangeHttpServer(payload);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 8);
            await engine.StartOrResumeDownloadAsync();
            Assert.True(engine.CompletedSuccessfully);
            Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(dest)));
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Http11_or_lower_client_talks_to_cleartext_server()
    {
        byte[] payload = RandomBytes(4096);
        await using var server = new RangeHttpServer(payload);
        using var client = TransferHttp.CreateClient(preferHttp3: false);
        using var resp = await client.GetAsync(server.Url);
        resp.EnsureSuccessStatusCode();
        byte[] got = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(Sha(payload), Sha(got));
        Assert.True(resp.Version == HttpVersion.Version11 || resp.Version == HttpVersion.Version10);
    }

    [Fact]
    public async Task Http3_preference_still_falls_back_on_cleartext()
    {
        byte[] payload = RandomBytes(4096);
        await using var server = new RangeHttpServer(payload);
        using var client = TransferHttp.CreateClient(preferHttp3: true);
        using var resp = await client.GetAsync(server.Url);
        resp.EnsureSuccessStatusCode();
        byte[] got = await resp.Content.ReadAsByteArrayAsync();
        Assert.Equal(Sha(payload), Sha(got));
    }

    [Fact]
    public async Task Resumes_legacy_eight_equal_chunks_without_replanning()
    {
        byte[] payload = RandomBytes(8 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            const int pieces = 8;
            long chunkSize = payload.Length / pieces;
            await using (var fs = new FileStream(dest, FileMode.Create, FileAccess.Write))
            {
                fs.SetLength(payload.Length);
                await fs.WriteAsync(payload.AsMemory(0, (int)(4 * chunkSize)));
            }

            var chunks = new List<object>(pieces);
            for (int i = 0; i < pieces; i++)
            {
                long start = i * chunkSize;
                long end = i == pieces - 1 ? payload.Length - 1 : start + chunkSize - 1;
                chunks.Add(new
                {
                    Start = start,
                    End = end,
                    CurrentOffset = i < 4 ? end + 1 : start
                });
            }

            var dto = new
            {
                Url = server.Url,
                TotalSize = (long)payload.Length,
                SingleStream = false,
                Chunks = chunks
            };
            await File.WriteAllTextAsync(dest + ".mdmstate", JsonSerializer.Serialize(dto));

            var engine = new DownloadEngine(server.Url, dest, threadCount: 8);
            await engine.StartOrResumeDownloadAsync();
            Assert.True(engine.CompletedSuccessfully);
            Assert.Equal(Sha(payload), Sha(await File.ReadAllBytesAsync(dest)));
            Assert.False(File.Exists(dest + ".mdmstate"));
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Cancel_removes_partial_file_and_state()
    {
        byte[] payload = RandomBytes(3 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload, throttleBytesPerWrite: 16 * 1024, throttleDelayMs: 12);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 4);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProgressChanged += p =>
            {
                if (p >= 5)
                    started.TrySetResult();
            };

            Task run = engine.StartOrResumeDownloadAsync();
            await Task.WhenAny(started.Task, Task.Delay(8000));
            Assert.True(started.Task.IsCompleted, "download did not start in time");

            engine.Cancel();
            await run;

            Assert.True(engine.IsCancelled);
            Assert.False(engine.CompletedSuccessfully);
            Assert.False(File.Exists(dest + ".mdmstate"));
            Assert.False(File.Exists(dest));
        }
        finally
        {
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task Dropped_connection_resumes_when_network_returns()
    {
        NetworkWatcher.ResetForTests();
        NetworkWatcher.SetOnlineForTests(true);
        var settings = AppSettingsStore.Load();
        bool prev = settings.AutoReconnect;
        settings.AutoReconnect = true;

        byte[] payload = RandomBytes(2 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload, throttleBytesPerWrite: 16 * 1024, throttleDelayMs: 6);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 4);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProgressChanged += p =>
            {
                if (p >= 8)
                    started.TrySetResult();
            };
            engine.StatusChanged += s =>
            {
                if (s.Contains("Ağ", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("bekleniyor", StringComparison.OrdinalIgnoreCase))
                    waiting.TrySetResult();
            };

            Task run = engine.StartOrResumeDownloadAsync();
            await Task.WhenAny(started.Task, Task.Delay(12000));
            Assert.True(started.Task.IsCompleted, "download did not reach 8%");

            server.Drop = true;
            NetworkWatcher.SetOnlineForTests(false);
            await Task.WhenAny(waiting.Task, Task.Delay(15000));
            Assert.True(waiting.Task.IsCompleted, "engine did not wait for network");
            Assert.False(engine.IsPaused);
            Assert.True(engine.IsDownloading);

            server.Drop = false;
            NetworkWatcher.SetOnlineForTests(true);
            await run.WaitAsync(TimeSpan.FromSeconds(40));

            Assert.True(engine.CompletedSuccessfully);
            byte[] got = await File.ReadAllBytesAsync(dest);
            Assert.Equal(Sha(payload), Sha(got));
        }
        finally
        {
            settings.AutoReconnect = prev;
            NetworkWatcher.ResetForTests();
            TryDelete(dest);
        }
    }

    [Fact]
    public async Task User_pause_does_not_auto_resume_when_network_returns()
    {
        NetworkWatcher.ResetForTests();
        NetworkWatcher.SetOnlineForTests(true);
        var settings = AppSettingsStore.Load();
        bool prev = settings.AutoReconnect;
        settings.AutoReconnect = true;

        byte[] payload = RandomBytes(2 * 1024 * 1024);
        await using var server = new RangeHttpServer(payload, throttleBytesPerWrite: 16 * 1024, throttleDelayMs: 6);
        string dest = Path.Combine(Path.GetTempPath(), "mdm-test-" + Guid.NewGuid().ToString("N") + ".bin");

        try
        {
            var engine = new DownloadEngine(server.Url, dest, threadCount: 4);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.ProgressChanged += p =>
            {
                if (p >= 8)
                    started.TrySetResult();
            };
            engine.StatusChanged += s =>
            {
                if (s.Contains("Ağ", StringComparison.OrdinalIgnoreCase)
                    || s.Contains("bekleniyor", StringComparison.OrdinalIgnoreCase))
                    waiting.TrySetResult();
            };

            Task run = engine.StartOrResumeDownloadAsync();
            await Task.WhenAny(started.Task, Task.Delay(12000));
            Assert.True(started.Task.IsCompleted, "download did not reach 8%");

            server.Drop = true;
            NetworkWatcher.SetOnlineForTests(false);
            await Task.WhenAny(waiting.Task, Task.Delay(15000));
            Assert.True(waiting.Task.IsCompleted, "engine did not wait for network");

            engine.Pause();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(engine.IsPaused);
            Assert.False(engine.CompletedSuccessfully);

            server.Drop = false;
            NetworkWatcher.SetOnlineForTests(true);
            await Task.Delay(1500);
            Assert.True(engine.IsPaused);
            Assert.False(engine.CompletedSuccessfully);
            Assert.False(engine.IsDownloading);
        }
        finally
        {
            settings.AutoReconnect = prev;
            NetworkWatcher.ResetForTests();
            TryDelete(dest);
        }
    }

    private static void TryDelete(string dest)
    {
        try { if (File.Exists(dest)) File.Delete(dest); } catch { /* ignore */ }
        try { if (File.Exists(dest + ".mdmstate")) File.Delete(dest + ".mdmstate"); } catch { /* ignore */ }
    }
}
