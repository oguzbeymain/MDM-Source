// DownloadMuck / MDM tarayici entegrasyonu
// Onemli: Once masaustu uygulamasina ilet, BASARILI olursa tarayici indirmesini iptal et.

const APP_ENDPOINTS = [
  "http://127.0.0.1:6800/",
  "http://localhost:6800/"
];

async function handoffToDesktop(url, filename) {
  const payload = JSON.stringify({ url, filename });

  for (const endpoint of APP_ENDPOINTS) {
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 2500);

      const response = await fetch(endpoint, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: payload,
        signal: controller.signal
      });

      clearTimeout(timer);

      if (response.ok) {
        return true;
      }
    } catch (err) {
      console.warn("MDM handoff failed via", endpoint, err);
    }
  }

  return false;
}

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  // Eklentinin kendi baslattigi indirmeleri atla
  if (downloadItem.byExtensionId === chrome.runtime.id) {
    return;
  }

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  // Cerez / blob vb. desteklenmeyenler
  if (url.startsWith("blob:") || url.startsWith("data:")) {
    return;
  }

  const fullPath = downloadItem.filename || "";
  const rawFileName = fullPath.split(/[/\\]/).pop() || "download";

  // Tarayici indirmesini gecici durdur (iptal etme) — app yoksa devam edebilsin
  try {
    await chrome.downloads.pause(downloadItem.id);
  } catch (_) {
    /* bazi indirmeler pause desteklemeyebilir */
  }

  const accepted = await handoffToDesktop(url, rawFileName);

  if (accepted) {
    // Masaustu uygulamasi aldi — tarayici indirmesini kaldir
    try {
      await chrome.downloads.cancel(downloadItem.id);
    } catch (_) { /* ignore */ }

    try {
      await chrome.downloads.erase({ id: downloadItem.id });
    } catch (_) { /* ignore */ }

    console.log("MDM: indirme masaustu uygulamasina aktarildi:", rawFileName);
    return;
  }

  // Uygulama kapali / ulasilamiyor — tarayici indirmesine devam et
  console.warn("MDM: masaustu uygulamaya ulasilamadi, tarayici indirmesi surduruluyor.");
  try {
    await chrome.downloads.resume(downloadItem.id);
  } catch (err) {
    // Resume olmazsa yeniden baslat
    console.warn("MDM: resume basarisiz, yeniden baslatiliyor", err);
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
