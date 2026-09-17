import {
  DEFAULT_SERVER, SESSION_KEYS, LOCAL_KEYS, hostnameOf,
  type BgRequest, type ProxyRequest, type ProxyResponse, type StoredUser, type ExtProject,
} from './shared';
import { injectMain } from './inject-main';

// ---- token / prefs -------------------------------------------------------
async function getToken(): Promise<string | null> {
  const s = await chrome.storage.session.get(SESSION_KEYS.token);
  return (s[SESSION_KEYS.token] as string) || null;
}
async function getServer(): Promise<string> {
  const s = await chrome.storage.local.get(LOCAL_KEYS.server);
  return ((s[LOCAL_KEYS.server] as string) || DEFAULT_SERVER).replace(/\/$/, '');
}
async function getProjectMap(): Promise<Record<string, { project: string; environment: string }>> {
  const s = await chrome.storage.local.get(LOCAL_KEYS.projectByDomain);
  return (s[LOCAL_KEYS.projectByDomain] as Record<string, { project: string; environment: string }>) || {};
}

// Authenticated call to the Pointer server from the background (the ONLY place that holds the JWT).
// Used for popup control actions (list/create projects, extension-activate entitlement gate).
async function apiFetch(path: string, init: RequestInit = {}): Promise<{ ok: boolean; status: number; data: any; message: string | null }> {
  const server = await getServer();
  const token = await getToken();
  const headers: Record<string, string> = { 'Content-Type': 'application/json', ...(init.headers as Record<string, string> || {}) };
  if (token) headers.Authorization = `Bearer ${token}`;
  let status = 0; let body: any = null;
  try {
    const r = await fetch(server + path, { ...init, headers });
    status = r.status;
    body = await r.json().catch(() => null);
  } catch { /* network error → status stays 0 */ }
  // Envelope-aware: Result<T> wraps payload in `data` and carries `message` on failure.
  return { ok: status >= 200 && status < 300, status, data: body?.data ?? body, message: body?.message ?? null };
}

// ---- CSP bypass (per-tab session DNR rule) -------------------------------
// Removing the CSP header lets the page load the remote widget.js/widget.css.
// Scoped to the activated tab and removed on deactivate / tab close.
const CSP_RULE_BASE = 100000;
const ruleId = (tabId: number) => CSP_RULE_BASE + tabId;

async function addCspBypass(tabId: number): Promise<void> {
  await chrome.declarativeNetRequest.updateSessionRules({
    removeRuleIds: [ruleId(tabId)],
    addRules: [{
      id: ruleId(tabId),
      priority: 1,
      action: {
        type: chrome.declarativeNetRequest.RuleActionType.MODIFY_HEADERS,
        responseHeaders: [
          { header: 'content-security-policy', operation: chrome.declarativeNetRequest.HeaderOperation.REMOVE },
          { header: 'content-security-policy-report-only', operation: chrome.declarativeNetRequest.HeaderOperation.REMOVE },
        ],
      },
      condition: {
        tabIds: [tabId],
        // Restrict to MAIN_FRAME only — sub-frames (including cross-origin iframes)
        // do not need their CSP removed, and stripping them is unnecessary over-reach (3.2).
        resourceTypes: [
          chrome.declarativeNetRequest.ResourceType.MAIN_FRAME,
        ],
      },
    }],
  });
}
async function removeCspBypass(tabId: number): Promise<void> {
  await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [ruleId(tabId)] });
}
async function isActive(tabId: number): Promise<boolean> {
  const rules = await chrome.declarativeNetRequest.getSessionRules();
  return rules.some((r) => r.id === ruleId(tabId));
}
// Reload every currently-activated tab. Called after a fresh sign-in so an already-injected
// widget (which may be sitting on its own login prompt after a 401) re-injects with the new
// token — the widget picks up auth without the user manually refreshing.
async function reloadActiveTabs(): Promise<void> {
  const rules = await chrome.declarativeNetRequest.getSessionRules();
  const tabIds = rules.filter((r) => r.id >= CSP_RULE_BASE).map((r) => r.id - CSP_RULE_BASE);
  for (const id of tabIds) {
    try { await chrome.tabs.reload(id); } catch { /* tab may be gone */ }
  }
}
// The inverse of reloadActiveTabs — called on sign-out so every activated tab actually leaves the
// page instead of sitting there re-injected-but-unauthenticated (401 → the widget's own login modal,
// with no way to dismiss it since the popup's "Deactivate on this tab" button only exists once
// signed back in).
async function deactivateAllTabs(): Promise<void> {
  const rules = await chrome.declarativeNetRequest.getSessionRules();
  const tabIds = rules.filter((r) => r.id >= CSP_RULE_BASE).map((r) => r.id - CSP_RULE_BASE);
  for (const id of tabIds) {
    try { await deactivate(id); } catch { /* tab may be gone */ }
  }
}

