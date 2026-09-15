// MDM — MAIN world hooks (fetch/XHR/media src/MSE)
(function () {
  if (window.__mdmMainHook) return;
  window.__mdmMainHook = true;

  function post(kind, data) {
    try {
      window.postMessage({ source: "mdm-main", kind, ...data }, window.location.origin);
    } catch (_) {}
  }

  const origFetch = window.fetch;
  if (origFetch) {
    window.fetch = async function (input, init) {
      const resp = await origFetch.apply(this, arguments);
      try {
        const url = typeof input === "string" ? input : (input && input.url) || "";
        const ct = (resp.headers && resp.headers.get("content-type")) || "";
        if (/m3u8|mpegurl|dash\+xml|json|\/hls\/|master\.txt|playlist\.txt/i.test(ct + url)) {
          const clone = resp.clone();
          const text = await clone.text();
          post("fetch", { url: resp.url || url, mime: ct, snippet: text.slice(0, 512000), kind: "fetch" });
        } else if (/video|audio/i.test(ct) || /\.(mp4|webm|m3u8|mpd)(\?|$)/i.test(url) || /\/hls\//i.test(url)) {
          post("fetch", { url: resp.url || url, mime: ct, kind: "fetch" });
        }
      } catch (_) {}
      return resp;
    };
  }

  const XHROpen = XMLHttpRequest.prototype.open;
  const XHRSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function (method, url) {
    this.__mdmUrl = url;
    return XHROpen.apply(this, arguments);
  };
  XMLHttpRequest.prototype.send = function () {
    this.addEventListener("load", function () {
      try {
        const url = this.responseURL || this.__mdmUrl || "";
        const ct = this.getResponseHeader("content-type") || "";
        if (/m3u8|mpegurl|dash|json|\/hls\/|master\.txt|playlist\.txt/i.test(ct + url)) {
          const text = typeof this.responseText === "string" ? this.responseText.slice(0, 512000) : "";
          post("xhr", { url, mime: ct, snippet: text, kind: "xhr" });
        }
      } catch (_) {}
    });
    return XHRSend.apply(this, arguments);
  };

  const srcDesc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "src");
  if (srcDesc && srcDesc.set) {
    Object.defineProperty(HTMLMediaElement.prototype, "src", {
      ...srcDesc,
      set(v) {
        post("media-src", { src: v, tag: this.tagName, videoWidth: this.videoWidth || 0 });
        return srcDesc.set.call(this, v);
      }
    });
  }

  const origSetAttr = Element.prototype.setAttribute;
  Element.prototype.setAttribute = function (name, value) {
    if ((name === "src" || name === "href") && (this.tagName === "VIDEO" || this.tagName === "AUDIO" || this.tagName === "SOURCE")) {
      post("media-src", { src: value, tag: this.tagName });
    }
    return origSetAttr.call(this, name, value);
  };

  if (window.MediaSource && MediaSource.prototype.addSourceBuffer) {
    const origAdd = MediaSource.prototype.addSourceBuffer;
    MediaSource.prototype.addSourceBuffer = function (mime) {
      post("mse", { mime, pageUrl: location.href });
      return origAdd.call(this, mime);
    };
  }
})();
