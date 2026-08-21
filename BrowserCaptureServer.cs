using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

        private readonly Action<string, string> _onDownloadRequested;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning { get; private set; }
        public int ActivePort { get; private set; }
        public string? LastError { get; private set; }

        public BrowserCaptureServer(Action<string, string> onDownloadRequested)
        {
            _onDownloadRequested = onDownloadRequested;
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
                    var listener = new TcpListener(IPAddress.Loopback, port);
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
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MDM");
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "capture_port.txt"),
                    port.ToString(),
                    Encoding.ASCII);
            }
            catch { /* ignore */ }
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
                        await WriteResponseAsync(stream, 400, "Bad Request");
                        return;
                    }

                    string method = requestLine.Split(' ')[0].ToUpperInvariant();
                    int contentLength = 0;

                    while (true)
                    {
                        string? header = await reader.ReadLineAsync();
                        if (header == null || header.Length == 0) break;

                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            _ = int.TryParse(header.Substring("Content-Length:".Length).Trim(), out contentLength);
                    }

                    if (method == "OPTIONS")
                    {
                        await WriteResponseAsync(stream, 200, "OK");
                        return;
                    }

                    if (method == "GET")
                    {
                        await WriteResponseAsync(stream, 200, $"MDM capture ready on {ActivePort}");
                        return;
                    }

                    if (method != "POST")
                    {
                        await WriteResponseAsync(stream, 405, "Method Not Allowed");
                        return;
                    }

                    char[] bodyBuffer = new char[Math.Max(contentLength, 0)];
                    int read = 0;
                    while (read < contentLength)
                    {
                        int n = await reader.ReadAsync(bodyBuffer, read, contentLength - read);
                        if (n <= 0) break;
                        read += n;
                    }

                    string json = new string(bodyBuffer, 0, read);
                    string url = "";
                    string filename = "";

                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        using JsonDocument doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("url", out JsonElement urlEl))
                            url = urlEl.GetString() ?? "";
                        if (doc.RootElement.TryGetProperty("filename", out JsonElement nameEl))
                            filename = nameEl.GetString() ?? "";
                    }

                    if (!string.IsNullOrWhiteSpace(url))
                        _onDownloadRequested(url, filename);

                    await WriteResponseAsync(stream, 200, "OK");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Client handle error: {ex.Message}");
                }
            }
        }

        private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string body)
        {
            string reason = statusCode switch
            {
                200 => "OK",
                400 => "Bad Request",
                405 => "Method Not Allowed",
                _ => "Error"
            };

            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            var sb = new StringBuilder();
            sb.Append($"HTTP/1.1 {statusCode} {reason}\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: POST, GET, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Accept\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
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
