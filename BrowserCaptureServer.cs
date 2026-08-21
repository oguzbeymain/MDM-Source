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
    /// Tarayici eklentisi icin 127.0.0.1:6800 uzerinde hafif HTTP sunucu.
    /// HttpListener/URL ACL gerektirmez — arkadas makinelerinde de calisir.
    /// </summary>
    public sealed class BrowserCaptureServer : IDisposable
    {
        public const int Port = 6800;

        private readonly Action<string, string> _onDownloadRequested;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning { get; private set; }
        public string? LastError { get; private set; }

        public BrowserCaptureServer(Action<string, string> onDownloadRequested)
        {
            _onDownloadRequested = onDownloadRequested;
        }

        public void Start()
        {
            if (IsRunning) return;

            try
            {
                _cts = new CancellationTokenSource();
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                IsRunning = true;
                LastError = null;
                _loopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                Debug.WriteLine($"BrowserCaptureServer listening on 127.0.0.1:{Port}");
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = ex.Message;
                Debug.WriteLine($"BrowserCaptureServer start failed: {ex.Message}");
                throw;
            }
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
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;

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
                        {
                            _ = int.TryParse(header.Substring("Content-Length:".Length).Trim(), out contentLength);
                        }
                    }

                    if (method == "OPTIONS")
                    {
                        await WriteResponseAsync(stream, 200, "OK");
                        return;
                    }

                    if (method == "GET")
                    {
                        await WriteResponseAsync(stream, 200, "MDM capture ready");
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
