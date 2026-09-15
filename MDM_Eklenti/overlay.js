// MDM — video overlay (Shadow DOM) — kalite paneli
(function () {
  if (window.__mdmOverlay) return;
  window.__mdmOverlay = true;

  const hosts = new Map();

  function cssText() {
    return `
      .mdm-host { position:fixed; z-index:2147483646; pointer-events:none; font-family:"Segoe UI",system-ui,sans-serif; }
      .mdm-btn { pointer-events:auto; cursor:pointer; background:rgba(20,20,20,.82); color:#fff; border:1px solid rgba(255,107,0,.7);
        border-radius:6px; padding:4px 8px; font-size:11px; font-weight:700; box-shadow:0 2px 8px rgba(0,0,0,.35); }
      .mdm-btn:hover { background:rgba(255,107,0,.92); }
      .mdm-btn.offline { opacity:.55; border-color:#666; }
      .mdm-panel { pointer-events:auto; margin-top:6px; width:min(260px,calc(100vw - 24px)); max-width:min(260px,92vw); background:rgba(24,24,24,.96);
        border:1px solid #333; border-radius:10px; padding:10px; color:#eee; box-shadow:0 8px 24px rgba(0,0,0,.45);
        max-height:min(70vh,420px); overflow-y:auto; }
      .mdm-panel h4 { margin:0 0 8px; font-size:12px; color:#ccc; font-weight:600; }
      .mdm-row { display:flex; justify-content:space-between; align-items:center; gap:8px; padding:7px 8px; border-radius:6px;
        cursor:pointer; font-size:12px; line-height:1.3; }
      .mdm-row:hover { background:rgba(255,107,0,.18); }
      .mdm-row.sent { color:#8bc34a; cursor:default; }
      .mdm-muted { color:#888; font-size:11px; }
      .mdm-note { color:#999; font-size:11px; margin-top:8px; line-height:1.35; }
    `;
  }

  function ensureHost(elementId) {
    if (hosts.has(elementId)) return hosts.get(elementId);
    const root = document.createElement("div");
    root.className = "mdm-host";
    root.style.cssText = "position:fixed;z-index:2147483646;pointer-events:none;left:0;top:0;";
    (document.body || document.documentElement).appendChild(root);
    const shadow = root.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = cssText();
    shadow.appendChild(style);
    const wrap = document.createElement("div");
    shadow.appendChild(wrap);
    const state = { root, shadow, wrap, panel: null, open: false };
    hosts.set(elementId, state);
    return state;
  }

  function destroyHost(elementId) {
    const h = hosts.get(elementId);
    if (h) {
      h.root.remove();
      hosts.delete(elementId);
    }
  }

  function positionHost(state, bbox) {
    if (!bbox || bbox.width < 40) {
      state.root.style.display = "none";
      return;
    }
    state.root.style.display = "block";
    const vw = window.innerWidth || document.documentElement.clientWidth || 800;
    const vh = window.innerHeight || document.documentElement.clientHeight || 600;
    const panelW = 280;
    // Buton sağ üstte; paneli viewport içinde tut
    let left = bbox.left + bbox.width - 44;
    let top = bbox.top + 8;
    left = Math.min(left, vw - panelW - 8);
    left = Math.max(8, left);
    top = Math.max(8, Math.min(top, vh - 48));
    state.root.style.left = `${left}px`;
    state.root.style.top = `${top}px`;
  }

  function clampPanelInViewport(state) {
    if (!state || !state.panel) return;
    requestAnimationFrame(() => {
      try {
        const pr = state.panel.getBoundingClientRect();
        const vw = window.innerWidth || 800;
        const vh = window.innerHeight || 600;
        let left = parseFloat(state.root.style.left) || 0;
        let top = parseFloat(state.root.style.top) || 0;
        if (pr.right > vw - 8) left -= pr.right - (vw - 8);
        if (pr.left < 8) left += 8 - pr.left;
        if (pr.bottom > vh - 8) top -= pr.bottom - (vh - 8);
        if (pr.top < 8) top += 8 - pr.top;
        left = Math.max(8, left);
        top = Math.max(8, top);
        state.root.style.left = `${left}px`;
        state.root.style.top = `${top}px`;
      } catch (_) { /* ignore */ }
    });
  }

  function renderButton(state, { online, onClick }) {
    state.wrap.innerHTML = "";
    state.panel = null;
    state.open = false;
    const btn = document.createElement("button");
    btn.className = "mdm-btn" + (online ? "" : " offline");
    btn.textContent = "MDM";
    btn.title = online ? "MDM ile indir — kalite seç" : "MDM uygulamasını başlatın";
    btn.addEventListener("click", (e) => {
      e.stopPropagation();
      e.preventDefault();
      onClick();
    });
    state.wrap.appendChild(btn);
    state.btn = btn;
  }

  function renderPanel(state, { title, formats, loading, error, note, onPick }) {
    if (state.panel) state.panel.remove();
    state.panel = document.createElement("div");
    state.panel.className = "mdm-panel";
    const h4 = document.createElement("h4");
    h4.textContent = title || "Video indir";
    state.panel.appendChild(h4);

    if (loading) {
      const p = document.createElement("div");
      p.className = "mdm-muted";
      p.textContent = "Kaliteler alınıyor…";
      state.panel.appendChild(p);
    } else if (error) {
      const p = document.createElement("div");
      p.className = "mdm-muted";
      p.textContent = error;
      state.panel.appendChild(p);
    } else {
      (formats || []).forEach((f) => {
        const row = document.createElement("div");
        row.className = "mdm-row";
        const left = document.createElement("span");
        left.textContent = f.label || f.id || "Video";
        row.appendChild(left);
        const right = document.createElement("span");
        right.className = "mdm-muted";
        if (f.filesize && !/MB|GB/i.test(f.label || "")) {
          right.textContent = `${Math.round(f.filesize / 1048576)} MB`;
        }
        row.appendChild(right);
        row.addEventListener("click", (e) => {
          e.stopPropagation();
          e.preventDefault();
          if (row.classList.contains("sent")) return;
          row.className = "mdm-row sent";
          row.textContent = "Gönderildi ✓";
          if (typeof onPick === "function") onPick(f);
          setTimeout(() => {
            state.panel?.remove();
            state.panel = null;
            state.open = false;
          }, 1200);
        });
        state.panel.appendChild(row);
      });
      if (note) {
        const n = document.createElement("div");
        n.className = "mdm-note";
        n.textContent = note;
        state.panel.appendChild(n);
      }
    }
    state.wrap.appendChild(state.panel);
    state.open = true;
    clampPanelInViewport(state);
  }

  window.mdmOverlayApi = {
    update(elementId, bbox, online, meta) {
      const state = ensureHost(elementId);
      positionHost(state, bbox);
      if (!state.btn) {
        renderButton(state, {
          online,
          onClick: () => {
            state.open = true;
            if (meta && meta.onOpen) meta.onOpen(state);
          }
        });
      } else {
        state.btn.className = "mdm-btn" + (online ? "" : " offline");
        state.btn.title = online ? "MDM ile indir — kalite seç" : "MDM uygulamasını başlatın";
      }
    },
    showPanel(elementId, opts) {
      const state = hosts.get(elementId);
      if (!state) return;
      renderPanel(state, opts || {});
    },
    remove(elementId) { destroyHost(elementId); },
    removeAll() { for (const id of [...hosts.keys()]) destroyHost(id); }
  };
})();
