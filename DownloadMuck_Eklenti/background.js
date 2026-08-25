// DownloadMuck / MDM tarayici entegrasyonu v1.9
// Once masaustu uygulamasina ilet; BASARILI olursa tarayici indirmesini iptal et.
// Chrome/Edge acilista eski indirmeleri onCreated ile tekrar firlatir — bunlari yut.

const CANDIDATE_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];
const recentHandoffs = new Map(); // url -> timestamp
const HANDOFF_DEBOUNCE_MS = 900;
const PROBE_TIMEOUT_MS = 900;
const STARTUP_GUARD_MS = 45000;
const MAX_FRESH_AGE_MS = 12000;

let startupGuardUntil = 0;

async function disableBrowserDownloadUi() {
  try {
    if (chrome.downloads.setShelfEnabled) {
      chrome.downloads.setShelfEnabled(false);
    }
  } catch (e) {
    console.warn("MDM: setShelfEnabled desteklenmiyor", e);
  }

  try {
    if (chrome.downloads.setUiOptions) {
      await chrome.downloads.setUiOptions({ enabled: false });
    }
  } catch (_) { /* eski chrome */ }
}

function buildEndpoints(preferredPort) {
  const ports = preferredPort
    ? [preferredPort, ...CANDIDATE_PORTS.filter(p => p !== preferredPort)]
    : CANDIDATE_PORTS.slice();

  const list = [];
  for (const port of ports) {
    list.push({ port, url: `http://127.0.0.1:${port}/` });
    list.push({ port, url: `http://localhost:${port}/` });
  }
  return list;
}

async function getPreferredPort() {
  try {
    const data = await chrome.storage.local.get("mdmPort");
    return data.mdmPort || null;
  } catch (_) {
    return null;
  }
}

async function savePreferredPort(port) {
  try {
    await chrome.storage.local.set({ mdmPort: port });
  } catch (_) { /* ignore */ }
}

async function postToEndpoint(endpoint, payload, timeoutMs) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetch(endpoint.url, {
      method: "POST",
      headers: { "Content-Type": "application/json; charset=utf-8" },
      body: payload,
      signal: controller.signal
    });
    return response.ok;
  } finally {
    clearTimeout(timer);
  }
}

async function handoffToDesktop(url, filename, mime) {
  const payload = JSON.stringify({ url, filename, mime: mime || "" });
  const preferred = await getPreferredPort();
  const endpoints = buildEndpoints(preferred);

  // Once tercih edilen portu hizli dene
  if (preferred) {
    for (const endpoint of endpoints.filter(e => e.port === preferred)) {
      try {
        if (await postToEndpoint(endpoint, payload, 1500)) {
          await savePreferredPort(endpoint.port);
          console.log("MDM: handoff OK via", endpoint.url);
          return true;
        }
      } catch (_) { /* sonraki */ }
    }
  }

  // Ayni porta cift istek atma (127.0.0.1 yeterli)
  const pending = endpoints
    .filter(e => e.url.includes("127.0.0.1"))
    .map(async (endpoint) => {
    try {
      if (await postToEndpoint(endpoint, payload, PROBE_TIMEOUT_MS)) {
        return endpoint;
      }
    } catch (_) { /* ignore */ }
    return null;
  });

  const results = await Promise.all(pending);
  const hit = results.find(Boolean);
  if (hit) {
    await savePreferredPort(hit.port);
    console.log("MDM: handoff OK via", hit.url);
    return true;
  }

  return false;
}

function shouldSkipHandoff(url) {
  const now = Date.now();
  const last = recentHandoffs.get(url);
  if (last && now - last < HANDOFF_DEBOUNCE_MS) {
    console.log("MDM: debounce skip", url);
    return true;
  }
  for (const [k, t] of recentHandoffs) {
    if (now - t > HANDOFF_DEBOUNCE_MS * 2) recentHandoffs.delete(k);
  }
  return false;
}

disableBrowserDownloadUi();

chrome.runtime.onInstalled.addListener(() => {
  disableBrowserDownloadUi();
  startPresencePing();
});

chrome.runtime.onStartup.addListener(() => {
  startupGuardUntil = Date.now() + STARTUP_GUARD_MS;
  disableBrowserDownloadUi();
  startPresencePing();
});

