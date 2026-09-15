// MDM — service worker orchestrator
importScripts("classifier.js", "capture-store.js", "formats.js", "desktop.js", "menus.js");

const MDM_STARTUP_GUARD_MS = 45000;
const MDM_MAX_FRESH_AGE_MS = 12000;
let mdmStartupGuardUntil = 0;
let mdmCaptureEnabled = true;
const mdmDisabledTabs = new Set();

async function mdmDisableBrowserDownloadUi() {
  try { if (chrome.downloads.setShelfEnabled) chrome.downloads.setShelfEnabled(false); } catch (_) {}
  try { if (chrome.downloads.setUiOptions) await chrome.downloads.setUiOptions({ enabled: false }); } catch (_) {}
}

function mdmIsSessionRestoreReplay(downloadItem) {
  const now = Date.now();
  const started = downloadItem.startTime ? Date.parse(downloadItem.startTime) : NaN;
  const ageMs = Number.isNaN(started) ? 0 : now - started;
  if (downloadItem.state === "interrupted" || downloadItem.paused === true) return true;
  if ((downloadItem.bytesReceived || 0) > 0 && ageMs > 3000) return true;
  if (ageMs > MDM_MAX_FRESH_AGE_MS) return true;
  if (now < mdmStartupGuardUntil && ageMs > 2500) return true;
  return false;
}

async function mdmSendCaptureToDesktop(payload) {
  if (!mdmCaptureEnabled) return false;
  const ok = await mdmExtCapture(payload);
  if (ok) mdmMarkHandoff(payload.url || payload.pageUrl || "");
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
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map(c => `${c.name}=${c.value}`).join("; ");
  } catch (_) { return ""; }
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
    { urls: ["http://*/*", "https://*/*"], types: ["xmlhttprequest", "media", "object", "other", "sub_frame"] },
    ["responseHeaders"]
  );
} catch (e) {
  console.warn("MDM: webRequest unavailable", e);
}

// ——— downloads handoff (legacy file) ———
chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (downloadItem.byExtensionId === chrome.runtime.id) return;
  await mdmDisableBrowserDownloadUi();
  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url) && !/^magnet:/i.test(url)) return;
  if (url.startsWith("blob:") || url.startsWith("data:")) return;
  if (mdmIsSessionRestoreReplay(downloadItem)) return;
  if (mdmShouldSkipHandoff(url)) {
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) {}
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) {}
    return;
  }
  const rawFileName = (downloadItem.filename || "").split(/[/\\]/).pop() || "download";
  const accepted = await mdmHandoffLegacy(url, rawFileName, downloadItem.mime || "");
  if (accepted) {
    mdmMarkHandoff(url);
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) {}
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) {}
  }
});

// ——— messages ———
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg) return;

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
        return result || { ok: false, error: "MDM'ye ulaşılamadı veya kalite alınamadı" };
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
        return { ok: false, protected: true, error: "Bu video korunuyor" };
      }
      return result || { ok: false, error: "MDM'ye ulaşılamadı veya kalite alınamadı" };
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
        else return { ok: false, error: "Geçerli medya URL'si yakalanmadı — videoyu oynatıp tekrar deneyin" };
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
});

// ——— tab lifecycle ———
chrome.tabs.onRemoved.addListener((tabId) => mdmClearTab(tabId));
chrome.webNavigation?.onHistoryStateUpdated?.addListener?.((details) => {
  if (details.frameId === 0) mdmClearTab(details.tabId);
});

// ——— action toggle ———
chrome.action.onClicked.addListener(async (tab) => {
  if (!tab.id) return;
  if (mdmDisabledTabs.has(tab.id)) {
    mdmDisabledTabs.delete(tab.id);
    chrome.action.setBadgeText({ tabId: tab.id, text: "" });
  } else {
    mdmDisabledTabs.add(tab.id);
    chrome.action.setBadgeText({ tabId: tab.id, text: "X" });
    chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: "#666" });
  }
  const enabled = !mdmDisabledTabs.has(tab.id);
  chrome.tabs.sendMessage(tab.id, { type: "mdm-set-enabled", enabled }).catch(() => {});
});

// ——— init ———
mdmDisableBrowserDownloadUi();
mdmInitMenus(mdmCaptureUrl);

chrome.runtime.onInstalled.addListener(() => {
  mdmDisableBrowserDownloadUi();
  mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
  chrome.alarms.create("mdm-presence", { periodInMinutes: 1 });
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

mdmPingDesktop().then(() => mdmBroadcastDesktopStatus());
