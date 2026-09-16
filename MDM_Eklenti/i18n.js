// MDM — eklenti i18n (masaüstü /ext/ping dil kodu)
(function (global) {
  const DEFAULT = "tr";
  let code = DEFAULT;
  let map = {};

  const FALLBACK = {
    "ext.btn_title_online": "MDM ile indir — kalite seç",
    "ext.btn_title_offline": "MDM uygulamasını başlatın",
    "ext.panel_title": "Video indir",
    "ext.loading": "Kaliteler alınıyor…",
    "ext.sent": "Gönderildi ✓",
    "ext.error_start_app": "MDM uygulamasını başlatın",
    "ext.error_reload": "Eklenti güncellendi — bu sekmeyi yenileyin (F5)",
    "ext.error_formats": "Kalite listesi alınamadı",
    "ext.error_unreachable": "MDM'ye ulaşılamadı — uygulama açık mı?",
    "ext.error_wait_play": "Kalite henüz yakalanmadı — videoyu oynatıp tekrar deneyin",
    "ext.error_protected": "Bu video korunuyor",
    "ext.error_protected_content": "Korumalı içerik",
    "ext.error_no_media": "Geçerli medya URL’si yok — videoyu oynatıp tekrar deneyin",
    "ext.error_unreachable_formats": "MDM'ye ulaşılamadı veya kalite alınamadı",
    "ext.error_app_closed": "MDM açık değil",
    "ext.playing": "Oynayan · {0}p",
    "ext.video": "Video",
    "ext.menu_download": "MDM ile indir",
    "ext.menu_download_image": "Görseli MDM ile indir",
    "ext.menu_download_media": "Medyayı MDM ile indir",
    "ext.menu_links": "Seçili bağlantıları MDM ile indir",
    "ext.menu_scan": "Sayfayı tara",
    "ext.note_ytdlp": "Tam kalite listesi için yt-dlp önerilir"
  };

  function format(str, args) {
    let s = str || "";
    (args || []).forEach((a, i) => {
      s = s.replace(new RegExp("\\{" + i + "\\}", "g"), String(a));
    });
    return s;
  }

  function t(key, ...args) {
    const raw = (map && map[key]) || FALLBACK[key] || key;
    return args.length ? format(raw, args) : raw;
  }

  async function loadLocale(lang) {
    const c = (lang || DEFAULT).trim() || DEFAULT;
    code = c;
    try {
      const url = chrome.runtime.getURL(`locales/${c}.json`);
      const resp = await fetch(url);
      if (resp.ok) {
        map = await resp.json();
        try { await chrome.storage.local.set({ mdmLang: c, mdmLangMap: map }); } catch (_) {}
        return;
      }
    } catch (_) {}
    try {
      const data = await chrome.storage.local.get(["mdmLangMap", "mdmLang"]);
      if (data.mdmLangMap && typeof data.mdmLangMap === "object") {
        map = data.mdmLangMap;
        code = data.mdmLang || c;
        return;
      }
    } catch (_) {}
    map = Object.assign({}, FALLBACK);
  }

  async function syncFromDesktop(base) {
    try {
      const resp = await fetch(`${base}/ext/ping`);
      if (!resp.ok) return;
      const data = await resp.json();
      if (data && data.language) await loadLocale(data.language);
    } catch (_) {}
  }

  global.mdmI18n = { t, loadLocale, syncFromDesktop, get code() { return code; } };

  // Arka plan scriptleri için kısa yol: çeviri yoksa Türkçe metne düşer
  global.mdmErrText = function (key, fallback) {
    try {
      const v = t(key);
      return v && v !== key ? v : (fallback || key);
    } catch (_) {
      return fallback || key;
    }
  };
})(typeof self !== "undefined" ? self : window);
