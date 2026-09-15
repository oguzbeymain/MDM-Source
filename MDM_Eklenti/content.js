// MDM — content script: video tracker + overlay + kalite paneli
(function () {
  if (window.__mdmContent) return;
  window.__mdmContent = true;

  const MDM_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];

  let elementCounter = 0;
  const videoMap = new WeakMap();
  const tracked = new Map(); // id -> { el, update }
  /** @type {Map<string, object[]>} sniff masters keyed by page */
  const pageMasters = new Map();
  let captureEnabled = true;
  let desktopOnline = false;
  let ytDlpNoteShown = false;
  let contextDead = false;

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

  function refreshAll() {
    if (contextDead && !runtimeOk()) return;
    for (const { el, update } of tracked.values()) {
      if (el.isConnected) update();
    }
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
    document.querySelectorAll("video, audio").forEach((el) => registerMedia(el));
  }

  function observeMedia(el, id) {
    const update = () => {
      if (!captureEnabled) return;
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
    tracked.set(id, { el, update });
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

  async function orderedPorts() {
    const ports = MDM_PORTS.slice();
    try {
      if (runtimeOk()) {
        const data = await chrome.storage.local.get("mdmPort");
        if (data.mdmPort) {
          const p = Number(data.mdmPort);
          return [p, ...ports.filter(x => x !== p)];
        }
      }
    } catch (_) {}
    return ports;
  }

  async function findDesktopBase() {
    for (const port of await orderedPorts()) {
      const ctrl = new AbortController();
      const t = setTimeout(() => ctrl.abort(), 700);
      try {
        const r = await fetch(`http://127.0.0.1:${port}/ext/ping`, { signal: ctrl.signal });
        if (r.ok) {
          try {
            if (runtimeOk()) await chrome.storage.local.set({ mdmPort: port });
          } catch (_) {}
          return `http://127.0.0.1:${port}`;
        }
      } catch (_) {}
      finally { clearTimeout(t); }
    }
    return null;
  }

  async function postDesktop(path, payload, timeoutMs) {
    const base = await findDesktopBase();
    if (!base) return null;
    const ctrl = new AbortController();
    const t = setTimeout(() => ctrl.abort(), timeoutMs);
    try {
      const r = await fetch(`${base}${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: JSON.stringify(payload),
        signal: ctrl.signal
      });
      if (!r.ok) return null;
      const ct = r.headers.get("content-type") || "";
      if (ct.includes("json")) return await r.json();
      return { ok: true };
    } catch (_) {
      return null;
    } finally {
      clearTimeout(t);
    }
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

    // Önce service worker (host_permissions) — sayfa origin'inden localhost PNA engeline takılmaz
    let resp = await safeSend(payload);
    if (resp && (resp.ok || resp.error)) return resp;

    // Yedek: doğrudan masaüstü (PNA header'lı sunucu)
    let cookies = "";
    try { cookies = document.cookie || ""; } catch (_) {}
    const fromBg = await safeSend({ type: "mdm-get-cookies", url: pageUrl });
    if (fromBg && fromBg.cookies) cookies = fromBg.cookies;

    resp = await postDesktop("/ext/formats", {
      pageUrl,
      mediaUrl: "",
      candidates: [],
      playlistBody: "",
      cookies,
      referrer: document.referrer || pageUrl,
      headers: {
        "User-Agent": navigator.userAgent || "",
        Referer: pageUrl,
        Origin: location.origin
      },
      videoMeta: payload.videoMeta,
      title: payload.title
    }, 30000);
    return resp;
  }

  async function openPanel(id, el) {
    if (!window.mdmOverlayApi) return;

    if (contextDead || !runtimeOk()) {
      // Bağlam ölü olsa bile YouTube'da doğrudan masaüstünü dene
      if (!isYouTubePage()) {
        window.mdmOverlayApi.showPanel(id, {
          title: "Video indir",
          error: "Eklenti güncellendi — bu sekmeyi yenileyin (F5)"
        });
        return;
      }
    }

    if (!desktopOnline) {
      const base = await findDesktopBase();
      desktopOnline = !!base;
    }

    if (!desktopOnline && !isYouTubePage()) {
      window.mdmOverlayApi.showPanel(id, {
        title: "Video indir",
        error: "MDM uygulamasını başlatın"
      });
      return;
    }

    window.mdmOverlayApi.showPanel(id, {
      title: document.title || "Video indir",
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
        let err = resp?.error || "Kalite listesi alınamadı";
        if (!runtimeOk()) err = "Eklenti güncellendi — sekmeyi yenileyin (F5)";
        else if (!resp) err = "MDM'ye ulaşılamadı — uygulama açık mı?";
        window.mdmOverlayApi.showPanel(id, { title: "Video indir", error: err });
        return;
      }
      if (!runtimeOk()) {
        window.mdmOverlayApi.showPanel(id, {
          title: "Video indir",
          error: "Eklenti güncellendi — sekmeyi yenileyin (F5)"
        });
        return;
      }
      const h = el.videoHeight || 0;
      const hasMasters = mastersForPage().length > 0;
      if (!hasMasters && (mediaUrl.startsWith("blob:") || !mediaUrl)) {
        window.mdmOverlayApi.showPanel(id, {
          title: "Video indir",
          error: "Kalite henüz yakalanmadı — videoyu oynatıp tekrar deneyin"
        });
        return;
      }
      const fallback = [{
        id: "playing",
        label: h ? `Oynayan · ${h}p` : "Video",
        height: h || null,
        url: /^https?:\/\//i.test(mediaUrl) ? mediaUrl : pageUrl,
        kind: "progressive",
        type: "progressive",
        formatId: ""
      }];
      if (resp?.protected) {
        window.mdmOverlayApi.showPanel(id, { title: "Video indir", error: "Bu video korunuyor" });
        return;
      }
      let note;
      if (resp?.ytDlpSuggested && !ytDlpNoteShown) {
        ytDlpNoteShown = true;
        note = "Tam kalite listesi için yt-dlp önerilir";
      }
      window.mdmOverlayApi.showPanel(id, {
        title: document.title || "Video indir",
        formats: fallback,
        note,
        onPick: (f) => pickFormat(f, pageUrl, mediaUrl, document.title)
      });
      return;
    }

    if (resp.protected) {
      window.mdmOverlayApi.showPanel(id, { title: "Video indir", error: "Bu video korunuyor" });
      return;
    }

    const formats = resp.formats || [];
    let note;
    if (resp.ytDlpSuggested && !ytDlpNoteShown) {
      ytDlpNoteShown = true;
      note = "Tam kalite listesi için yt-dlp önerilir";
    }

    window.mdmOverlayApi.showPanel(id, {
      title: resp.title || document.title || "Video indir",
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
          window.mdmOverlayApi.showToast?.("Geçerli medya URL'si yok — videoyu oynatıp tekrar deneyin");
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

    // Doğrudan masaüstü capture
    const ok = await postDesktop("/ext/capture", payload, 5000);
    if (ok) return;

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
    chrome.runtime.onMessage.addListener((msg) => {
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

  setInterval(refreshAll, 400);
  window.addEventListener("resize", refreshAll);
  window.addEventListener("load", scanMedia);
  window.addEventListener("popstate", () => {
    pageMasters.delete(pageKey());
    if (window.mdmOverlayApi) window.mdmOverlayApi.removeAll();
    scanMedia();
  });

  const mo = new MutationObserver(() => scanMedia());
  mo.observe(document.documentElement, { childList: true, subtree: true });
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", scanMedia);
  } else {
    scanMedia();
  }

  safeSend({ type: "mdm-content-ready" }).then((r) => {
    if (r && typeof r.online === "boolean") desktopOnline = r.online;
  });

  // Masaüstü online mı — SW'ye bağlı olmadan
  findDesktopBase().then((b) => {
    if (b) {
      desktopOnline = true;
      refreshAll();
    }
  });
})();
