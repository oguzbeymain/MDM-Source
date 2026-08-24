using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DownloadMuck
{
    public interface INetworkProbe
    {
        Task<bool> ProbeAsync(string? extraHost, CancellationToken token);
    }

    public sealed class TcpNetworkProbe : INetworkProbe
    {
        private static readonly (string Host, int Port)[] Defaults =
        {
            ("1.1.1.1", 443),
            ("8.8.8.8", 443)
        };

        public async Task<bool> ProbeAsync(string? extraHost, CancellationToken token)
        {
            if (!string.IsNullOrWhiteSpace(extraHost)
                && !extraHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                && extraHost is not "127.0.0.1" && extraHost is not "::1" && extraHost is not "[::1]")
            {
                if (await TryConnectAsync(extraHost, 443, token).ConfigureAwait(false)
                    || await TryConnectAsync(extraHost, 80, token).ConfigureAwait(false))
                    return true;
            }

            foreach (var (host, port) in Defaults)
            {
                if (await TryConnectAsync(host, port, token).ConfigureAwait(false))
                    return true;
            }
            return false;
        }

        private static async Task<bool> TryConnectAsync(string host, int port, CancellationToken token)
        {
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
                return client.Connected;
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class NetworkWatcher
    {
        private static readonly object SharedGate = new();
        private static NetworkWatcher? _shared;

        public static NetworkWatcher Shared
        {
            get
            {
                lock (SharedGate)
                    return _shared ??= new NetworkWatcher(new TcpNetworkProbe());
            }
        }

        private readonly INetworkProbe _probe;
        private readonly object _gate = new();
        private readonly TimeSpan _debounce = TimeSpan.FromSeconds(1);
        private TaskCompletionSource _onlineSignal = NewSignal();
        private CancellationTokenSource? _refreshCts;
        private bool _started;
        private bool _probedOnline = true;
        private bool? _forcedOnline;
        private int _refreshSerial;

        public NetworkWatcher(INetworkProbe probe) => _probe = probe ?? throw new ArgumentNullException(nameof(probe));

        public event Action<bool>? Changed;

        public bool IsOnline
        {
            get
            {
                lock (_gate)
                    return _forcedOnline ?? _probedOnline;
            }
        }

        public static bool AutoReconnectEnabled => AppSettingsStore.Load().AutoReconnect;

        public static void SetOnlineForTests(bool? online)
        {
            var w = Shared;
            bool was = w.IsOnline;
            lock (w._gate)
                w._forcedOnline = online;
            w.Publish(was, w.IsOnline);
        }

        public static void ResetForTests()
        {
            lock (SharedGate)
            {
                _shared?.Stop();
                _shared = new NetworkWatcher(new TcpNetworkProbe());
                _shared._probedOnline = true;
                _shared._forcedOnline = null;
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                    return;
                _started = true;
            }
            NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
            NetworkChange.NetworkAddressChanged += OnAddressEvent;
            _ = RefreshAsync();
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (!_started)
                    return;
                _started = false;
            }
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkEvent;
            NetworkChange.NetworkAddressChanged -= OnAddressEvent;
            try { _refreshCts?.Cancel(); } catch { /* ignore */ }
        }

        public async Task WaitUntilOnlineAsync(CancellationToken token)
        {
            while (!IsOnline)
            {
                token.ThrowIfCancellationRequested();
                Task wait;
                lock (_gate)
                {
                    if (IsOnline)
                        return;
                    wait = _onlineSignal.Task;
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }

        public static async Task<bool> WaitForReconnectAsync(
            CancellationToken token,
            Action<string>? status,
            Action<string, string>? speed,
            string? hostHint = null)
        {
            if (!AutoReconnectEnabled)
                return false;

            status?.Invoke("Ağ bekleniyor...");
            speed?.Invoke("0 MB/s", "--:--:--");

            var watcher = Shared;
            if (watcher.IsOnline)
            {
                status?.Invoke("Ağ bekleniyor...");
                speed?.Invoke("0 MB/s", "--:--:--");
                await Task.Delay(800, token).ConfigureAwait(false);
                return true;
            }

            status?.Invoke("Ağ bekleniyor...");
            speed?.Invoke("0 MB/s", "--:--:--");

            if (hostHint != null)
                _ = watcher.RefreshAsync(hostHint);

            await watcher.WaitUntilOnlineAsync(token).ConfigureAwait(false);
            status?.Invoke("Kaldığı yerden devam ediliyor...");
            return true;
        }

        public static bool IsTransient(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                if (e is HttpRequestException or TimeoutException or SocketException)
                    return true;
                if (e is IOException)
                    return true;
            }
            return false;
        }

        private void OnNetworkEvent(object? sender, NetworkAvailabilityEventArgs e) => ScheduleRefresh();
        private void OnAddressEvent(object? sender, EventArgs e) => ScheduleRefresh();

        private void ScheduleRefresh()
        {
            int serial = Interlocked.Increment(ref _refreshSerial);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_debounce).ConfigureAwait(false);
                    if (serial != Volatile.Read(ref _refreshSerial))
                        return;
                    await RefreshAsync().ConfigureAwait(false);
                }
                catch { /* ignore */ }
            });
        }

        private async Task RefreshAsync(string? extraHost = null)
        {
            var cts = new CancellationTokenSource();
            var prev = Interlocked.Exchange(ref _refreshCts, cts);
            try { prev?.Cancel(); } catch { /* ignore */ }

            bool was = IsOnline;
            bool probed;
            try
            {
                probed = await _probe.ProbeAsync(extraHost, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                probed = false;
            }

            lock (_gate)
                _probedOnline = probed;

            Publish(was, IsOnline);
        }

        private void Publish(bool wasOnline, bool nowOnline)
        {
            if (nowOnline)
            {
                TaskCompletionSource old;
                lock (_gate)
                {
                    old = _onlineSignal;
                    if (!old.Task.IsCompleted)
                        _onlineSignal = NewSignal();
                }
                old.TrySetResult();
            }
            else
            {
                lock (_gate)
                {
                    if (_onlineSignal.Task.IsCompleted)
                        _onlineSignal = NewSignal();
                }
            }

            if (wasOnline != nowOnline)
                Changed?.Invoke(nowOnline);
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
