// MDM — content script: video tracker + overlay + kalite paneli
// ÖNEMLİ: Sayfa context'inden asla 127.0.0.1'e fetch yapma —
// Chrome/Edge her sitede «cihazdaki uygulamalara erişim» izni sorar (Local Network Access).
(function () {
  if (window.__mdmContent) return;
  window.__mdmContent = true;

  let elementCounter = 0;
  const videoMap = new WeakMap();
  const tracked = new Map(); // id -> { el, update }
  /** @type {Map<string, object[]>} sniff masters keyed by page */
  const pageMasters = new Map();
  let captureEnabled = true;
  let desktopOnline = false;
  let ytDlpNoteShown = false;
  let contextDead = false;
  // popup ayarlari (mdmPrefs): dugme kapatilabilir veya site devre disi olabilir
  let overlayAllowed = true;
  let overlayCleared = false;
  const isTopFrame = (() => { try { return window === window.top; } catch (_) { return true; } })();

  function t(key, ...args) {
    try {
      if (typeof mdmI18n !== "undefined" && mdmI18n.t) return mdmI18n.t(key, ...args);
    } catch (_) {}
    return key;
  }

  function nextId() {
    return `v${++elementCounter}`;
  }

  function pageKey() {
    try { return location.origin + location.pathname; } catch (_) { return location.href; }
  }

  function runtimeOk() {
    try {
      return !!(typeof chrome !== "undefined" && chrome.runtime && chrome.runtime.id);
    } catch (_) {
      return false;
    }
  }

  /** chrome.runtime.sendMessage — senkron throw (context invalidated) yakalar */
  function safeSend(msg) {
    try {
      if (!runtimeOk()) {
        contextDead = true;
        return Promise.resolve(null);
      }
      const p = chrome.runtime.sendMessage(msg);
      return Promise.resolve(p).catch(() => {
        contextDead = true;
        return null;
      });
    } catch (_) {
      contextDead = true;
      return Promise.resolve(null);
    }
  }

  function rememberMaster(capture) {
    if (!capture || !capture.url) return;
    const kind = capture.kind || "";
    if (kind !== "hls" && kind !== "dash") return;
    const key = pageKey();
    const list = pageMasters.get(key) || [];
    const norm = capture.url.split("#")[0];
    const existing = list.findIndex(c => (c.url || "").split("#")[0] === norm);
    const entry = {
      url: capture.url,
      kind,
      body: capture.body || "",
      isMaster: !!capture.isMaster,
      timeStamp: Date.now(),
      cookies: capture.cookies || ""
    };
    if (existing >= 0) {
      list[existing] = { ...list[existing], ...entry, body: entry.body || list[existing].body || "" };
    } else {
      list.push(entry);
      if (list.length > 24) list.shift();
    }
    pageMasters.set(key, list);
  }

  function mastersForPage() {
    const list = pageMasters.get(pageKey()) || [];
    const now = Date.now();
    return list.filter(c => now - (c.timeStamp || 0) < 90000);
  }

  function registerMedia(el) {
    if (videoMap.has(el)) return videoMap.get(el);
    const id = nextId();
    videoMap.set(el, id);
    observeMedia(el, id);
    return id;
  }

  function pickPrimaryVideoId() {
    let bestId = null;
    let bestArea = 0;
    for (const [id, { el }] of tracked) {
      if (!el || !el.isConnected || !isVisible(el)) continue;
      const r = el.getBoundingClientRect();
      const area = r.width * r.height;
      // Küçük preview / ads video'larını ele
      if (area < 120 * 80) continue;
      if (area > bestArea) {
        bestArea = area;
        bestId = id;
      }
    }
    return bestId;
  }

  function pruneTracked() {
    for (const [id, entry] of [...tracked.entries()]) {
      if (!entry || !entry.el || !entry.el.isConnected) {
        tracked.delete(id);
        if (window.mdmOverlayApi) window.mdmOverlayApi.remove(id);
      }
    }
  }

  function refreshAll() {
    if (contextDead && !runtimeOk()) return;
    if (!overlayAllowed) {
      // Devre disi sitede tek seferlik temizlik — periyodik DOM taramasi yapma
      if (!overlayCleared) {
        overlayCleared = true;
        if (window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
      }
      return;
    }
    overlayCleared = false;
    pruneTracked();
    if (!isTopFrame) {
      // iframe: overlay yok (çift buton önleme); sniff devam eder
      if (window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
      return;
    }
    const primary = pickPrimaryVideoId();
    for (const [id, { el, update }] of tracked) {
      if (id === primary && el.isConnected) update();
      else if (window.mdmOverlayApi) window.mdmOverlayApi.remove(id);
    }
  }

  function onSpaNavigate() {
    try { pageMasters.delete(pageKey()); } catch (_) {}
    pruneTracked();
    if (window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
    scanMedia();
    refreshAll();
  }

  function isVisible(el) {
    if (!el || !el.isConnected) return false;
    const r = el.getBoundingClientRect();
    if (r.width < 80 || r.height < 45) return false;
    const st = getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || parseFloat(st.opacity) === 0) return false;
    return true;
  }

  function bboxOf(el) {
    const r = el.getBoundingClientRect();
    return { left: r.left, top: r.top, width: r.width, height: r.height, dpr: window.devicePixelRatio || 1 };
  }

  function scanMedia() {
    if (!overlayAllowed) return;
    document.querySelectorAll("video, audio").forEach((el) => registerMedia(el));
  }

  /** Sayfayı tara: DOM'daki görsel/video/ses/dosya adaylarını toplar (masaüstü ekranı için) */
  function collectPageCandidates() {
    const out = [];
    const seen = new Set();
    const MAX = 800;

    function push(raw, kind) {
      if (out.length >= MAX) return;
      const value = String(raw || "").trim();
      if (!value || /^(data|blob|javascript|about):/i.test(value)) return;
      let url = "";
      try { url = new URL(value, location.href).href; } catch (_) { return; }
      if (!/^https?:/i.test(url)) return;
      const key = url.split("#")[0];
      if (seen.has(key)) return;
      seen.add(key);
      out.push({ url, kind: kind || "" });
    }

    function pushSrcSet(value, kind) {
      String(value || "").split(",").forEach((part) => {
        const first = part.trim().split(/\s+/)[0];
        push(first, kind);
      });
    }

    function pushBackground(el) {
      let image = "";
      try { image = getComputedStyle(el).backgroundImage || ""; } catch (_) { return; }
      if (!image || image === "none") return;
      const rx = /url\((['"]?)(.*?)\1\)/g;
      let m;
      while ((m = rx.exec(image)) !== null) push(m[2], "image");
    }

    try {
      document.querySelectorAll("img").forEach((img) => {
        push(img.currentSrc || img.getAttribute("src"), "image");
        pushSrcSet(img.getAttribute("srcset"), "image");
        ["data-src", "data-original", "data-lazy", "data-lazy-src", "data-full", "data-image"]
          .forEach((attr) => push(img.getAttribute(attr), "image"));
        pushSrcSet(img.getAttribute("data-srcset"), "image");
      });

      document.querySelectorAll("picture source, source[type^='image']").forEach((s) => {
        push(s.getAttribute("src"), "image");
        pushSrcSet(s.getAttribute("srcset"), "image");
      });

      document.querySelectorAll("video").forEach((v) => {
        push(v.currentSrc || v.getAttribute("src"), "video");
        push(v.getAttribute("poster"), "image");
      });
      document.querySelectorAll("video source").forEach((s) => push(s.getAttribute("src"), "video"));

      document.querySelectorAll("audio").forEach((a) => push(a.currentSrc || a.getAttribute("src"), "audio"));
      document.querySelectorAll("audio source").forEach((s) => push(s.getAttribute("src"), "audio"));

      // Sniff edilen HLS/DASH master'ları da listeye girsin
      mastersForPage().forEach((c) => push(c.url, "video"));

      // Bağlantılar: uzantısı tanınanları masaüstü tarafı seçer
      document.querySelectorAll("a[href]").forEach((a) => push(a.getAttribute("href"), ""));

      // Yalnızca inline background taşıyan öğeler — tüm ağacı taramak pahalı
      document.querySelectorAll("[style*='background']").forEach(pushBackground);
    } catch (_) { /* kısmi sonuç yeter */ }

    return out;
  }

  /** "www.a.com" -> "a.com" */
  function bareHost(value) {
    return String(value || "").trim().toLowerCase().replace(/^www\./, "");
  }

  function applyPrefs(raw) {
    const p = raw || {};
    const host = (() => { try { return bareHost(location.hostname); } catch (_) { return ""; } })();
    const blocked = host && Array.isArray(p.blocked)
      ? p.blocked.some((b) => {
        const entry = bareHost(b);
        return entry && (host === entry || host.endsWith("." + entry));
      })
      : false;

    overlayAllowed = p.overlay !== false && !blocked;
    if (window.mdmOverlayApi) {
      window.mdmOverlayApi.setTheme(p.theme === "light" ? "light" : "dark");
      if (!overlayAllowed) {
        overlayCleared = true;
        window.mdmOverlayApi.removeAll();
      }
    }
    if (overlayAllowed) {
      overlayCleared = false;
      scanMedia();
    }
    refreshAll();
  }

  function loadPrefs() {
    try {
      Promise.resolve(chrome.storage.local.get("mdmPrefs"))
        .then((d) => applyPrefs(d && d.mdmPrefs))
        .catch(() => { /* ignore */ });
    } catch (_) { /* ignore */ }
  }

  function observeMedia(el, id) {
    const update = () => {
      if (!captureEnabled) return;
      if (!overlayAllowed) {
        if (window.mdmOverlayApi) window.mdmOverlayApi.remove(id);
        return;
      }
      // iframe içinde overlay gösterme (üst frame tek buton)
      if (!isTopFrame) {
        if (window.mdmOverlayApi) window.mdmOverlayApi.remove(id);
        return;
      }
      const primary = pickPrimaryVideoId();
      if (primary && primary !== id) {
        if (window.mdmOverlayApi) window.mdmOverlayApi.remove(id);
        return;
      }
      const vis = isVisible(el);
      const bb = bboxOf(el);
      if (vis && window.mdmOverlayApi) {
        window.mdmOverlayApi.update(id, bb, desktopOnline, {
          onOpen: () => openPanel(id, el)
        });
      } else if (window.mdmOverlayApi) {
        window.mdmOverlayApi.remove(id);
      }
    };
    tracked.set(id, { el, update });
    update();
    try {
      const io = new IntersectionObserver(() => update(), { threshold: [0, 0.15, 0.5] });
      io.observe(el);
      const ro = new ResizeObserver(() => update());
      ro.observe(el);
    } catch (_) {}
    el.addEventListener("loadedmetadata", update);
    el.addEventListener("play", update);
    document.addEventListener("scroll", update, true);
    document.addEventListener("fullscreenchange", update);
  }

  function sanitizeTitle(t) {
    return String(t || "video").replace(/[<>:"/\\|?*\x00-\x1f]/g, "_").trim().slice(0, 120) || "video";
  }

  function isYouTubePage() {
    try {
      const h = location.hostname.toLowerCase();
      return h.includes("youtube.com") || h.includes("youtu.be") || h.includes("youtube-nocookie.com");
    } catch (_) { return false; }
  }

  async function pingDesktopViaBg() {
    const r = await safeSend({ type: "mdm-ping-desktop" });
    if (r && typeof r.online === "boolean") {
      desktopOnline = r.online;
      return r.online;
    }
    return desktopOnline;
  }

  async function fetchYouTubeFormats(pageUrl, el) {
    const payload = {
      type: "mdm-get-formats",
      pageUrl,
      mediaUrl: "",
      candidates: [],
      referrer: document.referrer || pageUrl,
      videoMeta: {
        videoWidth: el.videoWidth || 0,
        videoHeight: el.videoHeight || 0,
        duration: el.duration || 0
      },
      title: document.title || ""
    };

    // Yalnızca background (host_permissions) — sayfadan localhost = her sitede izin diyaloğu
    return await safeSend(payload);
  }

  async function openPanel(id, el) {
    if (!window.mdmOverlayApi) return;

    if (contextDead || !runtimeOk()) {
      // Bağlam ölü olsa bile YouTube'da doğrudan masaüstünü dene
      if (!isYouTubePage()) {
        window.mdmOverlayApi.showPanel(id, {
          title: t("ext.panel_title"),
          error: t("ext.error_reload")
        });
        return;
      }
    }

    if (!desktopOnline) {
      desktopOnline = await pingDesktopViaBg();
    }

    if (!desktopOnline && !isYouTubePage()) {
      window.mdmOverlayApi.showPanel(id, {
        title: t("ext.panel_title"),
        error: t("ext.error_start_app")
      });
      return;
    }

    window.mdmOverlayApi.showPanel(id, {
      title: document.title || t("ext.panel_title"),
      loading: true,
      formats: []
    });

    const yt = isYouTubePage();
    if (!yt && mastersForPage().length === 0) {
      await new Promise(r => setTimeout(r, 450));
    }

    const pageUrl = location.href;
    const mediaUrl = yt ? "" : (el.currentSrc || el.src || "");
    const candidates = yt ? [] : mastersForPage().map(c => c.url);

    let resp = null;
    if (yt) {
      resp = await fetchYouTubeFormats(pageUrl, el);
    } else {
      resp = await safeSend({
        type: "mdm-get-formats",
        pageUrl,
        mediaUrl,
        candidates,
        referrer: document.referrer || pageUrl,
        videoMeta: {
          videoWidth: el.videoWidth || 0,
          videoHeight: el.videoHeight || 0,
          duration: el.duration || 0
        },
        title: document.title || ""
      });
    }

    if (!resp || !resp.ok) {
      if (yt) {
        let err = resp?.error || t("ext.error_formats");
        if (!runtimeOk()) err = t("ext.error_reload");
        else if (!resp) err = t("ext.error_unreachable");
        window.mdmOverlayApi.showPanel(id, { title: t("ext.panel_title"), error: err });
        return;
      }
      if (!runtimeOk()) {
        window.mdmOverlayApi.showPanel(id, {
          title: t("ext.panel_title"),
          error: t("ext.error_reload")
        });
        return;
      }
      const h = el.videoHeight || 0;
      const hasMasters = mastersForPage().length > 0;
      if (!hasMasters && (mediaUrl.startsWith("blob:") || !mediaUrl)) {
        window.mdmOverlayApi.showPanel(id, {
          title: t("ext.panel_title"),
          error: t("ext.error_wait_play")
        });
        return;
      }
      const fallback = [{
        id: "playing",
        label: h ? t("ext.playing", h) : t("ext.video"),
        height: h || null,
        url: /^https?:\/\//i.test(mediaUrl) ? mediaUrl : pageUrl,
        kind: "progressive",
        type: "progressive",
        formatId: ""
      }];
      if (resp?.protected) {
        window.mdmOverlayApi.showPanel(id, { title: t("ext.panel_title"), error: t("ext.error_protected") });
        return;
      }
      let note;
      if (resp?.ytDlpSuggested && !ytDlpNoteShown) {
        ytDlpNoteShown = true;
        note = t("ext.note_ytdlp");
      }
      window.mdmOverlayApi.showPanel(id, {
        title: document.title || t("ext.panel_title"),
        formats: fallback,
        note,
        onPick: (f) => pickFormat(f, pageUrl, mediaUrl, document.title)
      });
      return;
    }

    if (resp.protected) {
      window.mdmOverlayApi.showPanel(id, { title: t("ext.panel_title"), error: t("ext.error_protected") });
      return;
    }

    const formats = resp.formats || [];
    let note;
    if (resp.ytDlpSuggested && !ytDlpNoteShown) {
      ytDlpNoteShown = true;
      note = t("ext.note_ytdlp");
    }

    window.mdmOverlayApi.showPanel(id, {
      title: resp.title || document.title || t("ext.panel_title"),
      formats,
      note,
      onPick: (f) => pickFormat(f, pageUrl, mediaUrl, resp.title || document.title)
    });
  }

  function isJunkMediaUrl(url) {
    const u = (url || "").toLowerCase();
    if (!u) return true;
    if (/\/txt\/sublist/i.test(u)) return true;
    if (/\/(sub|subs|subtitle|subtitles|caption|captions|thumb|thumbnail|poster)s?\//i.test(u)) return true;
    if (/\.(vtt|srt|ass|ssa|dfxp|ttml)(\?|$)/i.test(u)) return true;
    if (/\/(master|index|playlist|manifest|hls)\.(m3u8?|txt)(\?|$)/i.test(u)) return false;
    if (/\/hls\/.+\.txt(\?|$)/i.test(u) && !/\.m3u8?/i.test(u)) return true;
    return false;
  }

  function classifyMediaUrl(url) {
    if (!url) return "other";
    const lower = url.toLowerCase();
    if (isJunkMediaUrl(url)) return "other";
    if (/\.m3u8?(\?|$)/i.test(lower) || /\/(master|index|playlist|manifest|hls)\.txt(\?|$)/i.test(lower)
        || /\/hls\//i.test(lower) || /\/hls\?/i.test(lower)) return "hls";
    if (/\.mpd(\?|$)/i.test(lower) || /\/dash\//i.test(lower)) return "dash";
    if (/\.(mp4|webm|mkv|mov|m4v)(\?|$)/i.test(lower)) return "progressive";
    return "other";
  }

  async function pickFormat(f, pageUrl, mediaUrl, title) {
    const label = f.label || f.id || "video";
    const yt = isYouTubePage() || (f.kind || f.type) === "yt-dlp";
    let url = yt ? pageUrl : (f.url || mediaUrl || pageUrl);
    let kind = yt ? "yt-dlp" : (f.kind || f.type || "progressive");
    let formatId = f.formatId || f.id || (yt ? "bv*+ba/b" : "");

    // Film: "best" sayfa HTML'ine düşmesin; medya URL + kind koru
    if (!yt && (f.id === "best" || formatId === "best")) {
      url = f.url || mediaUrl || url;
      if (!kind || kind === "progressive") {
        const cls = classifyMediaUrl(url);
        kind = cls === "other" ? (/\/hls\/|\.m3u8/i.test(url || "") ? "hls" : "progressive") : cls;
      }
      formatId = (f.formatId && f.formatId !== "best" && f.formatId !== "playing")
        ? f.formatId
        : "best";
    }

    // Film/HLS: "720p" / "h-720-…" yt-dlp format id değil — URL zaten seçili kalite
    if (!yt) {
      if (!formatId || formatId === "playing" || /^\d{3,4}p\d*$/i.test(formatId)
          || /^(prog-|hls-media-|h-|hls-)/i.test(formatId))
        formatId = "best";
    }

    if (!yt && isJunkMediaUrl(url)) {
      const alt = (mediaUrl && !isJunkMediaUrl(mediaUrl)) ? mediaUrl
        : (f.url && !isJunkMediaUrl(f.url) ? f.url : "");
      if (alt) url = alt;
      else {
        if (window.mdmOverlayApi)
          window.mdmOverlayApi.showToast?.(t("ext.error_no_media"));
        return;
      }
    }

    // HTML sayfa URL'si progressive olarak inmesin
    if (!yt && kind === "progressive" && /\.(html?|php|aspx?)(\?|$)/i.test(url || "")) {
      if (mediaUrl && !isJunkMediaUrl(mediaUrl) && classifyMediaUrl(mediaUrl) !== "other") {
        url = mediaUrl;
        kind = classifyMediaUrl(mediaUrl);
      }
    }

    const format = Object.assign({}, f, yt ? { kind: "yt-dlp", url: pageUrl } : { kind, url });
    const payload = {
      url: yt ? pageUrl : url,
      filename: `${sanitizeTitle(title)} - ${sanitizeTitle(label)}.mp4`,
      mime: f.mime || "",
      pageUrl,
      referrer: pageUrl,
      kind: yt ? "yt-dlp" : kind,
      formatId: yt ? (formatId || "bv*+ba/b") : (formatId === "playing" ? "best" : formatId),
      title: title || document.title || "",
      filesize: f.filesize || 0,
      cookies: "",
      headers: Object.assign({ Referer: pageUrl }, f.headers || {})
    };

    try { payload.cookies = document.cookie || ""; } catch (_) {}
    const fromBg = await safeSend({ type: "mdm-get-cookies", url: pageUrl });
    if (fromBg?.cookies) payload.cookies = fromBg.cookies;

    await safeSend({
      type: "mdm-capture-format",
      format,
      pageUrl,
      mediaUrl: yt ? pageUrl : (f.url || mediaUrl || pageUrl),
      title: title || document.title,
      filename: payload.filename
    });
  }

  window.addEventListener("message", (ev) => {
    if (ev.source !== window || !ev.data || ev.data.source !== "mdm-main") return;
    const d = ev.data;
    if (d.url && (/\.m3u8/i.test(d.url) || /\.mpd/i.test(d.url) || /\/hls\//i.test(d.url)
        || /\/(master|index|playlist)\.txt/i.test(d.url)
        || /#EXTM3U/i.test(d.snippet || ""))) {
      rememberMaster({
        url: d.url,
        kind: /\.mpd/i.test(d.url) ? "dash" : "hls",
        timeStamp: Date.now()
      });
    }
    safeSend({
      type: "mdm-main-hook",
      data: d,
      pageUrl: location.href,
      referrer: document.referrer
    });
  });

  try {
    chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      if (msg?.type === "mdm-collect-page") {
        try {
          sendResponse({
            items: collectPageCandidates(),
            pageUrl: location.href,
            title: document.title || ""
          });
        } catch (_) {
          sendResponse({ items: [], pageUrl: location.href, title: "" });
        }
        return true;
      }
      if (msg?.type === "mdm-rescan") scanMedia();
      if (msg?.type === "mdm-set-enabled") {
        captureEnabled = msg.enabled !== false;
        if (!captureEnabled && window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
        else scanMedia();
      }
      if (msg?.type === "mdm-desktop-status") {
        desktopOnline = !!msg.online;
        refreshAll();
      }
      if (msg?.type === "mdm-media" && msg.capture) {
        rememberMaster(msg.capture);
      }
      if (msg?.type === "mdm-nav-clear") {
        pageMasters.delete(pageKey());
      }
    });
  } catch (_) {
    contextDead = true;
  }

  try {
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area !== "local" || !changes || !changes.mdmPrefs) return;
      applyPrefs(changes.mdmPrefs.newValue);
    });
  } catch (_) { /* ignore */ }

  loadPrefs();
  setInterval(refreshAll, 400);
  window.addEventListener("resize", refreshAll);
  window.addEventListener("load", scanMedia);
  window.addEventListener("popstate", onSpaNavigate);
  window.addEventListener("yt-navigate-start", () => {
    if (window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
  });
  window.addEventListener("yt-navigate-finish", onSpaNavigate);
  document.addEventListener("yt-navigate-finish", onSpaNavigate);
  // YouTube soft nav bazen sadece history API kullanır
  try {
    const _push = history.pushState;
    history.pushState = function () {
      const r = _push.apply(this, arguments);
      setTimeout(onSpaNavigate, 50);
      return r;
    };
    const _replace = history.replaceState;
    history.replaceState = function () {
      const r = _replace.apply(this, arguments);
      setTimeout(onSpaNavigate, 50);
      return r;
    };
  } catch (_) {}

  const mo = new MutationObserver(() => scanMedia());
  mo.observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", scanMedia);
  } else {
    scanMedia();
  }

  safeSend({ type: "mdm-content-ready" }).then((r) => {
    if (r && typeof r.online === "boolean") {
      desktopOnline = r.online;
      refreshAll();
    }
  });
})();
