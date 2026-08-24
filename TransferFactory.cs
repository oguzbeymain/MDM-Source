namespace DownloadMuck
{
    public static class TransferFactory
    {
        public static ITransferBackend Create(string url, string savePath, int threadCount = 8)
            => Create(new[] { url }, savePath, threadCount);

        public static ITransferBackend Create(IReadOnlyList<string> urls, string savePath, int threadCount = 8)
        {
            if (urls == null || urls.Count == 0)
                throw new ArgumentException("URL gerekli.", nameof(urls));

            string primary = urls[0];
            var plugin = PluginRegistry.TryCreate(primary, savePath, threadCount);
            if (plugin != null)
                return plugin;

            var kind = UrlClassifier.Classify(primary);
            return kind switch
            {
                TransferKind.Ftp => new FtpTransferBackend(primary, savePath),
                TransferKind.Sftp => new SftpTransferBackend(primary, savePath),
                TransferKind.Metalink => new MetalinkTransferBackend(primary, savePath, threadCount),
                TransferKind.Magnet or TransferKind.Torrent => new TorrentTransferBackend(primary, savePath),
                _ => new DownloadEngine(urls, savePath, threadCount)
            };
        }
    }
}
