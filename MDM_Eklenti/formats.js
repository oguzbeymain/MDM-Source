// MDM — FormatOption çözücü (HLS / DASH / progressive / birleştirme)
// Service worker + desktop /ext/formats yedeği

function mdmNormMediaUrl(url) {
  if (!url) return "";
  try {
    const u = new URL(url);
    u.hash = "";
    const drop = new Set(["range", "bytes"]);
    const keep = [];
    u.searchParams.forEach((v, k) => {
      if (!drop.has(k.toLowerCase())) keep.push([k, v]);
    });
    u.search = "";
    keep.sort((a, b) => a[0].localeCompare(b[0]));
    for (const [k, v] of keep) u.searchParams.set(k, v);
    return u.toString();
  } catch (_) {
    return String(url).split("#")[0];
  }
}

function mdmClassifyMediaUrl(url) {
  if (!url) return "other";
  const lower = url.toLowerCase();
  if (lower.startsWith("blob:") || lower.startsWith("mediasource:")) return "blob";
  if (typeof mdmIsJunkMediaUrl === "function" && mdmIsJunkMediaUrl(url)) return "other";
  // .m3u8 yok ama /hls/ yolu veya master.txt (film CDN'leri)
  if (/\.m3u8?(\?|$)/i.test(lower) || /mpegurl|application\/vnd\.apple\.mpegurl/i.test(lower)
      || /\/(master|index|playlist|manifest|hls)\.txt(\?|$)/i.test(lower)
      || /\/hls\//i.test(lower) || /\/hls\?/i.test(lower) || /[?&](format|type|playlist)=m3u8\b/i.test(lower))
    return "hls";
  if (/\.mpd(\?|$)/i.test(lower) || /dash\+xml/i.test(lower) || /\/dash\//i.test(lower)) return "dash";
  if (/\.(m4s|ts|m2ts)(\?|$)/i.test(lower) || /[?&]itag=|videoplayback/i.test(lower)) return "fragment";
  if (/\.(mp4|webm|mkv|mov|m4v)(\?|$)/i.test(lower)) return "progressive";
  if (/\.(mp3|m4a|aac|opus|flac|wav)(\?|$)/i.test(lower)) return "audio";
  return "other";
}

function mdmAbsUrl(uri, base) {
  try { return new URL(uri, base).toString(); } catch (_) { return uri; }
}

function mdmHeightLabel(h, fps) {
  if (!h) return "Video";
  const n = Math.round(h);
  if (fps && fps >= 50) return `${n}p${Math.round(fps)}`;
  return `${n}p`;
}

function mdmGuessHeightFromUrl(url) {
  if (!url) return 0;
  const m =
    /(?:^|[/_-])(2160|1440|1080|720|480|360|240|144)p(?:[/_-]|$)/i.exec(url) ||
    /[/_-](2160|1440|1080|720|480|360|240|144)(?:[/_-]|$)/i.exec(url) ||
    /[?&](?:h|height|quality)=(\d{3,4})/i.exec(url) ||
    /(\d{3,4})x(\d{3,4})/i.exec(url);
  if (!m) return 0;
  if (m[2] && parseInt(m[2], 10) < parseInt(m[1], 10)) return parseInt(m[2], 10);
  return parseInt(m[1], 10) || 0;
}

async function mdmFetchPlaylist(url, headers, pageUrl, timeoutMs = 5000) {
  const ctrl = new AbortController();
  const t = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const h = { ...(headers || {}) };
    if (!h.Referer && pageUrl) h.Referer = pageUrl;
    if (!h.Origin && pageUrl) {
      try { h.Origin = new URL(pageUrl).origin; } catch (_) {}
    }
    if (!h["User-Agent"]) {
      h["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
    }
    const resp = await fetch(url, {
      credentials: "omit",
      headers: h,
      signal: ctrl.signal,
      redirect: "follow"
    });
    if (!resp.ok) return null;
    return await resp.text();
  } catch (_) {
    return null;
  } finally {
    clearTimeout(t);
  }
}

function mdmParseHlsFormats(text, masterUrl) {
  const formats = [];
  if (!text || !text.includes("#EXTM3U")) return { formats, protected: false };

  if (/#EXT-X-KEY[^]*METHOD=(SAMPLE-AES|com\.apple\.streamingkeydelivery)/i.test(text)) {
    return { formats: [], protected: true };
  }

  // Audio renditions
  for (const raw of text.split("\n")) {
    const line = raw.trim();
    if (!line.startsWith("#EXT-X-MEDIA:") || !/TYPE=AUDIO/i.test(line)) continue;
    const uriM = /URI="([^"]+)"/i.exec(line);
    if (!uriM) continue;
    const nameM = /NAME="([^"]+)"/i.exec(line);
    const langM = /LANGUAGE="([^"]+)"/i.exec(line);
    const name = (nameM && nameM[1]) || (langM && langM[1]) || "AAC";
    formats.push({
      id: `audio-${mdmNormMediaUrl(uriM[1]).slice(-24)}`,
      label: `Ses ${name}`,
      height: null,
      width: null,
      fps: 0,
      vcodec: "none",
      acodec: "aac",
      bandwidth: 0,
      filesize: null,
      url: mdmAbsUrl(uriM[1], masterUrl),
      pageUrl: "",
      kind: "audio",
      type: "audio",
      formatId: ""
    });
  }

  if (!text.includes("#EXT-X-STREAM-INF")) {
    // Media playlist
    let h = mdmGuessHeightFromUrl(masterUrl);
    const label = h ? mdmHeightLabel(h, 0) : "Video";
    formats.push({
      id: `hls-media-${h || "x"}`,
      label,
      height: h || null,
      width: null,
      fps: 0,
      vcodec: "",
      acodec: "",
      bandwidth: 0,
      filesize: null,
      url: masterUrl,
      kind: "hls",
      type: "hls",
      formatId: ""
    });
    return { formats, protected: false };
  }

  let bw = 0, h = 0, w = 0, fps = 0, codecs = "", name = "";
  for (const raw of text.split("\n")) {
    const line = raw.trim();
    if (line.startsWith("#EXT-X-STREAM-INF")) {
      const bm = /BANDWIDTH=(\d+)/i.exec(line);
      bw = bm ? parseInt(bm[1], 10) : 0;
      const rm = /RESOLUTION=(\d+)x(\d+)/i.exec(line);
      if (rm) { w = parseInt(rm[1], 10); h = parseInt(rm[2], 10); }
      else { w = 0; h = 0; }
      const fm = /FRAME-RATE=([\d.]+)/i.exec(line);
      fps = fm ? parseFloat(fm[1]) : 0;
      const cm = /CODECS="([^"]+)"/i.exec(line);
      codecs = cm ? cm[1] : "";
      const nm = /NAME="([^"]+)"/i.exec(line);
      name = nm ? nm[1] : "";
      continue;
    }
    if (!line || line.startsWith("#")) {
      bw = 0; h = 0; w = 0; fps = 0; codecs = ""; name = "";
      continue;
    }
    if (bw || h) {
      const uri = mdmAbsUrl(line, masterUrl);
      const height = h || mdmGuessHeightFromUrl(uri) || 0;
      let label = height ? mdmHeightLabel(height, fps) : (name || `${Math.round(bw / 1000)}k`);
      const parts = (codecs || "").split(",");
      const vcodec = (parts[0] || "").trim();
      const acodec = (parts[1] || "").trim() || "";
      formats.push({
        id: `h-${height || 0}-${vcodec || "x"}-${bw}`,
        label,
        height: height || null,
        width: w || null,
        fps: fps ? Math.round(fps) : 0,
        vcodec: vcodec || "",
        acodec,
        bandwidth: bw,
        filesize: null,
        url: uri,
        kind: "hls",
        type: "hls",
        formatId: "",
        extras: { masterUrl }
      });
    }
    bw = 0; h = 0; w = 0; fps = 0; codecs = ""; name = "";
  }
  return { formats, protected: false };
}

