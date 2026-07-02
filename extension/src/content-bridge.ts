// Isolated-world content script. Bridges the page's MAIN-world transport
// (window.postMessage) to the background service worker (chrome.runtime), which
// holds the real token and performs the cross-origin fetch free of page CSP.
//
// Security: only accept messages that come from THIS window (e.source === window)
// and from THIS origin (e.origin === location.origin). Cross-origin sibling iframes
// cannot trigger the bridge. Responses are sent with an explicit targetOrigin so
// they are never visible to cross-origin frames (fix for review 6.1).
//
// Idempotency guard: the background re-injects this bridge on every load of an active tab,
// and an SPA can fire more than one 'complete' for the SAME document — without this guard each
// injection adds another message listener, so ONE comment POST would be relayed (and saved) N
// times. Register exactly once per isolated-world instance.
const w = window as unknown as { __pointerBridgeMounted?: boolean };
if (!w.__pointerBridgeMounted) {
  w.__pointerBridgeMounted = true;
  window.addEventListener('message', (e: MessageEvent) => {
    // Reject anything that did not originate from our own page window (fix 6.1 / defense-in-depth).
    if (e.source !== window) return;
    if (e.origin !== window.location.origin) return;
    const d = e.data;
    if (!d || d.source !== 'pointer-ext') return;
    chrome.runtime.sendMessage(d, (res) => {
      // On error (e.g. SW asleep) reply with a synthetic 0 so the page promise settles.
      const r = res || { ok: false, status: 0, body: '', contentType: null };
      // Use explicit targetOrigin so the response is not readable by cross-origin frames.
      window.postMessage({ source: 'pointer-ext-res', id: d.id, ...r }, window.location.origin);
    });
  });
}
