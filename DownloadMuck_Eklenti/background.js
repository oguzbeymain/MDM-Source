// DownloadMuck / MDM tarayici entegrasyonu v1.3
// Once masaustu uygulamasina ilet; BASARILI olursa tarayici indirmesini iptal et.
// Masaustu birden fazla port deneyebilir — ayni listeyi burada da dene.

const CANDIDATE_PORTS = [18680, 18681, 18682, 18700, 27182, 38472, 6800];

function buildEndpoints() {
  const list = [];
  for (const port of CANDIDATE_PORTS) {
    list.push(`http://127.0.0.1:${port}/`);
    list.push(`http://localhost:${port}/`);
  }
  return list;
}

async function handoffToDesktop(url, filename) {
  const payload = JSON.stringify({ url, filename });
  const endpoints = buildEndpoints();

  for (const endpoint of endpoints) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 800);

      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: payload,
        signal: controller.signal
      });

      clearTimeout(timer);

      if (response.ok) {
        console.log("MDM: handoff OK via", endpoint);
        return true;
      }
    } catch (_) {
      // sonraki porta gec
    }
  }

  return false;
}

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  if (downloadItem.byExtensionId === chrome.runtime.id) {
    return;
  }

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  if (url.startsWith("blob:") || url.startsWith("data:")) {
    return;
  }

  const fullPath = downloadItem.filename || "";
  const rawFileName = fullPath.split(/[/\\]/).pop() || "download";

  // Gecici durdur — app yoksa devam edebilsin
  try {
    await chrome.downloads.pause(downloadItem.id);
  } catch (_) { /* ignore */ }

  const accepted = await handoffToDesktop(url, rawFileName);

  if (accepted) {
    try { await chrome.downloads.cancel(downloadItem.id); } catch (_) { /* ignore */ }
    try { await chrome.downloads.erase({ id: downloadItem.id }); } catch (_) { /* ignore */ }
    console.log("MDM: indirme masaustu uygulamasina aktarildi:", rawFileName);
    return;
  }

  console.warn("MDM: masaustu uygulamaya ulasilamadi, tarayici indirmesi surduruluyor.");
  try {
    await chrome.downloads.resume(downloadItem.id);
  } catch (err) {
    try {
      await chrome.downloads.download({
        url: url,
        filename: rawFileName !== "download" ? rawFileName : undefined,
        saveAs: false
      });
    } catch (downloadErr) {
      console.error("MDM: tarayici indirmesi yeniden baslatilamadi:", downloadErr);
    }
  }
});
