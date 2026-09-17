// MDM — service worker / Firefox background orchestrator
// Firefox staging manifest scripts[] ile yükler; SW'de importScripts gerekir.
if (typeof importScripts === "function") {
  try { importScripts("mdm-channel.js"); } catch (_) { /* Chromium staging dışında yok */ }
  if (typeof mdmExtCapture !== "function") {
    try {
      importScripts("i18n.js", "classifier.js", "capture-store.js", "formats.js", "desktop.js", "menus.js");
    } catch (e) {
      console.warn("MDM: importScripts failed", e);
    }
  }
}

// i18n yüklenmezse hata metinleri Türkçe kalsın, ReferenceError atmasın
if (typeof mdmErrText !== "function") {
  self.mdmErrText = (key, fallback) => fallback || key;
}

const MDM_STARTUP_GUARD_MS = 45000;
const MDM_MAX_FRESH_AGE_MS = 12000;
let mdmStartupGuardUntil = 0;
let mdmCaptureEnabled = true;
const mdmDisabledTabs = new Set();

// ——— popup ayarlari (mdmPrefs): tema / dugme / devre disi siteler ———
const MDM_PREFS_DEFAULTS = { theme: "dark", overlay: true, blocked: [] };
let mdmPrefs = Object.assign({}, MDM_PREFS_DEFAULTS);

function mdmBareHost(value) {
  return String(value || "").trim().toLowerCase().replace(/^www\./, "");
}

function mdmHostOf(url) {
  try { return mdmBareHost(new URL(url).hostname); } catch (_) { return ""; }
}

function mdmIsBlockedHost(host) {
  if (!host || !Array.isArray(mdmPrefs.blocked)) return false;
  return mdmPrefs.blocked.some((b) => {
    const entry = mdmBareHost(b);
    return entry && (host === entry || host.endsWith("." + entry));
  });
}

function mdmApplyTabBlock(tabId, blocked) {
  if (blocked) mdmDisabledTabs.add(tabId);
  else mdmDisabledTabs.delete(tabId);
  try {
    chrome.action.setBadgeText({ tabId, text: blocked ? "X" : "" });
    if (blocked) chrome.action.setBadgeBackgroundColor({ tabId, color: "#666" });
  } catch (_) {}
  chrome.tabs.sendMessage(tabId, { type: "mdm-set-enabled", enabled: !blocked }).catch(() => {});
}

async function mdmSyncBlockedTabs() {
  try {
    const tabs = await chrome.tabs.query({});
    for (const tab of tabs) {
      if (tab.id == null) continue;
      mdmApplyTabBlock(tab.id, mdmIsBlockedHost(mdmHostOf(tab.url || "")));
    }
  } catch (_) {}
}

function mdmSetPrefs(raw) {
  mdmPrefs = Object.assign({}, MDM_PREFS_DEFAULTS, raw || {});
  mdmSyncBlockedTabs();
}

try {
  Promise.resolve(chrome.storage.local.get("mdmPrefs"))
    .then((d) => mdmSetPrefs(d && d.mdmPrefs))
    .catch(() => {});
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes || !changes.mdmPrefs) return;
    mdmSetPrefs(changes.mdmPrefs.newValue);
  });
} catch (_) {}

chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (!info || (!info.url && info.status !== "complete")) return;
  mdmApplyTabBlock(tabId, mdmIsBlockedHost(mdmHostOf(info.url || (tab && tab.url) || "")));
});

async function mdmDisableBrowserDownloadUi() {
  try { if (chrome.downloads.setShelfEnabled) chrome.downloads.setShelfEnabled(false); } catch (_) {}
  try { if (chrome.downloads.setUiOptions) await chrome.downloads.setUiOptions({ enabled: false }); } catch (_) {}
}

