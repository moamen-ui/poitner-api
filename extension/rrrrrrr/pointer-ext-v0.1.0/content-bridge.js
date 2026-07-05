"use strict";
(() => {
  // src/content-bridge.ts
  var w = window;
  if (!w.__pointerBridgeMounted) {
    w.__pointerBridgeMounted = true;
    window.addEventListener("message", (e) => {
      if (e.source !== window) return;
      if (e.origin !== window.location.origin) return;
      const d = e.data;
      if (!d || d.source !== "pointer-ext") return;
      chrome.runtime.sendMessage(d, (res) => {
        const r = res || { ok: false, status: 0, body: "", contentType: null };
        window.postMessage({ source: "pointer-ext-res", id: d.id, ...r }, window.location.origin);
      });
    });
  }
})();
