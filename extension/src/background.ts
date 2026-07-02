import {
  DEFAULT_SERVER, SESSION_KEYS, LOCAL_KEYS, hostnameOf,
  type BgRequest, type ProxyRequest, type ProxyResponse, type StoredUser,
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
// Removing the CSP header lets the page load the remote pointer.js/pointer.css.
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

// ---- activation ----------------------------------------------------------
// Tabs waiting for their post-reload 'complete' so we inject exactly once.
// Persisted in chrome.storage.session (not in-memory) so a terminated and
// re-awakened MV3 service worker can still complete the injection (fix 3.1).
const SESSION_PENDING = 'pendingInject';

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
  // Only pass the display name into the page — email and role are PII (fix 1.3).
  const displayName: string | undefined = user?.displayName || undefined;
  // Isolated bridge first (relays the page's proxied requests), then the
  // MAIN-world config + widget loader.
  await chrome.scripting.executeScript({ target: { tabId }, files: ['content-bridge.js'] });
  await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: injectMain,
    args: [{ server, project: entry.project, environment: entry.environment, displayName }],
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
const ALLOWED_METHODS = new Set(['GET', 'POST', 'PATCH']);

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
        return { active: await isActive(m.tabId), remembered: map[m.hostname] || null };
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
        // Any signed-in user; the API scopes to the caller's tenant. Return active projects only.
        const r = await apiFetch('/api/admin/projects', { method: 'GET' });
        if (!r.ok) return { ok: false, projects: [], error: r.message || 'Could not load projects.' };
        const projects = (Array.isArray(r.data) ? r.data : [])
          .filter((p: any) => p && p.key && p.isActive !== false)
          .map((p: any) => ({ key: p.key, name: p.name || p.key, isActive: p.isActive !== false }));
        return { ok: true, projects };
      }
      case 'createProject': {
        const r = await apiFetch('/api/admin/projects', { method: 'POST', body: JSON.stringify({ key: m.key, name: m.name }) });
        if (!r.ok) return { ok: false, error: r.message || (r.status === 409 ? 'A project with that key already exists.' : 'Could not create project.') };
        return { ok: true, project: { key: m.key, name: m.name, isActive: true } };
      }
      case 'activate': {
        // Entitlement gate + site recording: /api/extension/activate enforces ExtensionEnabled and
        // MaxExtensionSites (inert while the enforcement kill-switch is off) and validates the project
        // exists. Block injection — and surface why — when the plan denies it.
        const gate = await apiFetch('/api/extension/activate', {
          method: 'POST',
          body: JSON.stringify({ projectKey: m.project, origin: m.origin }),
        });
        if (!gate.ok) {
          const reason = gate.status === 404 ? 'Project not found in your workspace.'
            : (gate.message || 'The browser extension is not available on your current plan.');
          return { ok: false, error: reason };
        }
        await activate(m.tabId, m.hostname, m.project, m.environment);
        return { ok: true };
      }
      default: return { ok: false, error: 'unknown message' };
    }
  })().then(sendResponse).catch((e) => sendResponse({ ok: false, error: String(e) }));
  return true;
});
