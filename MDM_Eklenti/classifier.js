// MDM — URL/MIME sınıflandırıcı
const MDM_VIDEO_EXT = /\.(mp4|m4v|m4s|webm|mkv|mov|avi|flv|ts|m2ts|3gp|ogv|f4v|f4f)(\?|$)/i;
const MDM_AUDIO_EXT = /\.(mp3|m4a|aac|wav|ogg|opus|flac|wma)(\?|$)/i;
const MDM_MANIFEST_EXT = /\.(m3u8|m3u|mpd|f4m|ism)(\?|$)/i;
const MDM_FILE_EXT = /\.(zip|rar|7z|pdf|exe|msi|iso|dmg|apk|torrent)(\?|$)/i;
const MDM_AD_PATTERNS = /\/(ad|ads)\/|doubleclick|googlesyndication|\/adserver/i;

const MDM_CT_MAP = {
  "video/mp4": "progressive",
  "video/webm": "progressive",
  "video/quicktime": "progressive",
  "video/x-matroska": "progressive",
  "audio/mpeg": "audio",
  "audio/mp4": "audio",
  "audio/webm": "audio",
  "application/vnd.apple.mpegurl": "hls",
  "application/x-mpegurl": "hls",
  "audio/mpegurl": "hls",
  "application/dash+xml": "dash",
  "video/vnd.mpeg.dash.mpd": "dash",
  "video/mp2t": "fragment",
  "application/json": "json",
  "text/plain": "json"
};

const MDM_IGNORE_CT = /^(text\/html|text\/css|application\/javascript|application\/json\+|image\/|font\/)/i;

function mdmNormalizeUrl(url) {
  if (!url) return "";
  try {
    const u = new URL(url);
    u.hash = "";
    const params = [...u.searchParams.entries()].sort((a, b) => a[0].localeCompare(b[0]));
    u.search = "";
    for (const [k, v] of params) {
      if (k.toLowerCase() === "range") continue;
      u.searchParams.set(k, v);
    }
    return u.toString();
  } catch (_) {
    return String(url);
  }
}

function mdmExtFromUrl(url) {
  try {
    const p = new URL(url).pathname;
    const i = p.lastIndexOf(".");
    return i >= 0 ? p.slice(i + 1).toLowerCase() : "";
  } catch (_) {
    return "";
  }
}

/** Gerçek HLS playlist adları (.txt uzantılı master dahil) */
function mdmIsHlsPlaylistName(url) {
  return /\/(master|index|playlist|manifest|hls)\.(m3u8?|txt)(\?|$)/i.test(url || "");
}

/** Altyazı / metadata — /hls/ altında bile video değil */
function mdmIsJunkMediaUrl(url) {
  const u = (url || "").toLowerCase();
  if (!u) return true;
  if (/\/txt\/sublist/i.test(u)) return true;
  if (/\/(sub|subs|subtitle|subtitles|caption|captions|thumb|thumbnail|poster)s?\//i.test(u)) return true;
  if (/\.(vtt|srt|ass|ssa|dfxp|ttml)(\?|$)/i.test(u)) return true;
  // master.txt / index.txt = playlist; sublist_*.txt = junk
  if (mdmIsHlsPlaylistName(u)) return false;
  if (/\/hls\/.+\.txt(\?|$)/i.test(u) && !/\.m3u8?/i.test(u)) return true;
  return false;
}

function mdmClassifyCapture(url, mime, contentLength, durationHint) {
  const ext = mdmExtFromUrl(url);
  const ct = (mime || "").split(";")[0].trim().toLowerCase();
  const len = parseInt(contentLength, 10) || 0;

  if (MDM_AD_PATTERNS.test(url)) return { action: "ignore" };
  if (mdmIsJunkMediaUrl(url)) return { action: "ignore" };
  if (durationHint && durationHint < 6 && len > 0 && len < 200000) return { action: "ignore" };

  if (MDM_IGNORE_CT.test(ct) && !MDM_MANIFEST_EXT.test(url) && !MDM_VIDEO_EXT.test(url))
    return { action: "ignore" };

  let kind = MDM_CT_MAP[ct] || "other";
  if (kind === "other") {
    if (MDM_MANIFEST_EXT.test(url) || ext === "m3u8" || ext === "mpd") kind = ext === "mpd" ? "dash" : "hls";
    else if (mdmIsHlsPlaylistName(url) || /\/hls\//i.test(url) || /\/hls\?/i.test(url) || /mpegurl/i.test(url)) kind = "hls";
    else if (/\/dash\//i.test(url)) kind = "dash";
    else if (MDM_VIDEO_EXT.test(url)) kind = "progressive";
    else if (MDM_AUDIO_EXT.test(url)) kind = "audio";
    else if (ext === "ts" || ext === "m4s") kind = "fragment";
    else if (MDM_FILE_EXT.test(url)) kind = "file";
    else if (ct === "application/octet-stream") {
      if (MDM_VIDEO_EXT.test(url) || MDM_MANIFEST_EXT.test(url) || mdmIsHlsPlaylistName(url) || /\/hls\//i.test(url))
        kind = (mdmIsHlsPlaylistName(url) || /\/hls\//i.test(url)) ? "hls" : "progressive";
      else kind = "file";
    }
  }

  if (kind === "fragment") return { action: "store", kind };
  // Dosya indirmeleri (zip/rar/exe/octet-stream) — MDM'ye yakalat
  if (kind === "file") return { action: "capture", kind };
  if (kind === "json") return { action: "store", kind };
  if (["progressive", "hls", "dash", "audio"].includes(kind))
    return { action: "capture", kind };

  return { action: "ignore" };
}

function mdmFilenameFromHeaders(url, contentDisposition) {
  if (contentDisposition) {
    const m = /filename\*?=(?:UTF-8''|")?([^";]+)/i.exec(contentDisposition);
    if (m) return decodeURIComponent(m[1].replace(/"/g, "").trim());
  }
  try {
    const p = new URL(url).pathname.split("/").pop();
    return p || "download";
  } catch (_) {
    return "download";
  }
}
