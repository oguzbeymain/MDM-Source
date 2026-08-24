using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace DownloadMuck
{
    /// <summary>
    /// Tarayici eklentisi icin 127.0.0.1 uzerinde hafif HTTP sunucu.
    /// Windows excluded-port / WSAEACCES icin birden fazla port dener.
    /// </summary>
    public sealed class BrowserCaptureServer : IDisposable
    {
        // 6800 Hyper-V / Windows tarafindan sikca engellenir; once daha guvenli portlar
        public static readonly int[] CandidatePorts = { 18680, 18681, 18682, 18700, 27182, 38472, 6800 };

        private readonly Action<string, string, string> _onDownloadRequested;
        private readonly bool _lan;
        private readonly string _token;
        private readonly IRemoteJobHost? _jobs;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning { get; private set; }
        public int ActivePort { get; private set; }
        public string? LastError { get; private set; }

        public BrowserCaptureServer(
            Action<string, string, string> onDownloadRequested,
            bool lan = false,
            string? token = null,
            IRemoteJobHost? jobs = null)
        {
            _onDownloadRequested = onDownloadRequested;
            _lan = lan;
            _token = token ?? "";
            _jobs = jobs;
        }

        public void Start()
        {
            if (IsRunning) return;

            var errors = new List<string>();

            foreach (int port in CandidatePorts)
            {
                try
                {
                    _cts = new CancellationTokenSource();
                    var listener = new TcpListener(_lan ? IPAddress.Any : IPAddress.Loopback, port);
                    listener.Start();

                    _listener = listener;
                    ActivePort = port;
                    IsRunning = true;
                    LastError = null;
                    _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));

                    TryWritePortFile(port);
                    Debug.WriteLine($"BrowserCaptureServer listening on 127.0.0.1:{port}");
                    return;
                }
                catch (SocketException ex)
                {
                    errors.Add($"{port}: {ex.Message}");
                    Debug.WriteLine($"Port {port} failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    errors.Add($"{port}: {ex.Message}");
                    Debug.WriteLine($"Port {port} failed: {ex.Message}");
                }
            }

            IsRunning = false;
            ActivePort = 0;
            LastError = string.Join(" | ", errors);
            throw new InvalidOperationException(
                "Hicbir eklenti portu acilamadi. " + LastError);
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _listener?.Stop();
            }
            catch { /* ignore */ }

            IsRunning = false;
        }

        public void Dispose() => Stop();

        private static void TryWritePortFile(int port)
        {
            string text = port.ToString();
            // Eski yol + ayarlar/eklenti klasörü (MuckDownloadManager)
            foreach (string folder in new[] { "MDM", "MuckDownloadManager" })
            {
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        folder);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "capture_port.txt"), text, Encoding.ASCII);
                }
                catch { /* ignore */ }
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Accept error: {ex.Message}");
                    client?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using NetworkStream stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync();
                    if (string.IsNullOrWhiteSpace(requestLine))
                    {
                        await WriteResponseAsync(stream, RemoteApiResult.Text(400, "Bad Request"));
                        return;
                    }

                    string method = requestLine.Split(' ')[0].ToUpperInvariant();
                    string path = "/";
                    var parts = requestLine.Split(' ');
                    if (parts.Length > 1)
                        path = parts[1];
                    int contentLength = 0;
                    string auth = "";

                    while (true)
                    {
                        string? header = await reader.ReadLineAsync();
                        if (header == null || header.Length == 0) break;

                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            _ = int.TryParse(header.Substring("Content-Length:".Length).Trim(), out contentLength);
                        if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                            auth = header.Substring("Authorization:".Length).Trim();
                    }

                    if (contentLength > 512_000)
                    {
                        await WriteResponseAsync(stream, RemoteApiResult.Text(413, "Payload too large"));
                        return;
                    }

                    if (method == "OPTIONS")
                    {
                        await WriteResponseAsync(stream, RemoteApiResult.Text(200, "OK"));
                        return;
                    }

                    bool isLoopback = ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.Equals(IPAddress.Loopback) == true
                        || ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.Equals(IPAddress.IPv6Loopback) == true;

                    char[] bodyBuffer = new char[Math.Max(contentLength, 0)];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = await reader.ReadAsync(bodyBuffer, read, contentLength - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    string json = new string(bodyBuffer, 0, read);
                    var result = RemoteApiRouter.Route(
                        method, path, json, isLoopback, auth, _lan, _token, _onDownloadRequested, _jobs);
                    await WriteResponseAsync(stream, result);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Client handle error: {ex.Message}");
                }
            }
        }

        private static async Task WriteResponseAsync(NetworkStream stream, RemoteApiResult result)
        {
            int statusCode = result.Status;
            string reason = statusCode switch
            {
                200 => "OK",
                202 => "Accepted",
                401 => "Unauthorized",
                404 => "Not Found",
                405 => "Method Not Allowed",
                413 => "Payload Too Large",
                _ => "Error"
            };

            byte[] bodyBytes = Encoding.UTF8.GetBytes(result.Body ?? "");
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Accept, Authorization\r\n");
            sb.Append($"Content-Type: {result.ContentType}\r\n");
            sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(headerBytes);
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }
    }
}
