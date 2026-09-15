(function () {
  document.addEventListener("click", function (e) {
    const a = e.target && e.target.closest && e.target.closest("a[href^='magnet:']");
    if (!a || !a.href) return;
    e.preventDefault();
    e.stopPropagation();
    try {
      chrome.runtime.sendMessage({ type: "mdm-handoff", url: a.href, filename: "" });
    } catch (_) { /* ignore */ }
  }, true);
})();
