chrome.downloads.onCreated.addListener(async (downloadItem) => {
  // Eklentinin fallback olarak yeniden başlattığı indirmeleri atla
  if (downloadItem.byExtensionId === chrome.runtime.id) {
    return;
  }

  const url = downloadItem.finalUrl || downloadItem.url || "";
  if (!/^https?:\/\//i.test(url)) {
    return;
  }

  const fullPath = downloadItem.filename || "";
  const rawFileName = fullPath.split(/[/\\]/).pop() || "download";

  // Tarayıcı native indirmesini hemen durdur ve listeden sil (iptal olarak görünmesin)
  try {
    await chrome.downloads.cancel(downloadItem.id);
  } catch (_) { /* zaten bitmiş olabilir */ }

  try {
    await chrome.downloads.erase({ id: downloadItem.id });
  } catch (_) { /* yok say */ }

  // İndirmeyi yalnızca DownloadMuck masaüstü uygulamasına aktar
  try {
    const response = await fetch("http://localhost:6800/", {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        url: url,
        filename: rawFileName
      })
    });

    if (!response.ok) {
      throw new Error("DownloadMuck HTTP " + response.status);
    }
  } catch (err) {
    console.error("DownloadMuck'a bağlanılamadı, indirme tarayıcıya geri veriliyor:", err);

    // Uygulama kapalıysa indirmeyi tarayıcıda yeniden başlat
    try {
      await chrome.downloads.download({
        url: url,
        filename: rawFileName !== "download" ? rawFileName : undefined,
        saveAs: false
      });
    } catch (downloadErr) {
      console.error("Tarayıcı indirmesi yeniden başlatılamadı:", downloadErr);
    }
  }
});
