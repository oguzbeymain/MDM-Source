using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownloadMuck
{
    internal sealed record GoFileMetadata(
        string DownloadUrl,
        string FileName,
        long Size,
        string AccountToken,
        string WebsiteToken,
        string UserAgent,
        string Language,
        string ContentId);

    internal static class GoFileResolver
    {
        private const string WebsiteLanguage = "en-US";
        private const string WebsiteSalt = "12af056dacea0b";
        private const string WebsiteUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private static readonly Regex ContentIdPattern = new(
            @"gofile\.io/(?:d|download/web)/([^/?#\s]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryGetContentId(string url, out string contentId)
        {
            contentId = "";
            var match = ContentIdPattern.Match(url ?? "");
            if (!match.Success)
                return false;

            contentId = match.Groups[1].Value;
            return !string.IsNullOrWhiteSpace(contentId);
        }

        public static async Task<GoFileMetadata?> TryResolveAsync(
            string url, CancellationToken token = default)
        {
            if (!TryGetContentId(url, out string contentId))
                return null;

            using HttpClient client = TransferHttp.CreateClient(preferHttp3: false);
            client.DefaultRequestHeaders.Remove("User-Agent");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", WebsiteUserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://gofile.io");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://gofile.io/");

            using var accountRequest = new HttpRequestMessage(
                HttpMethod.Post, "https://api.gofile.io/accounts");
            using HttpResponseMessage accountResponse =
                await client.SendAsync(accountRequest, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            accountResponse.EnsureSuccessStatusCode();

            using JsonDocument accountJson =
                JsonDocument.Parse(await accountResponse.Content.ReadAsStringAsync(token)
                    .ConfigureAwait(false));
            if (!TryGetString(accountJson.RootElement, "data", "token", out string? accountToken) ||
                string.IsNullOrWhiteSpace(accountToken))
                throw new HttpRequestException("GoFile misafir oturumu oluşturulamadı.");

            string websiteToken = CreateWebsiteToken(accountToken, DateTimeOffset.UtcNow);
            string query = "page=1&pageSize=100&sortField=name&sortDirection=1";
            using var metadataRequest = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.gofile.io/contents/{Uri.EscapeDataString(contentId)}?{query}");
            AddWebsiteHeaders(metadataRequest, accountToken, websiteToken);

            using HttpResponseMessage metadataResponse =
                await client.SendAsync(metadataRequest, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);
            metadataResponse.EnsureSuccessStatusCode();

            using JsonDocument metadataJson =
                JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync(token)
                    .ConfigureAwait(false));
            JsonElement root = metadataJson.RootElement;
            if (!TryGetString(root, "status", out string? status) ||
                !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("GoFile dosya bilgilerini doğrulayamadı.");

            if (!root.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetString(data, "link", out string? downloadUrl) ||
                string.IsNullOrWhiteSpace(downloadUrl))
                throw new HttpRequestException("GoFile geçerli bir indirme bağlantısı döndürmedi.");

            TryGetString(data, "name", out string? name);
            long size = data.TryGetProperty("size", out JsonElement sizeElement) &&
                        sizeElement.TryGetInt64(out long parsedSize)
                ? parsedSize
                : 0;

            return new GoFileMetadata(
                downloadUrl,
                string.IsNullOrWhiteSpace(name) ? "" : name,
                size,
                accountToken,
                websiteToken,
                WebsiteUserAgent,
                WebsiteLanguage,
                contentId);
        }

        public static void AddWebsiteHeaders(
            HttpRequestMessage request, string accountToken, string websiteToken,
            string? contentId = null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accountToken}");
            request.Headers.TryAddWithoutValidation("X-Website-Token", websiteToken);
            request.Headers.TryAddWithoutValidation("X-BL", WebsiteLanguage);
            request.Headers.TryAddWithoutValidation("Origin", "https://gofile.io");
            request.Headers.TryAddWithoutValidation(
                "Referer",
                string.IsNullOrWhiteSpace(contentId)
                    ? "https://gofile.io/"
                    : $"https://gofile.io/d/{contentId}");
        }

        private static string CreateWebsiteToken(
            string accountToken, DateTimeOffset now)
        {
            long window = now.ToUnixTimeSeconds() / 14_400;
            string raw = string.Create(
                CultureInfo.InvariantCulture,
                $"{WebsiteUserAgent}::{WebsiteLanguage}::{accountToken}::{window}::{WebsiteSalt}");
            byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(digest).ToLowerInvariant();
        }

        private static bool TryGetString(
            JsonElement element, string property, out string? value)
        {
            value = null;
            return element.TryGetProperty(property, out JsonElement propertyElement) &&
                   propertyElement.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(value = propertyElement.GetString());
        }

        private static bool TryGetString(
            JsonElement element, string parent, string property, out string? value)
        {
            value = null;
            return element.TryGetProperty(parent, out JsonElement parentElement) &&
                   parentElement.ValueKind == JsonValueKind.Object &&
                   TryGetString(parentElement, property, out value);
        }
    }
}