// ---- activation ----------------------------------------------------------
// Tabs waiting for their post-reload 'complete' so we inject exactly once.
// Persisted in chrome.storage.session (not in-memory) so a terminated and
// re-awakened MV3 service worker can still complete the injection (fix 3.1).
const SESSION_PENDING = 'pendingInject';

// First activation on a site: the popup must call chrome.permissions.request (user gesture), and
// Chrome closes the popup the moment its prompt opens — the click handler never resumes, so the
// 'activate' message was never sent and "nothing happened". The popup therefore STAGES the
// activation before asking, and the background finishes it from chrome.permissions.onAdded.
const SESSION_STAGED = 'stagedActivation';
interface StagedActivation { tabId: number; hostname: string; origin: string; project: string; environment: string; at: number }
async function getStaged(): Promise<StagedActivation | null> {
  const r = await chrome.storage.session.get(SESSION_STAGED);
  return (r[SESSION_STAGED] as StagedActivation) || null;
}
async function setStaged(s: StagedActivation | null): Promise<void> {
  if (s) await chrome.storage.session.set({ [SESSION_STAGED]: s });
  else await chrome.storage.session.remove(SESSION_STAGED);
}
// Popup-alive + onAdded can both try to activate the same tab within a second; the second run
// would reload the tab a second time. Remember the last completed activation per tab.
const recentlyActivated = new Map<number, { project: string; at: number }>();

async function runActivation(m: { tabId: number; hostname: string; origin: string; project: string; environment: string }): Promise<{ ok: boolean; error?: string }> {
  const recent = recentlyActivated.get(m.tabId);
  if (recent && recent.project === m.project && Date.now() - recent.at < 5000) return { ok: true };
  const su = (await chrome.storage.local.get(LOCAL_KEYS.user))[LOCAL_KEYS.user] as StoredUser | null;
  if (su?.isSuperAdmin) return { ok: false, error: 'Super admin accounts can’t use Pointer here — sign in with a workspace account instead.' };
  if (!(await hasHostPermission(m.origin))) {
    return { ok: false, error: 'Permission for this site was not granted — click Activate again and allow access when Chrome asks.' };
  }
  const gate = await apiFetch('/api/extension/activate', {
    method: 'POST',
    body: JSON.stringify({ projectKey: m.project, origin: m.origin }),
  });
  if (!gate.ok) {
    const reason = gate.status === 404 ? 'Project not found in your workspace.'
      : (gate.message || 'The browser extension is not available on your current plan.');
    return { ok: false, error: reason };
  }
  await setStaged(null);
  recentlyActivated.set(m.tabId, { project: m.project, at: Date.now() });
  await activate(m.tabId, m.hostname, m.project, m.environment);
  return { ok: true };
}

chrome.permissions.onAdded.addListener((added) => {
  (async () => {
    const staged = await getStaged();
    if (!staged) return;
    if (Date.now() - staged.at > 5 * 60 * 1000) { await setStaged(null); return; }
    const origins = added.origins || [];
    if (!origins.some((o) => o.startsWith(`${staged.origin}/`) || o === `${staged.origin}/*` || o === '<all_urls>')) return;
    await runActivation(staged);
  })().catch(() => { /* best effort — the popup path still works on the next click */ });
});

async function getPendingInject(): Promise<Set<number>> {
  const s = await chrome.storage.session.get(SESSION_PENDING);
  return new Set<number>((s[SESSION_PENDING] as number[]) || []);
}
async function addPendingInject(tabId: number): Promise<void> {
  const set = await getPendingInject();
  set.add(tabId);
  await chrome.storage.session.set({ [SESSION_PENDING]: Array.from(set) });
}
async function removePendingInject(tabId: number): Promise<void> {
  const set = await getPendingInject();
  set.delete(tabId);
  await chrome.storage.session.set({ [SESSION_PENDING]: Array.from(set) });
}

