// MDM — context menus
function mdmT(key, ...args) {
  try {
    if (typeof mdmI18n !== "undefined" && mdmI18n.t) return mdmI18n.t(key, ...args);
  } catch (_) {}
  return key;
}

// URL yolundaki dosya adi (uzantisiz veya blob/data ise bos)
function mdmNameFromUrl(url) {
  try {
    const path = new URL(url).pathname || "";
    const last = decodeURIComponent(path.split("/").filter(Boolean).pop() || "");
    return last.includes(".") ? last : "";
  } catch (_) {
    return "";
  }
}

// Baglanti dogrudan bir dosyaya mi gidiyor (sayfa degil)?
const MDM_FILE_URL_RE = /\.(jpe?g|png|gif|webp|avif|bmp|svg|ico|tiff?|heic|mp4|webm|mkv|mov|m4v|avi|flv|ts|m3u8|mpd|mp3|m4a|aac|flac|wav|ogg|opus|zip|rar|7z|tar|gz|bz2|xz|pdf|docx?|xlsx?|pptx?|txt|csv|epub|apk|exe|msi|iso|dmg|torrent)(?:[?#]|$)/i;

function mdmLooksLikeFileUrl(url) {
  try {
    return MDM_FILE_URL_RE.test(new URL(url).pathname || "");
  } catch (_) {
    return false;
  }
}

function mdmInstallMenus() {
  if (mdmMenuInstallBusy) {
    mdmMenuInstallQueued = true;
    return;
  }
  mdmMenuInstallBusy = true;
  const gen = ++mdmMenuInstallGen;
  try {
    chrome.contextMenus.removeAll(() => {
      void chrome.runtime.lastError;
      if (gen !== mdmMenuInstallGen) {
        mdmFinishMenuInstall();
        return;
      }
      const items = [
        { id: "mdm-dl-link", title: mdmT("ext.menu_download"), contexts: ["link"] },
        { id: "mdm-dl-image", title: mdmT("ext.menu_download_image"), contexts: ["image"] },
        { id: "mdm-dl-media", title: mdmT("ext.menu_download_media"), contexts: ["video", "audio"] },
        { id: "mdm-dl-selection", title: mdmT("ext.menu_links"), contexts: ["selection"] },
        { id: "mdm-scan-page", title: mdmT("ext.menu_scan"), contexts: ["page", "image", "video", "audio"] }
      ];
      for (const item of items) {
        try {
          chrome.contextMenus.create(item, () => { void chrome.runtime.lastError; });
        } catch (_) {}
      }
      mdmFinishMenuInstall();
    });
  } catch (_) {
    mdmFinishMenuInstall();
  }
}

function mdmFinishMenuInstall() {
  mdmMenuInstallBusy = false;
  if (mdmMenuInstallQueued) {
    mdmMenuInstallQueued = false;
    mdmInstallMenus();
  }
}

let mdmMenuInstallGen = 0;
let mdmMenuInstallBusy = false;
let mdmMenuInstallQueued = false;
let mdmMenusClickBound = false;

function mdmInitMenus(onCaptureUrl, onScanPage) {
  mdmInstallMenus();
  if (mdmMenusClickBound) return;
  mdmMenusClickBound = true;
  chrome.contextMenus.onClicked.addListener(async (info, tab) => {
    if (!tab || tab.id == null) return;
    const page = info.pageUrl || tab.url || "";

    if (info.menuItemId === "mdm-dl-image") {
      const src = info.srcUrl || info.linkUrl || "";
      if (src) await onCaptureUrl(src, mdmNameFromUrl(src), tab, page, { kind: "image" });
      return;
    }
    if (info.menuItemId === "mdm-dl-media") {
      const src = info.srcUrl || info.linkUrl || "";
      if (src) await onCaptureUrl(src, mdmNameFromUrl(src), tab, page, { kind: "progressive" });
      return;
    }
    if (info.menuItemId === "mdm-dl-link") {
      const link = info.linkUrl || "";
      const src = info.srcUrl || "";
      // Gorselin uzerinden tiklandiysa baglanti hedefi sayfa ise gorseli indir
      const useImage = info.mediaType === "image" && src && !mdmLooksLikeFileUrl(link);
      const target = useImage ? src : link || src;
      if (!target) return;
      await onCaptureUrl(target, useImage ? mdmNameFromUrl(target) : "", tab, page,
        useImage ? { kind: "image" } : {});
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
      if (typeof onScanPage === "function") await onScanPage(tab);
    }
  });
}
