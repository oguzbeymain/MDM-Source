using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MDM.Tests;

/// <summary>
/// Minimal HTTP/1.1 Range sunucusu — HttpListener URL ACL gerektirmez.
/// </summary>
internal sealed class RangeHttpServer : IAsyncDisposable
{
    private readonly byte[] _payload;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/file.bin";
    public bool SendAcceptRanges { get; init; } = true;
    public int ThrottleBytesPerWrite { get; init; }
    public int ThrottleDelayMs { get; init; }
    public bool Drop { get; set; }
    /// <summary>Açıkken aynı soket üzerinde birden fazla istek yanıtlanır.</summary>
    public bool KeepAlive { get; init; }
    public int RequestCount => _requestCount;
    /// <summary>Kabul edilen TCP bağlantısı sayısı — bağlantı yeniden kullanımını ölçmek için.</summary>
    public int ConnectionCount => _connectionCount;
    private int _requestCount;
    private int _connectionCount;

    public RangeHttpServer(byte[] payload, int throttleBytesPerWrite = 0, int throttleDelayMs = 0)
    {
        _payload = payload;
        ThrottleBytesPerWrite = throttleBytesPerWrite;
        ThrottleDelayMs = throttleDelayMs;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
                _ = Task.Run(() => HandleClientAsync(client, token), token);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                client?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            Interlocked.Increment(ref _connectionCount);
            do
            {
                if (!await ServeOneAsync(stream, token))
                    return;
            }
            while (KeepAlive && !token.IsCancellationRequested);
        }
    }

    /// <summary>Tek isteği yanıtlar; bağlantı canlı kalabiliyorsa true döner.</summary>
    private async Task<bool> ServeOneAsync(NetworkStream stream, CancellationToken token)
    {
        {
            Interlocked.Increment(ref _requestCount);
            if (Drop)
                return false;

            string header = await ReadHeadersAsync(stream, token);
            if (string.IsNullOrEmpty(header))
                return false;

            long start = 0;
            long end = _payload.Length - 1;
            bool isRange = false;

            foreach (string line in header.Split("\r\n"))
            {
                if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"bytes=(\d+)-(\d*)");
                if (!m.Success)
                    continue;
                start = long.Parse(m.Groups[1].Value);
                end = m.Groups[2].Value.Length > 0 ? long.Parse(m.Groups[2].Value) : _payload.Length - 1;
                end = Math.Min(end, _payload.Length - 1);
                isRange = true;
            }

            if (start < 0 || start >= _payload.Length || end < start)
            {
                byte[] err = Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(err, token);
                return false;
            }

            int length = (int)(end - start + 1);
            var sb = new StringBuilder();
            if (isRange && SendAcceptRanges)
            {
                sb.Append("HTTP/1.1 206 Partial Content\r\n");
                sb.Append($"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n");
            }
            else
            {
                sb.Append("HTTP/1.1 200 OK\r\n");
                start = 0;
                end = _payload.Length - 1;
                length = _payload.Length;
            }

            if (SendAcceptRanges)
                sb.Append("Accept-Ranges: bytes\r\n");
            sb.Append($"Content-Length: {length}\r\n");
            sb.Append("Content-Type: application/octet-stream\r\n");
            sb.Append(KeepAlive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            await stream.WriteAsync(head, token);

            int offset = (int)start;
            int remaining = length;
            int chunk = ThrottleBytesPerWrite > 0 ? ThrottleBytesPerWrite : 64 * 1024;
            while (remaining > 0 && !token.IsCancellationRequested)
            {
                if (Drop)
                    return false;
                int n = Math.Min(chunk, remaining);
                await stream.WriteAsync(_payload.AsMemory(offset, n), token);
                offset += n;
                remaining -= n;
                if (ThrottleDelayMs > 0 && remaining > 0)
                    await Task.Delay(ThrottleDelayMs, token);
            }

            return true;
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        var ms = new MemoryStream();
        byte[] buf = new byte[1];
        while (ms.Length < 16_384)
        {
            int n = await stream.ReadAsync(buf, token);
            if (n <= 0)
                break;
            ms.WriteByte(buf[0]);
            if (ms.Length >= 4)
            {
                byte[] a = ms.ToArray();
                int i = a.Length;
                if (a[i - 4] == '\r' && a[i - 3] == '\n' && a[i - 2] == '\r' && a[i - 1] == '\n')
                    return Encoding.ASCII.GetString(a);
            }
        }
        return Encoding.ASCII.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
