// MDM — context menus
function mdmInstallMenus() {
  try {
    chrome.contextMenus.removeAll(() => {
      chrome.contextMenus.create({ id: "mdm-dl-link", title: "MDM ile indir", contexts: ["link", "image", "video", "audio"] });
      chrome.contextMenus.create({ id: "mdm-dl-selection", title: "Seçili bağlantıları MDM ile indir", contexts: ["selection"] });
      chrome.contextMenus.create({ id: "mdm-scan-page", title: "Sayfadaki videoları tara", contexts: ["page"] });
    });
  } catch (_) {}
}

function mdmInitMenus(onCaptureUrl) {
  mdmInstallMenus();
  chrome.contextMenus.onClicked.addListener(async (info, tab) => {
    if (!tab || tab.id == null) return;
    if (info.menuItemId === "mdm-dl-link" && info.linkUrl) {
      await onCaptureUrl(info.linkUrl, "", tab, info.pageUrl || tab.url || "");
      return;
    }
    if (info.menuItemId === "mdm-dl-selection" && info.selectionText) {
      const urls = (info.selectionText.match(/https?:\/\/[^\s<>"']+/gi) || []);
      for (const u of urls) await onCaptureUrl(u, "", tab, tab.url || "");
      return;
    }
    if (info.menuItemId === "mdm-scan-page") {
      try {
        await chrome.tabs.sendMessage(tab.id, { type: "mdm-rescan" });
      } catch (_) {}
    }
  });
}
