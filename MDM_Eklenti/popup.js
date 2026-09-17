// MDM — eklenti karsilama/ayar ekrani (tema, dugme, devre disi siteler)
(function () {
  const DEFAULTS = { theme: "dark", overlay: true, blocked: [] };
  let prefs = Object.assign({}, DEFAULTS);
  let currentHost = "";
  let toastTimer = 0;

  const el = {
    body: document.body,
    status: document.getElementById("status"),
    statusText: document.getElementById("statusText"),
    offlineHint: document.getElementById("offlineHint"),
    themeDark: document.getElementById("themeDark"),
    themeLight: document.getElementById("themeLight"),
    overlayToggle: document.getElementById("overlayToggle"),
    currentSiteRow: document.getElementById("currentSiteRow"),
    currentHost: document.getElementById("currentHost"),
    siteToggle: document.getElementById("siteToggle"),
    siteInput: document.getElementById("siteInput"),
    siteAdd: document.getElementById("siteAdd"),
    siteList: document.getElementById("siteList"),
    sitesEmpty: document.getElementById("sitesEmpty"),
    scanPage: document.getElementById("scanPage"),
    toast: document.getElementById("toast")
  };

  function t(key) {
    try {
      if (typeof mdmI18n !== "undefined" && mdmI18n.t) return mdmI18n.t(key);
    } catch (_) { /* ignore */ }
    return key;
  }

  /** "https://www.a.com/x" -> "a.com" */
  function normalizeHost(value) {
    let s = String(value || "").trim().toLowerCase();
    if (!s) return "";
    s = s.replace(/^[a-z]+:\/\//, "").replace(/^www\./, "");
    s = s.split("/")[0].split("?")[0].split("#")[0].split(":")[0];
    return /^[a-z0-9.-]+\.[a-z]{2,}$/i.test(s) || s === "localhost" ? s : "";
  }

  function normalizePrefs(raw) {
    const p = Object.assign({}, DEFAULTS, raw || {});
    p.theme = p.theme === "light" ? "light" : "dark";
    p.overlay = p.overlay !== false;
    const seen = new Set();
    const out = [];
    for (const item of Array.isArray(p.blocked) ? p.blocked : []) {
      const h = normalizeHost(item);
      if (h && !seen.has(h)) {
        seen.add(h);
        out.push(h);
      }
    }
    p.blocked = out.sort();
    return p;
  }

  function isBlocked(host) {
    if (!host) return false;
    return prefs.blocked.some((b) => host === b || host.endsWith("." + b));
  }

  async function save() {
    try { await chrome.storage.local.set({ mdmPrefs: prefs }); } catch (_) { /* ignore */ }
    showToast();
  }

  function showToast(text) {
    el.toast.textContent = text || t("popup.saved");
    el.toast.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.toast.classList.remove("show"), text ? 2200 : 1200);
  }

  // ——— render ———
  function applyTheme() {
    el.body.className = prefs.theme === "light" ? "theme-light" : "theme-dark";
    el.themeDark.classList.toggle("active", prefs.theme !== "light");
    el.themeLight.classList.toggle("active", prefs.theme === "light");
  }

  function renderSites() {
    el.siteList.textContent = "";
    prefs.blocked.forEach((host) => {
      const li = document.createElement("li");
      const label = document.createElement("span");
      label.textContent = host;
      li.appendChild(label);
      const rm = document.createElement("button");
      rm.textContent = "×";
      rm.title = t("popup.site_remove");
      rm.addEventListener("click", () => {
        prefs.blocked = prefs.blocked.filter((h) => h !== host);
        renderSites();
        syncCurrentSiteToggle();
        save();
      });
      li.appendChild(rm);
      el.siteList.appendChild(li);
    });
    el.sitesEmpty.hidden = prefs.blocked.length > 0;
  }

  function syncCurrentSiteToggle() {
    el.siteToggle.checked = isBlocked(currentHost);
  }

  function renderStatus(state) {
    el.status.classList.remove("online", "offline");
    if (state === "online") {
      el.status.classList.add("online");
      el.statusText.textContent = t("popup.status_online");
      el.offlineHint.hidden = true;
    } else if (state === "offline") {
      el.status.classList.add("offline");
      el.statusText.textContent = t("popup.status_offline");
      el.offlineHint.hidden = false;
    } else {
      el.statusText.textContent = t("popup.status_checking");
      el.offlineHint.hidden = true;
    }
  }

  // ——— init ———
  function applyTexts(lang) {
    document.documentElement.lang = lang;
    document.documentElement.dir = /^(ar|fa|he|ur)/i.test(lang) ? "rtl" : "ltr";
    document.querySelectorAll("[data-i18n]").forEach((node) => {
      node.textContent = t(node.dataset.i18n);
    });
    el.siteInput.placeholder = t("popup.site_placeholder");
  }

  async function initI18n() {
    let lang = "tr";
    try {
      const d = await chrome.storage.local.get("mdmLang");
      if (d && d.mdmLang) lang = d.mdmLang;
    } catch (_) { /* ignore */ }
    try { await mdmI18n.loadLocale(lang); } catch (_) { /* ignore */ }
    applyTexts(lang);
    // Popup acilirken atilan ping yeni dili getirebilir; metinler o an tazelenir
    try { mdmI18n.onChange((next) => applyTexts(next)); } catch (_) { /* ignore */ }
  }

  async function initTab() {
    try {
      const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
      const url = tabs && tabs[0] ? tabs[0].url || "" : "";
      currentHost = /^https?:/i.test(url) ? normalizeHost(url) : "";
    } catch (_) { currentHost = ""; }
    el.currentSiteRow.hidden = !currentHost;
    el.currentHost.textContent = currentHost;
    syncCurrentSiteToggle();
  }

  async function initStatus() {
    renderStatus("checking");
    try {
      const r = await chrome.runtime.sendMessage({ type: "mdm-ping-desktop" });
      renderStatus(r && r.online ? "online" : "offline");
    } catch (_) {
      renderStatus("offline");
    }
  }

  function bind() {
    el.scanPage.addEventListener("click", async () => {
      el.scanPage.disabled = true;
      showToast(t("popup.scan_running"));
      let ok = false;
      try {
        const r = await chrome.runtime.sendMessage({ type: "mdm-scan-page" });
        ok = !!(r && r.ok);
      } catch (_) { ok = false; }
      if (ok) {
        window.close();
        return;
      }
      showToast(t("popup.scan_offline"));
      el.scanPage.disabled = false;
    });

    el.themeDark.addEventListener("click", () => {
      if (prefs.theme === "dark") return;
      prefs.theme = "dark";
      applyTheme();
      save();
    });
    el.themeLight.addEventListener("click", () => {
      if (prefs.theme === "light") return;
      prefs.theme = "light";
      applyTheme();
      save();
    });

    el.overlayToggle.addEventListener("change", () => {
      prefs.overlay = el.overlayToggle.checked;
      save();
    });

    el.siteToggle.addEventListener("change", () => {
      if (!currentHost) return;
      if (el.siteToggle.checked) {
        if (!prefs.blocked.includes(currentHost)) {
          prefs.blocked.push(currentHost);
          prefs.blocked.sort();
        }
      } else {
        prefs.blocked = prefs.blocked.filter((h) => h !== currentHost && !currentHost.endsWith("." + h));
      }
      renderSites();
      syncCurrentSiteToggle();
      save();
    });

    const addSite = () => {
      const host = normalizeHost(el.siteInput.value);
      if (!host) {
        el.siteInput.focus();
        el.siteInput.select();
        return;
      }
      if (!prefs.blocked.includes(host)) {
        prefs.blocked.push(host);
        prefs.blocked.sort();
      }
      el.siteInput.value = "";
      renderSites();
      syncCurrentSiteToggle();
      save();
    };
    el.siteAdd.addEventListener("click", addSite);
    el.siteInput.addEventListener("keydown", (e) => {
      if (e.key === "Enter") addSite();
    });
  }

  (async function start() {
    try {
      const d = await chrome.storage.local.get("mdmPrefs");
      prefs = normalizePrefs(d && d.mdmPrefs);
    } catch (_) { prefs = normalizePrefs(null); }

    applyTheme();
    el.overlayToggle.checked = prefs.overlay;
    renderSites();
    bind();
    await initI18n();
    renderSites();          // kaldir ipucu metni cevrildi
    await initTab();
    initStatus();
  })();
})();
