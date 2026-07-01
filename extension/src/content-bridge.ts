// Isolated-world content script. Bridges the page's MAIN-world transport
// (window.postMessage) to the background service worker (chrome.runtime), which
// holds the real token and performs the cross-origin fetch free of page CSP.
window.addEventListener('message', (e: MessageEvent) => {
  const d = e.data;
  if (!d || d.source !== 'pointer-ext') return;
  chrome.runtime.sendMessage(d, (res) => {
    // On error (e.g. SW asleep) reply with a synthetic 0 so the page promise settles.
    const r = res || { ok: false, status: 0, body: '', contentType: null };
    window.postMessage({ source: 'pointer-ext-res', id: d.id, ...r }, '*');
  });
});
