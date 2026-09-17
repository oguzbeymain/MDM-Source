// MDM — masaüstü köprüsü
const MDM_CANDIDATE_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];
const MDM_PROBE_TIMEOUT_MS = 900;
const MDM_HANDOFF_DEBOUNCE_MS = 900;

const mdmRecentHandoffs = new Map();
let mdmDesktopOnline = false;
let mdmDesktopCheckAt = 0;

function mdmIsYouTubeUrl(url) {
  if (!url) return false;
  try {
    const h = new URL(url).hostname.toLowerCase();
    return h.includes("youtube.com") || h.includes("youtu.be")
      || h.includes("youtube-nocookie.com") || h.includes("googlevideo.com");
  } catch (_) { return false; }
}

function mdmBuildEndpoints(preferredPort) {
  const ports = preferredPort
    ? [preferredPort, ...MDM_CANDIDATE_PORTS.filter(p => p !== preferredPort)]
    : MDM_CANDIDATE_PORTS.slice();
  const list = [];
  for (const port of ports) {
    list.push({ port, base: `http://127.0.0.1:${port}` });
  }
  return list;
}

async function mdmGetPreferredPort() {
  try {
    const data = await chrome.storage.local.get("mdmPort");
    return data.mdmPort || null;
  } catch (_) { return null; }
}

async function mdmSavePreferredPort(port) {
  try { await chrome.storage.local.set({ mdmPort: port }); } catch (_) {}
}

async function mdmResolveLiveBase() {
  const preferred = await mdmGetPreferredPort();
  if (mdmDesktopOnline && preferred && (Date.now() - mdmDesktopCheckAt) < 20000)
    return `http://127.0.0.1:${preferred}`;

  const endpoints = mdmBuildEndpoints(preferred);
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), MDM_PROBE_TIMEOUT_MS);
  try {
    const winner = await Promise.any(endpoints.map(async (ep) => {
      const resp = await fetch(`${ep.base}/ext/ping`, { signal: ctrl.signal });
      if (!resp.ok) throw new Error("down");
      let data = null;
      try { data = await resp.json(); } catch (_) {}
      return { ep, data };
    }));
    try { ctrl.abort(); } catch (_) {}
    await mdmSavePreferredPort(winner.ep.port);
    mdmDesktopOnline = true;
    mdmDesktopCheckAt = Date.now();
    try {
      if (winner.data && winner.data.language && typeof mdmI18n !== "undefined"
          && mdmI18n.code !== winner.data.language) {
        await mdmI18n.loadLocale(winner.data.language);
        if (typeof mdmInstallMenus === "function") mdmInstallMenus();
      }
    } catch (_) {}
    return winner.ep.base;
  } catch (_) {
    mdmDesktopOnline = false;
    return null;
  } finally {
    clearTimeout(timer);
  }
}

