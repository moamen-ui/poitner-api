"use strict";
(() => {
  // src/shared.ts
  var DEFAULT_SERVER = "https://api.pointer.moamen.work";
  var SESSION_KEYS = { token: "token" };
  var LOCAL_KEYS = {
    server: "server",
    user: "user",
    projectByDomain: "projectByDomain"
    // { [hostname]: projectKey }
  };
  function hostnameOf(url) {
    try {
      return new URL(url).hostname;
    } catch {
      return "";
    }
  }

  // src/inject-main.ts
  function injectMain(cfg) {
    const w = window;
    if (w.__pointerExtMounted) return;
    w.__pointerExtMounted = true;
    const PROXY_TOKEN = "__pointer_via_proxy__";
    const pending = {};
    let counter = 0;
    window.addEventListener("message", (e) => {
      if (e.origin !== window.location.origin) return;
      const d = e.data;
      if (!d || d.source !== "pointer-ext-res") return;
      const resolve = pending[d.id];
      if (!resolve) return;
      delete pending[d.id];
      resolve(new Response(d.body, {
        status: d.status || 0,
        headers: d.contentType ? { "Content-Type": d.contentType } : {}
      }));
    });
    w.__POINTER_FETCH__ = (url, opts) => {
      opts = opts || {};
      return new Promise((resolve, reject) => {
        const id = ++counter;
        pending[id] = resolve;
        const body = opts.body;
        if (typeof FormData !== "undefined" && body instanceof FormData) {
          const file = body.get("file");
          const project = body.get("project") || cfg.project;
          const reader = new FileReader();
          reader.onload = () => {
            const base64 = String(reader.result).split(",")[1] || "";
            window.postMessage({
              source: "pointer-ext",
              kind: "upload",
              id,
              url,
              base64,
              filename: file && file.name || "screenshot",
              contentType: file && file.type || "application/octet-stream",
              project
            }, window.location.origin);
          };
          reader.onerror = () => {
            delete pending[id];
            reject(new Error("read failed"));
          };
          reader.readAsDataURL(file);
        } else {
          const headers = opts.headers || {};
          const auth = !!(headers.Authorization || headers.authorization);
          window.postMessage({
            source: "pointer-ext",
            kind: "fetch",
            id,
            url,
            method: opts.method || "GET",
            headers,
            body: typeof body === "string" ? body : null,
            auth
          }, window.location.origin);
        }
      });
    };
    w.__POINTER_CONFIG__ = {
      server: cfg.server,
      project: cfg.project,
      environment: cfg.environment,
      token: PROXY_TOKEN,
      // Only expose the display name — email and roleName are PII and not needed by the widget (1.3).
      user: cfg.displayName ? { displayName: cfg.displayName } : void 0,
      proxy: true,
      // Bundled-asset URLs (extension origin) so the widget never loads code/CSS from the server.
      cssUrl: cfg.cssUrl,
      snapdomUrl: cfg.snapdomUrl
    };
    const mount = () => {
      if (!document.querySelector("pointer-feedback")) {
        (document.body || document.documentElement).appendChild(document.createElement("pointer-feedback"));
      }
    };
    mount();
    const observer = new MutationObserver(() => mount());
    observer.observe(document.documentElement, { childList: true, subtree: true });
  }

  // src/background.ts
  async function getToken() {
    const s = await chrome.storage.session.get(SESSION_KEYS.token);
    return s[SESSION_KEYS.token] || null;
  }
  async function getServer() {
    const s = await chrome.storage.local.get(LOCAL_KEYS.server);
    return (s[LOCAL_KEYS.server] || DEFAULT_SERVER).replace(/\/$/, "");
  }
  async function getProjectMap() {
    const s = await chrome.storage.local.get(LOCAL_KEYS.projectByDomain);
    return s[LOCAL_KEYS.projectByDomain] || {};
  }
  async function apiFetch(path, init = {}) {
    const server = await getServer();
    const token = await getToken();
    const headers = { "Content-Type": "application/json", ...init.headers || {} };
    if (token) headers.Authorization = `Bearer ${token}`;
    let status = 0;
    let body = null;
    try {
      const r = await fetch(server + path, { ...init, headers });
      status = r.status;
      body = await r.json().catch(() => null);
    } catch {
    }
    return { ok: status >= 200 && status < 300, status, data: body?.data ?? body, message: body?.message ?? null };
  }
  var CSP_RULE_BASE = 1e5;
  var ruleId = (tabId) => CSP_RULE_BASE + tabId;
  async function addCspBypass(tabId) {
    await chrome.declarativeNetRequest.updateSessionRules({
      removeRuleIds: [ruleId(tabId)],
      addRules: [{
        id: ruleId(tabId),
        priority: 1,
        action: {
          type: chrome.declarativeNetRequest.RuleActionType.MODIFY_HEADERS,
          responseHeaders: [
            { header: "content-security-policy", operation: chrome.declarativeNetRequest.HeaderOperation.REMOVE },
            { header: "content-security-policy-report-only", operation: chrome.declarativeNetRequest.HeaderOperation.REMOVE }
          ]
        },
        condition: {
          tabIds: [tabId],
          // Restrict to MAIN_FRAME only — sub-frames (including cross-origin iframes)
          // do not need their CSP removed, and stripping them is unnecessary over-reach (3.2).
          resourceTypes: [
            chrome.declarativeNetRequest.ResourceType.MAIN_FRAME
          ]
        }
      }]
    });
  }
  async function removeCspBypass(tabId) {
    await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [ruleId(tabId)] });
  }
  async function isActive(tabId) {
    const rules = await chrome.declarativeNetRequest.getSessionRules();
    return rules.some((r) => r.id === ruleId(tabId));
  }
  async function reloadActiveTabs() {
    const rules = await chrome.declarativeNetRequest.getSessionRules();
    const tabIds = rules.filter((r) => r.id >= CSP_RULE_BASE).map((r) => r.id - CSP_RULE_BASE);
    for (const id of tabIds) {
      try {
        await chrome.tabs.reload(id);
      } catch {
      }
    }
  }
  var SESSION_PENDING = "pendingInject";
  async function getPendingInject() {
    const s = await chrome.storage.session.get(SESSION_PENDING);
    return new Set(s[SESSION_PENDING] || []);
  }
  async function addPendingInject(tabId) {
    const set = await getPendingInject();
    set.add(tabId);
    await chrome.storage.session.set({ [SESSION_PENDING]: Array.from(set) });
  }
  async function removePendingInject(tabId) {
    const set = await getPendingInject();
    set.delete(tabId);
    await chrome.storage.session.set({ [SESSION_PENDING]: Array.from(set) });
  }
  async function activate(tabId, hostname, project, environment) {
    const map = await getProjectMap();
    map[hostname] = { project, environment };
    await chrome.storage.local.set({ [LOCAL_KEYS.projectByDomain]: map });
    await addCspBypass(tabId);
    await addPendingInject(tabId);
    await chrome.tabs.reload(tabId);
  }
  async function deactivate(tabId) {
    await removeCspBypass(tabId);
    await removePendingInject(tabId);
    try {
      await chrome.tabs.reload(tabId);
    } catch {
    }
  }
  async function injectInto(tabId, url) {
    const server = await getServer();
    const localUser = await chrome.storage.local.get(LOCAL_KEYS.user);
    const user = localUser[LOCAL_KEYS.user] || null;
    const map = await getProjectMap();
    const entry = map[hostnameOf(url)];
    if (!entry) return;
    const displayName = user?.displayName || void 0;
    const cssUrl = chrome.runtime.getURL("pointer.css");
    const snapdomUrl = chrome.runtime.getURL("vendor/snapdom.js");
    await chrome.scripting.executeScript({ target: { tabId }, files: ["content-bridge.js"] });
    await chrome.scripting.executeScript({
      target: { tabId },
      world: "MAIN",
      func: injectMain,
      args: [{ server, project: entry.project, environment: entry.environment, displayName, cssUrl, snapdomUrl }]
    });
    await chrome.scripting.executeScript({ target: { tabId }, world: "MAIN", files: ["pointer.js"] });
  }
  chrome.tabs.onUpdated.addListener((tabId, info, tab) => {
    if (info.status !== "complete" || !tab.url) return;
    const url = tab.url;
    (async () => {
      const pending = await getPendingInject();
      const wasPending = pending.has(tabId);
      if (wasPending) await removePendingInject(tabId);
      if (wasPending || await isActive(tabId)) await injectInto(tabId, url);
    })().catch((e) => console.error("[pointer-ext] inject failed", e));
  });
  chrome.tabs.onRemoved.addListener((tabId) => {
    removeCspBypass(tabId).catch(() => {
    });
    removePendingInject(tabId).catch(() => {
    });
  });
  async function login(email, password, server) {
    const base = server.replace(/\/$/, "");
    const r = await fetch(`${base}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password })
    }).catch(() => null);
    const env = r ? await r.json().catch(() => null) : null;
    const data = env && env.data;
    if (data && data.status === "ok" && data.token) {
      await chrome.storage.session.set({ [SESSION_KEYS.token]: data.token });
      await chrome.storage.local.set({ [LOCAL_KEYS.server]: base, [LOCAL_KEYS.user]: data.user || null });
      return { ok: true, user: data.user || null };
    }
    return { ok: false, status: data && data.status || "error", message: env && env.message || "Login failed" };
  }
  var ALLOWED_METHODS = /* @__PURE__ */ new Set(["GET", "POST", "PATCH"]);
  async function validateProxyUrl(url, method) {
    const server = await getServer();
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      return null;
    }
    if (parsed.origin !== new URL(server).origin) return null;
    if (!parsed.pathname.startsWith("/api/")) return null;
    if (method !== void 0 && !ALLOWED_METHODS.has(method.toUpperCase())) return null;
    return server;
  }
  async function handleProxy(msg) {
    const blocked = { ok: false, status: 0, body: "blocked", contentType: null };
    if (msg.kind === "upload") {
      const trusted2 = await validateProxyUrl(msg.url, "POST");
      if (!trusted2) return blocked;
      const token2 = await getToken();
      const bytes = Uint8Array.from(atob(msg.base64), (c) => c.charCodeAt(0));
      const fd = new FormData();
      fd.append("file", new Blob([bytes], { type: msg.contentType }), msg.filename);
      fd.append("project", msg.project);
      const r2 = await fetch(msg.url, { method: "POST", headers: token2 ? { Authorization: `Bearer ${token2}` } : {}, body: fd });
      return { ok: r2.ok, status: r2.status, body: await r2.text(), contentType: r2.headers.get("content-type") };
    }
    const trusted = await validateProxyUrl(msg.url, msg.method);
    if (!trusted) return blocked;
    const token = await getToken();
    const headers = { ...msg.headers || {} };
    delete headers.authorization;
    delete headers.Authorization;
    if (token) headers.Authorization = `Bearer ${token}`;
    const r = await fetch(msg.url, { method: msg.method, headers, body: msg.body ?? void 0 });
    return { ok: r.ok, status: r.status, body: await r.text(), contentType: r.headers.get("content-type") };
  }
  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg.source === "pointer-ext") {
      handleProxy(msg).then(sendResponse).catch(() => sendResponse({ ok: false, status: 0, body: "", contentType: null }));
      return true;
    }
    const m = msg;
    (async () => {
      switch (m.type) {
        case "getState": {
          return { server: await getServer(), user: (await chrome.storage.local.get(LOCAL_KEYS.user))[LOCAL_KEYS.user] || null, hasToken: !!await getToken() };
        }
        case "getTabState": {
          const map = await getProjectMap();
          return { active: await isActive(m.tabId), remembered: map[m.hostname] || null };
        }
        case "deactivate": {
          await deactivate(m.tabId);
          return { ok: true };
        }
        case "login": {
          const res = await login(m.email, m.password, m.server);
          if (res.ok) await reloadActiveTabs();
          return res;
        }
        case "logout": {
          await chrome.storage.session.remove(SESSION_KEYS.token);
          await chrome.storage.local.remove(LOCAL_KEYS.user);
          return { ok: true };
        }
        case "setServer": {
          await chrome.storage.local.set({ [LOCAL_KEYS.server]: m.server.replace(/\/$/, "") });
          return { ok: true };
        }
        case "setProjectForDomain": {
          const map = await getProjectMap();
          const prev = map[m.hostname];
          map[m.hostname] = { project: m.project, environment: prev?.environment || "staging" };
          await chrome.storage.local.set({ [LOCAL_KEYS.projectByDomain]: map });
          return { ok: true };
        }
        case "listProjects": {
          const r = await apiFetch("/api/admin/projects", { method: "GET" });
          if (!r.ok) return { ok: false, projects: [], error: r.message || "Could not load projects." };
          const projects = (Array.isArray(r.data) ? r.data : []).filter((p) => p && p.key && p.isActive !== false).map((p) => ({ key: p.key, name: p.name || p.key, isActive: p.isActive !== false }));
          return { ok: true, projects };
        }
        case "createProject": {
          const r = await apiFetch("/api/admin/projects", { method: "POST", body: JSON.stringify({ key: m.key, name: m.name }) });
          if (!r.ok) return { ok: false, error: r.message || (r.status === 409 ? "A project with that key already exists." : "Could not create project.") };
          return { ok: true, project: { key: m.key, name: m.name, isActive: true } };
        }
        case "activate": {
          const gate = await apiFetch("/api/extension/activate", {
            method: "POST",
            body: JSON.stringify({ projectKey: m.project, origin: m.origin })
          });
          if (!gate.ok) {
            const reason = gate.status === 404 ? "Project not found in your workspace." : gate.message || "The browser extension is not available on your current plan.";
            return { ok: false, error: reason };
          }
          await activate(m.tabId, m.hostname, m.project, m.environment);
          return { ok: true };
        }
        default:
          return { ok: false, error: "unknown message" };
      }
    })().then(sendResponse).catch((e) => sendResponse({ ok: false, error: String(e) }));
    return true;
  });
})();