async function activate(tabId: number, hostname: string, project: string, environment: string): Promise<void> {
  const map = await getProjectMap();
  map[hostname] = { project, environment };
  await chrome.storage.local.set({ [LOCAL_KEYS.projectByDomain]: map });
  await addCspBypass(tabId);
  await addPendingInject(tabId);
  await chrome.tabs.reload(tabId);
}

// Defense in depth: the popup is responsible for requesting this origin's host permission (a
// user-gesture requirement chrome.permissions.request can't satisfy from here), but every path
// into activate() ultimately comes from that same message, so double-check it actually landed
// before wiring up a CSP-bypass rule that would otherwise silently do nothing for this origin.
async function hasHostPermission(origin: string): Promise<boolean> {
  try { return await chrome.permissions.contains({ origins: [`${origin}/*`] }); }
  catch { return false; }
}

async function deactivate(tabId: number): Promise<void> {
  await removeCspBypass(tabId);
  await removePendingInject(tabId);
  try { await chrome.tabs.reload(tabId); } catch { /* tab may be gone */ }
}

async function injectInto(tabId: number, url: string): Promise<void> {
  const server = await getServer();
  const localUser = await chrome.storage.local.get(LOCAL_KEYS.user);
  const user = (localUser[LOCAL_KEYS.user] as StoredUser) || null;
  const map = await getProjectMap();
  const entry = map[hostnameOf(url)];
  if (!entry) return;
  // Only the display name plus three non-PII facts reach the page — email and role name stay out
  // (fix 1.3). The opaque id lets the widget mark the viewer's own comments (so the author sees the
  // verify buttons), isAdmin extends that to admins, isQuickAccess hides backlog actions.
  const displayName: string | undefined = user?.displayName || undefined;
  const userId: string | undefined = user?.id ? String(user.id) : undefined;
  const isAdmin = !!user?.isAdmin;
  const isQuickAccess = !!user?.isQuickAccess;
  // snapdom (screenshot capture) stays bundled — it changes rarely, unlike widget.js/css below.
  const snapdomUrl = chrome.runtime.getURL('vendor/snapdom.js');
  // 1) Isolated bridge (relays proxied requests). 2) MAIN-world config + host mount — injectMain
  // itself appends <script src>/<link> tags pointing at the server's live widget.js/widget.css,
  // exactly like the plain (non-extension) widget embed does. That's page-context code, not
  // extension-privileged code, so it isn't "remotely hosted code" under MV3's policy — and it means
  // a CSS tweak or a small widget fix ships by deploying the server, no extension update needed.
  await chrome.scripting.executeScript({ target: { tabId }, files: ['content-bridge.js'] });
  await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: injectMain,
    args: [{ server, project: entry.project, environment: entry.environment, displayName, userId, isAdmin, isQuickAccess, snapdomUrl }],
  });
}

chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (info.status !== 'complete' || !tab.url) return;
  const url = tab.url;
  (async () => {
    // Re-inject on EVERY load of an active tab, not just the first. A hard reload resets the
    // page's MAIN world and drops the widget; `pendingInject` only covers the initial
    // activate-reload, so without the `isActive` check a reload leaves an activated tab bare.
    // (SPA soft-navigations that don't fire 'complete' are handled by the MutationObserver the
    // injected code installs — it re-appends the widget host if the app evicts it.)
    const pending = await getPendingInject();
    const wasPending = pending.has(tabId);
    if (wasPending) await removePendingInject(tabId);
    if (wasPending || await isActive(tabId)) await injectInto(tabId, url);
  })().catch((e) => console.error('[pointer-ext] inject failed', e));
});

chrome.tabs.onRemoved.addListener((tabId) => {
  removeCspBypass(tabId).catch(() => {});
  removePendingInject(tabId).catch(() => {});
});