function mdmIsFirefox() {
  try { return /Firefox\//.test(navigator.userAgent || ""); } catch (_) { return false; }
}

const mdmAcceptedCaptureUrls = new Map(); // url -> timestamp

function mdmRememberAccepted(url) {
  if (!url) return;
  mdmAcceptedCaptureUrls.set(url, Date.now());
  if (typeof mdmMarkHandoff === "function") mdmMarkHandoff(url);
}

function mdmAcceptedAgeMs(url) {
  const t = mdmAcceptedCaptureUrls.get(url);
  if (!t) return -1;
  const age = Date.now() - t;
  if (age > 120000) {
    mdmAcceptedCaptureUrls.delete(url);
    return -1;
  }
  return age;
}

function mdmWasAccepted(url) {
  return mdmAcceptedAgeMs(url) >= 0;
}

/** Kısa süre içindeki redirect/spam kopyası mı, yoksa kullanıcının tekrar indirme isteği mi? */
function mdmIsFreshAcceptedDuplicate(url) {
  const age = mdmAcceptedAgeMs(url);
  return age >= 0 && age < 2500;
}

function mdmIsSessionRestoreReplay(downloadItem) {
  const now = Date.now();
  const started = downloadItem.startTime ? Date.parse(downloadItem.startTime) : NaN;
  const ageMs = Number.isNaN(started) ? 0 : now - started;
  if (downloadItem.state === "complete") return true;
  if ((downloadItem.bytesReceived || 0) > 512 * 1024 && ageMs > 8000) return true;
  return false;
}

async function mdmSendCaptureToDesktop(payload) {
  if (!mdmCaptureEnabled) return false;
  const ok = await mdmExtCapture(payload);
  if (ok) {
    const u = payload.url || payload.pageUrl || "";
    if (u) mdmRememberAccepted(u);
    else mdmMarkHandoff(u);
  }
  return ok;
}

async function mdmCaptureUrl(url, filename, tab, pageUrl, extra = {}) {
  if (!url || mdmShouldSkipHandoff(url)) return false;
  const cookies = extra.cookies || await mdmGetCookies(url);
  const payload = {
    url,
    filename: filename || "",
    mime: extra.mime || "",
    pageUrl: pageUrl || tab?.url || "",
    referrer: extra.referrer || tab?.url || "",
    kind: extra.kind || "progressive",
    formatId: extra.formatId || "",
    title: extra.title || "",
    cookies,
    headers: extra.headers || {}
  };
  const ok = await mdmSendCaptureToDesktop(payload);
  if (!ok) return await mdmHandoffLegacy(url, filename, extra.mime);
  return ok;
}

async function mdmGetCookies(url) {
  const urls = [];
  if (url) urls.push(url);
  try {
    const u = new URL(url);
    const host = (u.hostname || "").toLowerCase();
    // Google Drive: auth çerezleri drive.google.com / .google.com üzerinde
    if (host.includes("google.com") || host.includes("googleusercontent.com")) {
      urls.push("https://drive.google.com/");
      urls.push("https://docs.google.com/");
      urls.push("https://accounts.google.com/");
      urls.push("https://drive.usercontent.google.com/");
    }
  } catch (_) {}
  const map = new Map();
  for (const u of urls) {
    try {
      const list = await chrome.cookies.getAll({ url: u });
      for (const c of list || []) {
        if (c && c.name) map.set(c.name, c.value);
      }
    } catch (_) {}
  }
  return [...map.entries()].map(([k, v]) => `${k}=${v}`).join("; ");
}

function mdmBroadcastDesktopStatus() {
  const online = mdmIsDesktopOnline();
  chrome.tabs.query({}, (tabs) => {
    for (const t of tabs) {
      if (t.id != null) {
        chrome.tabs.sendMessage(t.id, { type: "mdm-desktop-status", online }).catch(() => {});
      }
    }
  });
}

// ——— webRequest sniffing ———
function mdmOnBeforeRequest(details) {
  if (!mdmCaptureEnabled) return;
  if (details.tabId < 0) return;
  if (mdmDisabledTabs.has(details.tabId)) return;
  const method = (details.method || "GET").toUpperCase();
  if (method !== "GET" && method !== "POST") return;
  const url = details.url || "";
  if (url.startsWith("chrome-extension://")) return;

  const cls = mdmClassifyCapture(url, "", 0);
  if (cls.action === "ignore") return;

  const cap = {
    id: `${details.requestId}`,
    requestId: details.requestId,
    tabId: details.tabId,
    frameId: details.frameId,
    pageUrl: details.initiator || "",
    url,
    finalUrl: url,
    method,
    type: details.type,
    kind: cls.kind,
    timeStamp: Date.now(),
    fromHook: false
  };
  mdmUpsertCapture(cap);

  if (cls.action === "capture") {
    chrome.tabs.sendMessage(details.tabId, {
      type: "mdm-media",
      capture: cap
    }).catch(() => {});
  }
}

function mdmOnHeadersReceived(details) {
  if (!mdmCaptureEnabled || details.tabId < 0) return;
  const status = details.statusCode || 0;
  if (status !== 200 && status !== 206 && status !== 304) return;

  let mime = "";
  let contentLength = "";
  let contentDisposition = "";
  for (const h of details.responseHeaders || []) {
    const n = (h.name || "").toLowerCase();
    if (n === "content-type") mime = h.value || "";
    if (n === "content-length") contentLength = h.value || "";
    if (n === "content-disposition") contentDisposition = h.value || "";
  }

  const isAttachment = /attachment/i.test(contentDisposition || "");

  // Firefox: Content-Disposition: attachment → yalnızca gerçek dosya (stream/YouTube değil)
  if (isAttachment && mdmIsFirefox() && /^https?:\/\//i.test(details.url)
      && !mdmIsFreshAcceptedDuplicate(details.url) && !mdmShouldSkipHandoff(details.url)
      && mdmLooksLikeBinaryDownload(details.url, mime, contentDisposition)) {
    const fname = mdmFilenameFromHeaders(details.url, contentDisposition) || "download";
    mdmGetCookies(details.url).then((cookies) => {
      const referrer = details.originUrl || details.documentUrl || "";
      const headers = {};
      if (referrer) headers.Referer = referrer;
      if (/google\.com|googleusercontent\.com/i.test(details.url))
        headers.Referer = headers.Referer || "https://drive.google.com/";
      mdmSendCaptureToDesktop({
        url: details.url,
        filename: fname,
        mime,
        pageUrl: referrer || details.url,
        referrer: headers.Referer || referrer,
        kind: "progressive",
        cookies,
        headers
      }).then((ok) => {
        if (ok) mdmRememberAccepted(details.url);
      }).catch(() => {});
    });
  }

  const cls = mdmClassifyCapture(details.url, mime, contentLength);
  if (cls.action !== "capture" && cls.action !== "store") return;

  mdmGetCookies(details.url).then((cookies) => {
    const cap = {
      id: `${details.requestId}`,
      requestId: details.requestId,
      tabId: details.tabId,
      frameId: details.frameId,
      url: details.url,
      finalUrl: details.url,
      mime,
      filenameHint: mdmFilenameFromHeaders(details.url, contentDisposition),
      contentLength: parseInt(contentLength, 10) || 0,
      cookies,
      kind: cls.kind,
      timeStamp: Date.now()
    };
    mdmUpsertCapture(cap);
    if (cls.kind === "hls" || cls.kind === "dash") {
      chrome.tabs.sendMessage(details.tabId, { type: "mdm-media", capture: cap }).catch(() => {});
    }
  });
}

try {
  chrome.webRequest.onBeforeRequest.addListener(
    mdmOnBeforeRequest,
    { urls: ["http://*/*", "https://*/*"], types: ["xmlhttprequest", "media", "object", "other", "sub_frame", "main_frame", "ping"] },
    []
  );
  chrome.webRequest.onHeadersReceived.addListener(
    mdmOnHeadersReceived,
    { urls: ["http://*/*", "https://*/*"], types: ["xmlhttprequest", "media", "object", "other", "sub_frame", "main_frame"] },
    ["responseHeaders"]
  );
} catch (e) {
  console.warn("MDM: webRequest unavailable", e);
}

// Firefox: indirmeyi Save As'tan ÖNCE kes (blocking) — Chromium MV3'te yok
function mdmHeaderValue(headers, name) {
  const n = name.toLowerCase();
  for (const h of headers || []) {
    if ((h.name || "").toLowerCase() === n) return h.value || "";
  }
  return "";
}

function mdmIsStreamingMediaUrl(url) {
  if (!url) return false;
  try {
    const h = new URL(url).hostname.toLowerCase();
    if (h.includes("googlevideo.com") || h.includes("youtube.com") || h.includes("youtu.be")
        || h.includes("youtube-nocookie.com") || h.includes("ytimg.com")
        || h.includes("vimeocdn.com") || h.includes("akamaized.net")
        || h.includes("ttvnw.net") || h.includes("twitch.tv")
        || h.includes("fbcdn.net") || h.includes("cdninstagram.com")
        || h.includes("tiktokcdn") || h.includes("byteoversea.com"))
      return true;
  } catch (_) {}
  // HLS/DASH segment / videoplayback parçaları — otomatik indirme değil
  if (/\/videoplayback\b/i.test(url) || /[?&]range=/i.test(url)) return true;
  if (/\.m3u8(\?|$)/i.test(url) || /\.mpd(\?|$)/i.test(url)) return true;
  if (/\/hls\/|\/dash\/|fragment|seg-\d/i.test(url)) return true;
  return false;
}

/** ChatGPT / Gemini vb. — stream/XHR'yi tarayıcı bazen «indirme» sanır */
function mdmIsChatAiSiteUrl(url) {
  if (!url) return false;
  try {
    const h = new URL(url).hostname.toLowerCase();
    if (h === "gemini.google.com" || h.endsWith(".gemini.google.com")) return true;
    if (h === "bard.google.com") return true;
    if (h.includes("chatgpt.com") || h.includes("chat.openai.com")) return true;
    if (h.includes("claude.ai") || h.includes("anthropic.com")) return true;
    if (h.includes("copilot.microsoft.com")) return true;
    if (h.includes("perplexity.ai") || h.includes("poe.com") || h.includes("character.ai")) return true;
    if (h.includes("deepseek.com") || h === "x.ai" || h.includes("grok.x.ai")) return true;
    if (h.includes("generativelanguage.googleapis.com")) return true;
  } catch (_) {}
  if (/\/\$rpc\/|\/StreamGenerate|\/BardChatUi\//i.test(url)) return true;
  return false;
}

/** Arama önerisi / XHR API — tarayıcı «indirme» diye gösterse bile MDM'ye alma */
function mdmIsNoiseApiUrl(url) {
  if (!url) return false;
  if (mdmIsChatAiSiteUrl(url)) return true;
  const u = url.toLowerCase();
  if (/google\.[^/]+\/complete\//i.test(u)) return true;
  if (/\/complete\/search\b/i.test(u) || /\/complete\/s\b/i.test(u)) return true;
  if (/suggestqueries\.google/i.test(u)) return true;
  if (/\/client\/(suggest|complete)\b/i.test(u)) return true;
  if (/\/gen_204\b/i.test(u) || /\/csi\b/i.test(u)) return true;
  if (/\/(beacon|pixel)\b/i.test(u) || /\/pagead\//i.test(u)) return true;
  if (/doubleclick\.|googlesyndication\.|googleadservices\./i.test(u)) return true;
  if (/bing\.[^/]+\/(AS\/|api\/v7\/suggestions)/i.test(u)) return true;
  if (/duckduckgo\.[^/]+\/ac\/\?/i.test(u)) return true;
  return false;
}

/** Tarayıcının octet-stream için uydurduğu isimler — gerçek arşiv/uygulama değil */
function mdmIsJunkDownloadName(name) {
  const n = ((name || "").split(/[/\\]/).pop() || "").trim().toLowerCase();
  if (!n) return true;
  if (/^(f\.txt|download|untitled|unknown|document|file|blob|response|data|stream|payload)(\.|$)/i.test(n))
    return true;
  if (/^(response|download|file|data|blob|stream|octet|binary|temp|tmp)(\.(bin|dat|tmp|part))?$/i.test(n))
    return true;
  // Tek başına .bin / .dat — Gemini/ChatGPT stream varsayılanı
  if (/\.(bin|dat|part|tmp)$/i.test(n) && /^(response|download|file|data|blob|stream|octet|binary)/i.test(n))
    return true;
  if (n === "response.bin" || n === "download.bin" || n === "file.bin") return true;
  return false;
}

// .bin bilerek yok — tarayıcı AI stream'lerini response.bin diye adlandırıyor
const MDM_REAL_FILE_NAME = /\.(zip|rar|7z|tar|gz|bz2|pdf|exe|msi|iso|dmg|apk|torrent|doc|docx|xls|xlsx|ppt|pptx|rtf|odt|appx|msix|mp4|mkv|avi|mov|webm|mp3|wav|flac)(\s|$)/i;

function mdmLooksLikeBinaryDownload(url, mime, disposition) {
  if (mdmIsStreamingMediaUrl(url) || mdmIsNoiseApiUrl(url)) return false;

  const fname = (typeof mdmFilenameFromHeaders === "function"
    ? (mdmFilenameFromHeaders(url, disposition) || "") : "");
  if (mdmIsJunkDownloadName(fname)) return false;
  if (/^f\.txt$/i.test((fname || "").trim())) return false;

  const fileNameHint = MDM_REAL_FILE_NAME.test(fname || "");
  if (fileNameHint) return true;
  if (typeof MDM_FILE_EXT !== "undefined" && MDM_FILE_EXT.test(url)) return true;

  const ct = (mime || "").split(";")[0].trim().toLowerCase();
  const isAttachment = /attachment/i.test(disposition || "");

  if (/^video\//i.test(ct) || /^audio\//i.test(ct))
    return isAttachment && fileNameHint;
  if (/^text\//i.test(ct) || /javascript/i.test(ct) || /^image\//i.test(ct) || /^application\/json/i.test(ct))
    return false;
  // protobuf / event-stream — chat AI
  if (/^application\/(x-)?protobuf/i.test(ct) || /^text\/event-stream/i.test(ct))
    return false;

  // Drive / export=download — MIME belirsiz olsa da gerçek dosya
  if (/drive\.usercontent\.google\.com\/download/i.test(url)) return true;
  if (/[?&]export=download\b/i.test(url) && /google\.com/i.test(url)) return true;

  if (/^application\/(zip|x-zip|x-rar|rar|vnd\.rar|x-7z|pdf|msword|vnd\.ms-|vnd\.openxmlformats)/i.test(ct))
    return true;
  if (/^application\/(octet-stream|force-download|binary)/i.test(ct))
    return isAttachment && fileNameHint;

  if (isAttachment && fileNameHint) return true;
  return false;
}

function mdmShouldAutoTakeoverDownload(url, mime, filename) {
  if (!url) return false;
  if (/^magnet:/i.test(url)) return true;
  if (/^blob:/i.test(url) || /^data:/i.test(url) || /^file:/i.test(url)) return false;
  if (mdmIsStreamingMediaUrl(url) || mdmIsNoiseApiUrl(url)) return false;
  const name = (filename || "").split(/[/\\]/).pop() || "";
  if (name && mdmIsJunkDownloadName(name)) return false;
  if (/^videoplayback(\.|$)/i.test(name)) return false;
  if (/\.(m3u8|mpd|ts|m4s)(\?|$)/i.test(url) || /\.(m3u8|mpd|ts|m4s)$/i.test(name)) return false;
  if (name && /\.bin$/i.test(name) && !MDM_REAL_FILE_NAME.test(name.replace(/\.bin$/i, ".zip"))) return false;

  const ct = (mime || "").split(";")[0].trim().toLowerCase();
  if (/^text\/(html|css|javascript)/i.test(ct) || /^application\/json/i.test(ct)) return false;
  if (/^application\/(x-)?protobuf/i.test(ct) || /^text\/event-stream/i.test(ct)) return false;
  if (/^video\//i.test(ct) || /^audio\//i.test(ct)) {
    return /\.(mp4|mkv|avi|mov|webm|m4v|flv|wmv|mp3|wav|flac|m4a|aac|ogg)(\?|$)/i.test(url)
      || /\.(mp4|mkv|avi|mov|webm|m4v|flv|wmv|mp3|wav|flac|m4a|aac|ogg)$/i.test(name);
  }
  return true;
}

function mdmHandoffFromHeaders(details, mime, disposition) {
  const url = details.url || "";
  if (!/^https?:\/\//i.test(url)) return;
  // Redirect spam (<2.5 sn): tekrar gönderme; kullanıcı tekrar tıkladıysa (daha eski) gönder
  if (mdmIsFreshAcceptedDuplicate(url)) return;
  if (mdmWasAccepted(url)) mdmAcceptedCaptureUrls.delete(url);
  const fname = (typeof mdmFilenameFromHeaders === "function"
    ? mdmFilenameFromHeaders(url, disposition) : "") || "download";
  const referrer = details.originUrl || details.documentUrl || "";
  const headers = {};
  if (referrer) headers.Referer = referrer;
  if (/google\.com|googleusercontent\.com/i.test(url))
    headers.Referer = headers.Referer || "https://drive.google.com/";
  mdmGetCookies(url).then((cookies) => {
    mdmSendCaptureToDesktop({
      url,
      filename: fname,
      mime: mime || "",
      pageUrl: referrer || url,
      referrer: headers.Referer || referrer,
      kind: "progressive",
      cookies,
      headers
    }).then((ok) => {
      if (ok) mdmRememberAccepted(url);
      else {
        mdmHandoffLegacy(url, fname, mime || "").then((ok2) => {
          if (ok2) mdmRememberAccepted(url);
        });
      }
    }).catch(() => {});
  });
}

if (mdmIsFirefox()) {
  try {
    // İndirmeyi kesme — iptal + async handoff yarışı dosyayı hem tarayıcıda hem MDM'de kaybettiriyordu.
    // Sadece orijinal URL'yi hatırla; asıl yakalama downloads.onCreated/onChanged.
    chrome.webRequest.onHeadersReceived.addListener(
      function mdmFirefoxNoteDownload(details) {
        if (!mdmCaptureEnabled) return;
        const status = details.statusCode || 0;
        if (status !== 200 && status !== 206) return;
        const mime = mdmHeaderValue(details.responseHeaders, "content-type");
        const disposition = mdmHeaderValue(details.responseHeaders, "content-disposition");
        if (!mdmLooksLikeBinaryDownload(details.url, mime, disposition)
            && !/attachment/i.test(disposition || "")) return;
        if (/^text\/html/i.test((mime || "").split(";")[0])) return;
        const fname = (typeof mdmFilenameFromHeaders === "function"
          ? (mdmFilenameFromHeaders(details.url, disposition) || "") : "");
        mdmRememberBinaryUrl(details.url, fname);
      },
      {
        urls: ["http://*/*", "https://*/*"],
        types: ["main_frame", "sub_frame", "xmlhttprequest", "other", "object"]
      },
      ["responseHeaders"]
    );
  } catch (e) {
    console.warn("MDM: Firefox webRequest unavailable", e);
  }
}

// ——— downloads handoff (legacy file) ———
const mdmHandledDownloadIds = new Set();
const mdmPendingFirefoxDownloads = new Set();
const mdmRecentBinaryUrls = [];

function mdmRememberBinaryUrl(url, filename) {
  if (!url || !/^https?:\/\//i.test(url)) return;
  mdmRecentBinaryUrls.unshift({ url, filename: (filename || "").split(/[/\\]/).pop() || "", t: Date.now() });
  if (mdmRecentBinaryUrls.length > 40) mdmRecentBinaryUrls.pop();
}

function mdmResolveDownloadUrl(downloadItem) {
  let url = downloadItem.finalUrl || downloadItem.url || "";
  if (/^https?:\/\//i.test(url) || /^magnet:/i.test(url)) return url;
  const name = (downloadItem.filename || "").split(/[/\\]/).pop() || "";
  const now = Date.now();
  for (const x of mdmRecentBinaryUrls) {
    if (now - x.t > 25000) continue;
    if (name && x.filename && name.toLowerCase() === x.filename.toLowerCase()) return x.url;
  }
  for (const x of mdmRecentBinaryUrls) {
    if (now - x.t > 8000) continue;
    return x.url;
  }
  return "";
}

function mdmReferrerString(v) {
  if (!v) return "";
  if (typeof v === "string") return v;
  try { return String(v.url || v.href || ""); } catch (_) { return ""; }
}

async function mdmCancelBrowserDownload(id) {
  try { await chrome.downloads.cancel(id); } catch (_) {}
  try { await chrome.downloads.erase({ id }); } catch (_) {}
}

async function mdmTakeoverDownload(downloadItem) {
  if (!downloadItem || mdmHandledDownloadIds.has(downloadItem.id)) return false;
  if (downloadItem.byExtensionId && downloadItem.byExtensionId === chrome.runtime.id) return false;

  await mdmDisableBrowserDownloadUi();
  const url = mdmResolveDownloadUrl(downloadItem);
  // Popup'ta devre disi birakilan sitede indirme tarayicida kalir
  if (mdmIsBlockedHost(mdmHostOf(downloadItem.referrer || "") || mdmHostOf(url))) return false;
  if (!url) {
    if (mdmIsFirefox()) mdmPendingFirefoxDownloads.add(downloadItem.id);
    return false;
  }
  if (url.startsWith("blob:") || url.startsWith("data:") || url.startsWith("file:")) {
    if (mdmIsFirefox()) mdmPendingFirefoxDownloads.add(downloadItem.id);
    return false;
  }
  if (!/^https?:\/\//i.test(url) && !/^magnet:/i.test(url)) return false;
  if (mdmIsSessionRestoreReplay(downloadItem)) return false;

  const mimeEarly = downloadItem.mime || "";
  const nameEarly = (downloadItem.filename || "").split(/[/\\]/).pop() || "";
  // YouTube Shorts / site içi video: otomatik takeover yok (MDM butonu ile indirilir)
  if (!mdmShouldAutoTakeoverDownload(url, mimeEarly, nameEarly)) return false;

  // Daha önce MDM kabul ettiyse: kısa süreli kopyayı iptal et; tekrar tıklamada yeniden handoff
  if (mdmWasAccepted(url)) {
    if (mdmIsFreshAcceptedDuplicate(url)) {
      mdmHandledDownloadIds.add(downloadItem.id);
      await mdmCancelBrowserDownload(downloadItem.id);
      return true;
    }
    mdmAcceptedCaptureUrls.delete(url);
  }

  mdmHandledDownloadIds.add(downloadItem.id);
  mdmPendingFirefoxDownloads.delete(downloadItem.id);
  const mime = downloadItem.mime || "";
  let rawFileName = (downloadItem.filename || "").split(/[/\\]/).pop() || "";
  if (!rawFileName || rawFileName === "download") rawFileName = "";
  // Drive vb.: URL'de uzantı yok — mime'dan ipucu
  if (!rawFileName && /rar/i.test(mime)) rawFileName = "download.rar";
  if (!rawFileName && /zip/i.test(mime)) rawFileName = "download.zip";
  if (!rawFileName && /7z/i.test(mime)) rawFileName = "download.7z";
  if (!rawFileName && /pdf/i.test(mime)) rawFileName = "download.pdf";
  const referrer = mdmReferrerString(downloadItem.referrer);
  const headers = {};
  if (referrer) headers.Referer = referrer;
  if (/google\.com|googleusercontent\.com/i.test(url))
    headers.Referer = headers.Referer || "https://drive.google.com/";

  // Önce tarayıcı indirmesini kes; MDM cevabını beklemek dosyayı tarayıcıya bırakıyordu
  await mdmCancelBrowserDownload(downloadItem.id);

  let accepted = false;
  try {
    const cookies = await mdmGetCookies(url);
    accepted = await mdmSendCaptureToDesktop({
      url,
      filename: rawFileName || "download",
      mime,
      pageUrl: referrer || url,
      referrer: headers.Referer || referrer,
      kind: "progressive",
      cookies,
      headers
    });
    if (!accepted) accepted = await mdmHandoffLegacy(url, rawFileName || "download", mime);
  } catch (_) {
    accepted = await mdmHandoffLegacy(url, rawFileName || "download", mime);
  }

  if (accepted) {
    mdmRememberAccepted(url);
    return true;
  }

  mdmHandledDownloadIds.delete(downloadItem.id);
  try {
    await chrome.downloads.download({ url, filename: rawFileName || undefined });
  } catch (_) {}
  return false;
}

chrome.downloads.onCreated.addListener((downloadItem) => {
  if (mdmIsFirefox() && downloadItem && downloadItem.id != null) {
    const u = downloadItem.finalUrl || downloadItem.url || "";
    if (!u || /^blob:/i.test(u)) mdmPendingFirefoxDownloads.add(downloadItem.id);
  }
  mdmTakeoverDownload(downloadItem).catch(() => {});
});

try {
  if (chrome.downloads.onDeterminingFilename) {
    chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
      try { suggest(); } catch (_) {}
      mdmTakeoverDownload(item).catch(() => {});
    });
  }
} catch (_) {}

// Firefox: URL/filename bazen onCreated'da boş; Save As sonrası onChanged gelir
chrome.downloads.onChanged.addListener((delta) => {
  if (!delta || delta.id == null) return;
  if (mdmHandledDownloadIds.has(delta.id)) return;
  const pending = mdmPendingFirefoxDownloads.has(delta.id);
  const interesting = delta.url || delta.filename || delta.state || delta.mime || pending;
  if (!interesting) return;
  try {
    const p = chrome.downloads.search({ id: delta.id });
    Promise.resolve(p).then((items) => {
      const item = Array.isArray(items) ? items[0] : null;
      if (item) mdmTakeoverDownload(item).catch(() => {});
    }).catch(() => {});
  } catch (_) {}
});

// ——— sayfayı tara ———
async function mdmFlashBadge(tab, text, color) {
  const tabId = tab?.id;
  if (tabId == null) return;
  try {
    chrome.action.setBadgeText({ tabId, text });
    chrome.action.setBadgeBackgroundColor({ tabId, color });
  } catch (_) {}
  setTimeout(() => {
    try {
      const blocked = mdmIsBlockedHost(mdmHostOf(tab.url || ""));
      chrome.action.setBadgeText({ tabId, text: blocked ? "X" : "" });
      if (blocked) chrome.action.setBadgeBackgroundColor({ tabId, color: "#666" });
    } catch (_) {}
  }, 3000);
}

/** Sekmedeki medyayı toplayıp masaüstündeki seçim ekranına gönderir. */
async function mdmScanPage(tab) {
  if (!tab || tab.id == null) return { ok: false, reason: "tab" };

  let collected = null;
  try {
    collected = await chrome.tabs.sendMessage(tab.id, { type: "mdm-collect-page" }, { frameId: 0 });
  } catch (_) {
    try { collected = await chrome.tabs.sendMessage(tab.id, { type: "mdm-collect-page" }); }
    catch (_) { collected = null; }
  }

  const items = Array.isArray(collected?.items) ? collected.items : [];
  const payload = {
    pageUrl: collected?.pageUrl || tab.url || "",
    title: collected?.title || tab.title || "",
    items
  };

  const resp = await mdmPostJson("/ext/scan", payload, 8000);
  if (!resp) {
    await mdmFlashBadge(tab, "!", "#E53935");
    return { ok: false, reason: "offline" };
  }
  return { ok: true, count: typeof resp.count === "number" ? resp.count : items.length };
}

// ——— messages ———
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg) return;

  if (msg.type === "mdm-scan-page") {
    (async () => {
      let tab = null;
      if (msg.tabId != null) {
        try { tab = await chrome.tabs.get(msg.tabId); } catch (_) { tab = null; }
      }
      if (!tab) {
        try {
          const list = await chrome.tabs.query({ active: true, currentWindow: true });
          tab = list && list[0] ? list[0] : null;
        } catch (_) { tab = null; }
      }
      sendResponse(await mdmScanPage(tab));
    })();
    return true;
  }

  if (msg.type === "mdm-get-cookies") {
    mdmGetCookies(msg.url || "").then((cookies) => sendResponse({ cookies }));
    return true;
  }

  if (msg.type === "mdm-handoff" && msg.url) {
    mdmCaptureUrl(msg.url, msg.filename || "", sender.tab, sender.tab?.url).then(ok => sendResponse({ ok }));
    return true;
  }

  if (msg.type === "mdm-get-formats") {
    return (async () => {
      const tabId = sender.tab?.id;
      const cookies = await mdmGetCookies(msg.pageUrl || msg.mediaUrl || "");

      if (typeof mdmIsYouTubeUrl === "function" && mdmIsYouTubeUrl(msg.pageUrl)) {
        const result = await mdmFetchFormats({
          pageUrl: msg.pageUrl,
          mediaUrl: "",
          candidates: [],
          playlistBody: "",
          cookies,
          referrer: msg.referrer || msg.pageUrl,
          headers: Object.assign({
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            Referer: msg.referrer || msg.pageUrl || "",
            Origin: (() => { try { return new URL(msg.pageUrl).origin; } catch (_) { return ""; } })()
          }, msg.headers || {}),
          videoMeta: msg.videoMeta || {},
          title: msg.title || ""
        });
        return result || { ok: false, error: mdmErrText("ext.error_unreachable_formats", "MDM'ye ulaşılamadı veya kalite alınamadı") };
      }

      const best = tabId != null ? mdmPickBestMediaCapture(tabId) : null;
      const fromStore = tabId != null ? mdmGetCaptures(tabId) : [];
      const candidates = [];
      const push = (u) => {
        if (u && /^https?:\/\//i.test(u) && !candidates.includes(u)) candidates.push(u);
      };
      for (const u of (msg.candidates || [])) push(u);
      for (const c of fromStore) {
        if (c.kind === "hls" || c.kind === "dash") push(c.finalUrl || c.url);
      }
      if (best) push(best.finalUrl || best.url);

      let mediaUrl = "";
      let playlistBody = "";
      if (best) {
        mediaUrl = best.finalUrl || best.url || "";
        playlistBody = best.body || "";
      }
      const rawMedia = msg.mediaUrl || "";
      if (!mediaUrl && rawMedia && /^https?:\/\//i.test(rawMedia)
          && !/\.(m4s|ts|m2ts)(\?|$)/i.test(rawMedia)) {
        mediaUrl = rawMedia;
        push(mediaUrl);
      }
      if (!mediaUrl) {
        mediaUrl = candidates.find(u => /\/(master|index|playlist)\.(m3u8?|txt)/i.test(u))
          || candidates.find(u => /\.m3u8/i.test(u))
          || candidates.find(u => /\.mpd/i.test(u))
          || candidates.find(u => /\/hls\//i.test(u) && !/sublist/i.test(u))
          || candidates[0]
          || "";
      }

      if (!playlistBody && mediaUrl && (/\.m3u8?|\.mpd|\/(master|index|playlist)\.txt/i.test(mediaUrl) || /\/hls\//i.test(mediaUrl))) {
        try {
          const local = await mdmResolveFormats({
            pageUrl: msg.pageUrl,
            mediaUrl,
            candidates: [mediaUrl],
            cookies,
            headers: { Cookie: cookies, Referer: msg.pageUrl },
            videoMeta: msg.videoMeta || {}
          });
          if (local?.ok && (local.formats || []).filter(f => f.id !== "playing").length > 1) {
            return local;
          }
        } catch (_) {}
      }

      const result = await mdmFetchFormats({
        pageUrl: msg.pageUrl,
        mediaUrl,
        candidates,
        playlistBody,
        cookies,
        referrer: msg.referrer || msg.pageUrl,
        headers: Object.assign({
          "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
          Referer: msg.referrer || msg.pageUrl || "",
          Origin: (() => { try { return new URL(msg.pageUrl).origin; } catch (_) { return ""; } })()
        }, msg.headers || {}),
        videoMeta: msg.videoMeta || {},
        title: msg.title || ""
      });

      if (result?.protected || result?.formats?.some(f => /sample-aes|widevine|playready|drm/i.test((f.label || "") + (f.url || "")))) {
        return { ok: false, protected: true, error: mdmErrText("ext.error_protected", "Bu video korunuyor") };
      }
      return result || { ok: false, error: mdmErrText("ext.error_unreachable_formats", "MDM'ye ulaşılamadı veya kalite alınamadı") };
    })();
  }

  if (msg.type === "mdm-capture-format") {
    return (async () => {
      const f = msg.format || {};
      let kind = f.kind || f.type || "progressive";
      let url = f.url || msg.mediaUrl || "";
      let formatId = f.formatId || f.id || "";
      const pageUrl = msg.pageUrl || "";
      const isYt = typeof mdmIsYouTubeUrl === "function" && mdmIsYouTubeUrl(pageUrl);

      // YouTube: her zaman watch URL + yt-dlp. Film sitelerinde id=best sayfa URL'sine zorlanmaz.
      if (isYt) {
        kind = "yt-dlp";
        url = pageUrl;
        if (!formatId || formatId === "best" || formatId === "playing")
          formatId = "bv*+ba/b";
      } else if (kind === "yt-dlp") {
        if (!url || url.startsWith("blob:")) url = pageUrl || "";
        if (!formatId || formatId === "playing") formatId = "best";
      } else if (!url || url.startsWith("blob:")) {
        url = pageUrl || "";
      }

      // Film: etiket / iç id → best (medya URL kaliteyi taşır)
      if (!isYt && (!formatId || formatId === "playing" || /^\d{3,4}p\d*$/i.test(formatId)
          || /^(prog-|hls-media-|h-|hls-)/i.test(formatId)))
        formatId = "best";

      // Altyazı / junk URL ile indirme gönderme
      if (typeof mdmIsJunkMediaUrl === "function" && mdmIsJunkMediaUrl(url) && !isYt) {
        const alt = (msg.mediaUrl && !mdmIsJunkMediaUrl(msg.mediaUrl)) ? msg.mediaUrl : "";
        if (alt) url = alt;
        else return { ok: false, error: mdmErrText("ext.error_no_media", "Geçerli medya URL'si yakalanmadı — videoyu oynatıp tekrar deneyin") };
      }

      const cookies = await mdmGetCookies(pageUrl || url);
      const headers = Object.assign({
        Referer: pageUrl || "",
        "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
      }, f.headers || {});
      if (!headers.Referer && pageUrl) headers.Referer = pageUrl;
      const ok = await mdmSendCaptureToDesktop({
        url,
        filename: msg.filename || "",
        mime: f.mime || "",
        pageUrl,
        referrer: pageUrl,
        kind,
        formatId,
        title: msg.title || "",
        filesize: f.filesize || 0,
        cookies,
        headers
      });
      return { ok };
    })();
  }

  if (msg.type === "mdm-main-hook") {
    const d = msg.data || {};
    const url = d.url || d.src || "";
    if (!url || sender.tab?.id == null) return;
    const snippet = d.snippet || "";
    if (/METHOD=SAMPLE-AES|widevine|playready/i.test(snippet)) return;

    let kind = "json";
    let body = "";
    let isMaster = false;
    if (snippet && (/#EXTM3U/i.test(snippet) || /<MPD[\s>]/i.test(snippet) || /\.m3u8?|\.mpd/i.test(url))) {
      const ann = mdmAnnotatePlaylistBody(url, snippet);
      kind = ann.kind;
      body = ann.body;
      isMaster = ann.isMaster;
    } else if (/\.m3u8?/i.test(url) || /\/hls\//i.test(url)) kind = "hls";
    else if (/\.mpd/i.test(url) || /\/dash\//i.test(url)) kind = "dash";
    else if ((d.kind === "media-src" || d.kind === "fetch") && /^https?:\/\//i.test(url)
             && (/\.(mp4|webm|m3u8|mpd)/i.test(url) || /\/hls\//i.test(url))) {
      kind = /\/hls\//i.test(url) || /\.m3u8/i.test(url) ? "hls" : (/\.mpd/i.test(url) ? "dash" : "progressive");
    } else if (!snippet && !/\.m3u8?|\.mpd/i.test(url) && !/\/hls\//i.test(url)) {
      return;
    }

    if (!["hls", "dash", "progressive"].includes(kind)) return;

    const cap = {
      id: `hook-${Date.now()}`,
      tabId: sender.tab.id,
      frameId: sender.frameId || 0,
      url,
      finalUrl: url,
      kind,
      body,
      isMaster,
      fromHook: true,
      timeStamp: Date.now()
    };
    mdmUpsertCapture(cap);
    if (kind === "hls" || kind === "dash") {
      chrome.tabs.sendMessage(sender.tab.id, { type: "mdm-media", capture: cap }).catch(() => {});
    }
    return;
  }
  if (msg.type === "mdm-content-ready") {
    mdmPingDesktop().then(() => {
      mdmBroadcastDesktopStatus();
      sendResponse({ online: mdmIsDesktopOnline() });
    });
    return true;
  }

  if (msg.type === "mdm-ping-desktop") {
    mdmPingDesktop().then((ok) => {
      sendResponse({ online: !!ok || mdmIsDesktopOnline() });
    });
    return true;
  }
});

// ——— tab lifecycle ———
chrome.tabs.onRemoved.addListener((tabId) => mdmClearTab(tabId));
chrome.webNavigation?.onHistoryStateUpdated?.addListener?.((details) => {
  if (details.frameId === 0) mdmClearTab(details.tabId);
});

// ——— action ———
// Arac cubugu simgesi popup.html acar; site devre disi birakma popup'tan yonetilir.

// ——— init ———
mdmDisableBrowserDownloadUi();
mdmInitMenus(mdmCaptureUrl, mdmScanPage);
try { chrome.alarms.create("mdm-presence", { periodInMinutes: 1 }); } catch (_) {}

chrome.runtime.onInstalled.addListener(() => {
  mdmDisableBrowserDownloadUi();
  mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
  try { chrome.alarms.create("mdm-presence", { periodInMinutes: 1 }); } catch (_) {}
});

chrome.runtime.onStartup.addListener(() => {
  mdmStartupGuardUntil = Date.now() + MDM_STARTUP_GUARD_MS;
  mdmDisableBrowserDownloadUi();
  mdmPingDesktop();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm?.name === "mdm-presence") {
    mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
  }
});

// Sekme degisiminde de yoklama: uygulamada dil/durum degisince 1 dakikalik
// alarmi beklemek yerine kullanici sekmeye dondugunde guncellenir.
let mdmTabPingAt = 0;
try {
  chrome.tabs.onActivated.addListener(() => {
    const now = Date.now();
    if (now - mdmTabPingAt < 10000) return;
    mdmTabPingAt = now;
    mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
  });
} catch (_) { /* ignore */ }

mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
