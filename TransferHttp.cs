using System.Net;
using System.Net.Http;

namespace MDM
{
    /// <summary>
    /// Paylaşılan HttpClient ayarı. HTTPS'te HTTP/3 dene (RequestVersionOrLower
    /// ile 2/1.1'e düşer). Açık HTTP'de 2/1.1 kullanılır.
    /// </summary>
    public static class TransferHttp
    {
        public const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        public static HttpClient CreateClient(CookieContainer? cookies = null, bool preferHttp3 = false)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = cookies != null,
                CookieContainer = cookies ?? new CookieContainer(),
                EnableMultipleHttp2Connections = true,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                // Parça istekleri aynı istemciyi paylaşır; bağlantılar indirme boyunca ayakta kalsın
                PooledConnectionLifetime = TimeSpan.FromMinutes(30),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 64,
                // 64 KB'lik varsayılan akış penceresi yüksek gecikmede hızı kesiyor
                InitialHttp2StreamWindowSize = 16 * 1024 * 1024,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
                ResponseDrainTimeout = TimeSpan.FromSeconds(2)
            };

            var client = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                DefaultRequestVersion = preferHttp3 ? HttpVersion.Version30 : HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            return client;
        }
    }
}
