// DownloadMuck / MDM tarayici entegrasyonu v1.6
// Once masaustu uygulamasina ilet; BASARILI olursa tarayici indirmesini iptal et.

const CANDIDATE_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];
const recentHandoffs = new Map(); // url -> timestamp
const HANDOFF_DEBOUNCE_MS = 15000;
const PROBE_TIMEOUT_MS = 900;

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
});

chrome.runtime.onStartup.addListener(() => {
  disableBrowserDownloadUi();
});

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (downloadItem.byExtensionId === chrome.runtime.id) {
    return;
  }

  disableBrowserDownloadUi();

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  if (url.startsWith("blob:") || url.startsWith("data:")) {
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