async function mdmGuessMasterUrls(mediaUrl) {
  const out = [];
  const push = (cand) => {
    if (cand && cand !== mediaUrl && !out.includes(cand)) out.push(cand);
  };
  try {
    const u = new URL(mediaUrl);
    // /hls/foo.mp4/txt/sublist_2.txt → /hls/foo.mp4/
    let path = u.pathname;
    path = path.replace(/\/txt\/[^/]+$/i, "/");
    path = path.replace(/\/(sub|subs|subtitle|subtitles|caption|captions)s?\/[^/]*$/i, "/");
    // .../foo.mp4/segment → .../foo.mp4/
    const mp4Base = path.match(/^(.*?\.mp4)\//i);
    if (mp4Base) path = mp4Base[1] + "/";
    else path = path.replace(/\/[^/]*$/, "/");

    const names = [
      "index.m3u8", "master.m3u8", "playlist.m3u8", "manifest.m3u8", "hls.m3u8",
      "index.txt", "master.txt", "playlist.txt", "manifest.txt", "hls.txt"
    ];
    for (const n of names) {
      push(new URL(path + n, u.origin).toString() + (u.search || ""));
    }
    const parent = path.replace(/\/[^/]+\/$/, "/");
    if (parent !== path) {
      for (const n of ["master.m3u8", "index.m3u8", "playlist.m3u8", "master.txt", "index.txt"]) {
        push(new URL(parent + n, u.origin).toString() + (u.search || ""));
      }
    }
  } catch (_) {}
  return out.slice(0, 12);
}

function mdmParseDashFormats(xml, mpdUrl) {
  const formats = [];
  if (!xml || !/<MPD[\s>]/i.test(xml)) return formats;
  try {
    // Lightweight regex parse (no DOMParser in SW reliably for all cases)
    const reps = xml.match(/<Representation\b[^>]*>[\s\S]*?<\/Representation>/gi) || [];
    const selfClose = xml.match(/<Representation\b[^>]*\/>/gi) || [];
    const blocks = reps.concat(selfClose);
    for (const block of blocks) {
      const attr = (name) => {
        const m = new RegExp(name + '="([^"]*)"', "i").exec(block);
        return m ? m[1] : "";
      };
      const h = parseInt(attr("height"), 10) || 0;
      const w = parseInt(attr("width"), 10) || 0;
      const bw = parseInt(attr("bandwidth"), 10) || 0;
      const codecs = attr("codecs");
      const mime = attr("mimeType") || "";
      const id = attr("id") || `d-${h}-${bw}`;
      const fr = attr("frameRate");
      let fps = 0;
      if (fr) {
        if (fr.includes("/")) {
          const [a, b] = fr.split("/");
          fps = (parseFloat(a) || 0) / (parseFloat(b) || 1);
        } else fps = parseFloat(fr) || 0;
      }
      let base = "";
      const bu = /<BaseURL[^>]*>([^<]+)<\/BaseURL>/i.exec(block);
      if (bu) base = bu[1].trim();
      const audioOnly = /^audio\//i.test(mime) || (!h && /^audio/i.test(mime));
      formats.push({
        id: `dash-${id}`,
        label: audioOnly ? "Sadece ses" : (h ? mdmHeightLabel(h, fps) : "DASH"),
        height: audioOnly ? null : (h || null),
        width: w || null,
        fps: Math.round(fps) || 0,
        vcodec: audioOnly ? "none" : codecs,
        acodec: audioOnly ? codecs : "",
        bandwidth: bw,
        filesize: null,
        url: base ? mdmAbsUrl(base, mpdUrl) : mpdUrl,
        kind: "dash",
        type: "dash",
        formatId: id,
        extras: { mpdUrl }
      });
    }
  } catch (_) {}
  return formats;
}

function mdmPlayingFormat(videoMeta, mediaUrl) {
  const h = (videoMeta && (videoMeta.videoHeight || videoMeta.height)) || 0;
  const w = (videoMeta && (videoMeta.videoWidth || videoMeta.width)) || 0;
  if (!h && !mediaUrl) return null;
  const http = mediaUrl && /^https?:\/\//i.test(mediaUrl) && mdmClassifyMediaUrl(mediaUrl) === "progressive";
  return {
    id: "playing",
    label: h ? `Oynayan · ${h}p` : "Oynayan",
    height: h || null,
    width: w || null,
    fps: 0,
    vcodec: "",
    acodec: "",
    bandwidth: 0,
    filesize: null,
    url: http ? mediaUrl : "",
    kind: "progressive",
    type: "progressive",
    formatId: ""
  };
}

function mdmMergeFormats(list) {
  const byKey = new Map();
  for (const f of list || []) {
    if (!f) continue;
    const h = f.height || 0;
    const kind = f.kind || f.type || "";
    const fps = f.fps || 0;
    const bw = f.bandwidth || 0;
    const key = f.id === "best" || f.id === "playing"
      ? f.id
      : `${kind}|${h}|${fps}|${(f.vcodec || "").split(".")[0]}`;
    const prev = byKey.get(key);
    if (!prev || (bw > (prev.bandwidth || 0))) {
      // Prefer non-empty url
      if (prev && !f.url && prev.url) f.url = prev.url;
      byKey.set(key, f);
    }
  }

  let formats = [...byKey.values()];

  // Merge "Oynayan" into real height row
  const playing = formats.find(f => f.id === "playing");
  if (playing && playing.height) {
    const twin = formats.find(f => f !== playing && f.height === playing.height && (f.kind === "progressive" || f.type === "progressive" || f.kind === "hls"));
    if (twin) {
      formats = formats.filter(f => f.id !== "playing");
      if (!twin.url && playing.url) twin.url = playing.url;
    }
  }

  // Dedupe same height: keep higher bandwidth; second codec → label suffix
  const heightMap = new Map();
  const out = [];
  for (const f of formats.sort((a, b) => (b.bandwidth || 0) - (a.bandwidth || 0))) {
    if (f.id === "best" || f.id === "playing" || !f.height) {
      out.push(f);
      continue;
    }
    const hk = `${f.height}|${f.fps || 0}`;
    if (!heightMap.has(hk)) {
      heightMap.set(hk, f);
      out.push(f);
    } else {
      const first = heightMap.get(hk);
      const c1 = (first.vcodec || "").split(/[.,]/)[0];
      const c2 = (f.vcodec || "").split(/[.,]/)[0];
      if (c2 && c1 && c2 !== c1 && !/ · /.test(f.label)) {
        f.label = `${f.label} · ${c2}`;
        out.push(f);
      }
    }
  }

  // filesize in label
  for (const f of out) {
    if (f.filesize && f.label && !/MB|GB/i.test(f.label)) {
      const mb = f.filesize / 1048576;
      const size = mb >= 1024 ? `${(mb / 1024).toFixed(1)} GB` : `${Math.round(mb)} MB`;
      f.label = `${f.label} · ${size}`;
    }
  }

  const videos = out.filter(f => f.id !== "best" && (f.height || (f.kind !== "audio" && f.type !== "audio")));
  const audios = out.filter(f => f.kind === "audio" || f.type === "audio" || (!f.height && /ses/i.test(f.label || "")));
  const special = out.filter(f => f.id === "best" || f.id === "playing");

  videos.sort((a, b) => (b.height || 0) - (a.height || 0) || (b.fps || 0) - (a.fps || 0) || (b.bandwidth || 0) - (a.bandwidth || 0));
  audios.sort((a, b) => (b.bandwidth || 0) - (a.bandwidth || 0));

  // Hide < 144p unless only option
  let filtered = videos.filter(f => !f.height || f.height >= 144);
  if (!filtered.length) filtered = videos;

  // Cap ~12
  const capped = filtered.slice(0, 10);

  // Synthetic En iyi
  const result = [];
  const bestSrc = capped.find(f => f.height > 0);
  if (bestSrc && capped.length > 1) {
    result.push({
      id: "best",
      label: "En iyi kalite",
      height: bestSrc.height,
      width: bestSrc.width,
      fps: bestSrc.fps,
      vcodec: bestSrc.vcodec,
      acodec: bestSrc.acodec,
      bandwidth: bestSrc.bandwidth,
      filesize: bestSrc.filesize,
      url: bestSrc.url,
      kind: bestSrc.kind || bestSrc.type || "progressive",
      type: bestSrc.type || bestSrc.kind || "progressive",
      formatId: bestSrc.formatId || (bestSrc.kind === "yt-dlp" ? "bv*+ba/b" : bestSrc.id || ""),
      extras: bestSrc.extras
    });
  }
  for (const s of special) {
    if (s.id === "playing" && !result.some(x => x.id === "playing"))
      result.push(s);
  }
  result.push(...capped.filter(f => f.id !== "playing"));
  result.push(...audios.slice(0, 3));
  return result.slice(0, 12);
}

async function mdmResolveOneUrl(url, pageUrl, headers, cookies, videoMeta) {
  const kind = mdmClassifyMediaUrl(url);
  const hdr = { ...(headers || {}) };
  if (cookies) hdr.Cookie = cookies;

  if (kind === "fragment" || kind === "blob") {
    return { formats: [], protected: false, deferDesktop: true };
  }

  if (kind === "hls") {
    let text = await mdmFetchPlaylist(url, hdr, pageUrl);
    if (!text) return { formats: [], protected: false, deferDesktop: true };
    let parsed = mdmParseHlsFormats(text, url);
    if (parsed.protected) return parsed;
    // Media-only → try guess master
    if (parsed.formats.length <= 1 && !text.includes("#EXT-X-STREAM-INF")) {
      for (const cand of await mdmGuessMasterUrls(url)) {
        const t2 = await mdmFetchPlaylist(cand, hdr, pageUrl, 4000);
        if (t2 && t2.includes("#EXT-X-STREAM-INF")) {
          parsed = mdmParseHlsFormats(t2, cand);
          if (parsed.formats.length > 1) break;
        }
      }
    }
    return { ...parsed, deferDesktop: false };
  }

  if (kind === "dash") {
    const xml = await mdmFetchPlaylist(url, hdr, pageUrl);
    if (!xml) return { formats: [], protected: false, deferDesktop: true };
    const formats = mdmParseDashFormats(xml, url);
    return { formats, protected: false, deferDesktop: formats.length === 0 };
  }

  if (kind === "progressive" || kind === "audio") {
    const h = (videoMeta && videoMeta.videoHeight) || mdmGuessHeightFromUrl(url) || 0;
    const w = (videoMeta && videoMeta.videoWidth) || 0;
    return {
      formats: [{
        id: kind === "audio" ? "audio-1" : `prog-${h || "x"}`,
        label: kind === "audio" ? "Sadece ses" : (h ? mdmHeightLabel(h, 0) : "Video"),
        height: kind === "audio" ? null : (h || null),
        width: w || null,
        fps: 0,
        vcodec: kind === "audio" ? "none" : "",
        acodec: kind === "audio" ? "aac" : "",
        bandwidth: 0,
        filesize: null,
        url,
        kind,
        type: kind,
        formatId: ""
      }],
      protected: false,
      deferDesktop: false
    };
  }

  return { formats: [], protected: false, deferDesktop: true };
}

/**
 * Ana giriş: capture + videoMeta → FormatOption[]
 * candidates: sniff edilen master URL listesi
 */
async function mdmResolveFormats(payload) {
  const pageUrl = payload.pageUrl || "";
  const mediaUrl = payload.mediaUrl || "";
  const headers = payload.headers || {};
  const cookies = payload.cookies || "";
  const videoMeta = payload.videoMeta || {};
  const candidates = [];
  const seen = new Set();
  const push = (u) => {
    const n = mdmNormMediaUrl(u);
    if (!n || seen.has(n)) return;
    seen.add(n);
    candidates.push(u);
  };

  for (const u of (payload.candidates || [])) push(u);
  push(mediaUrl);

  const all = [];
  let protectedHit = false;
  let needDesktop = true;
  const deadline = Date.now() + 8000;

  for (const url of candidates) {
    if (Date.now() > deadline) break;
    const kind = mdmClassifyMediaUrl(url);
    if (kind === "fragment") continue;
    const r = await mdmResolveOneUrl(url, pageUrl, headers, cookies, videoMeta);
    if (r.protected) protectedHit = true;
    if (r.formats && r.formats.length) {
      needDesktop = false;
      for (const f of r.formats) {
        f.pageUrl = pageUrl;
        if (cookies && !f.headers) f.headers = { Cookie: cookies, Referer: pageUrl };
        all.push(f);
      }
    } else if (r.deferDesktop) {
      needDesktop = true;
    }
  }

  const playing = mdmPlayingFormat(videoMeta, mediaUrl);
  if (playing) all.unshift(playing);

  if (protectedHit && all.filter(f => f.id !== "playing").length === 0) {
    return { ok: false, protected: true, error: "Korumalı içerik", formats: [] };
  }

  let formats = mdmMergeFormats(all);

  // Hiç kalite yoksa playing veya tek satır bırak — boş panel yok
  if (!formats.length) {
    if (playing) formats = [playing];
    else if (mediaUrl && /^https?:\/\//i.test(mediaUrl)) {
      formats = [{
        id: "fallback",
        label: "Video",
        height: videoMeta.videoHeight || null,
        url: mediaUrl,
        kind: "progressive",
        type: "progressive",
        formatId: ""
      }];
    }
  }

  if (formats.length) {
    return {
      ok: true,
      title: payload.title || "Video",
      formats,
      deferDesktop: needDesktop && formats.filter(f => f.id !== "playing").length <= 1
    };
  }

  // Tamamen boş → desktop'a bırak
  return null;
}
