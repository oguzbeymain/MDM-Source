using System.IO;
using FluentFTP;

namespace DownloadMuck
{
    public sealed class FtpTransferBackend : StreamCopyBackend
    {
        private readonly RemoteEndpoint _ep;

        public FtpTransferBackend(string url, string savePath) : base(savePath)
        {
            if (!RemoteEndpointParser.TryParse(url, "ftp", 21, out _ep))
                throw new ArgumentException("Geçersiz FTP adresi.", nameof(url));
        }

        protected override string? ProbeHost => _ep.Host;

        protected override async Task<(Stream Stream, long Total)> OpenReadAsync(long offset, CancellationToken token)
        {
            Status("FTP bağlanıyor...");
            var client = new AsyncFtpClient(_ep.Host, _ep.User, _ep.Password, _ep.Port);
            client.Config.ConnectTimeout = 20000;
            client.Config.ReadTimeout = 30000;
            await client.Connect(token);
            Hold(client);

            long total = await client.GetFileSize(_ep.RemotePath, -1, token);
            if (total < 0)
                total = 0;
            if (total > 0)
                NotifySize(total);

            Status("FTP indiriliyor...");
            Stream stream = await client.OpenRead(_ep.RemotePath, FtpDataType.Binary, offset);
            return (stream, total);
        }
    }

    public sealed class SftpTransferBackend : StreamCopyBackend
    {
        private readonly RemoteEndpoint _ep;

        public SftpTransferBackend(string url, string savePath) : base(savePath)
        {
            if (!RemoteEndpointParser.TryParse(url, "sftp", 22, out _ep))
                throw new ArgumentException("Geçersiz SFTP adresi.", nameof(url));
        }

        protected override string? ProbeHost => _ep.Host;

        protected override Task<(Stream Stream, long Total)> OpenReadAsync(long offset, CancellationToken token)
        {
            Status("SFTP bağlanıyor...");
            var client = new Renci.SshNet.SftpClient(_ep.Host, _ep.Port, _ep.User, _ep.Password);
            client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(20);
            client.Connect();
            Hold(client);

            if (!client.Exists(_ep.RemotePath))
                throw new FileNotFoundException("SFTP dosyası bulunamadı.", _ep.RemotePath);

            var info = client.GetAttributes(_ep.RemotePath);
            long total = info.Size;
            if (total > 0)
                NotifySize(total);

            Status("SFTP indiriliyor...");
            var stream = client.OpenRead(_ep.RemotePath);
            if (offset > 0)
                stream.Seek(offset, SeekOrigin.Begin);
            return Task.FromResult<(Stream, long)>((stream, total));
        }
    }
}