async function detectBrowser() {
  try {
    const ua = navigator.userAgent || "";
    if (/Edg\//.test(ua)) return "edge";
    if (navigator.brave && typeof navigator.brave.isBrave === "function") {
      try { if (await navigator.brave.isBrave()) return "brave"; } catch (_) { /* ignore */ }
    }
    if (/Brave/i.test(ua)) return "brave";
  } catch (_) { /* ignore */ }
  return "chrome";
}

async function pingDesktop() {
  const preferred = await getPreferredPort();
  const browser = await detectBrowser();
  const endpoints = buildEndpoints(preferred).filter(e => e.url.includes("127.0.0.1"));
  for (const endpoint of endpoints) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 800);
      const base = endpoint.url.replace(/\/?$/, "");
      const response = await fetch(`${base}/ext/ping?browser=${encodeURIComponent(browser)}`, {
        method: "GET",
        signal: controller.signal
      });
      clearTimeout(timer);
      if (response.ok) {
        await savePreferredPort(endpoint.port);
        return true;
      }
    } catch (_) { /* sonraki */ }
  }
  return false;
}

function startPresencePing() {
  pingDesktop();
  try {
    // MV3: alarms en az ~1 dk; service worker uyurken setInterval çalışmaz
    chrome.alarms.create("mdm-presence", { periodInMinutes: 1 });
  } catch (_) { /* ignore */ }
}

try {
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm && alarm.name === "mdm-presence") pingDesktop();
  });
} catch (_) { /* ignore */ }

// SW uyanınca hemen ping
startPresencePing();

function isSessionRestoreReplay(downloadItem) {
  const now = Date.now();
  const started = downloadItem.startTime ? Date.parse(downloadItem.startTime) : NaN;
  const ageMs = Number.isNaN(started) ? 0 : now - started;

  // Tarayicinin geri yukledigi kesilmis / duraklatilmis kayitlar
  if (downloadItem.state === "interrupted" || downloadItem.paused === true) {
    return true;
  }
  // Eski oturum kaydi: hem byte almis hem de birkac saniyeden eski
  if ((downloadItem.bytesReceived || 0) > 0 && ageMs > 3000) {
    return true;
  }
  if (ageMs > MAX_FRESH_AGE_MS) {
    return true;
  }
  // Profil yeni acildi: sadece taze, sifir baytlik tiklamalari ilet
  if (now < startupGuardUntil && ageMs > 2500) {
    return true;
  }
  return false;
}

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (downloadItem.byExtensionId === chrome.runtime.id) {
    return;
  }

  disableBrowserDownloadUi();

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url) && !/^magnet:/i.test(url)) {
    return;
  }

  if (url.startsWith("blob:") || url.startsWith("data:")) {
    return;
  }

  // Windows/tarayici acilisi: eski indirme listesini MDM oturum penceresine cevirme
  if (isSessionRestoreReplay(downloadItem)) {
    console.log("MDM: oturum geri yukleme atlandi", url);
    return;
  }

  // Ayni URL kisa surede tekrar gelirse (redirect/retry) yeni pencere acma
  if (shouldSkipHandoff(url)) {
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) { /* ignore */ }
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) { /* ignore */ }
    return;
  }

  const fullPath = downloadItem.filename || "";
  const rawFileName = fullPath.split(/[/\\]/).pop() || "download";
  const mime = downloadItem.mime || "";

  const accepted = await handoffToDesktop(url, rawFileName, mime);

  if (accepted) {
    recentHandoffs.set(url, Date.now());
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) { /* ignore */ }
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) { /* ignore */ }
    console.log("MDM: indirme masaustu uygulamasina aktarildi:", rawFileName);
    return;
  }

  console.warn("MDM: masaustu uygulamaya ulasilamadi, tarayici indirmesi suruyor.");
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || msg.type !== "mdm-handoff" || !msg.url) {
    return;
  }
  const url = String(msg.url);
  if (shouldSkipHandoff(url)) {
    sendResponse({ ok: true });
    return true;
  }
  handoffToDesktop(url, msg.filename || "", "").then((ok) => {
    if (ok) recentHandoffs.set(url, Date.now());
    sendResponse({ ok: !!ok });
  }).catch(() => sendResponse({ ok: false }));
  return true;
});