async function mdmPostJson(path, payload, timeoutMs = 1500) {
  const base = await mdmResolveLiveBase();
  if (!base) return null;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const resp = await fetch(`${base}${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: JSON.stringify(payload),
      signal: ctrl.signal
    });
    if (resp.ok) {
      mdmDesktopOnline = true;
      mdmDesktopCheckAt = Date.now();
      const ct = resp.headers.get("content-type") || "";
      if (ct.includes("json")) return await resp.json();
      return { ok: true };
    }
  } catch (_) { /* fail */ }
  finally { clearTimeout(timer); }
  return null;
}

async function mdmGetJson(path, timeoutMs = 2000) {
  const base = await mdmResolveLiveBase();
  if (!base) return null;
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const resp = await fetch(`${base}${path}`, { signal: ctrl.signal });
    if (resp.ok) {
      mdmDesktopOnline = true;
      return await resp.json();
    }
  } catch (_) {}
  finally { clearTimeout(timer); }
  return null;
}

async function mdmHandoffLegacy(url, filename, mime) {
  return !!(await mdmPostJson("/", { url, filename, mime: mime || "" }));
}

async function mdmExtCapture(payload) {
  return !!(await mdmPostJson("/ext/capture", payload, 2500));
}

async function mdmFetchFormats(payload) {
  // YouTube = IDM modeli: eklentide sniff yok, doğrudan masaüstü yt-dlp
  if (mdmIsYouTubeUrl(payload.pageUrl)) {
    const remote = await mdmPostJson("/ext/formats", {
      pageUrl: payload.pageUrl || "",
      mediaUrl: "",
      candidates: [],
      playlistBody: "",
      cookies: payload.cookies || "",
      referrer: payload.referrer || payload.pageUrl || "",
      headers: payload.headers || {},
      videoMeta: payload.videoMeta || {},
      title: payload.title || ""
    }, 30000);
    return remote || { ok: false, error: mdmErrText("ext.error_unreachable_formats", "MDM'ye ulaşılamadı veya kalite alınamadı") };
  }

  // Sniff body varsa önce yerelde parse (IDM: body → kalite)
  if (payload.playlistBody && payload.mediaUrl) {
    try {
      if (/#EXT-X-STREAM-INF/i.test(payload.playlistBody)) {
        const parsed = mdmParseHlsFormats(payload.playlistBody, payload.mediaUrl);
        if (parsed.protected) return { ok: false, protected: true, error: "Bu video korunuyor", formats: [] };
        if (parsed.formats && parsed.formats.length) {
          const playing = typeof mdmPlayingFormat === "function"
            ? mdmPlayingFormat(payload.videoMeta || {}, payload.mediaUrl)
            : null;
          const list = playing ? [playing, ...parsed.formats] : parsed.formats;
          const formats = typeof mdmMergeFormats === "function" ? mdmMergeFormats(list) : list;
          if (formats.filter(f => f.id !== "playing").length > 0) {
            return { ok: true, title: payload.title || "HLS video", formats };
          }
        }
      }
      if (/<MPD[\s>]/i.test(payload.playlistBody)) {
        const dash = mdmParseDashFormats(payload.playlistBody, payload.mediaUrl);
        if (dash.length) {
          const formats = typeof mdmMergeFormats === "function" ? mdmMergeFormats(dash) : dash;
          return { ok: true, title: payload.title || "DASH video", formats };
        }
      }
    } catch (_) {}
  }

  let local = null;
  try {
    local = await mdmResolveFormats(payload);
  } catch (_) { local = null; }

  const localOk = local && local.ok && local.formats && local.formats.length > 0;
  const realCount = localOk
    ? local.formats.filter(f => f.id !== "playing" && f.id !== "fallback").length
    : 0;

  if (localOk && realCount > 1 && !local.deferDesktop) {
    return local;
  }

  const remote = await mdmPostJson("/ext/formats", {
    pageUrl: payload.pageUrl || "",
    mediaUrl: payload.mediaUrl || "",
    candidates: payload.candidates || [],
    playlistBody: payload.playlistBody || "",
    cookies: payload.cookies || "",
    referrer: payload.referrer || payload.pageUrl || "",
    headers: payload.headers || {},
    videoMeta: payload.videoMeta || {},
    title: payload.title || ""
  }, 12000);

  if (remote && remote.ok && remote.formats && remote.formats.length) {
    if (localOk && local.formats.length) {
      const merged = [];
      const seen = new Set();
      for (const f of [...(remote.formats || []), ...(local.formats || [])]) {
        const key = `${f.kind || f.type}|${f.height || 0}|${f.id || f.label}`;
        if (seen.has(key)) continue;
        seen.add(key);
        merged.push(f);
      }
      if (typeof mdmMergeFormats === "function") {
        return {
          ok: true,
          title: remote.title || local.title || "Video",
          formats: mdmMergeFormats(merged),
          ytDlpSuggested: !!remote.ytDlpSuggested
        };
      }
      return {
        ok: true,
        title: remote.title || local.title || "Video",
        formats: merged,
        ytDlpSuggested: !!remote.ytDlpSuggested
      };
    }
    return remote;
  }

  if (localOk) return local;
  return remote || { ok: false, error: mdmErrText("ext.error_app_closed", "MDM açık değil"), ytDlpSuggested: false };
}

function mdmDetectBrowser() {
  try {
    if (typeof MDM_BROWSER_CHANNEL === "string" && MDM_BROWSER_CHANNEL)
      return MDM_BROWSER_CHANNEL;
  } catch (_) {}
  const ua = navigator.userAgent || "";
  try {
    const brands = navigator.userAgentData && navigator.userAgentData.brands;
    if (Array.isArray(brands)) {
      const names = brands.map((b) => b && b.brand ? String(b.brand) : "").join(" ");
      if (/Brave/i.test(names)) return "brave";
      if (/Opera GX/i.test(names)) return "opera-gx";
      if (/Opera/i.test(names)) return "opera";
      if (/Vivaldi/i.test(names)) return "vivaldi";
      if (/Yandex/i.test(names)) return "yandex";
      if (/DuckDuckGo/i.test(names)) return "duckduckgo";
      if (/Thorium/i.test(names)) return "thorium";
      if (/Edge/i.test(names) || /Microsoft Edge/i.test(names)) return "edge";
    }
  } catch (_) {}
  try {
    if (navigator.brave && typeof navigator.brave.isBrave === "function")
      return "brave";
  } catch (_) {}
  if (/LibreWolf/i.test(ua)) return "librewolf";
  if (/Waterfox/i.test(ua)) return "waterfox";
  if (/Floorp/i.test(ua)) return "floorp";
  if (/Zen\//i.test(ua) || /ZenBrowser/i.test(ua)) return "zen";
  if (/Firefox\//.test(ua)) return "firefox";
  if (/Edg\//.test(ua)) return "edge";
  if (/OPR\//.test(ua) || /Opera/i.test(ua))
    return /GX/i.test(ua) ? "opera-gx" : "opera";
  if (/Vivaldi/i.test(ua)) return "vivaldi";
  if (/YaBrowser/i.test(ua)) return "yandex";
  if (/DuckDuckGo/i.test(ua)) return "duckduckgo";
  if (/Thorium/i.test(ua)) return "thorium";
  if (/Brave/i.test(ua)) return "brave";
  return "chrome";
}

async function mdmPingDesktop() {
  const preferred = await mdmGetPreferredPort();
  let browser = mdmDetectBrowser();
  for (const ep of mdmBuildEndpoints(preferred)) {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 800);
    try {
      const resp = await fetch(`${ep.base}/ext/ping?browser=${browser}`, { signal: ctrl.signal });
      if (resp.ok) {
        await mdmSavePreferredPort(ep.port);
        mdmDesktopOnline = true;
        mdmDesktopCheckAt = Date.now();
        try {
          const data = await resp.json();
          if (data && data.language && typeof mdmI18n !== "undefined" && mdmI18n.loadLocale) {
            const prev = mdmI18n.code;
            await mdmI18n.loadLocale(data.language);
            if (typeof mdmInstallMenus === "function" && mdmI18n.code !== prev)
              mdmInstallMenus();
          }
        } catch (_) { /* ping may be plain text on old builds */ }
        return true;
      }
    } catch (_) {}
    finally { clearTimeout(timer); }
  }
  mdmDesktopOnline = false;
  return false;
}

function mdmShouldSkipHandoff(url) {
  const now = Date.now();
  const last = mdmRecentHandoffs.get(url);
  if (last && now - last < MDM_HANDOFF_DEBOUNCE_MS) return true;
  for (const [k, t] of mdmRecentHandoffs) {
    if (now - t > MDM_HANDOFF_DEBOUNCE_MS * 2) mdmRecentHandoffs.delete(k);
  }
  return false;
}

function mdmMarkHandoff(url) {
  mdmRecentHandoffs.set(url, Date.now());
}

function mdmIsDesktopOnline() {
  return mdmDesktopOnline && Date.now() - mdmDesktopCheckAt < 120000;
}
