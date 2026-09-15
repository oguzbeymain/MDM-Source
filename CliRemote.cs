using System.IO;
using System.Net.Http;
using System.Text;

namespace MDM
{
    public static class CliRemote
    {
        public static bool TryReadPort(out int port)
        {
            port = 0;
            foreach (string folder in new[] { "MuckDownloadManager", "MDM" })
            {
                try
                {
                    string path = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        folder,
                        "capture_port.txt");
                    if (!File.Exists(path))
                        continue;
                    if (int.TryParse(File.ReadAllText(path).Trim(), out port) && port > 0)
                        return true;
                }
                catch { /* ignore */ }
            }
            return false;
        }

        public static async Task<(int Status, string Body)> CallAsync(string method, string path, string? jsonBody = null)
        {
            if (!TryReadPort(out int port))
                return (0, "MDM capture portu yok.");

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
            if (!string.IsNullOrEmpty(jsonBody))
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var resp = await client.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, body);
        }
    }
}
