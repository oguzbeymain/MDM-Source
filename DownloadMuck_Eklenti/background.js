// DownloadMuck / MDM tarayici entegrasyonu v1.4
// Once masaustu uygulamasina ilet; BASARILI olursa tarayici indirmesini iptal et.

const CANDIDATE_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];

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

async function handoffToDesktop(url, filename, mime) {
  const payload = JSON.stringify({ url, filename, mime: mime || "" });
  const preferred = await getPreferredPort();
  const endpoints = buildEndpoints(preferred);

  for (const endpoint of endpoints) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 600);

      const response = await fetch(endpoint.url, {
        method: "POST",
        headers: { "Content-Type": "application/json; charset=utf-8" },
        body: payload,
        signal: controller.signal
      });

      clearTimeout(timer);

      if (response.ok) {
        await savePreferredPort(endpoint.port);
        console.log("MDM: handoff OK via", endpoint.url);
        return true;
      }
    } catch (_) {
      // sonraki porta gec
    }
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

  // Shelf/UI kapali kalsin
  disableBrowserDownloadUi();

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  if (url.startsWith("blob:") || url.startsWith("data:")) {
    return;
  }

  const fullPath = downloadItem.filename || "";
  const rawFileName = fullPath.split(/[/\\]/).pop() || "download";
  const mime = downloadItem.mime || "";

  // Once uygulamaya ilet — basariliysa hemen sil (bildirim/raf azalir)
  const accepted = await handoffToDesktop(url, rawFileName, mime);

  if (accepted) {
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) { /* ignore */ }
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) { /* ignore */ }
    console.log("MDM: indirme masaustu uygulamasina aktarildi:", rawFileName);
    return;
  }

  // Uygulama yok — tarayici indirsin (pause etmedik, zaten devam ediyor)
  console.warn("MDM: masaustu uygulamaya ulasilamadi, tarayici indirmesi suruyor.");
});