// ---- login ---------------------------------------------------------------
async function login(email: string, password: string, server: string) {
  const base = server.replace(/\/$/, '');
  const r = await fetch(`${base}/api/auth/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  }).catch(() => null);
  const env = r ? await r.json().catch(() => null) : null;
  const data = env && env.data;
  if (data && data.status === 'ok' && data.token) {
    await chrome.storage.session.set({ [SESSION_KEYS.token]: data.token });
    await chrome.storage.local.set({ [LOCAL_KEYS.server]: base, [LOCAL_KEYS.user]: data.user || null });
    return { ok: true, user: data.user || null };
  }
  return { ok: false, status: (data && data.status) || 'error', message: (env && env.message) || 'Login failed' };
}

// ---- proxied API traffic (from the page via the content bridge) ----------

// Allowed HTTP methods for regular fetch proxying.
// Every method the widget uses against /api/: PATCH (status/edit), DELETE (delete comment), PUT
// (settings such as the comment shortcut). Anything else is refused as `blocked` (status 0).
const ALLOWED_METHODS = new Set(['GET', 'POST', 'PATCH', 'PUT', 'DELETE']);

/**
 * Validate that a URL is safe to proxy:
 * - origin must match the configured Pointer server
 * - path must start with /api/
 * - method (for fetch requests) must be GET, POST, or PATCH
 * Returns the trusted server origin if valid, null if it should be blocked.
 */
async function validateProxyUrl(url: string, method?: string): Promise<string | null> {
  const server = await getServer(); // already strips trailing slash
  let parsed: URL;
  try { parsed = new URL(url); } catch { return null; }
  if (parsed.origin !== new URL(server).origin) return null;
  if (!parsed.pathname.startsWith('/api/')) return null;
  if (method !== undefined && !ALLOWED_METHODS.has(method.toUpperCase())) return null;
  return server;
}

async function handleProxy(msg: ProxyRequest): Promise<ProxyResponse> {
  const blocked: ProxyResponse = { ok: false, status: 0, body: 'blocked', contentType: null };

  if (msg.kind === 'upload') {
    // Validate: must be POST to the trusted server's /api/ path
    const trusted = await validateProxyUrl(msg.url, 'POST');
    if (!trusted) return blocked;

    const token = await getToken();
    const bytes = Uint8Array.from(atob(msg.base64), (c) => c.charCodeAt(0));
    const fd = new FormData();
    fd.append('file', new Blob([bytes], { type: msg.contentType }), msg.filename);
    fd.append('project', msg.project);
    // Only attach the token when the URL is the trusted server — the allowlist
    // check above guarantees this, but we make it explicit here.
    const r = await fetch(msg.url, { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: fd });
    return { ok: r.ok, status: r.status, body: await r.text(), contentType: r.headers.get('content-type') };
  }

  // kind === 'fetch'
  // Validate origin + path + method. IGNORE the page-supplied `auth` flag (#2 fix):
  // the background decides whether to attach the token based solely on the allowlist match.
  const trusted = await validateProxyUrl(msg.url, msg.method);
  if (!trusted) return blocked;

  const token = await getToken();
  // Strip any Authorization header the page may have supplied; the background is
  // the sole authority on whether and which token rides the request.
  const headers: Record<string, string> = { ...(msg.headers || {}) };
  delete headers.authorization;
  delete headers.Authorization;
  // Attach the real token — we only reach here when the URL is our trusted server.
  if (token) headers.Authorization = `Bearer ${token}`;

  const r = await fetch(msg.url, { method: msg.method, headers, body: msg.body ?? undefined });
  return { ok: r.ok, status: r.status, body: await r.text(), contentType: r.headers.get('content-type') };
}

// ---- message router ------------------------------------------------------
chrome.runtime.onMessage.addListener((msg: BgRequest | ProxyRequest, _sender, sendResponse) => {
  // Page proxy traffic
  if ((msg as ProxyRequest).source === 'pointer-ext') {
    handleProxy(msg as ProxyRequest)
      .then(sendResponse)
      .catch(() => sendResponse({ ok: false, status: 0, body: '', contentType: null }));
    return true;
  }
  // Popup / options control messages
  const m = msg as BgRequest;
  (async () => {
    switch (m.type) {
      case 'getState': {
        return { server: await getServer(), user: (await chrome.storage.local.get(LOCAL_KEYS.user))[LOCAL_KEYS.user] || null, hasToken: !!(await getToken()) };
      }
      case 'getTabState': {
        const map = await getProjectMap();
        const staged = await getStaged();
        const stagedHere = staged && staged.hostname === m.hostname ? { project: staged.project, environment: staged.environment } : null;
        return { active: await isActive(m.tabId), remembered: map[m.hostname] || stagedHere || null };
      }
      case 'stageActivation': {
        await setStaged({ tabId: m.tabId, hostname: m.hostname, origin: m.origin, project: m.project, environment: m.environment, at: Date.now() });
        return { ok: true };
      }
      case 'deactivate': { await deactivate(m.tabId); return { ok: true }; }
      case 'login': {
        const res = await login(m.email, m.password, m.server);
        // On success, refresh any activated tabs so their widget re-injects authenticated.
        if ((res as { ok?: boolean }).ok) await reloadActiveTabs();
        return res;
      }
      case 'logout': {
        await chrome.storage.session.remove(SESSION_KEYS.token);
        await chrome.storage.local.remove(LOCAL_KEYS.user);
        await deactivateAllTabs();
        return { ok: true };
      }
      case 'setServer': { await chrome.storage.local.set({ [LOCAL_KEYS.server]: m.server.replace(/\/$/, '') }); return { ok: true }; }
      case 'setProjectForDomain': {
        const map = await getProjectMap();
        const prev = map[m.hostname];
        map[m.hostname] = { project: m.project, environment: prev?.environment || 'staging' };
        await chrome.storage.local.set({ [LOCAL_KEYS.projectByDomain]: map });
        return { ok: true };
      }
      case 'listProjects': {
        // A super admin owns no tenant, so GET /api/admin/projects returns every tenant's projects
        // at once (by design, for platform management) rather than "the caller's workspace" — showing
        // that list here would look like duplicate project keys and can never activate (see 'activate').
        const su = (await chrome.storage.local.get(LOCAL_KEYS.user))[LOCAL_KEYS.user] as StoredUser | null;
        if (su?.isSuperAdmin) return { ok: false, projects: [], error: 'Super admin accounts can’t use Pointer here — sign in with a workspace account instead.' };
        const r = await apiFetch('/api/admin/projects', { method: 'GET' });
        if (!r.ok) return { ok: false, projects: [], error: r.message || 'Could not load projects.' };
        const projects = (Array.isArray(r.data) ? r.data : [])
          .filter((p: any) => p && p.key && p.isActive !== false)
          .map((p: any) => ({ key: p.key, name: p.name || p.key, isActive: p.isActive !== false }));
        return { ok: true, projects };
      }
      case 'projectForOrigin': {
        // For quick-access (Client) accounts: a single scoped lookup by the tab's own origin,
        // instead of listProjects's tenant-wide browse (which they're barred from).
        const r = await apiFetch(`/api/extension/project-for-origin?origin=${encodeURIComponent(m.origin)}`, { method: 'GET' });
        if (!r.ok) return { ok: false, project: null, error: r.message || 'No project is set up for this site.' };
        return { ok: true, project: { key: r.data.key, name: r.data.name, isActive: true } as ExtProject };
      }
      case 'createProject': {
        // Stamp the AppUrl from wherever the project was created — otherwise a project created
        // through the extension has no recorded site at all, and the next visit here can't be
        // auto-matched (see 'projectForOrigin' / activate defaults).
        const r = await apiFetch('/api/admin/projects', { method: 'POST', body: JSON.stringify({ key: m.key, name: m.name, appUrl: m.appUrl || null }) });
        if (!r.ok) return { ok: false, error: r.message || (r.status === 409 ? 'A project with that key already exists.' : 'Could not create project.') };
        return { ok: true, project: { key: m.key, name: m.name, isActive: true } };
      }
      case 'activate': {
        // Super-admin guard, host-permission check, entitlement gate (/api/extension/activate) and the
        // injection itself live in runActivation so the permissions.onAdded path shares them.
        return runActivation({ tabId: m.tabId, hostname: m.hostname, origin: m.origin, project: m.project, environment: m.environment });
      }
      default: return { ok: false, error: 'unknown message' };
    }
  })().then(sendResponse).catch((e) => sendResponse({ ok: false, error: String(e) }));
  return true;
});
