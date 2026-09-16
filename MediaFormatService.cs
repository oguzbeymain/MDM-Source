using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MDM
{
    public static class MediaFormatService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public static FormatsResponse GetFormats(FormatsRequest req, TimeSpan? timeout = null)
        {
            timeout ??= TimeSpan.FromSeconds(8);
            var resp = new FormatsResponse { Ok = true };
            var collected = new List<FormatOptionDto>();
            var deadline = DateTime.UtcNow + timeout.Value;
            bool isYt = YtDlpHelper.IsYouTubeUrl(req.PageUrl);

            try
            {
                // YouTube / IDM modeli: sniff yok — doğrudan yt-dlp (masaüstü)
                if (isYt && !string.IsNullOrWhiteSpace(req.PageUrl))
                {
                    YtDlpHelper.EnsureAvailableAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
                    // İlk açılışta yt-dlp soğuk olabilir; sabit uzun timeout (eklenti 28s bekler)
                    var yt = TryYtDlpFormats(req, TimeSpan.FromSeconds(28));
                    if (yt != null && yt.Formats.Count > 0)
                    {
                        yt.YtDlpSuggested = false;
                        return SortAndFinish(yt);
                    }
                    resp.Ok = false;
                    if (!YtDlpHelper.IsAvailable())
                        resp.Error = "yt-dlp indirilemedi — internet bağlantısını kontrol edin";
                    else if (!string.IsNullOrWhiteSpace(YtDlpHelper.LastError))
                        resp.Error = "YouTube kalite listesi alınamadı: " + YtDlpHelper.LastError;
                    else
                        resp.Error = "YouTube kalite listesi alınamadı";
                    resp.YtDlpSuggested = !YtDlpHelper.IsAvailable();
                    return resp;
                }

                // IDM modeli: eklentiden gelen playlist gövdesini önce parse et (CORS yok)
                if (!string.IsNullOrWhiteSpace(req.PlaylistBody))
                {
                    string bodyUrl = !string.IsNullOrWhiteSpace(req.MediaUrl) ? req.MediaUrl : req.PageUrl;
                    if (req.PlaylistBody.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsProtectedHls(req.PlaylistBody))
                        {
                            resp.Protected = true;
                            resp.Ok = false;
                            resp.Error = "Bu video korunuyor";
                            return resp;
                        }
                        collected.AddRange(ParseHls(req.PlaylistBody, bodyUrl));
                        if (collected.Count(f => f.Height > 0) > 0)
                        {
                            resp.Title = string.IsNullOrWhiteSpace(req.Title) ? "HLS video" : req.Title;
                            resp.Formats = collected;
                            if (req.VideoHeight > 0)
                            {
                                resp.Formats.Insert(0, new FormatOptionDto
                                {
                                    Id = "playing",
                                    Label = $"Oynayan · {req.VideoHeight}p",
                                    Height = req.VideoHeight,
                                    Width = req.VideoWidth,
                                    Type = "progressive",
                                    Kind = "progressive"
                                });
                            }
                            StampFormatHeaders(resp.Formats, req);
                            return SortAndFinish(resp);
                        }
                    }
                    if (req.PlaylistBody.Contains("<MPD", StringComparison.OrdinalIgnoreCase))
                    {
                        collected.AddRange(ParseDash(req.PlaylistBody, bodyUrl));
                        if (collected.Count > 0)
                        {
                            resp.Title = string.IsNullOrWhiteSpace(req.Title) ? "DASH video" : req.Title;
                            resp.Formats = collected;
                            return SortAndFinish(resp);
                        }
                    }
                }

                var urls = new List<string>();
                void Push(string? u)
                {
                    if (string.IsNullOrWhiteSpace(u)) return;
                    if (u.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) return;
                    if (IsJunkMediaUrl(u)) return;
                    if (!urls.Contains(u, StringComparer.OrdinalIgnoreCase))
                        urls.Add(u);
                }
                foreach (string c in req.Candidates) Push(c);
                Push(req.MediaUrl);

                // Embed / player sayfası → HTML içinden HLS adayları
                foreach (string seed in new[] { req.MediaUrl, req.PageUrl })
                {
                    if (string.IsNullOrWhiteSpace(seed)) continue;
                    if (!LooksLikeEmbedPlayerUrl(seed) && !LooksLikeHtmlPageUrl(seed)) continue;
                    if (LooksLikeHlsUrl(seed)) continue;
                    string? html = TryFetchText(seed, req, Remaining(deadline));
                    if (string.IsNullOrWhiteSpace(html)) continue;
                    foreach (string hlsUrl in ExtractHlsUrlsFromHtml(html))
                        Push(hlsUrl);
                }

                foreach (string url in urls)
                {
                    if (DateTime.UtcNow > deadline) break;
                    if (IsJunkMediaUrl(url)) continue;
                    string kind = ClassifyUrl(url);
                    if (kind is "fragment" or "other") continue;

                    if (kind == "hls")
                    {
                        string masterUrl = url;
                        string? hls = TryFetchText(url, req, Remaining(deadline));
                        if (string.IsNullOrWhiteSpace(hls) || !hls.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (string cand in GuessHlsMasterUrls(url))
                            {
                                if (DateTime.UtcNow > deadline) break;
                                string? alt = TryFetchText(cand, req, Remaining(deadline));
                                if (!string.IsNullOrWhiteSpace(alt)
                                    && alt.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                                {
                                    hls = alt;
                                    masterUrl = cand;
                                    break;
                                }
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(hls)
                            && hls.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                        {
                            if (IsProtectedHls(hls))
                            {
                                resp.Protected = true;
                                continue;
                            }
                            collected.AddRange(ParseHls(hls, masterUrl));
                        }
                    }
                    else if (kind == "dash")
                    {
                        var mpd = TryFetchText(url, req, Remaining(deadline));
                        if (!string.IsNullOrWhiteSpace(mpd))
                            collected.AddRange(ParseDash(mpd, url));
                    }
                    else if (kind is "progressive" or "audio")
                    {
                        int h = req.VideoHeight > 0 ? req.VideoHeight : GuessHeightFromUrl(url);
                        collected.Add(new FormatOptionDto
                        {
                            Id = kind == "audio" ? "audio-1" : $"prog-{h}",
                            Label = kind == "audio" ? "Sadece ses" : (h > 0 ? FormatHeightLabel(h, 0) : GuessLabelFromUrl(url)),
                            Height = kind == "audio" ? 0 : h,
                            Width = req.VideoWidth,
                            Url = url,
                            Type = kind,
                            Kind = kind,
                            Headers = BuildHeaders(req)
                        });
                    }
                }

                // yt-dlp: blob / az kalite
                bool needYt = collected.Count(f => f.Height > 0) <= 1
                              || urls.Count == 0
                              || (!string.IsNullOrWhiteSpace(req.MediaUrl)
                                  && req.MediaUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(req.PageUrl) && needYt && DateTime.UtcNow < deadline)
                {
                    YtDlpHelper.EnsureAvailableAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                    var yt = TryYtDlpFormats(req, Remaining(deadline));
                    if (yt != null)
                    {
                        if (!string.IsNullOrWhiteSpace(yt.Title))
                            resp.Title = yt.Title;
                        if (!string.IsNullOrWhiteSpace(yt.Thumbnail))
                            resp.Thumbnail = yt.Thumbnail;
                        collected.AddRange(yt.Formats);
                    }
                    else if (!YtDlpHelper.IsAvailable())
                        resp.YtDlpSuggested = true;
                }

                if (req.VideoHeight > 0 && !collected.Any(f => f.Height == req.VideoHeight && f.Id != "playing"))
                {
                    collected.Insert(0, new FormatOptionDto
                    {
                        Id = "playing",
                        Label = $"Oynayan · {req.VideoHeight}p",
                        Height = req.VideoHeight,
                        Width = req.VideoWidth,
                        Url = ClassifyUrl(req.MediaUrl) == "progressive" ? req.MediaUrl : "",
                        Type = "progressive",
                        Kind = "progressive",
                        Headers = BuildHeaders(req)
                    });
                }

                if (!string.IsNullOrWhiteSpace(req.Title))
                    resp.Title = req.Title;
                else if (string.IsNullOrWhiteSpace(resp.Title) && collected.Count > 0)
                    resp.Title = "Video";

                resp.Formats = collected;
                StampFormatHeaders(resp.Formats, req);
                if (resp.Protected && resp.Formats.Count(f => f.Id != "playing" && f.Height > 0) == 0)
                {
                    resp.Ok = false;
                    resp.Error = "Bu video korunuyor";
                    resp.Formats.Clear();
                    return resp;
                }

                if (resp.Formats.Count == 0)
                {
                    resp.Ok = false;
                    resp.Error = "Kalite listesi bulunamadı";
                    if (!YtDlpHelper.IsAvailable())
                        resp.YtDlpSuggested = true;
                }
            }
            catch (Exception ex)
            {
                resp.Ok = false;
                resp.Error = ex.Message;
            }

            return SortAndFinish(resp);
        }

        /// <summary>
        /// Görsel/doküman gibi doğrudan dosya yakalamaları. Sayfa YouTube veya video sayfası
        /// olsa bile bunlar video sanılmamalı (örn. kapak fotoğrafına sağ tık).
        /// </summary>
        public static bool IsDirectFileCapture(ExtCaptureRequest req)
        {
            string kind = (req.Kind ?? "").Trim().ToLowerInvariant();
            if (kind is "image" or "file") return true;
            if (kind is "hls" or "dash" or "yt-dlp") return false;

            return PageScanService.KindOf(PageScanService.ExtensionOfUrl(req.Url))
                is ScanKind.Image or ScanKind.Document or ScanKind.Archive or ScanKind.App;
        }

        public static ExtCaptureRequest ResolveCapture(ExtCaptureRequest req)
        {
            string kind = (req.Kind ?? "").Trim().ToLowerInvariant();
            string formatId = req.FormatId ?? "";

            // Doğrudan dosya: video/kalite çözümlemesine hiç girmez
            if (IsDirectFileCapture(req))
            {
                req.Kind = kind is "image" or "file" ? kind : "progressive";
                req.FormatId = "";
                return req;
            }

            // YouTube: asla CDN — watch URL + formatId
            bool youtube = YtDlpHelper.IsYouTubeUrl(req.PageUrl) || YtDlpHelper.IsYouTubeUrl(req.Url);
            if (youtube)
            {
                string page = !string.IsNullOrWhiteSpace(req.PageUrl) ? req.PageUrl! : (req.Url ?? "");
                if (YtDlpHelper.IsYouTubeUrl(page))
                    page = YtDlpHelper.NormalizeYouTubeWatchUrl(page) ?? page;
                string fid = string.IsNullOrWhiteSpace(formatId) || formatId is "best" or "playing"
                    ? "bv*+ba/b"
                    : formatId;
                // Yanlışlıkla YouTube-olmayan format seçicileri (bv*) film URL'sine yapışmasın — sadece YT
                req.PageUrl = page;
                req.Url = page;
                req.Kind = "yt-dlp";
                req.FormatId = fid;
                return req;
            }

            // Embed / player HTML → sayfa içinden HLS URL çek (master.txt / m3u8)
            TryResolveEmbedOrPageToHls(req);

            kind = (req.Kind ?? "").Trim().ToLowerInvariant();
            formatId = req.FormatId ?? "";

            // HLS: seçilen satır zaten variant URI ise dokunma; master ise ve "best" ise PickBest
            if ((kind == "hls" || LooksLikeHlsUrl(req.Url)) && !string.IsNullOrWhiteSpace(req.Url)
                && LooksLikeHlsUrl(req.Url))
            {
                bool looksLikeMaster = formatId is "" or "best" or "hls" or "hls-media"
                    || formatId.StartsWith("hls-media", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(req.Url, @"/(master|index|playlist|manifest)\.(m3u8?|txt)", RegexOptions.IgnoreCase);
                // Spesifik variant id (h-1080-...) — URL'yi koru
                bool specificVariant = formatId.StartsWith("h-", StringComparison.OrdinalIgnoreCase)
                    || formatId.StartsWith("hls-", StringComparison.OrdinalIgnoreCase)
                       && formatId.Contains('-') && !formatId.StartsWith("hls-media");

                if (!specificVariant && (looksLikeMaster || string.Equals(formatId, "best", StringComparison.OrdinalIgnoreCase)))
                {
                    string? playlist = TryFetchText(req.Url, ToFormats(req), TimeSpan.FromSeconds(6));
                    if (string.IsNullOrWhiteSpace(playlist) || !playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string cand in GuessHlsMasterUrls(req.Url))
                        {
                            playlist = TryFetchText(cand, ToFormats(req), TimeSpan.FromSeconds(4));
                            if (!string.IsNullOrWhiteSpace(playlist)
                                && playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                            {
                                req.Url = cand;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(playlist) && playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                    {
                        string? best = HlsPlaylist.PickBestVariant(playlist, req.Url);
                        if (!string.IsNullOrWhiteSpace(best))
                            req.Url = best;
                    }
                    req.Kind = "hls";
                }
            }

            // dash + yt-dlp formatId sayısal → yt-dlp
            if (kind == "dash" && !string.IsNullOrWhiteSpace(formatId) && YtDlpHelper.IsAvailable()
                && !string.IsNullOrWhiteSpace(req.PageUrl))
            {
                string? direct = YtDlpHelper.ResolveDownloadUrl(
                    req.PageUrl, formatId, req.Cookies, req.Headers, TimeSpan.FromSeconds(12));
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    req.Url = direct;
                    req.Kind = "progressive";
                }
            }

            return req;
        }

        /// <summary>
        /// Embed player veya HTML sayfa URL'siyse gövdeden HLS (master.txt/m3u8) çıkar.
        /// </summary>
        private static void TryResolveEmbedOrPageToHls(ExtCaptureRequest req)
        {
            string url = req.Url ?? "";
            string page = req.PageUrl ?? "";
            if (LooksLikeHlsUrl(url) && !IsJunkMediaUrl(url) && !LooksLikeHtmlPageUrl(url))
                return;

            string? scrapeTarget = null;
            if (LooksLikeEmbedPlayerUrl(url) || (LooksLikeHtmlPageUrl(url) && !LooksLikeHlsUrl(url)))
                scrapeTarget = url;
            else if (LooksLikeEmbedPlayerUrl(page) || LooksLikeHtmlPageUrl(page))
                scrapeTarget = page;
            else if (!LooksLikeHlsUrl(url) && !string.IsNullOrWhiteSpace(page)
                     && (page.Contains("hdfilm", StringComparison.OrdinalIgnoreCase)
                         || page.Contains("/dizi/", StringComparison.OrdinalIgnoreCase)
                         || page.Contains("/sezon/", StringComparison.OrdinalIgnoreCase)))
                scrapeTarget = !string.IsNullOrWhiteSpace(url) && url.Contains("/embed/", StringComparison.OrdinalIgnoreCase)
                    ? url : null;

            // Embed yoksa ama URL embed ise
            if (scrapeTarget == null && url.Contains("/embed/", StringComparison.OrdinalIgnoreCase))
                scrapeTarget = url;

            if (string.IsNullOrWhiteSpace(scrapeTarget)) return;

            // Referer: site sayfası tercihen
            if (string.IsNullOrWhiteSpace(req.Referrer) && !string.IsNullOrWhiteSpace(page)
                && !page.Equals(scrapeTarget, StringComparison.OrdinalIgnoreCase))
                req.Referrer = page;

            string? html = TryFetchText(scrapeTarget, ToFormats(req), TimeSpan.FromSeconds(8));
            if (string.IsNullOrWhiteSpace(html)) return;

            var found = ExtractHlsUrlsFromHtml(html);
            if (found.Count == 0) return;

            // Çalışan playlisti tercih et
            foreach (string cand in found)
            {
                string? body = TryFetchText(cand, ToFormats(req), TimeSpan.FromSeconds(5));
                if (!string.IsNullOrWhiteSpace(body) && body.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(req.PageUrl))
                        req.PageUrl = scrapeTarget;
                    req.Url = cand;
                    req.Kind = "hls";
                    if (string.IsNullOrWhiteSpace(req.FormatId) || req.FormatId is "best" or "playing")
                        req.FormatId = "best";
                    return;
                }
            }

            // Fetch başarısız (token/CDN) — yine de en iyi adayı ver; yt-dlp + Referer denesin
            req.Url = found[0];
            req.Kind = "hls";
            if (string.IsNullOrWhiteSpace(req.PageUrl))
                req.PageUrl = scrapeTarget;
            if (string.IsNullOrWhiteSpace(req.Referrer))
                req.Referrer = scrapeTarget;
            if (!req.Headers.ContainsKey("Referer"))
                req.Headers["Referer"] = scrapeTarget;
        }

        private static TimeSpan Remaining(DateTime deadline)
        {
            var left = deadline - DateTime.UtcNow;
            return left < TimeSpan.FromMilliseconds(500) ? TimeSpan.FromMilliseconds(500) : left;
        }

        private static FormatsRequest ToFormats(ExtCaptureRequest req) => new()
        {
            PageUrl = req.PageUrl,
            MediaUrl = req.Url,
            Cookies = req.Cookies,
            Referrer = req.Referrer,
            Headers = new Dictionary<string, string>(req.Headers, StringComparer.OrdinalIgnoreCase)
        };

        private static FormatsResponse SortAndFinish(FormatsResponse resp)
        {
            // playing'i gerçek height ile birleştir
            var playing = resp.Formats.FirstOrDefault(f => f.Id == "playing");
            if (playing != null && playing.Height > 0)
            {
                var twin = resp.Formats.FirstOrDefault(f =>
                    f.Id != "playing" && f.Height == playing.Height);
                if (twin != null)
                {
                    if (string.IsNullOrWhiteSpace(twin.Url) && !string.IsNullOrWhiteSpace(playing.Url))
                        twin.Url = playing.Url;
                    resp.Formats.Remove(playing);
                }
            }

            resp.Formats = DeduplicateLabels(
                resp.Formats
                    .Where(f => f.Height == 0 || f.Height >= 144 || f.Id is "playing" or "best" || f.Type == "audio" || f.Kind == "audio")
                    .ToList());

            if (resp.Formats.Count(f => f.Height > 0) > 1
                && !resp.Formats.Any(f => f.Id == "best"))
            {
                var best = resp.Formats
                    .Where(f => f.Height > 0)
                    .OrderByDescending(f => f.Height)
                    .ThenByDescending(f => f.Bandwidth)
                    .First();
                resp.Formats.Insert(0, new FormatOptionDto
                {
                    Id = "best",
                    Label = "En iyi kalite",
                    Height = best.Height,
                    Width = best.Width,
                    Fps = best.Fps,
                    Url = best.Url,
                    Type = string.IsNullOrWhiteSpace(best.Type) ? "yt-dlp" : best.Type,
                    Kind = string.Equals(best.Type, "yt-dlp", StringComparison.OrdinalIgnoreCase) ? "yt-dlp" : best.Kind,
                    FormatId = !string.IsNullOrWhiteSpace(best.FormatId) ? best.FormatId
                        : (string.Equals(best.Type, "yt-dlp", StringComparison.OrdinalIgnoreCase) ? "bv*+ba/b" : best.Id),
                    Headers = best.Headers
                });
            }

            // Label'a dosya boyutu
            foreach (var f in resp.Formats)
            {
                if (f.Filesize is > 512_000 && f.Label.IndexOf("MB", StringComparison.OrdinalIgnoreCase) < 0
                    && f.Label.IndexOf("GB", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    double mb = f.Filesize.Value / 1048576.0;
                    string size = mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{Math.Max(1, Math.Round(mb))} MB";
                    f.Label = $"{f.Label} · {size}";
                }
                if (string.IsNullOrWhiteSpace(f.Kind))
                    f.Kind = f.Type;
            }

            resp.Formats = resp.Formats
                .OrderByDescending(f => f.Id == "best")
                .ThenByDescending(f => f.Id == "playing")
                .ThenByDescending(f => f.Type != "audio" && f.Kind != "audio")
                .ThenByDescending(f => f.Height)
                .ThenByDescending(f => f.Fps)
                .ThenByDescending(f => f.Bandwidth)
                .Take(12)
                .ToList();
            return resp;
        }

        private static bool IsProtectedHls(string playlist)
            => Regex.IsMatch(playlist, @"METHOD=(SAMPLE-AES|com\.apple\.streamingkeydelivery)", RegexOptions.IgnoreCase);

        private static string ClassifyUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "other";
            if (IsJunkMediaUrl(url)) return "other";
            string lower = url.ToLowerInvariant();
            if (lower.StartsWith("blob:") || lower.StartsWith("mediasource:")) return "blob";
            if (LooksLikeHlsUrl(url)) return "hls";
            if (LooksLikeDashUrl(url)) return "dash";
            if (Regex.IsMatch(lower, @"\.(m4s|ts|m2ts)(\?|$)") || lower.Contains("videoplayback") || lower.Contains("itag="))
                return "fragment";
            if (Regex.IsMatch(lower, @"\.(mp3|m4a|aac|opus|flac|wav)(\?|$)", RegexOptions.IgnoreCase))
                return "audio";
            if (LooksLikeProgressiveFile(url))
                return "progressive";
            return "other";
        }

        /// <summary>
        /// .m3u8 uzantısı olmayan CDN yolları: /hls/rick... veya master.txt.
        /// Altyazı /txt/sublist_*.txt yolları HLS sayılmaz.
        /// </summary>
        public static bool LooksLikeHlsUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (IsJunkMediaUrl(url)) return false;
            string u = url.ToLowerInvariant();
            if (u.Contains(".m3u8") || u.Contains(".m3u?") || u.EndsWith(".m3u")
                || u.Contains("mpegurl") || u.Contains("x-mpegurl"))
                return true;
            if (Regex.IsMatch(u, @"/(master|index|playlist|manifest|hls)\.txt(\?|$)"))
                return true;
            if (u.Contains("/hls/") || u.Contains("/hls?") || u.Contains("/live/hls")
                || u.Contains("playlisttype=hls") || u.Contains("format=m3u8")
                || u.Contains("type=m3u8") || u.Contains("/playlist.m3u"))
                return true;
            return false;
        }

        /// <summary>Altyazı / poster / metadata — video playlist değil.</summary>
        public static bool IsJunkMediaUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return true;
            string u = url.ToLowerInvariant();
            if (u.Contains("/txt/sublist")) return true;
            if (Regex.IsMatch(u, @"/(sub|subs|subtitle|subtitles|caption|captions|thumb|thumbnail|poster)s?/"))
                return true;
            if (Regex.IsMatch(u, @"\.(vtt|srt|ass|ssa|dfxp|ttml)(\?|$)")) return true;
            // master.txt / index.txt = geçerli HLS; diğer .txt junk
            if (Regex.IsMatch(u, @"/(master|index|playlist|manifest|hls)\.(m3u8?|txt)(\?|$)"))
                return false;
            if (Regex.IsMatch(u, @"/hls/.+\.txt(\?|$)") && !u.Contains(".m3u8") && !u.Contains(".m3u"))
                return true;
            return false;
        }

        /// <summary>Player HTML / embed / site sayfası — progressive dosya değil.</summary>
        public static bool LooksLikeHtmlPageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Regex.IsMatch(url, @"\.(html?|php|aspx?)(\?|$)", RegexOptions.IgnoreCase)
                   || Regex.IsMatch(url, @"/player/\d+", RegexOptions.IgnoreCase)
                   || LooksLikeEmbedPlayerUrl(url);
        }

        /// <summary>hdfilmcehennemi / rapidrame tarzı embed player.</summary>
        public static bool LooksLikeEmbedPlayerUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string u = url.ToLowerInvariant();
            if (u.Contains("rapidrame_id=")) return true;
            if (Regex.IsMatch(u, @"/video/embed/[a-z0-9_-]+", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(u, @"/(?:e|embed|player)/[a-z0-9_-]{6,}(?:/|\?|$)", RegexOptions.IgnoreCase)
                && !IsYouTubeHost(u))
                return true;
            return false;
        }

        private static bool IsYouTubeHost(string urlLower)
            => urlLower.Contains("youtube.com") || urlLower.Contains("youtu.be")
               || urlLower.Contains("youtube-nocookie.com");

        /// <summary>Embed/HTML içinden düz metin HLS URL'leri (JS deşifre yok).</summary>
        public static List<string> ExtractHlsUrlsFromHtml(string? html)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(html)) return list;
            foreach (Match m in Regex.Matches(html,
                         @"https?://[^\s""'<>\\]+", RegexOptions.IgnoreCase))
            {
                string raw = m.Value.TrimEnd('\\', '"', '\'', ')', ']', '}', ',', ';');
                raw = raw.Replace("\\/", "/");
                if (!LooksLikeHlsUrl(raw) || IsJunkMediaUrl(raw)) continue;
                if (!list.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    list.Add(raw);
            }
            // master.txt / m3u8 önce
            return list
                .OrderByDescending(u => Regex.IsMatch(u, @"/(master|index|playlist)\.(m3u8?|txt)", RegexOptions.IgnoreCase))
                .ThenByDescending(u => u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>/hls/foo.mp4/txt/sublist → /hls/foo.mp4/master.txt|m3u8 adayları.</summary>
        public static IEnumerable<string> GuessHlsMasterUrls(string mediaUrl)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(mediaUrl)) return list;
            try
            {
                if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out var u)) return list;
                string path = u.AbsolutePath;
                path = Regex.Replace(path, @"/txt/[^/]+$", "/", RegexOptions.IgnoreCase);
                path = Regex.Replace(path, @"/(sub|subs|subtitle|subtitles|caption|captions)s?/[^/]*$", "/", RegexOptions.IgnoreCase);
                var mp4 = Regex.Match(path, @"^(.*?\.mp4)/", RegexOptions.IgnoreCase);
                if (mp4.Success) path = mp4.Groups[1].Value + "/";
                else path = Regex.Replace(path, @"/[^/]*$", "/");

                string[] names =
                [
                    "master.txt", "index.txt", "playlist.txt", "manifest.txt", "hls.txt",
                    "index.m3u8", "master.m3u8", "playlist.m3u8", "manifest.m3u8", "hls.m3u8"
                ];
                foreach (string n in names)
                {
                    string cand = $"{u.Scheme}://{u.Authority}{path}{n}{u.Query}";
                    if (!cand.Equals(mediaUrl, StringComparison.OrdinalIgnoreCase)
                        && !list.Contains(cand, StringComparer.OrdinalIgnoreCase))
                        list.Add(cand);
                }
                string parent = Regex.Replace(path, @"/[^/]+/$", "/");
                if (!parent.Equals(path, StringComparison.Ordinal))
                {
                    foreach (string n in new[] { "master.txt", "master.m3u8", "index.m3u8", "playlist.m3u8", "index.txt" })
                    {
                        string cand = $"{u.Scheme}://{u.Authority}{parent}{n}{u.Query}";
                        if (!list.Contains(cand, StringComparer.OrdinalIgnoreCase))
                            list.Add(cand);
                    }
                }
            }
            catch { /* ignore */ }
            return list.Take(12);
        }

        public static bool LooksLikeDashUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string u = url.ToLowerInvariant();
            return u.Contains(".mpd") || u.Contains("dash+xml") || u.Contains("/dash/")
                   || u.Contains("manifest.mpd");
        }

        public static bool LooksLikeProgressiveFile(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return Regex.IsMatch(url, @"\.(mp4|webm|mkv|mov|m4v|avi)(\?|$)", RegexOptions.IgnoreCase);
        }

        private static string? TryFetchText(string url, FormatsRequest req, TimeSpan timeout)
        {
            try
            {
                using var client = TransferHttp.CreateClient();
                client.Timeout = timeout;
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyHeaders(message, req);
                using var response = client.Send(message);
                if (!response.IsSuccessStatusCode) return null;
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyHeaders(HttpRequestMessage msg, FormatsRequest req)
        {
            string referer = !string.IsNullOrWhiteSpace(req.Referrer) ? req.Referrer : req.PageUrl;
            if (!string.IsNullOrWhiteSpace(referer))
                msg.Headers.TryAddWithoutValidation("Referer", referer);
            try
            {
                string originSrc = !string.IsNullOrWhiteSpace(referer) ? referer : req.PageUrl;
                if (!string.IsNullOrWhiteSpace(originSrc) && Uri.TryCreate(originSrc, UriKind.Absolute, out var ou))
                    msg.Headers.TryAddWithoutValidation("Origin", ou.GetLeftPart(UriPartial.Authority));
            }
            catch { /* ignore */ }
            if (!string.IsNullOrWhiteSpace(req.Cookies))
                msg.Headers.TryAddWithoutValidation("Cookie", req.Cookies);
            foreach (var kv in req.Headers)
            {
                if (kv.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(req.Cookies))
                    continue;
                msg.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        private static Dictionary<string, string> BuildHeaders(FormatsRequest req)
        {
            var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string referer = !string.IsNullOrWhiteSpace(req.Referrer) ? req.Referrer : req.PageUrl;
            if (!string.IsNullOrWhiteSpace(referer)) h["Referer"] = referer;
            if (!string.IsNullOrWhiteSpace(req.Cookies)) h["Cookie"] = req.Cookies;
            foreach (var kv in req.Headers) h[kv.Key] = kv.Value;
            return h;
        }

        private static void StampFormatHeaders(List<FormatOptionDto> formats, FormatsRequest req)
        {
            var hdr = BuildHeaders(req);
            if (hdr.Count == 0) return;
            foreach (var f in formats)
            {
                if (f.Headers == null)
                    f.Headers = new Dictionary<string, string>(hdr, StringComparer.OrdinalIgnoreCase);
                else
                {
                    foreach (var kv in hdr)
                    {
                        if (!f.Headers.ContainsKey(kv.Key) || string.IsNullOrWhiteSpace(f.Headers[kv.Key]))
                            f.Headers[kv.Key] = kv.Value;
                    }
                }
            }
        }

        public static List<FormatOptionDto> ParseHls(string playlist, string masterUrl)
        {
            var list = new List<FormatOptionDto>();
            if (!playlist.Contains("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return list;

            // Audio
            foreach (string raw in playlist.Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase)) continue;
                string? uri = ParseAttr(line, "URI");
                if (string.IsNullOrWhiteSpace(uri)) continue;
                string name = ParseAttr(line, "NAME") ?? ParseAttr(line, "LANGUAGE") ?? "AAC";
                list.Add(new FormatOptionDto
                {
                    Id = $"audio-{name}",
                    Label = $"Ses {name}",
                    Type = "audio",
                    Kind = "audio",
                    Vcodec = "none",
                    Acodec = "aac",
                    Url = HlsPlaylist.Resolve(uri, masterUrl),
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                });
            }

            if (!playlist.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                int h = GuessHeightFromUrl(masterUrl);
                list.Add(new FormatOptionDto
                {
                    Id = $"hls-media-{h}",
                    Label = h > 0 ? FormatHeightLabel(h, 0) : "Video",
                    Height = h,
                    Url = masterUrl,
                    Type = "hls",
                    Kind = "hls",
                    MasterUrl = masterUrl,
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                });
                return list;
            }

            int pendingBw = -1;
            int pendingH = 0;
            int pendingW = 0;
            double pendingFps = 0;
            string pendingCodecs = "";

            foreach (string raw in playlist.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                {
                    pendingBw = ParseAttrInt(line, "BANDWIDTH");
                    string res = ParseAttr(line, "RESOLUTION") ?? "";
                    if (!string.IsNullOrWhiteSpace(res))
                    {
                        var parts = res.Split('x');
                        if (parts.Length == 2)
                        {
                            _ = int.TryParse(parts[0], out pendingW);
                            _ = int.TryParse(parts[1], out pendingH);
                        }
                    }
                    pendingFps = ParseAttrDouble(line, "FRAME-RATE");
                    pendingCodecs = ParseAttr(line, "CODECS") ?? "";
                    continue;
                }

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    pendingBw = -1;
                    continue;
                }

                if (pendingBw >= 0 || pendingH > 0)
                {
                    string uri = HlsPlaylist.Resolve(line, masterUrl);
                    int h = pendingH > 0 ? pendingH : GuessHeightFromUrl(uri);
                    string label = h > 0
                        ? FormatHeightLabel(h, pendingFps)
                        : (pendingBw > 0 ? $"{pendingBw / 1000}k" : "HLS");
                    string[] codecParts = pendingCodecs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    list.Add(new FormatOptionDto
                    {
                        Id = $"h-{h}-{codecParts.FirstOrDefault() ?? "x"}-{pendingBw}",
                        Label = label,
                        Height = h,
                        Width = pendingW,
                        Fps = (int)Math.Round(pendingFps),
                        Bandwidth = pendingBw,
                        Vcodec = codecParts.ElementAtOrDefault(0) ?? "",
                        Acodec = codecParts.ElementAtOrDefault(1) ?? "",
                        Url = uri,
                        Type = "hls",
                        Kind = "hls",
                        MasterUrl = masterUrl
                    });
                }
                pendingBw = -1;
                pendingH = 0;
                pendingW = 0;
                pendingFps = 0;
                pendingCodecs = "";
            }

            return DeduplicateLabels(list);
        }

        public static List<FormatOptionDto> ParseDash(string mpd, string mpdUrl)
        {
            var list = new List<FormatOptionDto>();
            try
            {
                var doc = XDocument.Parse(mpd);
                foreach (var rep in doc.Descendants().Where(e => e.Name.LocalName == "Representation"))
                {
                    int h = (int?)rep.Attribute("height") ?? 0;
                    int w = (int?)rep.Attribute("width") ?? 0;
                    long bw = (long?)rep.Attribute("bandwidth") ?? 0;
                    string codecs = rep.Attribute("codecs")?.Value ?? "";
                    string mime = rep.Attribute("mimeType")?.Value ?? "";
                    string id = rep.Attribute("id")?.Value ?? Guid.NewGuid().ToString("N")[..8];
                    string? baseUrl = rep.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value
                        ?? rep.Parent?.Elements().FirstOrDefault(e => e.Name.LocalName == "BaseURL")?.Value;
                    if (string.IsNullOrWhiteSpace(baseUrl))
                        baseUrl = mpdUrl;
                    else
                        baseUrl = HlsPlaylist.Resolve(baseUrl.Trim(), mpdUrl);

                    bool audioOnly = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                    list.Add(new FormatOptionDto
                    {
                        Id = $"dash-{id}",
                        Label = audioOnly ? "Sadece ses" : (h > 0 ? FormatHeightLabel(h, 0) : "DASH"),
                        Height = h,
                        Width = w,
                        Bandwidth = bw,
                        Vcodec = audioOnly ? "none" : codecs,
                        Acodec = audioOnly ? codecs : "",
                        Url = baseUrl,
                        Type = "dash",
                        Kind = "dash",
                        FormatId = id,
                        MpdUrl = mpdUrl
                    });
                }
            }
            catch { /* ignore bad xml */ }
            return DeduplicateLabels(list);
        }

        private static FormatsResponse? TryYtDlpFormats(FormatsRequest req, TimeSpan timeout)
        {
            if (!YtDlpHelper.IsAvailable())
                return null;

            using var doc = YtDlpHelper.ExtractInfoJson(req.PageUrl, req.Cookies, req.Headers, timeout);
            if (doc == null)
                return null;

            var root = doc.RootElement;
            var resp = new FormatsResponse { Ok = true };
            resp.Title = JsonStr(root, "title");
            string? thumb = JsonStr(root, "thumbnail");
            if (!string.IsNullOrWhiteSpace(thumb))
                resp.Thumbnail = thumb;

            bool youtube = YtDlpHelper.IsYouTubeUrl(req.PageUrl);
            var byHeight = new Dictionary<int, FormatOptionDto>();
            FormatOptionDto? bestAudio = null;

            if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in formats.EnumerateArray())
                {
                    try
                    {
                        string protocol = JsonStr(f, "protocol");
                        if (protocol is "mhtml" or "storyboard") continue;
                        if (protocol.Contains("mhtml", StringComparison.OrdinalIgnoreCase)) continue;
                        // YouTube m3u8 (Premium/HLS) filesize çoğu zaman NA → yanlış 3–8 MB tahmini
                        if (youtube && protocol.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string formatId = JsonStr(f, "format_id");
                        string ext = JsonStr(f, "ext");
                        if (ext is "mhtml" or "jpg" or "png" or "webp" or "jpeg") continue;
                        if (string.IsNullOrWhiteSpace(formatId)) continue;

                        string vcodec = JsonStr(f, "vcodec");
                        string acodec = JsonStr(f, "acodec");
                        int height = JsonInt(f, "height");
                        int fps = (int)Math.Round(JsonDouble(f, "fps"));
                        double abr = JsonDouble(f, "abr");
                        double tbr = JsonDouble(f, "tbr");
                        long? filesize = JsonLong(f, "filesize") ?? JsonLong(f, "filesize_approx");
                        // NA ise süre×bitrate ile yaklaşık boyut
                        if (filesize is null or <= 0 && tbr > 0)
                        {
                            double dur = JsonDouble(root, "duration");
                            if (dur > 0)
                                filesize = (long)(tbr * 1000.0 / 8.0 * dur);
                        }

                        bool audioOnly = (vcodec == "none" || string.IsNullOrWhiteSpace(vcodec))
                                         && !string.IsNullOrWhiteSpace(acodec) && acodec != "none";
                        bool videoOnly = !string.IsNullOrWhiteSpace(vcodec) && vcodec != "none"
                                         && (string.IsNullOrWhiteSpace(acodec) || acodec == "none");
                        bool hasVideo = height > 0 && !audioOnly;

                        if (audioOnly)
                        {
                            var a = new FormatOptionDto
                            {
                                Id = formatId,
                                FormatId = formatId,
                                Label = abr > 0 ? $"Ses {Math.Round(abr)}k" : "Sadece ses",
                                Height = 0,
                                Bandwidth = (long)(abr * 1000),
                                Vcodec = "none",
                                Acodec = acodec,
                                Filesize = filesize,
                                Url = "",
                                Type = "audio",
                                Kind = "yt-dlp",
                                Headers = BuildHeaders(req)
                            };
                            if (bestAudio == null || a.Bandwidth > bestAudio.Bandwidth)
                                bestAudio = a;
                            continue;
                        }

                        if (!hasVideo || height < 144) continue;

                        string dlId = (youtube && videoOnly) ? $"{formatId}+bestaudio" : formatId;
                        var opt = new FormatOptionDto
                        {
                            Id = dlId,
                            FormatId = dlId,
                            Label = FormatHeightLabel(height, fps),
                            Height = height,
                            Fps = fps,
                            Bandwidth = (long)(tbr * 1000),
                            Vcodec = vcodec,
                            Acodec = videoOnly ? "bestaudio" : acodec,
                            Filesize = filesize,
                            Url = "",
                            Type = "yt-dlp",
                            Kind = "yt-dlp",
                            Headers = BuildHeaders(req)
                        };

                        if (!byHeight.TryGetValue(height, out var prev) || PreferFormat(opt, prev, videoOnly))
                            byHeight[height] = opt;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"yt-dlp format skip: {ex.Message}");
                    }
                }
            }

            foreach (int h in byHeight.Keys.OrderByDescending(x => x))
                resp.Formats.Add(byHeight[h]);

            if (bestAudio != null)
                resp.Formats.Add(bestAudio);

            // Video-only+bestaudio: listedeki boyuta sesi ekle
            if (bestAudio?.Filesize is > 0)
            {
                foreach (var f in resp.Formats)
                {
                    if (f.Height <= 0) continue;
                    if (!string.Equals(f.Acodec, "bestaudio", StringComparison.OrdinalIgnoreCase)) continue;
                    if (f.Filesize is > 0)
                        f.Filesize += bestAudio.Filesize;
                }
            }

            if (resp.Formats.Any(f => f.Height > 0))
            {
                resp.Formats.Insert(0, new FormatOptionDto
                {
                    Id = "best",
                    // m3u8/Premium yerine https progressive/DASH
                    FormatId = "bv*[protocol^=http][vcodec^=avc1]+ba[protocol^=http]/bv*[protocol^=http]+ba/b",
                    Label = "En iyi kalite",
                    Height = resp.Formats.Where(f => f.Height > 0).Max(f => f.Height),
                    Type = "yt-dlp",
                    Kind = "yt-dlp",
                    Url = "",
                    Filesize = resp.Formats.Where(f => f.Height > 0).Select(f => f.Filesize ?? 0).DefaultIfEmpty(0).Max(),
                    Headers = BuildHeaders(req)
                });
            }

            if (resp.Formats.Count == 0)
                return null;
            return resp;
        }

        private static bool PreferFormat(FormatOptionDto candidate, FormatOptionDto existing, bool candidateVideoOnly)
        {
            // Bilinen dosya boyutu olan (https/DASH) m3u8 NA'dan iyidir
            bool cHasSize = (candidate.Filesize ?? 0) > 500_000;
            bool eHasSize = (existing.Filesize ?? 0) > 500_000;
            if (cHasSize != eHasSize)
                return cHasSize;

            bool existingMuxed = !string.Equals(existing.Acodec, "bestaudio", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(existing.Acodec, "none", StringComparison.OrdinalIgnoreCase)
                                 && !string.IsNullOrWhiteSpace(existing.Acodec);
            bool candidateMuxed = !candidateVideoOnly
                                  && !string.Equals(candidate.Acodec, "bestaudio", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(candidate.Acodec, "none", StringComparison.OrdinalIgnoreCase)
                                  && !string.IsNullOrWhiteSpace(candidate.Acodec);
            // Aynı yükseklikte muxed (tek dosya) video-only+ses'ten iyidir — ama boyutu bilinen DASH'i ezmesin
            if (candidateMuxed != existingMuxed && cHasSize == eHasSize)
                return candidateMuxed;
            if (candidate.Bandwidth != existing.Bandwidth)
                return candidate.Bandwidth > existing.Bandwidth;
            return (candidate.Filesize ?? 0) > (existing.Filesize ?? 0);
        }

        private static string JsonStr(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p)) return "";
            return p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? "",
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => ""
            };
        }

        private static int JsonInt(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
                return 0;
            if (p.TryGetInt32(out int i)) return i;
            if (p.TryGetDouble(out double d)) return (int)Math.Round(d);
            return 0;
        }

        private static double JsonDouble(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
                return 0;
            return p.TryGetDouble(out double d) ? d : 0;
        }

        private static long? JsonLong(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number)
                return null;
            if (p.TryGetInt64(out long l)) return l;
            if (p.TryGetDouble(out double d) && d > 0) return (long)Math.Round(d);
            return null;
        }

        private static List<FormatOptionDto> DeduplicateLabels(List<FormatOptionDto> list)
        {
            var bestByHeight = new Dictionary<string, FormatOptionDto>(StringComparer.OrdinalIgnoreCase);
            var outList = new List<FormatOptionDto>();
            foreach (var f in list.OrderByDescending(x => x.Height).ThenByDescending(x => x.Bandwidth))
            {
                if (f.Id is "best" or "playing" || f.Type == "audio" || f.Kind == "audio")
                {
                    outList.Add(f);
                    continue;
                }
                string key = $"{f.Height}|{f.Fps}|{(f.Vcodec ?? "").Split('.')[0]}";
                if (!bestByHeight.ContainsKey(key))
                {
                    bestByHeight[key] = f;
                    outList.Add(f);
                }
                else
                {
                    var prev = bestByHeight[key];
                    string c1 = (prev.Vcodec ?? "").Split('.', ',')[0];
                    string c2 = (f.Vcodec ?? "").Split('.', ',')[0];
                    if (!string.IsNullOrEmpty(c2) && !string.Equals(c1, c2, StringComparison.OrdinalIgnoreCase)
                        && f.Label.IndexOf('·') < 0)
                    {
                        f.Label = $"{f.Label} · {c2}";
                        outList.Add(f);
                    }
                }
            }
            return outList;
        }

        private static string FormatHeightLabel(int height, double fps)
            => fps >= 50 ? $"{height}p{(int)Math.Round(fps)}" : $"{height}p";

        private static string GuessLabelFromUrl(string url)
        {
            int h = GuessHeightFromUrl(url);
            return h > 0 ? FormatHeightLabel(h, 0) : "Video";
        }

        private static int GuessHeightFromUrl(string url)
        {
            var m = Regex.Match(url, @"(?:^|[/_-])(2160|1440|1080|720|480|360|240|144)p?(?:[/_?-]|$)", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int h)) return h;
            m = Regex.Match(url, @"(\d{3,4})x(\d{3,4})", RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[2].Value, out int h2)) return h2;
            return 0;
        }

        private static string? ParseAttr(string line, string key)
        {
            var m = Regex.Match(line, key + @"=(""([^""]*)""|([^,]*))", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
        }

        private static int ParseAttrInt(string line, string key)
        {
            string? v = ParseAttr(line, key);
            return int.TryParse(v, out int n) ? n : 0;
        }

        private static double ParseAttrDouble(string line, string key)
        {
            string? v = ParseAttr(line, key);
            return double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : 0;
        }

        public static string SerializeFormats(FormatsResponse resp)
            => JsonSerializer.Serialize(resp, JsonOpts);
    }
}
