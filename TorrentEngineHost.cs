using System.IO;
using System.Net;
using MonoTorrent.Client;

namespace DownloadMuck
{
    public static class TorrentEngineHost
    {
        private static readonly object Gate = new();
        private static ClientEngine? _shared;

        public static ClientEngine Shared
        {
            get
            {
                lock (Gate)
                {
                    if (_shared != null)
                        return _shared;
                    string cache = Path.Combine(AppSettingsStore.StoreDir, "torrent-cache");
                    Directory.CreateDirectory(cache);
                    var app = AppSettingsStore.Load();
                    int port = app.TorrentListenPort <= 0 ? 6881 : Math.Clamp(app.TorrentListenPort, 1, 65535);
                    try
                    {
                        _shared = new ClientEngine(BuildFromApp(cache, port, app));
                    }
                    catch
                    {
                        _shared = new ClientEngine(BuildFromApp(cache, 0, app));
                    }
                    return _shared;
                }
            }
        }

        public static EngineSettings BuildFromApp(string cacheDir, int listenPort, AppSettings? settings = null)
        {
            var s = settings ?? AppSettingsStore.Load();
            return BuildSettings(
                cacheDir,
                listenPort,
                loopback: false,
                dht: s.TorrentDht,
                lpd: s.TorrentLocalPeers,
                portForward: s.TorrentPortForward,
                maxConnections: Math.Clamp(s.TorrentMaxConnections, 20, 400),
                maxDownloadRate: Math.Max(0, s.SpeedLimitKBps) * 1024);
        }

        public static EngineSettings BuildSettings(
            string cacheDir,
            int listenPort,
            bool loopback,
            bool dht,
            bool lpd,
            bool portForward = false,
            int maxConnections = 120,
            int maxDownloadRate = 0)
        {
            Directory.CreateDirectory(cacheDir);
            var listen = loopback ? IPAddress.Loopback : IPAddress.Any;
            var builder = new EngineSettingsBuilder
            {
                AllowPortForwarding = portForward,
                AllowLocalPeerDiscovery = lpd,
                AutoSaveLoadFastResume = true,
                AutoSaveLoadMagnetLinkMetadata = true,
                AutoSaveLoadDhtCache = dht,
                CacheDirectory = cacheDir,
                ListenEndPoints = new Dictionary<string, IPEndPoint>
                {
                    ["ipv4"] = new IPEndPoint(listen, listenPort)
                },
                DhtEndPoint = dht ? new IPEndPoint(listen, listenPort) : null,
                MaximumConnections = maxConnections,
                MaximumDownloadRate = Math.Max(0, maxDownloadRate)
            };
            return builder.ToSettings();
        }

        public static ClientEngine CreateIsolated(string cacheDir, int listenPort, bool loopback, bool dht, bool lpd)
            => new(BuildSettings(cacheDir, listenPort, loopback, dht, lpd));

        public static void ReloadIfIdle()
        {
            lock (Gate)
            {
                if (_shared == null)
                    return;
                if (_shared.Torrents.Count > 0)
                    return;
                try { _shared.Dispose(); } catch { /* ignore */ }
                _shared = null;
            }
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                if (_shared == null)
                    return;
                try { _shared.Dispose(); } catch { /* ignore */ }
                _shared = null;
            }
        }
    }
}
