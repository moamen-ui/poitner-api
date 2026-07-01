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
        resourceTypes: [
          chrome.declarativeNetRequest.ResourceType.MAIN_FRAME,
          chrome.declarativeNetRequest.ResourceType.SUB_FRAME,
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

// ---- activation ----------------------------------------------------------
// Tabs waiting for their post-reload 'complete' so we inject exactly once.
const pendingInject = new Set<number>();

async function activate(tabId: number, hostname: string, project: string, environment: string): Promise<void> {
  const map = await getProjectMap();
  map[hostname] = { project, environment };
  await chrome.storage.local.set({ [LOCAL_KEYS.projectByDomain]: map });
  await addCspBypass(tabId);
  pendingInject.add(tabId);
  await chrome.tabs.reload(tabId);
}

async function deactivate(tabId: number): Promise<void> {
  await removeCspBypass(tabId);
  pendingInject.delete(tabId);
  try { await chrome.tabs.reload(tabId); } catch { /* tab may be gone */ }
}

async function injectInto(tabId: number, url: string): Promise<void> {
  const server = await getServer();
  const localUser = await chrome.storage.local.get(LOCAL_KEYS.user);
  const user = (localUser[LOCAL_KEYS.user] as StoredUser) || null;
  const map = await getProjectMap();
  const entry = map[hostnameOf(url)];
  if (!entry) return;
  // Isolated bridge first (relays the page's proxied requests), then the
  // MAIN-world config + widget loader.
  await chrome.scripting.executeScript({ target: { tabId }, files: ['content-bridge.js'] });
  await chrome.scripting.executeScript({
    target: { tabId },
    world: 'MAIN',
    func: injectMain,
    args: [{ server, project: entry.project, environment: entry.environment, user }],
  });
}

chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
  if (info.status !== 'complete' || !pendingInject.has(tabId)) return;
  pendingInject.delete(tabId);
  if (tab.url) injectInto(tabId, tab.url).catch((e) => console.error('[pointer-ext] inject failed', e));
});

chrome.tabs.onRemoved.addListener((tabId) => { removeCspBypass(tabId).catch(() => {}); });

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
async function handleProxy(msg: ProxyRequest): Promise<ProxyResponse> {
  const token = await getToken();
  if (msg.kind === 'upload') {
    const bytes = Uint8Array.from(atob(msg.base64), (c) => c.charCodeAt(0));
    const fd = new FormData();
    fd.append('file', new Blob([bytes], { type: msg.contentType }), msg.filename);
    fd.append('project', msg.project);
    const r = await fetch(msg.url, { method: 'POST', headers: token ? { Authorization: `Bearer ${token}` } : {}, body: fd });
    return { ok: r.ok, status: r.status, body: await r.text(), contentType: r.headers.get('content-type') };
  }
  const headers: Record<string, string> = { ...(msg.headers || {}) };
  if (msg.auth) {
    delete headers.authorization;
    if (token) headers.Authorization = `Bearer ${token}`; else delete headers.Authorization;
  }
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
      case 'login': return login(m.email, m.password, m.server);
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
      case 'activate': { await activate(m.tabId, m.hostname, m.project, m.environment); return { ok: true }; }
      default: return { ok: false, error: 'unknown message' };
    }
  })().then(sendResponse).catch((e) => sendResponse({ ok: false, error: String(e) }));
  return true;
});
