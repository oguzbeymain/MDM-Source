using System.Net.Http;
using System.Net.Sockets;
using DownloadMuck;
using Xunit;

namespace DownloadMuck.Tests;

[Collection("Network")]
public class NetworkWatcherTests
{
    public NetworkWatcherTests() => NetworkWatcher.ResetForTests();

    [Fact]
    public async Task WaitUntilOnline_releases_when_forced_online()
    {
        NetworkWatcher.SetOnlineForTests(false);
        Assert.False(NetworkWatcher.Shared.IsOnline);

        var wait = NetworkWatcher.Shared.WaitUntilOnlineAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        NetworkWatcher.SetOnlineForTests(true);
        await wait.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(NetworkWatcher.Shared.IsOnline);
    }

    [Fact]
    public void Transient_detects_socket_and_http()
    {
        Assert.True(NetworkWatcher.IsTransient(new HttpRequestException("fail")));
        Assert.True(NetworkWatcher.IsTransient(new SocketException()));
        Assert.True(NetworkWatcher.IsTransient(new TimeoutException()));
        Assert.True(NetworkWatcher.IsTransient(new IOException("reset", new SocketException())));
        Assert.False(NetworkWatcher.IsTransient(new ArgumentException("no")));
    }

    [Fact]
    public async Task WaitForReconnect_false_when_disabled()
    {
        var settings = AppSettingsStore.Load();
        bool prev = settings.AutoReconnect;
        settings.AutoReconnect = false;
        try
        {
            NetworkWatcher.SetOnlineForTests(false);
            bool ok = await NetworkWatcher.WaitForReconnectAsync(CancellationToken.None, null, null);
            Assert.False(ok);
        }
        finally
        {
            settings.AutoReconnect = prev;
            NetworkWatcher.ResetForTests();
        }
    }
}
