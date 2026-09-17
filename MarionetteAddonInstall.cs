using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MDM
{
    /// <summary>
    /// Firefox Marionette ile imzasız eklenti yükler. Normal (release) Firefox
    /// XPI dosyasını «doğrulanmamış» diye reddeder; geçici yükleme «Ekle» diyaloğunu açar.
    /// </summary>
    internal static class MarionetteAddonInstall
    {
        private static readonly int[] Ports = { 28280, 28281, 28282 };

        public static bool TryLaunchAndInstall(string firefoxExe, string addonPath, bool temporary)
        {
            if (string.IsNullOrWhiteSpace(firefoxExe) || !File.Exists(firefoxExe))
                return false;
            if (!IsAddonPath(addonPath))
                return false;

            string fullAddon = Path.GetFullPath(addonPath);

            foreach (int port in Ports)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = firefoxExe,
                        Arguments = $"-marionette --marionette-port {port}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(firefoxExe) ?? ""
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Marionette launch: {ex.Message}");
                    return false;
                }

                if (!WaitForPort(port, 20000))
                    continue;

                if (TryInstall(port, fullAddon, temporary))
                    return true;
            }

            return false;
        }

        public static bool TryInstall(int port, string addonPath, bool temporary)
        {
            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = 12000;
                client.SendTimeout = 5000;
                client.Connect("127.0.0.1", port);
                using NetworkStream stream = client.GetStream();

                string hello = ReadPacket(stream, 8000);
                if (string.IsNullOrEmpty(hello))
                    return false;

                if (!TryNewSession(stream))
                    return false;

                string pathJson = JsonSerializer.Serialize(addonPath);
                string tmp = temporary ? "true" : "false";
                SendPacket(stream, $"[0,2,\"Addon:Install\",{{\"path\":{pathJson},\"temporary\":{tmp}}}]");
                string result = ReadPacket(stream, 15000);
                // Zaman aşımı: «Ekle» diyaloğu açık kalmış olabilir
                if (string.IsNullOrEmpty(result))
                    return true;
                if (!LooksLikeError(result))
                    return true;

                SendPacket(stream, $"[0,3,\"Addon:Install\",{{\"path\":{pathJson},\"temporaryAddon\":{tmp}}}]");
                result = ReadPacket(stream, 12000);
                if (string.IsNullOrEmpty(result))
                    return true;
                return !LooksLikeError(result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Marionette install: {ex.Message}");
                return false;
            }
        }

        private static bool IsAddonPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (File.Exists(path)) return true;
            return Directory.Exists(path)
                   && File.Exists(Path.Combine(path, "manifest.json"));
        }

        private static bool TryNewSession(NetworkStream stream)
        {
            string[] payloads =
            {
                "[0,1,\"WebDriver:NewSession\",{\"capabilities\":{}}]",
                "[0,1,\"WebDriver:NewSession\",{}]",
                "[0,1,\"WebDriver:NewSession\",{\"capabilities\":{\"alwaysMatch\":{}}}]",
            };
            foreach (string payload in payloads)
            {
                SendPacket(stream, payload);
                string session = ReadPacket(stream, 10000);
                if (!string.IsNullOrEmpty(session) && !LooksLikeError(session))
                    return true;
            }
            return false;
        }

        private static bool LooksLikeError(string packet)
        {
            try
            {
                using var doc = JsonDocument.Parse(packet);
                JsonElement el = doc.RootElement;
                if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 3)
                    el = el[2];
                if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("error", out _))
                    return true;
                return false;
            }
            catch
            {
                return packet.Contains("\"error\"", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool WaitForPort(int port, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using var c = new TcpClient();
                    c.ReceiveTimeout = 500;
                    c.Connect("127.0.0.1", port);
                    if (c.Connected) return true;
                }
                catch { /* not up yet */ }
                Thread.Sleep(250);
            }
            return false;
        }

        private static void SendPacket(NetworkStream stream, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            byte[] prefix = Encoding.ASCII.GetBytes(payload.Length + ":");
            stream.Write(prefix, 0, prefix.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        private static string ReadPacket(NetworkStream stream, int timeoutMs)
        {
            stream.ReadTimeout = timeoutMs;
            var lenBuf = new StringBuilder();
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return "";
                if (b == ':') break;
                lenBuf.Append((char)b);
                if (lenBuf.Length > 16) return "";
            }
            if (!int.TryParse(lenBuf.ToString(), out int n) || n < 0 || n > 4_000_000)
                return "";
            byte[] buf = new byte[n];
            int read = 0;
            while (read < n)
            {
                int k = stream.Read(buf, read, n - read);
                if (k <= 0) break;
                read += k;
            }
            return Encoding.UTF8.GetString(buf, 0, read);
        }
    }
}
