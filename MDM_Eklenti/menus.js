// MDM — context menus
function mdmT(key, ...args) {
  try {
    if (typeof mdmI18n !== "undefined" && mdmI18n.t) return mdmI18n.t(key, ...args);
  } catch (_) {}
  return key;
}

function mdmInstallMenus() {
  try {
    chrome.contextMenus.removeAll(() => {
      chrome.contextMenus.create({ id: "mdm-dl-link", title: mdmT("ext.menu_download"), contexts: ["link", "image", "video", "audio"] });
      chrome.contextMenus.create({ id: "mdm-dl-selection", title: mdmT("ext.menu_links"), contexts: ["selection"] });
      chrome.contextMenus.create({ id: "mdm-scan-page", title: mdmT("ext.menu_scan"), contexts: ["page"] });
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
