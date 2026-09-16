using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MDM
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
        private readonly Action<ExtCaptureRequest>? _onExtCapture;
        private readonly Action<ScanRequest>? _onExtScan;
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
            IRemoteJobHost? jobs = null,
            Action<ExtCaptureRequest>? onExtCapture = null,
            Action<ScanRequest>? onExtScan = null)
        {
            _onDownloadRequested = onDownloadRequested;
            _onExtCapture = onExtCapture;
            _onExtScan = onExtScan;
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
                    // StreamReader KULLANMA — Content-Length bayt cinsinden; UTF-8 başlık/gövde
                    // Türkçe title vb. ile char-okuma kilitlenmesine yol açıyordu.
                    var headerBuf = new MemoryStream();
                    var window = new byte[1];
                    int matched = 0;
                    // header sonu: \r\n\r\n
                    while (matched < 4)
                    {
                        int n = await stream.ReadAsync(window.AsMemory(0, 1));
                        if (n <= 0) return;
                        headerBuf.WriteByte(window[0]);
                        byte b = window[0];
                        if (matched == 0 && b == (byte)'\r') matched = 1;
                        else if (matched == 1 && b == (byte)'\n') matched = 2;
                        else if (matched == 2 && b == (byte)'\r') matched = 3;
                        else if (matched == 3 && b == (byte)'\n') matched = 4;
                        else if (b == (byte)'\r') matched = 1;
                        else matched = 0;
                        if (headerBuf.Length > 64_000) return;
                    }

                    string headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
                    string[] headerLines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (headerLines.Length == 0 || string.IsNullOrWhiteSpace(headerLines[0]))
                    {
                        await WriteResponseAsync(stream, RemoteApiResult.Text(400, "Bad Request"));
                        return;
                    }

                    string requestLine = headerLines[0];
                    string method = requestLine.Split(' ')[0].ToUpperInvariant();
                    string path = "/";
                    var parts = requestLine.Split(' ');
                    if (parts.Length > 1)
                        path = parts[1];

                    int contentLength = 0;
                    string auth = "";
                    bool expectContinue = false;
                    bool chunked = false;

                    for (int i = 1; i < headerLines.Length; i++)
                    {
                        string header = headerLines[i];
                        if (string.IsNullOrEmpty(header)) continue;
                        if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            _ = int.TryParse(header.AsSpan("Content-Length:".Length).Trim(), out contentLength);
                        else if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                            auth = header["Authorization:".Length..].Trim();
                        else if (header.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)
                                 && header.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
                            expectContinue = true;
                        else if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)
                                 && header.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                            chunked = true;
                    }

                    if (expectContinue)
                    {
                        byte[] cont = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                        await stream.WriteAsync(cont);
                        await stream.FlushAsync();
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
                        || ((IPEndPoint?)client.Client.RemoteEndPoint)?.Address.Equals(IPAddress.IPv6Loopback) == true
                        || IPAddress.IsLoopback(((IPEndPoint?)client.Client.RemoteEndPoint)?.Address ?? IPAddress.None);

                    byte[] bodyBytes;
                    if (chunked)
                        bodyBytes = await ReadChunkedBodyAsync(stream);
                    else
                        bodyBytes = await ReadExactAsync(stream, Math.Max(contentLength, 0));

                    string json = bodyBytes.Length == 0 ? "" : Encoding.UTF8.GetString(bodyBytes);
                    var result = RemoteApiRouter.Route(
                        method, path, json, isLoopback, auth, _lan, _token, _onDownloadRequested, _jobs,
                        _onExtCapture, _onExtScan);
                    await WriteResponseAsync(stream, result);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Client handle error: {ex.Message}");
                }
            }
        }

        private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int length)
        {
            if (length <= 0) return Array.Empty<byte>();
            byte[] buf = new byte[length];
            int read = 0;
            while (read < length)
            {
                int n = await stream.ReadAsync(buf.AsMemory(read, length - read));
                if (n <= 0) break;
                read += n;
            }
            if (read == length) return buf;
            if (read == 0) return Array.Empty<byte>();
            var partial = new byte[read];
            Buffer.BlockCopy(buf, 0, partial, 0, read);
            return partial;
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                string sizeLine = await ReadLineAsciiAsync(stream);
                if (string.IsNullOrEmpty(sizeLine)) break;
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine[..semi];
                if (!int.TryParse(sizeLine.Trim(), System.Globalization.NumberStyles.HexNumber, null, out int size))
                    break;
                if (size == 0)
                {
                    // trailing headers
                    while (true)
                    {
                        string t = await ReadLineAsciiAsync(stream);
                        if (string.IsNullOrEmpty(t)) break;
                    }
                    break;
                }
                byte[] chunk = await ReadExactAsync(stream, size);
                await ms.WriteAsync(chunk);
                await ReadLineAsciiAsync(stream); // CRLF after chunk
                if (ms.Length > 512_000) break;
            }
            return ms.ToArray();
        }

        private static async Task<string> ReadLineAsciiAsync(NetworkStream stream)
        {
            var ms = new MemoryStream();
            var b = new byte[1];
            while (true)
            {
                int n = await stream.ReadAsync(b.AsMemory(0, 1));
                if (n <= 0) break;
                if (b[0] == (byte)'\n') break;
                if (b[0] != (byte)'\r')
                    ms.WriteByte(b[0]);
                if (ms.Length > 4096) break;
            }
            return Encoding.ASCII.GetString(ms.ToArray());
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
            sb.Append("Access-Control-Allow-Headers: Content-Type, Accept, Authorization, Access-Control-Request-Private-Network\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
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
