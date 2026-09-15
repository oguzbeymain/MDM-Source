// MDM — tab/frame capture store (TTL 90s) — IDM-benzeri: URL + playlist body
const MDM_CAPTURE_TTL_MS = 90000;
const MDM_MAX_CAPTURES_PER_TAB = 80;

const mdmCaptureBuckets = new Map(); // tabId -> Map(frameId -> captures[])

function mdmGetBucket(tabId, frameId) {
  if (!mdmCaptureBuckets.has(tabId))
    mdmCaptureBuckets.set(tabId, new Map());
  const tab = mdmCaptureBuckets.get(tabId);
  if (!tab.has(frameId)) tab.set(frameId, []);
  return tab.get(frameId);
}

function mdmPruneBucket(list) {
  const now = Date.now();
  const kept = list.filter(c => now - c.timeStamp < MDM_CAPTURE_TTL_MS);
  return kept.slice(-MDM_MAX_CAPTURES_PER_TAB);
}

function mdmUpsertCapture(capture) {
  const list = mdmGetBucket(capture.tabId, capture.frameId || 0);
  const norm = mdmNormalizeUrl(capture.finalUrl || capture.url);
  const idx = list.findIndex(c => mdmNormalizeUrl(c.finalUrl || c.url) === norm);
  if (idx >= 0) {
    const prev = list[idx];
    list[idx] = {
      ...prev,
      ...capture,
      // body korunur: yeni kayıtta body yoksa eskisi kalsın
      body: capture.body || prev.body || "",
      isMaster: capture.isMaster != null ? capture.isMaster : prev.isMaster,
      timeStamp: Date.now()
    };
  } else {
    list.push({ ...capture, timeStamp: Date.now() });
  }
  const pruned = mdmPruneBucket(list);
  mdmCaptureBuckets.get(capture.tabId).set(capture.frameId || 0, pruned);
  return list[list.length - 1];
}

function mdmGetCaptures(tabId, frameId) {
  const tab = mdmCaptureBuckets.get(tabId);
  if (!tab) return [];
  if (frameId != null) return mdmPruneBucket(tab.get(frameId) || []);
  const all = [];
  for (const list of tab.values()) all.push(...mdmPruneBucket(list));
  return all;
}

function mdmClearTab(tabId) {
  mdmCaptureBuckets.delete(tabId);
}

function mdmClearFrame(tabId, frameId) {
  const tab = mdmCaptureBuckets.get(tabId);
  if (tab) tab.delete(frameId);
}

/** En iyi HLS/DASH adayı: master body > master URL > media body > son hls/dash */
function mdmPickBestMediaCapture(tabId) {
  const all = mdmGetCaptures(tabId).filter(c => c.kind === "hls" || c.kind === "dash");
  if (!all.length) return null;

  const score = (c) => {
    let s = (c.timeStamp || 0) / 1e10; // yenilik
    const body = c.body || "";
    const url = c.finalUrl || c.url || "";
    if (typeof mdmIsJunkMediaUrl === "function" && mdmIsJunkMediaUrl(url)) s -= 200;
    if (c.kind === "hls") s += 10;
    if (c.kind === "dash") s += 8;
    if (body.includes("#EXT-X-STREAM-INF") || /<Representation[\s>]/i.test(body)) s += 100;
    else if (body.includes("#EXTM3U") || /<MPD[\s>]/i.test(body)) s += 40;
    if (c.isMaster) s += 50;
    if (/\.m3u8?/i.test(url)) s += 30;
    if (/\/(master|index|playlist|manifest)\.(m3u8?|txt)(\?|$)/i.test(url)) s += 40;
    if (/\.mpd(\?|$)/i.test(url)) s += 28;
    if (/master|index|playlist|manifest/i.test(url)) s += 15;
    if (/\/txt\/sublist|\.vtt|\.srt/i.test(url)) s -= 80;
    return s;
  };

  all.sort((a, b) => score(b) - score(a));
  return all[0];
}

function mdmAnnotatePlaylistBody(url, body, kindHint) {
  const text = body || "";
  let kind = kindHint || "hls";
  if (/<MPD[\s>]/i.test(text) || /\.mpd/i.test(url || "")) kind = "dash";
  else if (/#EXTM3U/i.test(text) || /\.m3u8?/i.test(url || "") || /\/(master|index|playlist)\.txt/i.test(url || "") || /\/hls\//i.test(url || "")) kind = "hls";
  const isMaster =
    text.includes("#EXT-X-STREAM-INF") ||
    /<Representation[\s>]/i.test(text) ||
    /master|index\.(m3u8|txt)|playlist\.(m3u8|txt)/i.test(url || "");
  return { kind, isMaster, body: text.slice(0, 512000) };
}
