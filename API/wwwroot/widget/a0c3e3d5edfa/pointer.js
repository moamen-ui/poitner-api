/* GENERATED from web-component/src — DO NOT EDIT. Run `npm run build` in web-component/. */
"use strict";
(() => {
  // src/pagecontext.ts
  var MAX_ENTRIES = 20;
  var MAX_AGE_MS = 30 * 60 * 1e3;
  var SLOW_REQUEST_MS = 3e3;
  var consoleEntries = [];
  var networkEntries = [];
  var started = false;
  var recording = false;
  var originalConsoleError = null;
  var originalConsoleWarn = null;
  var originalFetch = null;
  var originalXhrOpen = null;
  var originalXhrSend = null;
  var onWindowError = null;
  var onUnhandledRejection = null;
  function now() {
    return (/* @__PURE__ */ new Date()).toISOString();
  }
  function trim(list, maxAgeGetter) {
    const cutoff = Date.now() - MAX_AGE_MS;
    while (list.length && new Date(maxAgeGetter(list[0])).getTime() < cutoff) list.shift();
    while (list.length > MAX_ENTRIES) list.shift();
  }
  function stringifyArg(arg) {
    if (typeof arg === "string") return arg;
    if (arg instanceof Error) return arg.message;
    try {
      return JSON.stringify(arg);
    } catch {
      return String(arg);
    }
  }
  function extractStack(args) {
    var _a2;
    const err = args.find((a) => a instanceof Error);
    return (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3);
  }
  function recordConsole(level, args) {
    pushConsole(level, args.map(stringifyArg).join(" "), extractStack(args));
  }
  function pushConsole(level, rawMessage, stack) {
    if (recording) return;
    recording = true;
    try {
      const message = rawMessage.slice(0, 2e3);
      if (message.startsWith("[pointer-feedback]")) return;
      const last = consoleEntries[consoleEntries.length - 1];
      if (last && last.level === level && last.message === message) {
        last.count += 1;
        last.occurredAt = now();
      } else {
        consoleEntries.push({ level, message, stack, count: 1, occurredAt: now() });
      }
      trim(consoleEntries, (e) => e.occurredAt);
    } catch {
    } finally {
      recording = false;
    }
  }
  function stripQuery(url) {
    const cut = url.search(/[?#]/);
    return cut >= 0 ? url.slice(0, cut) : url;
  }
  function shouldRecord(statusCode, durationMs) {
    if (statusCode === null || statusCode === 0) return true;
    if (statusCode >= 400) return true;
    return durationMs >= SLOW_REQUEST_MS;
  }
  function recordNetwork(method, url, statusCode, durationMs) {
    try {
      networkEntries.push({ method, url: stripQuery(url), statusCode, durationMs, occurredAt: now() });
      trim(networkEntries, (e) => e.occurredAt);
    } catch {
    }
  }
  function rawFetch(url, opts) {
    const f = originalFetch != null ? originalFetch : window.fetch;
    return f.call(window, url, opts);
  }
  function patchFetch() {
    const original = window.fetch;
    originalFetch = original;
    window.fetch = (...args) => {
      var _a2, _b;
      const url = typeof args[0] === "string" ? args[0] : args[0] instanceof URL ? args[0].href : args[0].url;
      const method = (((_a2 = args[1]) == null ? void 0 : _a2.method) || ((_b = args[0]) == null ? void 0 : _b.method) || "GET").toUpperCase();
      const start = Date.now();
      return original.apply(window, args).then(
        (response) => {
          const durationMs = Date.now() - start;
          if (shouldRecord(response.status, durationMs)) recordNetwork(method, url, response.status, durationMs);
          return response;
        },
        (err) => {
          recordNetwork(method, url, null, Date.now() - start);
          throw err;
        }
      );
    };
  }
  function patchXhr() {
    if (typeof XMLHttpRequest === "undefined") return;
    const proto = XMLHttpRequest.prototype;
    originalXhrOpen = proto.open;
    originalXhrSend = proto.send;
    proto.open = function(method, url, ...rest) {
      try {
        this.__pfMethod = String(method || "GET").toUpperCase();
        this.__pfUrl = typeof url === "string" ? url : String(url);
      } catch {
      }
      return originalXhrOpen.call(this, method, url, ...rest);
    };
    proto.send = function(body) {
      try {
        const start = Date.now();
        const method = this.__pfMethod || "GET";
        const url = this.__pfUrl || "";
        this.addEventListener("loadend", () => {
          const durationMs = Date.now() - start;
          const status = this.status;
          if (shouldRecord(status === 0 ? null : status, durationMs)) {
            recordNetwork(method, url, status === 0 ? null : status, durationMs);
          }
        });
      } catch {
      }
      return originalXhrSend.call(this, body);
    };
  }
  function unpatchXhr() {
    if (typeof XMLHttpRequest === "undefined") return;
    if (originalXhrOpen) XMLHttpRequest.prototype.open = originalXhrOpen;
    if (originalXhrSend) XMLHttpRequest.prototype.send = originalXhrSend;
    originalXhrOpen = null;
    originalXhrSend = null;
  }
  function startPageContextCapture(_server, _scriptOrigin) {
    if (started) return;
    started = true;
    originalConsoleError = console.error.bind(console);
    originalConsoleWarn = console.warn.bind(console);
    console.error = (...args) => {
      recordConsole("error", args);
      originalConsoleError(...args);
    };
    console.warn = (...args) => {
      recordConsole("warn", args);
      originalConsoleWarn(...args);
    };
    onWindowError = (e) => {
      var _a2;
      const err = e.error instanceof Error ? e.error : void 0;
      const where = e.filename ? ` (${e.filename}:${e.lineno}:${e.colno})` : "";
      pushConsole("error", `Uncaught ${e.message || err && err.message || "error"}${where}`, (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3));
    };
    onUnhandledRejection = (e) => {
      var _a2;
      const reason = e.reason;
      const err = reason instanceof Error ? reason : void 0;
      pushConsole("error", `Unhandled promise rejection: ${err ? err.message : stringifyArg(reason)}`, (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3));
    };
    window.addEventListener("error", onWindowError);
    window.addEventListener("unhandledrejection", onUnhandledRejection);
    patchFetch();
    patchXhr();
  }
  function stopPageContextCapture() {
    if (!started) return;
    if (originalConsoleError) console.error = originalConsoleError;
    if (originalConsoleWarn) console.warn = originalConsoleWarn;
    if (originalFetch) window.fetch = originalFetch;
    if (onWindowError) window.removeEventListener("error", onWindowError);
    if (onUnhandledRejection) window.removeEventListener("unhandledrejection", onUnhandledRejection);
    unpatchXhr();
    originalConsoleError = null;
    originalConsoleWarn = null;
    originalFetch = null;
    onWindowError = null;
    onUnhandledRejection = null;
    started = false;
  }
  function getOrCreateSessionId() {
    const KEY = "pointer_page_session_id";
    try {
      let id = sessionStorage.getItem(KEY);
      if (!id) {
        id = typeof crypto !== "undefined" && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
        sessionStorage.setItem(KEY, id);
      }
      return id;
    } catch {
      return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }
  }
  function getPageContextPayload() {
    if (!started) return null;
    return {
      sessionId: getOrCreateSessionId(),
      consoleEntries: consoleEntries.slice(),
      networkEntries: networkEntries.slice()
    };
  }

  // src/constants.ts
  var HL_CLASS = "pointer-feedback-hl";
  var BACKDROP_SELECTOR = [
    ".cdk-overlay-backdrop",
    // Angular CDK / Angular Material
    ".modal-backdrop",
    // Bootstrap
    ".MuiBackdrop-root",
    // MUI
    ".ant-modal-mask",
    // Ant Design (modal)
    ".ant-drawer-mask",
    // Ant Design (drawer)
    ".v-overlay__scrim",
    // Vuetify
    ".el-overlay"
    // Element Plus
  ].join(", ");
  var DIALOG_CONTENT_SELECTOR = [
    ".cdk-overlay-pane",
    // Angular CDK / Angular Material (dialogs, menus, autocomplete, …)
    ".modal-content",
    // Bootstrap
    ".MuiDialog-paper",
    // MUI
    ".MuiPopover-paper",
    // MUI (menus/popovers)
    ".ant-modal-content",
    // Ant Design (modal)
    ".ant-drawer-content",
    // Ant Design (drawer)
    ".v-overlay__content",
    // Vuetify
    ".el-overlay-dialog"
    // Element Plus
  ].join(", ");
  var ENV_MAP = { unknown: 0, local: 1, staging: 2, production: 3 };
  var ENV_NAME = { 0: "unknown", 1: "local", 2: "staging", 3: "production" };
  var STATUS_STR = {
    1: "open",
    2: "pending-apply",
    3: "applied",
    4: "archived"
  };
  var STATUS_INT = {
    open: 1,
    "pending-apply": 2,
    applied: 3,
    archived: 4
  };
  var STATUS_FALLBACK = [
    { value: 1, name: "Open", label: "Open", color: "#2563eb", order: 1 },
    { value: 2, name: "ReadyToApply", label: "Ready", color: "#d97706", order: 2 },
    { value: 3, name: "Applied", label: "Completed", color: "#16a34a", order: 3 },
    { value: 4, name: "Archived", label: "Archived", color: "#6b7280", order: 4 }
  ];
  function pfFetch(url, opts) {
    const t = typeof window !== "undefined" ? window.__POINTER_FETCH__ : void 0;
    return t ? t(url, opts) : rawFetch(url, opts);
  }
  var _catalog = STATUS_FALLBACK;
  async function loadStatusCatalog(server) {
    var _a2;
    try {
      const res = await pfFetch(`${server.replace(/\/$/, "")}/api/statuses`);
      if (!res.ok) return;
      const body = await res.json();
      const data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
      if (Array.isArray(data) && data.length) _catalog = data.slice().sort((a, b) => a.order - b.order);
    } catch {
    }
  }
  function catalogToFilters() {
    const chips = [
      { key: "all", label: "All", color: "" }
    ];
    for (const item of _catalog) {
      const key = STATUS_STR[item.value];
      if (key) chips.push({ key, label: item.label, color: item.color });
    }
    return chips;
  }
  var _brandName = "Pointer";
  function getBrandName() {
    return _brandName;
  }
  async function loadBranding(server) {
    var _a2;
    try {
      const res = await pfFetch(`${server.replace(/\/$/, "")}/api/branding`);
      if (!res.ok) return;
      const body = await res.json();
      const data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
      if (data && typeof data.productName === "string" && data.productName.trim()) {
        _brandName = data.productName.trim();
      }
    } catch {
    }
  }
  var POSITIONS = ["top-start", "top-end", "bottom-start", "bottom-end"];
  var SHOT_MAX_WIDTH = 1280;
  var SHOT_HIGHLIGHT = "#2563eb";
  var _a;
  var SCRIPT_SRC = ((_a = document.currentScript) == null ? void 0 : _a.src) || "";
  var CSS_INTEGRITY = true ? "sha384-OVshXgU1mUTDb09VB2zGDW1nosazB7AC3kTMGTwqNik9HiWUNevis7ZG36c8khX3" : "";
  function resolveCssUrl(scriptSrc) {
    var _a2;
    if (!scriptSrc) return "pointer.css";
    try {
      const base = typeof window !== "undefined" && ((_a2 = window.location) == null ? void 0 : _a2.href) ? window.location.href : "http://localhost";
      const parsedScript = new URL(scriptSrc, base);
      const css = new URL("pointer.css", parsedScript);
      const v = parsedScript.searchParams.get("v");
      if (v) {
        css.searchParams.set("v", v);
      }
      return css.href;
    } catch {
      return "pointer.css";
    }
  }
  var CSS_URL = resolveCssUrl(SCRIPT_SRC);
  var SNAPDOM_URL = SCRIPT_SRC ? new URL("vendor/snapdom.js", SCRIPT_SRC).href : "vendor/snapdom.js";

  // src/dom.ts
  var escapeHtml = (s) => String(s == null ? "" : s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  var initials = (name) => {
    const parts = (name || "").trim().split(/\s+/).filter(Boolean);
    if (parts.length === 0) return "?";
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  };
  var timeAgo = (iso) => {
    if (!iso) return "";
    const then = new Date(iso).getTime();
    if (Number.isNaN(then)) return "";
    const seconds = Math.round((Date.now() - then) / 1e3);
    if (seconds < 45) return "just now";
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.round(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    return new Date(iso).toLocaleDateString();
  };
  var buildClipPathWithHoles = (rects, refBox) => {
    const w = refBox.width, h = refBox.height;
    let d = `M0 0H${w}V${h}H0Z`;
    for (const r of rects) {
      const x1 = Math.max(0, r.left - refBox.left - 2), y1 = Math.max(0, r.top - refBox.top - 2);
      const x2 = Math.min(w, r.right - refBox.left + 2), y2 = Math.min(h, r.bottom - refBox.top + 2);
      if (x2 <= x1 || y2 <= y1) continue;
      d += ` M${x1} ${y1}H${x2}V${y2}H${x1}Z`;
    }
    return `path(evenodd, "${d}")`;
  };
  var ensureHighlightStyle = () => {
    if (document.getElementById("pointer-feedback-hl-style")) return;
    const css = `
.${HL_CLASS}{
  outline:2px dashed #0969da!important;
  outline-offset:1px!important;
  cursor:crosshair!important;
  box-shadow:0 0 0 0 rgba(9,105,218,.3)!important;
  animation:pointer-feedback-hl-pulse 2.4s cubic-bezier(.25,1,.5,1) infinite!important;
}
@keyframes pointer-feedback-hl-pulse{
  0%,100%{outline-color:#0969da;box-shadow:0 0 0 0 rgba(9,105,218,.3);}
  50%{outline-color:#7c3aed;box-shadow:0 0 10px 2px rgba(124,58,237,.3);}
}
@media (prefers-reduced-motion: reduce){
  .${HL_CLASS}{animation:none!important;outline-color:#0969da!important;box-shadow:none!important;}
}`;
    try {
      if ("adoptedStyleSheets" in Document.prototype && typeof CSSStyleSheet !== "undefined") {
        const sheet = new CSSStyleSheet();
        sheet.replaceSync(css);
        document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
        const marker = document.createElement("meta");
        marker.id = "pointer-feedback-hl-style";
        document.head.appendChild(marker);
        return;
      }
    } catch {
    }
    const s = document.createElement("style");
    s.id = "pointer-feedback-hl-style";
    s.textContent = css;
    document.head.appendChild(s);
  };
  var generateSelector = (el) => {
    if (el === document.documentElement) return "html";
    if (el === document.body) return "body";
    if (el.id) {
      try {
        if (document.querySelector("#" + CSS.escape(el.id)) === el) return "#" + el.id;
      } catch (e) {
      }
    }
    const parts = [];
    let cur = el;
    while (cur && cur !== document.body && cur !== document.documentElement) {
      let selector = cur.tagName.toLowerCase();
      if (cur.id) {
        selector += "#" + cur.id;
        parts.unshift(selector);
        cur = null;
        break;
      }
      let nth = 1;
      let sib = cur.previousElementSibling;
      while (sib) {
        if (sib.tagName.toLowerCase() === cur.tagName.toLowerCase()) nth++;
        sib = sib.previousElementSibling;
      }
      if (nth > 1) selector += `:nth-of-type(${nth})`;
      parts.unshift(selector);
      cur = cur.parentElement;
    }
    if (cur === document.body) parts.unshift("body");
    else if (cur === document.documentElement) parts.unshift("html");
    return parts.join(" > ");
  };
  var matchElement = (comment) => {
    const selector = comment.element && comment.element.selector;
    const snapshot = comment.element && comment.element.snapshot;
    if (selector) {
      try {
        const el = document.querySelector(selector);
        if (el) return el;
      } catch (e) {
      }
    }
    if (snapshot) {
      const all = document.querySelectorAll("*");
      for (const el of Array.from(all)) {
        if (el.outerHTML === snapshot) return el;
      }
    }
    return null;
  };
  var pageIsRtl = () => {
    var _a2;
    try {
      const html = document.documentElement;
      const attr = (html.getAttribute("dir") || ((_a2 = document.body) == null ? void 0 : _a2.getAttribute("dir")) || "").toLowerCase();
      if (attr === "rtl" || attr === "ltr") return attr === "rtl";
      return getComputedStyle(html).direction === "rtl";
    } catch (e) {
      return false;
    }
  };
  function applyDataPosition(root, selector) {
    root.querySelectorAll(selector).forEach((el) => {
      const left = el.dataset.fbkLeft;
      const top = el.dataset.fbkTop;
      if (left !== void 0) el.style.left = `${left}px`;
      if (top !== void 0) el.style.top = `${top}px`;
    });
  }

  // src/icons.ts
  var ICON = {
    flag: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><line x1="4" y1="22" x2="4" y2="15"/></svg>',
    check: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>',
    // Plain checkmark (no circle) — for compact confirm actions like "confirm delete".
    checkPlain: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>',
    trash: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><line x1="10" y1="11" x2="10" y2="17"/><line x1="14" y1="11" x2="14" y2="17"/></svg>',
    pencil: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
    reopen: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/></svg>',
    archive: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="21 8 21 21 3 21 3 8"/><rect x="1" y="3" width="22" height="5"/><line x1="10" y1="12" x2="14" y2="12"/></svg>',
    // Eye + slash — the toolbar's Hide button.
    eyeOff: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M2 8s2.4-4.2 6-4.2S14 8 14 8s-2.4 4.2-6 4.2S2 8 2 8z"/><circle cx="8" cy="8" r="1.8"/><path d="M2.6 2.6l10.8 10.8"/></svg>',
    pin: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z"/><circle cx="12" cy="10" r="3"/></svg>',
    user: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>',
    lock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
    unlock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>',
    logout: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>',
    // Chrome-inspect-style: dashed square + mouse pointer (lucide square-dashed-mouse-pointer).
    // Kept only for the notifications menu's "new reply" row — the toolbar's own "Comment on an
    // element" button uses `crosshair` below.
    inspect: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3a2 2 0 0 0-2 2"/><path d="M19 3a2 2 0 0 1 2 2"/><path d="M5 21a2 2 0 0 1-2-2"/><path d="M9 3h1"/><path d="M9 21h2"/><path d="M14 3h1"/><path d="M3 9v1"/><path d="M21 9v2"/><path d="M3 14v1"/><path d="M12.034 12.681a.498.498 0 0 1 .647-.647l9 3.5a.5.5 0 0 1-.033.943l-3.444 1.068a1 1 0 0 0-.66.66l-1.067 3.443a.5.5 0 0 1-.943.033z"/></svg>',
    // Six-dot drag grip — the toolbar's drag handle.
    grip: '<svg viewBox="0 0 16 16" width="16" height="16" fill="currentColor" stroke="none"><circle cx="6" cy="3.6" r="1.1"/><circle cx="10" cy="3.6" r="1.1"/><circle cx="6" cy="8" r="1.1"/><circle cx="10" cy="8" r="1.1"/><circle cx="6" cy="12.4" r="1.1"/><circle cx="10" cy="12.4" r="1.1"/></svg>',
    close: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
    // Target-reset — "reset toolbar to its default position".
    restore: '<svg viewBox="0 0 16 16" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M8 2.4v3.2M8 10.4v3.2M2.4 8h3.2M10.4 8h3.2"/><rect x="5.6" y="5.6" width="4.8" height="4.8" rx="1"/></svg>',
    // Speech-bubble "feedback" mark — the launcher (22px) and the toolbar's Comments button (16px)
    // are drawn separately (bubble vs bubbleLg) rather than one path at two sizes.
    bubble: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" aria-hidden="true"><path d="M2 3.6a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H6.4L3.2 13.6v-3H3a1 1 0 0 1-1-1z"/></svg>',
    bubbleLg: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
    // Circle + 4 outward ticks — "Comment on an element" (pick mode), the toolbar's Inspect icon.
    crosshair: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" aria-hidden="true"><circle cx="8" cy="8" r="4.2"/><path d="M8 .9v3.2M8 11.9v3.2M.9 8h3.2M11.9 8h3.2"/></svg>',
    // Bell — the toolbar's Updates button.
    bell: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M4.2 6.6a3.8 3.8 0 0 1 7.6 0c0 2.9 1.2 3.9 1.2 3.9H3s1.2-1 1.2-3.9z"/><path d="M6.6 12.8a1.6 1.6 0 0 0 2.8 0"/></svg>',
    // Warning triangle — the warn-variant toast icon.
    warnTriangle: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
    // Danger circle — the danger-variant toast icon.
    dangerCircle: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>',
    // Bold checkmark — the applied/verified pin (thicker stroke reads at 12px inside a 28px pin).
    checkBold: '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>',
    // Small reply-count bubble — the pin hover preview's "N replies" line.
    bubbleSm: '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
    // User menu's theme toggle — light/dark option icons.
    sun: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2"/><path d="M12 20v2"/><path d="m4.93 4.93 1.41 1.41"/><path d="m17.66 17.66 1.41 1.41"/><path d="M2 12h2"/><path d="M20 12h2"/><path d="m6.34 17.66-1.41 1.41"/><path d="m19.07 4.93-1.41 1.41"/></svg>',
    moon: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z"/></svg>'
  };

  // src/templates.ts
  var TPL = {
    // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
    // shell. showLoginModal() renders the shell once and then swaps #fbk-auth-body
    // between loginBody and signupBody. The shell keeps the Skip control so
    // deferred-login dismissal works from either view.
    loginModal: (project) => `
        <div class="fbk-modal-overlay">
          <div class="fbk-modal">
            <h2>${escapeHtml(getBrandName())}</h2>
            <p>Leave feedback on <b>${escapeHtml(project)}</b>.</p>
            <div id="fbk-auth-body"></div>
            <button class="fbk-btn fbk-link fbk-btn-block fbk-auth-skip" id="fbk-login-skip">Skip for now</button>
          </div>
        </div>`,
    // Sign-in body. After a "rejected" login it also renders an inline re-apply
    // block (role select + "Request again"); pass rejected=true to show it.
    loginBody: (rejected) => `
        <input class="fbk-input fbk-stack-gap" id="fbk-email" type="email" placeholder="Email" />
        <input class="fbk-input fbk-stack-gap" id="fbk-password" type="password" placeholder="Password" />
        <div class="fbk-modal-error" id="fbk-login-error"></div>
        <button class="fbk-btn primary fbk-btn-block" id="fbk-login-submit">Sign in</button>
        ${rejected ? `
        <div class="fbk-reapply" id="fbk-reapply">
          <label class="fbk-field-label" for="fbk-reapply-role">Choose a role to request again</label>
          <select class="fbk-input fbk-stack-gap" id="fbk-reapply-role"></select>
          <button class="fbk-btn primary fbk-btn-block" id="fbk-reapply-submit">Request again</button>
        </div>` : ""}
        <div class="fbk-auth-foot">
          No account? <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-signup">Create account</button>
        </div>`,
    // Sign-up body. The role <select> is populated at runtime from GET /api/roles.
    signupBody: () => `
        <input class="fbk-input fbk-stack-gap" id="fbk-su-name" type="text" placeholder="Name" />
        <input class="fbk-input fbk-stack-gap" id="fbk-su-email" type="email" placeholder="Email" />
        <input class="fbk-input fbk-stack-gap" id="fbk-su-password" type="password" placeholder="Password" />
        <label class="fbk-field-label" for="fbk-su-role">Role</label>
        <select class="fbk-input fbk-stack-gap" id="fbk-su-role"></select>
        <div class="fbk-modal-error" id="fbk-signup-error"></div>
        <div class="fbk-modal-success" id="fbk-signup-success"></div>
        <button class="fbk-btn primary fbk-btn-block" id="fbk-signup-submit">Create account</button>
        <div class="fbk-auth-foot">
          Already have an account? <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-login">Back to sign in</button>
        </div>`,
    // `fixedEnvLabel`: when the host fixed the environment at install time (attribute or injected
    // config), pass its display name to render a read-only label instead of the switcher — letting a
    // visitor switch an environment that was already explicitly configured is redundant and risks
    // misfiling a comment into the wrong bucket. Pass null/undefined to render the normal switcher.
    // `projectName`: shown next to the environment indicator so a visitor can immediately tell which
    // project this install is bound to — project keys aren't unique across a workspace, so two
    // different installs can easily look identical without this.
    // `avatarInitials`: 1-2 letters (see dom.ts's initials()) for the account button, already
    // safe to interpolate as-is — computed by the caller from the RAW display name (before
    // displayName below gets HTML-escaped), so a name containing "&"/"<" can't leak into it.
    // `ariaShortcut`: the ARIA-format string ("Control+Alt+Shift+C" — see shortcut.ts's
    // ariaKeyshortcuts), separate from `shortcutLabel` (the human-display form, "Ctrl+Alt+Shift+C"
    // or "⌃⌥⇧C" on Mac) since ARIA wants full, platform-independent modifier names.
    chrome: (displayName, roleLabel, fixedEnvLabel, projectName = "", shortcutLabel = "", unreadNotifyCount = 0, avatarInitials = "", ariaShortcut = "") => `
        <aside class="fbk-toolbar" id="fbk-toolbar" role="toolbar" aria-label="${escapeHtml(getBrandName())}" part="toolbar">
          <span class="fbk-toolbar__grip" id="fbk-grip" data-fbk-drag data-toggle="tooltip" data-placement="top" title="Drag to reposition" aria-hidden="true">${ICON.grip}</span>
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--primary fbk-toolbar-btn--icon" id="fbk-add" data-fbk-act="inspect" aria-pressed="false" data-toggle="tooltip" data-placement="top" title="Comment on an element${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ""}" aria-label="Comment on an element"${ariaShortcut ? ` aria-keyshortcuts="${escapeHtml(ariaShortcut)}"` : ""}><span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--comments" id="fbk-toggle" data-fbk-act="comments" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="View comments list" aria-label="Comments"><span class="fbk-toolbar-btn__icon">${ICON.bubble}</span> <span class="fbk-toolbar-count" id="fbk-count" data-fbk-count>0</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-updates" data-fbk-act="updates" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="Recent activity &amp; updates" aria-label="Updates${unreadNotifyCount > 0 ? `, ${unreadNotifyCount > 99 ? "99+" : unreadNotifyCount} unread` : ""}"><span class="fbk-toolbar-btn__icon">${ICON.bell}</span><span class="fbk-toolbar-dot${unreadNotifyCount > 0 ? "" : " fbk-hidden"}" id="fbk-notify-count" data-fbk-unread aria-hidden="true"></span></button>
          ${displayName ? `
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--avatar" id="fbk-user" data-fbk-act="account" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="Signed in as ${displayName}${roleLabel ? " · " + roleLabel : ""}" aria-label="Account, ${displayName}">${avatarInitials}</button>` : ""}
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-hide" data-fbk-act="hide" data-toggle="tooltip" data-placement="top" title="Hide ${escapeHtml(getBrandName())}" aria-label="Hide ${escapeHtml(getBrandName())}"><span class="fbk-toolbar-btn__icon">${ICON.eyeOff}</span></button>
          <span class="fbk-toolbar__divider fbk-toolbar__divider--moved" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon fbk-toolbar__reset" id="fbk-reset-pos" data-fbk-act="reset-position" data-toggle="tooltip" data-placement="top" title="Reset toolbar position" aria-label="Reset toolbar position"><span class="fbk-toolbar-btn__icon">${ICON.restore}</span></button>
        </aside>
        <div class="fbk-sidebar" id="fbk-sidebar">
          <div class="fbk-sidebar-head">
            <div class="fbk-sidebar-head-row">
              <h2>Comments</h2>
              <button class="fbk-mini fbk-icon" id="fbk-close" title="Close" aria-label="Close">&#x2715;</button>
            </div>
            <div class="fbk-sidebar-head-row">
              <div class="fbk-sidebar-meta">
                <span class="fbk-project-name fbk-caption" id="fbk-project-name" title="${escapeHtml(projectName)}">${escapeHtml(projectName)}</span>
                ${fixedEnvLabel ? `<span class="fbk-env-label fbk-caption" title="Environment — fixed for this install">&middot; ${escapeHtml(fixedEnvLabel)}</span>` : `<select class="fbk-input fbk-env-select" id="fbk-env" title="Environment — comments are scoped per environment">
                <option value="local">local</option>
                <option value="staging">staging</option>
                <option value="production">production</option>
              </select>`}
              </div>
              <button class="fbk-mini fbk-icon" id="fbk-refresh" title="Refresh comments" aria-label="Refresh comments">&#8635;</button>
            </div>
            <div class="fbk-commit-style fbk-hidden" id="fbk-commit-style"></div>
          </div>
          <div class="fbk-filters" id="fbk-filters"></div>
          <div class="fbk-sidebar-body" id="fbk-list"></div>
        </div>
        <div class="fbk-pins-layer" id="fbk-pins-layer"></div>
        <div id="fbk-popover-host"></div>
        <div id="fbk-menu-host"></div>`,
    // Commit-style control (see element.ts's fetchCaptureConfig/renderCommitStyleControl) — only
    // ever rendered when the CURRENT caller is authorized to change project settings
    // (CaptureConfigResponse.CanEditSettings, same gate as the PATCH itself). Lets whoever's looking
    // choose whether the AI apply flow bundles applied comments into one commit or commits each one
    // separately — read by skill.md's Step 1 the next time an agent applies.
    commitStyleControl: (commitStyle) => `
        <span class="fbk-caption">Commit style</span>
        <select class="fbk-input fbk-commit-style-select" id="fbk-commit-style-select" title="How the AI apply flow commits applied comments">
          <option value="1" ${commitStyle === 1 ? "selected" : ""}>One commit</option>
          <option value="2" ${commitStyle === 2 ? "selected" : ""}>Separate commits</option>
        </select>`,
    // Dropdown under the user icon: shows identity, the per-user "add comment" shortcut
    // (click to rebind, ↺ to reset), theme + language toggles, and a Sign out action.
    userMenu: (displayName, roleLabel, shortcutLabel, authOwnedByHost, theme = "light", language = "en") => `
        <div class="fbk-menu" id="fbk-user-menu" role="menu">
          <div class="fbk-menu-id">
            <span>${displayName}</span>
            ${roleLabel ? `<span class="fbk-menu-role">${roleLabel}</span>` : ""}
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Add comment</span>
            <button type="button" id="fbk-shortcut-edit" class="fbk-mini" title="Click, then press a new key combo">${escapeHtml(shortcutLabel)}</button>
            <button type="button" id="fbk-shortcut-reset" class="fbk-mini fbk-icon" title="Reset to default">&#8635;</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Theme</span>
            <button type="button" id="fbk-theme-light" class="fbk-mini fbk-icon${theme === "light" ? " is-active" : ""}" title="Light" aria-label="Light theme" aria-pressed="${theme === "light"}">${ICON.sun}</button>
            <button type="button" id="fbk-theme-dark" class="fbk-mini fbk-icon${theme === "dark" ? " is-active" : ""}" title="Dark" aria-label="Dark theme" aria-pressed="${theme === "dark"}">${ICON.moon}</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Language</span>
            <button type="button" id="fbk-lang-en" class="fbk-mini${language === "en" ? " is-active" : ""}" aria-pressed="${language === "en"}">EN</button>
            <button type="button" id="fbk-lang-ar" class="fbk-mini${language === "ar" ? " is-active" : ""}" aria-pressed="${language === "ar"}">AR</button>
          </div>
          ${authOwnedByHost ? `<div class="fbk-menu-note fbk-caption">Signed in via the browser extension — sign out from its popup.</div>` : `<button class="fbk-menu-item" id="fbk-signout" role="menuitem">${ICON.logout}<span>Sign out</span></button>`}
        </div>`,
    // Collapsed state: a small floating launcher that re-opens the overlay.
    // `rtl` makes start/end resolve against the host page direction (the shadow
    // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
    launcher: (count, position, rtl, unreadNotifyCount = 0) => {
      const hasUnread = unreadNotifyCount > 0;
      const badgeCount = hasUnread ? unreadNotifyCount : count;
      return `
        <button class="fbk-launcher fbk-pos-${position || "bottom-end"}${rtl ? " fbk-rtl" : ""}" id="fbk-launcher" title="Open ${escapeHtml(getBrandName())} feedback" aria-label="Open ${escapeHtml(getBrandName())} feedback">
          <span class="fbk-launcher-ring" aria-hidden="true"></span>
          ${ICON.bubbleLg}
          ${badgeCount ? `<span class="fbk-launcher-badge${hasUnread ? " fbk-notify-badge" : ""}">${badgeCount > 99 ? "99+" : badgeCount}</span>` : ""}
        </button>`;
    },
    empty: (msg) => `<div class="fbk-empty">${msg}</div>`,
    // One toast card. `role="alert"` for danger (assertive — interrupts) vs `role="status"` for
    // success/warn (polite — announced without interrupting); the shared container around these
    // already carries `aria-live="polite"`, so screen readers pick either up without extra wiring.
    // `element.ts`'s toast() owns the click listeners (Undo/Retry callback + dismiss); this is a
    // pure string builder like the rest of TPL.
    toast: (variant, message, actionLabel) => {
      const icon = variant === "success" ? ICON.checkPlain : variant === "warn" ? ICON.warnTriangle : ICON.dangerCircle;
      const role = variant === "danger" ? "alert" : "status";
      return `
        <div class="fbk-toast fbk-toast-${variant}" role="${role}">
          <div class="fbk-toast-icon" aria-hidden="true">${icon}</div>
          <div class="fbk-toast-content"><span class="fbk-toast-message">${escapeHtml(message)}</span></div>
          ${actionLabel ? `<button type="button" class="fbk-toast-action">${escapeHtml(actionLabel)}</button>` : ""}
          <button type="button" class="fbk-toast-close" aria-label="Dismiss notification">${ICON.close}</button>
        </div>`;
    },
    // Status filter as a dropdown (rather than a row of chip buttons) — keeps the filter bar compact.
    statusFilterSelect: (filters, active, counts) => `<select class="fbk-status-select" id="fbk-status-filter" title="Filter by status">
             ${filters.map((f) => {
      var _a2;
      return `<option value="${f.key}" ${f.key === active ? "selected" : ""}>${escapeHtml(f.label)} (${(_a2 = counts[f.key]) != null ? _a2 : 0})</option>`;
    }).join("")}
           </select>`,
    // "Mine only" toggle — a chip that composes with the status chips above.
    // Rendered only when a user is logged in.
    mineToggle: (active) => `<button class="fbk-chip fbk-mine ${active ? "active" : ""}" id="fbk-mine-toggle" title="Show only my comments" aria-pressed="${active ? "true" : "false"}">
             &#x1f464; Mine only
           </button>`,
    // User filter — only rendered when the list has comments from >1 author.
    authorFilter: (authors, selectedId) => `<select class="fbk-userfilter" id="fbk-author-filter" title="Filter by user">
             <option value="">&#x1f465; All users</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? "selected" : ""}>${escapeHtml(a.name)}</option>`).join("")}
           </select>`,
    card: (c, i, isQuickAccess) => {
      const cls = c.status === "pending-apply" ? "pending" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "";
      const statusPill = c.status === "applied" && c.deployedAt ? `<span class="fbk-pill status-applied" title="Deployed in ${escapeHtml((c.deployedSha || "").slice(0, 7))}">&#x2713; live</span>` : c.status === "applied" ? '<span class="fbk-pill status-applied">&#x2713; completed</span>' : c.status === "pending-apply" ? '<span class="fbk-pill status-pending">pending</span>' : c.status === "archived" ? '<span class="fbk-pill status-archived">&#x1f4e6; archived</span>' : "";
      const verifiedPill = c.status === "applied" && c.verifiedAt ? '<span class="fbk-pill verified">&#x2713; Verified</span>' : "";
      const verifyGroup = c.status === "applied" && !c.verifiedAt && c._canVerify ? `<span class="fbk-verify-group">
          <button class="fbk-mini fbk-verify-ok" data-act="verify-ok" data-id="${c.id}" title="Looks right">&#x1f44d; Looks right</button>
          <button class="fbk-mini fbk-verify-reject" data-act="verify-reject" data-id="${c.id}" title="Not fixed">&#x1f44e; Not fixed</button>
        </span>` : "";
      const verifyBox = c.status === "applied" && !c.verifiedAt && c._canVerify ? `<div class="fbk-verify-box fbk-hidden" id="fbk-verify-box-${c.id}">
          <input class="fbk-input fbk-verify-note-input" id="fbk-verify-note-${c.id}" placeholder="Explain what is still not fixed…" />
          <div class="fbk-verify-actions">
            <button class="fbk-mini primary" data-act="verify-submit" data-id="${c.id}">Submit</button>
            <button class="fbk-mini" data-act="verify-cancel" data-id="${c.id}">Cancel</button>
          </div>
        </div>` : "";
      const commitLink = c.status === "applied" ? `<a class="fbk-pill" href="${c.commitUrl ? escapeHtml(c.commitUrl) : "#"}" ${c.commitUrl ? 'target="_blank" rel="noopener noreferrer"' : ""} title="${c.commitUrl ? "View commit" : "No commit recorded for this comment"}">&#x1f517; commit</a>` : "";
      const payloadPill = c.hasPayloadFlag ? `<span class="fbk-pill fbk-payload-flag" title="${escapeHtml((c.payloadFlags || []).join(", "))}">&#x26a0; contains a secret/payload?</span>` : "";
      const replies = (c.replies || []).map((r) => `<div class="fbk-reply ${r.isAi ? "ai" : ""}"><b>${escapeHtml(r.authorName || r.authorLabel || "User")}:</b> ${escapeHtml(r.body || r.text || "")}</div>`).join("");
      const envInt = c.environment;
      const envLabel = envInt === 1 ? "Local" : envInt === 2 ? "Staging" : envInt === 3 ? "Production" : envInt ? String(envInt) : "";
      const authorLabel = c.authorName || "";
      const shotUrl = c.element && c.element.screenshotUrl;
      const shot = shotUrl ? `<a class="fbk-shot-link" href="${escapeHtml(shotUrl)}" target="_blank" rel="noopener noreferrer" title="Open full screenshot">
            <img class="fbk-shot" src="${escapeHtml(shotUrl)}" alt="Element screenshot" loading="lazy" />
          </a>` : "";
      return `
          <div class="fbk-card ${cls}" data-id="${c.id}">
            <div class="fbk-meta">
              <span class="fbk-badge">${i + 1}</span>
              ${envLabel ? `<span class="fbk-pill env">${escapeHtml(envLabel)}</span>` : ""}
              ${payloadPill}
              ${statusPill}
              ${verifiedPill}
              ${verifyGroup}
              ${commitLink}
              <div class="fbk-actions-end">
                ${c._mine ? `<button class="fbk-mini fbk-icon${c.isPrivate ? " private-on" : ""}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? "false" : "true"}" title="${c.isPrivate ? "Private — click to make public" : "Make private (only you)"}" aria-label="${c.isPrivate ? "Make public" : "Make private"}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ""}
                ${c.status === "open" ? `<button class="fbk-mini danger fbk-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ""}
              </div>
            </div>
            <div class="fbk-text">${escapeHtml(c.body || c.text || "")}</div>
            ${shot}
            <div class="fbk-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ""}${c.editedAt ? ' &middot; <span class="fbk-edited">edited</span>' : ""}</div>
            ${verifyBox}
            ${replies ? `<div class="fbk-replies">${replies}</div>` : ""}
            <div class="fbk-reply-row">
              <input class="fbk-input fbk-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="fbk-actions">
              ${isQuickAccess ? "" : c.status === "applied" || c.status === "archived" ? "" : `<button class="fbk-mini ${c.status === "pending-apply" ? "apply" : "ready"}" data-act="apply" data-id="${c.id}" title="${c.status === "pending-apply" ? "Marked ready — click to unmark" : "Mark ready to apply"}">
                ${ICON.flag}<span>Ready</span>
              </button>`}
              ${!isQuickAccess && (c.status === "open" || c.status === "pending-apply") ? `<button class="fbk-mini done fbk-icon" data-act="complete" data-id="${c.id}" title="Mark completed" aria-label="Mark completed">${ICON.check}</button>` : ""}
              ${!isQuickAccess && c.status === "applied" ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>
              <button class="fbk-mini fbk-icon" data-act="archive" data-id="${c.id}" title="Archive" aria-label="Archive">${ICON.archive}</button>` : ""}
              ${!isQuickAccess && c.status === "archived" ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>` : ""}
              ${c._mine ? `<div class="fbk-actions-end"><button class="fbk-mini fbk-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button></div>` : ""}
            </div>
          </div>`;
    },
    // `bugReportEnabled`: only true when the project has page-context capture turned on — the
    // checkbox controls whether the console/network buffer already sitting in memory gets attached
    // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
    popover: (meta, left, top, shotEnabled, actions = [], bugReportEnabled = false) => `
        <div class="fbk-popover" data-fbk-left="${left}" data-fbk-top="${top}">
          <button type="button" class="fbk-popover-private-toggle" id="fbk-comment-private" title="Keep private — only me" aria-label="Keep private — only me" aria-pressed="false">${ICON.unlock}</button>
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="fbk-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="fbk-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ""}
          <textarea class="fbk-textarea" id="fbk-comment-text" placeholder="What should change here?"></textarea>
          ${actions.length ? `<div class="fbk-field-label">Predefined prompts</div>
          <div class="fbk-actions-pick" id="fbk-action-pick">
            ${actions.map((a) => `<label class="fbk-check"><input type="checkbox" class="fbk-action-opt" value="${a.id}" /> ${escapeHtml(a.text)}</label>`).join("")}
          </div>` : ""}
          ${shotEnabled ? `<label class="fbk-check"><input type="checkbox" id="fbk-comment-shot" /> &#x1f4f7; Attach screenshot</label>` : ""}
          ${bugReportEnabled ? `<label class="fbk-check" title="Attaches any console errors/warnings and failed or slow network requests seen on this page"><input type="checkbox" id="fbk-comment-bug" /> &#x1f41e; Report as a bug</label>` : ""}
          <div class="fbk-reply-row">
            <button class="fbk-btn primary fbk-btn-fill" id="fbk-submit">Add</button>
            <button class="fbk-mini" id="fbk-cancel">Cancel</button>
          </div>
        </div>`,
    // `isNew`: shows the attention ripple once, on the pin for the comment the viewer just added
    // this session (see element.ts's `_newPinId`) — never for a pin restored from a re-render.
    // `rect` only ever needs left/top (the pixel the pin's tip anchors to), not a full DOMRect —
    // element.ts's cluster grouping passes a plain centroid point, not a real element rect.
    // `tipSide`: 'top' (default) opens the hover tooltip above the pin, 'bottom' flips it below —
    // element.ts's renderPins() decides based on how close the pin sits to the viewport's top edge.
    pin: (c, i, rect, isNew = false, tipSide = "top") => {
      const status = c.status === "pending-apply" ? "ready" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "open";
      const statusLabel = status === "ready" ? "Ready" : status === "applied" ? "Applied" : status === "archived" ? "Archived" : "Open";
      const author = c.authorName || "";
      const bodyText = c.body || c.text || "";
      const replyCount = (c.replies || []).length;
      const selector = c.element && c.element.selector || "";
      const label = `Comment #${i + 1}${author ? ` by ${author}` : ""}${bodyText ? `: ${bodyText}` : ""}`;
      const showMeta = !!(selector || replyCount);
      return `
        <div class="fbk-pin-wrapper" data-id="${c.id}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}" data-fbk-tip-side="${tipSide}">
          <button type="button" class="fbk-pin fbk-pin-${status}" aria-label="${escapeHtml(label)}">
            ${isNew ? '<span class="fbk-pin-ring" aria-hidden="true"></span>' : ""}
            ${status === "applied" ? ICON.checkBold : `<span class="fbk-pin-number">${i + 1}</span>`}
          </button>
          <div class="fbk-pin-tooltip" role="tooltip">
            <div class="fbk-pin-tooltip-header">
              <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
              ${author ? `<span class="fbk-pin-author">${escapeHtml(author)}</span>` : ""}
              <span class="fbk-pin-time">${escapeHtml(timeAgo(c.createdAt))}</span>
            </div>
            ${bodyText ? `<p class="fbk-pin-tooltip-body">${escapeHtml(bodyText)}</p>` : ""}
            ${showMeta ? `
            <div class="fbk-pin-tooltip-meta">
              ${selector ? `<span class="fbk-pin-tag" title="${escapeHtml(selector)}">${escapeHtml(selector)}</span>` : "<span></span>"}
              ${replyCount ? `<span class="fbk-pin-reply-count">${ICON.bubbleSm} ${replyCount} ${replyCount === 1 ? "reply" : "replies"}</span>` : ""}
            </div>` : ""}
          </div>
        </div>`;
    },
    // 2+ pins within 24px of each other (see element.ts's renderPins clustering) render as one
    // expandable "N+" badge instead of stacking indistinguishable pins on top of each other.
    // Clicking it opens pinClusterMenu below, listing each one individually.
    pinCluster: (comments, rect) => `
        <div class="fbk-pin-wrapper" data-ids="${comments.map((c) => c.id).join(",")}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}">
          <button type="button" class="fbk-pin fbk-pin-cluster" aria-label="${comments.length} overlapping comments at this location" aria-expanded="false">
            <span class="fbk-pin-cluster-count">${comments.length}+</span>
            <span class="fbk-pin-cluster-dots" aria-hidden="true">&bull;&bull;&bull;</span>
          </button>
        </div>`,
    // Expanded cluster — one row per clustered comment, click-through to the same
    // scroll+highlight behavior as clicking a standalone pin.
    pinClusterMenu: (comments) => `
        <div class="fbk-pin-cluster-menu" id="fbk-pin-cluster-menu" role="menu">
          ${comments.map((c) => {
      const status = c.status === "pending-apply" ? "ready" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "open";
      const statusLabel = status === "ready" ? "Ready" : status === "applied" ? "Applied" : status === "archived" ? "Archived" : "Open";
      const bodyText = c.body || c.text || "";
      return `
          <button type="button" class="fbk-pin-cluster-item" data-id="${c.id}" role="menuitem">
            <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
            <span class="fbk-pin-cluster-item-text">${escapeHtml(bodyText)}</span>
          </button>`;
    }).join("")}
        </div>`,
    notificationsMenu: (items) => `
        <div class="fbk-notifications-menu" id="fbk-notifications-menu" role="menu">
          <div class="fbk-notifications-head">
            <h3>Updates</h3>
          </div>
          <div class="fbk-notifications-body">
            ${items.length === 0 ? '<div class="fbk-empty fbk-notifications-empty">No updates yet</div>' : items.map((item) => {
      var _a2, _b;
      const isUnread = !item.readAt;
      let typeLabel = "Update";
      let icon = ICON.pin;
      let detail = "";
      const typeNum = typeof item.type === "string" ? item.type === "CommentApplied" ? 1 : item.type === "CommentReopened" ? 2 : item.type === "ReplyAdded" ? 3 : 0 : item.type;
      if (typeNum === 1) {
        typeLabel = "Applied";
        icon = ICON.check;
        detail = ((_a2 = item.payload) == null ? void 0 : _a2.commitUrl) ? `<div class="fbk-notification-item-commit"><a class="fbk-pill" href="${escapeHtml(item.payload.commitUrl)}" target="_blank" rel="noopener noreferrer" onclick="event.stopPropagation()">&#x1f517; Commit</a></div>` : "";
      } else if (typeNum === 2) {
        typeLabel = "Reopened";
        icon = ICON.reopen;
      } else if (typeNum === 3) {
        typeLabel = "New reply";
        icon = ICON.inspect;
        if ((_b = item.payload) == null ? void 0 : _b.replyExcerpt) {
          detail = `<div class="fbk-notification-reply fbk-caption">"${escapeHtml(item.payload.replyExcerpt)}"</div>`;
        }
      }
      const timeAgo2 = item.createdAt ? new Date(item.createdAt).toLocaleDateString() : "";
      return `
                    <div class="fbk-notification-item${isUnread ? " unread" : ""}" data-id="${item.commentId}" role="menuitem">
                      <div class="fbk-notification-item-head">
                        <span class="fbk-notification-item-type">${icon} ${escapeHtml(typeLabel)}</span>
                        <span class="fbk-notification-item-time">${escapeHtml(timeAgo2)}</span>
                      </div>
                      <div class="fbk-notification-item-body">
                        ${escapeHtml(item.commentBodyExcerpt || "")}
                      </div>
                      ${detail}
                    </div>`;
    }).join("")}
          </div>
        </div>`
  };

  // src/framework-source.ts
  function toPortablePath(raw) {
    if (/^https?:\/\//.test(raw)) {
      try {
        return new URL(raw).pathname.replace(/^\//, "");
      } catch {
        return raw;
      }
    }
    const srcIdx = raw.lastIndexOf("/src/");
    if (srcIdx >= 0) return raw.slice(srcIdx + 1);
    return raw;
  }
  function reactStackToSourcePath(stack) {
    const lines = stack.split("\n").slice(1);
    for (const line of lines) {
      if (/node_modules|react-dom|react_jsx|react-jsx/.test(line)) continue;
      const m = line.match(/\((https?:\/\/[^\s)]+):(\d+):(\d+)\)/) || line.match(/at (https?:\/\/[^\s)]+):(\d+):(\d+)/);
      if (m) return `${toPortablePath(m[1])}:${m[2]}`;
    }
    return null;
  }
  function detectReactSource(el) {
    const key = Object.getOwnPropertyNames(el).find((k) => k.startsWith("__reactFiber"));
    if (!key) return null;
    const fiber = el[key];
    if (!fiber) return null;
    const legacy = fiber._debugSource;
    if (legacy && legacy.fileName) {
      return `${toPortablePath(legacy.fileName)}:${legacy.lineNumber || 0}`;
    }
    const debugStack = fiber._debugStack;
    if (debugStack && typeof debugStack.stack === "string") {
      const found = reactStackToSourcePath(debugStack.stack);
      if (found) return found;
    }
    return null;
  }
  function detectVueSource(el) {
    let instance = el.__vueParentComponent;
    let depth = 0;
    while (instance && depth < 6) {
      const type = instance.type;
      const file = type && type.__file || instance.__file;
      if (file) return toPortablePath(file);
      instance = instance.parent;
      depth++;
    }
    return null;
  }
  function detectFrameworkSourcePath(el) {
    try {
      const react = detectReactSource(el);
      if (react) return react;
    } catch {
    }
    try {
      const vue = detectVueSource(el);
      if (vue) return vue;
    } catch {
    }
    return null;
  }

  // src/capture.ts
  var snapdomPromise = null;
  function loadSnapdom() {
    if (window.snapdom) return Promise.resolve(window.snapdom);
    if (snapdomPromise) return snapdomPromise;
    snapdomPromise = new Promise((resolve, reject) => {
      var _a2;
      const s = document.createElement("script");
      s.src = ((_a2 = window.__POINTER_CONFIG__) == null ? void 0 : _a2.snapdomUrl) || SNAPDOM_URL;
      s.async = true;
      s.onload = () => window.snapdom ? resolve(window.snapdom) : reject(new Error("snapdom loaded but window.snapdom missing"));
      s.onerror = () => reject(new Error("failed to load " + SNAPDOM_URL));
      document.head.appendChild(s);
    });
    return snapdomPromise;
  }
  function canvasToBlob(canvas) {
    return new Promise((resolve) => {
      const done = (b) => resolve(b || null);
      try {
        canvas.toBlob((b) => {
          if (b) return done(b);
          canvas.toBlob(done, "image/jpeg", 0.6);
        }, "image/webp", 0.6);
      } catch (e) {
        try {
          canvas.toBlob(done, "image/jpeg", 0.6);
        } catch (e2) {
          done(null);
        }
      }
    });
  }
  async function captureScreenshot(el) {
    try {
      const snapdom = await loadSnapdom();
      const full = await snapdom.toCanvas(document.body, {
        backgroundColor: "#fff",
        // Skip our own shadow host entirely as a belt-and-braces measure.
        exclude: ["pointer-feedback"],
        fast: true
      });
      const dpr = window.devicePixelRatio || 1;
      const sx = Math.max(0, Math.round(window.scrollX * dpr));
      const sy = Math.max(0, Math.round(window.scrollY * dpr));
      const vw = Math.round(window.innerWidth * dpr);
      const vh = Math.round(window.innerHeight * dpr);
      const cw = Math.min(vw, full.width - sx);
      const ch = Math.min(vh, full.height - sy);
      const view = document.createElement("canvas");
      view.width = Math.max(1, cw);
      view.height = Math.max(1, ch);
      const vctx = view.getContext("2d");
      vctx.drawImage(full, sx, sy, view.width, view.height, 0, 0, view.width, view.height);
      const rect = el.getBoundingClientRect();
      const lineW = Math.max(2, Math.round(2 * dpr));
      vctx.strokeStyle = SHOT_HIGHLIGHT;
      vctx.lineWidth = lineW;
      vctx.strokeRect(
        Math.round(rect.left * dpr) + lineW / 2,
        Math.round(rect.top * dpr) + lineW / 2,
        Math.max(0, Math.round(rect.width * dpr) - lineW),
        Math.max(0, Math.round(rect.height * dpr) - lineW)
      );
      let out = view;
      if (view.width > SHOT_MAX_WIDTH) {
        const scale = SHOT_MAX_WIDTH / view.width;
        const small = document.createElement("canvas");
        small.width = SHOT_MAX_WIDTH;
        small.height = Math.max(1, Math.round(view.height * scale));
        small.getContext("2d").drawImage(view, 0, 0, small.width, small.height);
        out = small;
      }
      return await canvasToBlob(out);
    } catch (err) {
      console.warn("[pointer-feedback] screenshot capture failed", err);
      return null;
    }
  }
  var VOID_ELEMENTS = /^(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)$/;
  function escapeAttr(val) {
    return val.replace(/"/g, "&quot;").replace(/</g, "&lt;");
  }
  function isMasked(el) {
    if (typeof el.closest === "function") {
      return !!el.closest("[data-snapshot-mask]");
    }
    let curr = el;
    while (curr) {
      if (curr.hasAttribute && curr.hasAttribute("data-snapshot-mask")) return true;
      curr = curr.parentElement;
    }
    return false;
  }
  function isFormValueTag(tag) {
    return /^(input|textarea|select|option)$/i.test(tag);
  }
  function isSensitiveAttr(name) {
    const lower = name.toLowerCase();
    if (lower === "value" || lower === "authorization" || lower === "srcdoc") {
      return true;
    }
    if (lower.startsWith("data-")) {
      const rest = lower.slice(5);
      if (rest === "value" || rest === "email" || rest === "token" || rest === "secret" || rest.startsWith("user")) {
        return true;
      }
    }
    return false;
  }
  function maskAttrValue(name, val) {
    const lower = name.toLowerCase();
    const isStructural = lower === "id" || lower === "type" || lower === "role" || lower.startsWith("aria-");
    if (isStructural) {
      return escapeAttr(val);
    }
    return "•••";
  }
  function shallowSnapshot(el, captureText = true) {
    const tag = el.tagName.toLowerCase();
    const masked = isMasked(el);
    const isForm = isFormValueTag(tag);
    const rawAttrs = Array.from(el.attributes).filter(
      (a) => a.name !== "class" && a.name !== "style"
    );
    const keptAttrs = [];
    for (const a of rawAttrs) {
      const name = a.name;
      const lower = name.toLowerCase();
      if (isSensitiveAttr(lower)) continue;
      if (tag === "input") {
        if (lower === "data-snapshot-mask") continue;
        const isAllowed = lower === "type" || lower === "name" || lower === "id" || lower === "placeholder" || lower.startsWith("aria-") || lower.startsWith("data-");
        if (!isAllowed) continue;
      }
      if (isForm && lower === "value") continue;
      const rawVal = (a.value || "").slice(0, 120);
      if (masked) {
        const maskedVal = maskAttrValue(name, rawVal);
        keptAttrs.push(rawVal ? `${name}="${maskedVal}"` : name);
      } else {
        const escaped = escapeAttr(rawVal);
        keptAttrs.push(rawVal ? `${name}="${escaped}"` : name);
      }
    }
    if (tag === "input") {
      const inputEl = el;
      if (typeof inputEl.value === "string" && inputEl.value !== "") {
        keptAttrs.push('value="•••"');
      }
    }
    const attrs = keptAttrs.join(" ");
    const open = attrs ? `<${tag} ${attrs}>` : `<${tag}>`;
    if (VOID_ELEMENTS.test(tag)) return attrs ? `<${tag} ${attrs}/>` : `<${tag}/>`;
    let text = "";
    if (!captureText) {
      const raw = (el.textContent || "").replace(/\s+/g, " ").trim();
      text = raw ? "•••" : "";
    } else if (masked) {
      const raw = (el.textContent || "").replace(/\s+/g, " ").trim();
      text = raw ? "•••" : "";
    } else if (tag === "option") {
      text = "";
    } else if (tag === "textarea") {
      const ta = el;
      const val = typeof ta.value === "string" && ta.value !== "" ? ta.value : el.textContent || "";
      text = val.trim() ? "•••" : "";
    } else if (tag === "select") {
      const sel = el;
      const hasOpts = sel.options && sel.options.length > 0;
      const raw = (el.textContent || "").trim();
      text = hasOpts || raw || sel.value ? "•••" : "";
    } else {
      text = (el.textContent || "").replace(/\s+/g, " ").trim().slice(0, 160);
    }
    return `${open}${text}</${tag}>`;
  }
  function skipSelector(sel, classSet) {
    const s = sel.trim();
    if (s.includes("*")) return true;
    if (/::(before|after|backdrop|selection|placeholder|marker)/i.test(s)) return true;
    if (!/[.#[]/.test(s)) return true;
    if (!/[ >+~,]/.test(s) && s.startsWith(".")) {
      const cls = s.slice(1).replace(/\\/g, "");
      if (classSet.has(cls)) return true;
    }
    return false;
  }
  function collectAppliedRules(rules, el, out, cap, classSet) {
    for (const rule of Array.from(rules)) {
      if (out.length >= cap) return;
      const styleRule = rule;
      if (styleRule.selectorText) {
        const sel = styleRule.selectorText;
        if (skipSelector(sel, classSet)) continue;
        try {
          if (el.matches(sel)) {
            out.push({ selector: sel.slice(0, 160), styles: (styleRule.style.cssText || "").slice(0, 200) });
          }
        } catch (e) {
        }
      } else {
        const grouped = rule.cssRules;
        if (grouped) collectAppliedRules(grouped, el, out, cap, classSet);
      }
    }
  }
  function captureMetadata(el, sourceAttr, options) {
    const captureText = (options == null ? void 0 : options.captureText) !== false;
    const selector = generateSelector(el);
    const snapshot = shallowSnapshot(el, captureText);
    const classes = el.className && typeof el.className === "string" ? el.className.split(/\s+/).filter(Boolean) : [];
    const computed = {};
    const applied = [];
    const cs = window.getComputedStyle(el);
    ["color", "background-color", "font-size", "font-weight", "margin", "padding", "border", "text-align", "display", "flex-direction"].forEach((p) => {
      const v = cs.getPropertyValue(p);
      if (v) computed[p] = v.trim();
    });
    const inlineStyle = el.style;
    if (inlineStyle && inlineStyle.cssText) computed["inline-style"] = inlineStyle.cssText;
    const APPLIED_RULES_CAP = 6;
    const classSet = new Set(classes);
    for (const sheet of Array.from(document.styleSheets)) {
      if (applied.length >= APPLIED_RULES_CAP) break;
      let rules;
      try {
        rules = sheet.cssRules || sheet.rules;
      } catch (e) {
        continue;
      }
      if (!rules) continue;
      collectAppliedRules(rules, el, applied, APPLIED_RULES_CAP, classSet);
    }
    let parent = {};
    if (el.parentElement) {
      const p = el.parentElement;
      parent = {
        tag: p.tagName.toLowerCase(),
        classes: p.className && typeof p.className === "string" ? p.className.split(/\s+/).filter(Boolean) : [],
        id: p.id || null
      };
    }
    let sourcePath = null;
    let node = el;
    while (node && node.getAttribute) {
      const v = node.getAttribute(sourceAttr);
      if (v) {
        sourcePath = v;
        break;
      }
      node = node.parentElement;
    }
    if (!sourcePath) {
      sourcePath = detectFrameworkSourcePath(el);
    }
    return {
      // Internal display fields (not sent to API)
      _tag: el.tagName.toLowerCase(),
      _sourcePath: sourcePath,
      _snapshotPreview: snapshot,
      // API element shape (camelCase)
      selector,
      snapshot,
      classes: JSON.stringify(classes),
      computedStyles: JSON.stringify(computed),
      appliedCssRules: JSON.stringify(applied),
      sourcePath,
      parentInfo: JSON.stringify(parent)
    };
  }

  // src/shortcut.ts
  var DEFAULT_SHORTCUT = { code: "KeyC", alt: true, shift: true, ctrl: true, meta: false };
  var MODIFIER_TOKENS = /* @__PURE__ */ new Set(["ctrl", "alt", "shift", "meta"]);
  function parseShortcut(raw) {
    if (!raw) return { ...DEFAULT_SHORTCUT };
    const parts = raw.split("+").map((p) => p.trim()).filter(Boolean);
    const code = parts[parts.length - 1];
    if (!code || MODIFIER_TOKENS.has(code.toLowerCase())) return { ...DEFAULT_SHORTCUT };
    const mods = new Set(parts.slice(0, -1).map((p) => p.toLowerCase()));
    return {
      code,
      ctrl: mods.has("ctrl"),
      alt: mods.has("alt"),
      shift: mods.has("shift"),
      meta: mods.has("meta")
    };
  }
  function serializeShortcut(binding) {
    const parts = [];
    if (binding.ctrl) parts.push("ctrl");
    if (binding.alt) parts.push("alt");
    if (binding.shift) parts.push("shift");
    if (binding.meta) parts.push("meta");
    parts.push(binding.code);
    return parts.join("+");
  }
  function matchesShortcut(e, binding) {
    return e.code === binding.code && e.altKey === binding.alt && e.shiftKey === binding.shift && e.ctrlKey === binding.ctrl && e.metaKey === binding.meta;
  }
  function isMacPlatform() {
    if (typeof navigator === "undefined") return false;
    return /Mac|iPhone|iPad|iPod/.test(navigator.platform || navigator.userAgent || "");
  }
  function codeToLabel(code) {
    if (code.startsWith("Key")) return code.slice(3);
    if (code.startsWith("Digit")) return code.slice(5);
    if (code === "Space") return "Space";
    if (code === "Escape") return "Esc";
    if (code === "Enter") return "Enter";
    return code;
  }
  function formatShortcut(binding, mac = isMacPlatform()) {
    const label = codeToLabel(binding.code);
    const parts = [];
    if (mac) {
      if (binding.ctrl) parts.push("⌃");
      if (binding.alt) parts.push("⌥");
      if (binding.shift) parts.push("⇧");
      if (binding.meta) parts.push("⌘");
      parts.push(label);
      return parts.join("");
    }
    if (binding.ctrl) parts.push("Ctrl");
    if (binding.meta) parts.push("Win");
    if (binding.alt) parts.push("Alt");
    if (binding.shift) parts.push("Shift");
    parts.push(label);
    return parts.join("+");
  }
  function ariaKeyshortcuts(binding) {
    const parts = [];
    if (binding.ctrl) parts.push("Control");
    if (binding.alt) parts.push("Alt");
    if (binding.shift) parts.push("Shift");
    if (binding.meta) parts.push("Meta");
    parts.push(codeToLabel(binding.code));
    return parts.join("+");
  }

  // src/theme.ts
  function relativeLuminance(r, g, b) {
    const lin = (c) => {
      const s = c / 255;
      return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  }
  function parseRgba(color) {
    const m = color.match(/rgba?\(([^)]+)\)/i);
    if (!m) return null;
    const parts = m[1].split(",").map((s) => parseFloat(s.trim()));
    const [r, g, b] = parts;
    const a = parts[3];
    if (r === void 0 || g === void 0 || b === void 0) return null;
    if ([r, g, b].some((n) => Number.isNaN(n))) return null;
    return [r, g, b, a === void 0 || Number.isNaN(a) ? 1 : a];
  }
  function detectSystemTheme() {
    var _a2;
    try {
      return typeof window !== "undefined" && ((_a2 = window.matchMedia) == null ? void 0 : _a2.call(window, "(prefers-color-scheme: dark)").matches) ? "dark" : "light";
    } catch {
      return "light";
    }
  }
  function detectSiteTheme() {
    try {
      const candidates = [document.body, document.documentElement].filter(Boolean);
      for (const el of candidates) {
        const rgba = parseRgba(getComputedStyle(el).backgroundColor);
        if (rgba && rgba[3] > 0) return relativeLuminance(rgba[0], rgba[1], rgba[2]) < 0.5 ? "dark" : "light";
      }
    } catch {
    }
    return detectSystemTheme();
  }

  // src/auth-ui.ts
  function showLoginModal(host, afterLogin) {
    host.afterLogin = afterLogin || null;
    host.root.innerHTML = TPL.loginModal(host.project);
    const skipBtn = host.root.querySelector("#fbk-login-skip");
    if (skipBtn) skipBtn.addEventListener("click", () => {
      host.afterLogin = null;
      host.renderChrome();
    });
    renderLoginView(host);
  }
  async function populateRoles(host, selectEl, errEl) {
    if (!selectEl) return;
    selectEl.disabled = true;
    selectEl.innerHTML = '<option value="">Loading roles…</option>';
    try {
      const roles = await host.apiRoles();
      if (!roles.length) {
        selectEl.innerHTML = '<option value="">No roles available</option>';
        return;
      }
      selectEl.innerHTML = roles.map((r) => `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}</option>`).join("");
      selectEl.disabled = false;
    } catch (e) {
      selectEl.innerHTML = '<option value="">Could not load roles</option>';
      if (errEl) errEl.textContent = e.message || "Could not load roles.";
    }
  }
  function afterAuthOk(host, token, user) {
    host.saveAuth(token, user);
    host.root.innerHTML = "";
    if (host.afterLogin) {
      const cb = host.afterLogin;
      host.afterLogin = null;
      cb();
    } else {
      host.init();
    }
  }
  function renderLoginView(host, opts = {}) {
    const body = host.root.querySelector("#fbk-auth-body");
    if (!body) return;
    body.innerHTML = TPL.loginBody(!!opts.rejected);
    const emailEl = body.querySelector("#fbk-email");
    const passEl = body.querySelector("#fbk-password");
    const errEl = body.querySelector("#fbk-login-error");
    const submitBtn = body.querySelector("#fbk-login-submit");
    const doLogin = async () => {
      const email = emailEl.value.trim();
      const password = passEl.value;
      if (!email) {
        errEl.textContent = "Please enter your email.";
        return;
      }
      if (!password) {
        errEl.textContent = "Please enter your password.";
        return;
      }
      errEl.textContent = "";
      submitBtn.disabled = true;
      submitBtn.textContent = "Signing in…";
      const restore = () => {
        submitBtn.disabled = false;
        submitBtn.textContent = "Sign in";
      };
      try {
        const r = await host.apiLogin(email, password);
        const envelope = await r.json();
        const data = envelope.data || null;
        const status = data && data.status;
        if (status === "ok" && data.token) {
          afterAuthOk(host, data.token, data.user);
          return;
        }
        if (status === "pending") {
          errEl.textContent = envelope.message || "Your request is awaiting admin approval.";
          restore();
          return;
        }
        if (status === "disabled") {
          errEl.textContent = envelope.message || "Your account is disabled.";
          restore();
          return;
        }
        if (status === "rejected") {
          renderLoginView(host, { rejected: true });
          const re = host.root.querySelector("#fbk-auth-body");
          re.querySelector("#fbk-email").value = email;
          re.querySelector("#fbk-password").value = password;
          re.querySelector("#fbk-login-error").textContent = envelope.message || "Your request was rejected.";
          return;
        }
        errEl.textContent = envelope.message || "Invalid email or password.";
        restore();
      } catch (e) {
        errEl.textContent = "Network error. Please try again.";
        restore();
      }
    };
    submitBtn.addEventListener("click", doLogin);
    passEl.addEventListener("keydown", (e) => {
      if (e.key === "Enter") doLogin();
    });
    body.querySelector("#fbk-show-signup").addEventListener("click", () => renderSignupView(host));
    if (opts.rejected) {
      const roleEl = body.querySelector("#fbk-reapply-role");
      const reBtn = body.querySelector("#fbk-reapply-submit");
      populateRoles(host, roleEl, errEl);
      reBtn.addEventListener("click", async () => {
        const email = emailEl.value.trim();
        const password = passEl.value;
        const roleId = roleEl.value;
        if (!roleId) {
          errEl.textContent = "Please choose a role.";
          return;
        }
        if (!email || !password) {
          errEl.textContent = "Enter your email and password to request again.";
          return;
        }
        errEl.textContent = "";
        reBtn.disabled = true;
        reBtn.textContent = "Submitting…";
        try {
          const r = await host.apiRegister({ email, password, displayName: "", roleId });
          const envelope = await r.json();
          if (!r.ok || !envelope.isSuccess) {
            errEl.textContent = envelope.message || "Could not submit your request.";
            reBtn.disabled = false;
            reBtn.textContent = "Request again";
            return;
          }
          renderLoginView(host);
          const reBody = host.root.querySelector("#fbk-auth-body");
          reBody.querySelector("#fbk-email").value = email;
          reBody.querySelector("#fbk-login-error").textContent = envelope.message || "Request submitted — an admin will review it.";
        } catch (e) {
          errEl.textContent = "Network error. Please try again.";
          reBtn.disabled = false;
          reBtn.textContent = "Request again";
        }
      });
    }
  }
  function renderSignupView(host) {
    const body = host.root.querySelector("#fbk-auth-body");
    if (!body) return;
    body.innerHTML = TPL.signupBody();
    const nameEl = body.querySelector("#fbk-su-name");
    const emailEl = body.querySelector("#fbk-su-email");
    const passEl = body.querySelector("#fbk-su-password");
    const roleEl = body.querySelector("#fbk-su-role");
    const errEl = body.querySelector("#fbk-signup-error");
    const okEl = body.querySelector("#fbk-signup-success");
    const submitBtn = body.querySelector("#fbk-signup-submit");
    populateRoles(host, roleEl, errEl);
    body.querySelector("#fbk-show-login").addEventListener("click", () => renderLoginView(host));
    const doSignup = async () => {
      const displayName = nameEl.value.trim();
      const email = emailEl.value.trim();
      const password = passEl.value;
      const roleId = roleEl.value;
      errEl.textContent = "";
      okEl.textContent = "";
      if (!displayName) {
        errEl.textContent = "Please enter your name.";
        return;
      }
      if (!email) {
        errEl.textContent = "Please enter your email.";
        return;
      }
      if (!password) {
        errEl.textContent = "Please choose a password.";
        return;
      }
      if (!roleId) {
        errEl.textContent = "Please choose a role.";
        return;
      }
      submitBtn.disabled = true;
      submitBtn.textContent = "Submitting…";
      const restore = () => {
        submitBtn.disabled = false;
        submitBtn.textContent = "Create account";
      };
      try {
        const r = await host.apiRegister({ email, password, displayName, roleId });
        const envelope = await r.json();
        if (!r.ok || !envelope.isSuccess) {
          errEl.textContent = envelope.message || "Could not create your account.";
          restore();
          return;
        }
        okEl.textContent = envelope.message || "Request submitted — an admin will review it.";
        submitBtn.textContent = "Request submitted";
        submitBtn.disabled = true;
        [nameEl, emailEl, passEl, roleEl].forEach((el) => {
          el.disabled = true;
        });
      } catch (e) {
        errEl.textContent = "Network error. Please try again.";
        restore();
      }
    };
    submitBtn.addEventListener("click", doSignup);
    passEl.addEventListener("keydown", (e) => {
      if (e.key === "Enter") doSignup();
    });
  }

  // src/element.ts
  var PIN_CLUSTER_RADIUS = 24;
  var PIN_HALF_WIDTH = 16;
  var PIN_HEIGHT = 30;
  var PIN_TOOLTIP_HEIGHT_ESTIMATE = 150;
  var _PointerFeedback = class _PointerFeedback extends HTMLElement {
    constructor() {
      super(...arguments);
      this._mounted = false;
      this.project = "";
      this.environmentAttr = "";
      this.sourceAttr = "data-component-source";
      this.screenshotEnabled = true;
      this.launcherPosition = "bottom-end";
      this.server = "";
      this.environmentInt = 0;
      /** True when the page, the host config or a saved choice named an environment — the server's
       *  origin-resolved answer is then advisory and must not override it. */
      this.environmentExplicit = false;
      this.comments = [];
      this.statusFilter = "all";
      this.mineOnly = false;
      this.authorFilter = null;
      this.hiddenPrivateCount = 0;
      this._collapsed = true;
      this._disabled = false;
      this.picking = false;
      /** A magic-link token stripped from the URL, awaiting redemption in _boot(). */
      this._pendingInviteToken = null;
      this.sidebarOpen = false;
      this.hovered = null;
      this.token = null;
      this.user = null;
      this.afterLogin = null;
      this.predefinedActions = [];
      // Whether switching is hard-locked off regardless of role (an explicit `fixed-environment="true"`
      // attribute, or host-injected config like the browser extension) — when true, the toolbar shows a
      // read-only label instead of a switcher, no matter what /capture-config's role check says. Plain
      // `environment="..."` alone no longer implies this — it only seeds the starting value now, so a
      // normal install (which always sets `environment` from *_POINTER_ENV) doesn't silently defeat the
      // role-gated switcher below.
      this.hasFixedEnvironment = false;
      // Per-project, per-role: whether THIS logged-in caller may switch environments at all (vs a
      // read-only label). Defaults true (matches pre-existing behavior) until /capture-config
      // resolves post-login and possibly turns it off (e.g. for a Client/QuickAccess role by default,
      // or any role the project owner excluded). See ProjectService.ShowEnvironmentSelectorFor.
      this.showEnvironmentSelector = true;
      // Project-level opt-in (default off), read once at init via /capture-config. Gates both whether
      // the widget buffers console/network events at all and whether "Report as a bug" is shown.
      this.pageContextCaptureEnabled = false;
      // Per-project text capture toggle (default true until /capture-config resolves).
      // When false, the widget emits no text content in the DOM snapshot and masks pageTitle.
      this.captureTextContent = true;
      // Whether the AI apply flow (skill.md) bundles applied comments into one commit or commits each
      // one separately — 1=Single, 2=Separate (backend CommitStyle enum, read as-is like
      // environmentInt already is). Changeable via a small widget control, but only rendered when
      // canEditSettings is true (admin or the project's creator — same gate as the PATCH itself).
      this.commitStyle = 1;
      this.canEditSettings = false;
      // The numeric project id (distinct from the `project` key attribute) — needed to PATCH
      // /api/admin/projects/{id} for the commit-style control; resolved once via /capture-config.
      this.projectId = null;
      // Display name resolved from /capture-config (falls back to the raw `project` key attribute
      // until it loads). Shown next to the environment indicator so a visitor can immediately tell
      // which project an install is actually bound to — project keys aren't unique across a workspace.
      this.projectName = "";
      // Per-user "add comment" keyboard shortcut, synced to the account (User.AddCommentShortcut,
      // not localStorage) — set from `this.user.addCommentShortcut` in loadAuth()/saveAuth() below.
      // Default is Ctrl+Alt+Shift+C / Control+Option+Shift+C — see shortcut.ts for why.
      this.shortcut = parseShortcut(void 0);
      // True when a host (the browser extension) injected a token: auth is entirely owned by that
      // host, re-applied on every reload (see connectedCallback), so the widget's own sign-out would
      // be immediately overwritten and must not be offered — switching accounts happens in the
      // extension popup instead.
      this.authOwnedByHost = false;
      this._pendingShotPromise = null;
      // The comment id whose pin should show the attention ripple on its NEXT renderPins() — cleared
      // shortly after so re-renders (env switch, poll, etc.) don't replay it forever.
      this._newPinId = null;
      // The toolbar's drag offset from its default bottom-right anchor (see enableToolbarDrag).
      this._toolbarDx = 0;
      this._toolbarDy = 0;
      this.unreadNotifyCount = 0;
      this._notifyPollTimer = null;
      this._updatesMenuClose = null;
      this._onVisibilityChange = null;
      this._userMenuClose = null;
      this._clusterMenuClose = null;
      this._recordingShortcut = false;
      this._shortcutRecordingCleanup = null;
      this._backdropObserver = null;
      this._backdropRaf = 0;
      this._stylesPromise = null;
    }
    connectedCallback() {
      var _a2, _b;
      try {
        performance.mark("pf:boot:start");
      } catch {
      }
      if (this._mounted) return;
      this._mounted = true;
      this.project = this.getAttribute("project") || "";
      this.environmentAttr = this.getAttribute("environment") || "";
      this.hasFixedEnvironment = (this.getAttribute("fixed-environment") || "").toLowerCase() === "true";
      this.sourceAttr = this.getAttribute("source-attr") || "data-component-source";
      this.screenshotEnabled = (this.getAttribute("screenshot") || "").toLowerCase() !== "false";
      const pos = (this.getAttribute("launcher-position") || "").toLowerCase();
      this.launcherPosition = POSITIONS.includes(pos) ? pos : "bottom-end";
      this.server = (this.getAttribute("server") || (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, "");
      this.environmentExplicit = !!this.environmentAttr;
      this.environmentInt = this.environmentAttr ? (_a2 = ENV_MAP[this.environmentAttr.toLowerCase()]) != null ? _a2 : 2 : 0;
      const injected = typeof window !== "undefined" ? window.__POINTER_CONFIG__ : void 0;
      if (injected) {
        if (injected.server) this.server = injected.server.replace(/\/$/, "");
        if (injected.project) this.project = injected.project;
        if (injected.environment) {
          this.environmentAttr = injected.environment;
          this.environmentInt = ENV_MAP[injected.environment.toLowerCase()] || this.environmentInt;
          this.environmentExplicit = true;
        }
        if (injected.fixedEnvironment === true) this.hasFixedEnvironment = true;
      }
      if (!this.hasFixedEnvironment) {
        try {
          const savedEnv = localStorage.getItem("pointer_env_" + this.project);
          if (savedEnv && ENV_MAP[savedEnv.toLowerCase()]) {
            this.environmentAttr = savedEnv.toLowerCase();
            this.environmentInt = ENV_MAP[savedEnv.toLowerCase()];
            this.environmentExplicit = true;
          }
        } catch (e) {
        }
      }
      if (!this.environmentAttr) this.environmentAttr = ENV_NAME[this.environmentInt] || "unknown";
      this._collapsed = (() => {
        try {
          return sessionStorage.getItem("pointer_visible") !== "1";
        } catch (e) {
          return true;
        }
      })();
      this._pendingInviteToken = this.stripInviteTokenFromUrl();
      this.loadAuth();
      if (injected == null ? void 0 : injected.token) {
        this.token = injected.token;
        if (injected.user !== void 0) this.user = injected.user;
        this.shortcut = parseShortcut((_b = this.user) == null ? void 0 : _b.addCommentShortcut);
        this.authOwnedByHost = true;
      }
      this.applyTheme();
      this.style.position = "fixed";
      this.style.zIndex = "2147483647";
      this.style.top = "0";
      this.style.left = "0";
      this.style.pointerEvents = "none";
      this.attachShadow({ mode: "open" });
      this._styleLink = document.createElement("link");
      this._styleLink.rel = "stylesheet";
      if (CSS_INTEGRITY) {
        this._styleLink.integrity = CSS_INTEGRITY;
        this._styleLink.crossOrigin = "anonymous";
      }
      this._styleLink.href = (injected == null ? void 0 : injected.cssUrl) || CSS_URL || `${this.server}/pointer.css`;
      this.shadowRoot.appendChild(this._styleLink);
      this.root = document.createElement("div");
      this.shadowRoot.appendChild(this.root);
      this._stylesPromise = this._stylesReady();
      ensureHighlightStyle();
      if (!this.project) {
        console.error("[pointer-feedback] Missing required `project` attribute. Component disabled.");
        return;
      }
      this._onHover = this.onHover.bind(this);
      this._onPick = this.onPick.bind(this);
      this._onPickKey = this.onPickKey.bind(this);
      this._onShortcutKeydown = this.onShortcutKeydown.bind(this);
      this._reposition = () => {
        this.renderPins();
        this.updateMenuSide();
      };
      window.addEventListener("scroll", this._reposition, true);
      window.addEventListener("resize", this._reposition);
      this._scheduleBackdropUpdate = () => {
        if (this._backdropRaf) return;
        this._backdropRaf = requestAnimationFrame(() => {
          this._backdropRaf = 0;
          this.punchBackdropHoles();
        });
      };
      window.addEventListener("resize", this._scheduleBackdropUpdate);
      window.addEventListener("scroll", this._scheduleBackdropUpdate, true);
      this.root.addEventListener("transitionend", this._scheduleBackdropUpdate);
      this._backdropObserver = new MutationObserver((mutations) => {
        const isOwnMutation = (m) => m.target instanceof Element && m.target.matches(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
        if (mutations.some((m) => !isOwnMutation(m))) this._scheduleBackdropUpdate();
      });
      this._backdropObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["style", "class"] });
      this._backdropObserver.observe(this.root, { childList: true, subtree: true, attributes: true, attributeFilter: ["style", "class"] });
      this._scheduleBackdropUpdate();
      document.addEventListener("keydown", this._onShortcutKeydown);
      this._boot();
    }
    // Wait for the stylesheet to load, then render the first view (avoids a flash
    // of unstyled UI). A short timeout guarantees we never hang on slow CSS.
    async _boot() {
      if (!await this._checkWidgetActive()) return;
      await Promise.all([this._stylesReady(), loadBranding(this.server)]);
      let inviteFailed = false;
      if (this._pendingInviteToken) {
        inviteFailed = !await this.redeemInviteToken(this._pendingInviteToken);
        this._pendingInviteToken = null;
      }
      if (this.token) await this.hydrateIdentity();
      if (this.token) this.init();
      else this.renderChrome();
      try {
        performance.mark("pf:boot:end");
      } catch {
      }
      if (this.token) void this._reportBuildSha();
      if (inviteFailed) this.toast("This invite link is invalid or expired — ask for a new one.", "error");
    }
    /**
     * Reports `<html data-build-sha>` once per page load.
     *
     * Fire-and-forget and failure-silent by design: this is a nicety on top of the feedback loop,
     * and a visitor must never see an error — or a delayed widget — because a build beacon did not
     * land. Once per load because every additional call is a no-op the server still has to scan for.
     *
     * The CLI's `pointer status --deployed` is the primary path. This one only helps where the
     * widget actually ships to production, which the install guide advises against.
     */
    async _reportBuildSha() {
      var _a2, _b;
      if (_PointerFeedback._buildShaReported) return;
      try {
        const sha = (_b = (_a2 = document.documentElement) == null ? void 0 : _a2.dataset) == null ? void 0 : _b.buildSha;
        if (!sha || !/^[0-9a-f]{7,40}$/.test(sha)) return;
        _PointerFeedback._buildShaReported = true;
        await pfFetch(`${this.server}/api/projects/${encodeURIComponent(this.project)}/builds`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Authorization: `Bearer ${this.token}` },
          body: JSON.stringify({ sha })
        });
      } catch {
      }
    }
    // Anonymous, pre-auth: asks the server whether this project should render on this page's
    // origin at all (gates on the project's overall activation AND, if this origin matches a
    // configured "other environment" URL, that specific mapping's own active flag). A network
    // failure or non-OK response is treated as "keep hidden," not "fail open" — an outage in
    // this check must not accidentally show the widget where it was explicitly deactivated.
    async _checkWidgetActive() {
      var _a2;
      try {
        const origin = typeof window !== "undefined" ? window.location.origin : "";
        const url = `${this.server}/api/public/projects/${encodeURIComponent(this.project)}/widget-status?origin=${encodeURIComponent(origin)}`;
        const res = await pfFetch(url);
        if (!res.ok) return false;
        const body = await res.json();
        const data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
        return (data == null ? void 0 : data.active) === true;
      } catch {
        return false;
      }
    }
    // An admin disabled this project: tear the widget down silently — no toolbar,
    // no launcher, no toast/console error. The only trace is the 409 already visible
    // in the browser's network tab. Detected from the comments endpoint's
    // 409 "project disabled" response.
    disableSilently() {
      if (this._disabled) return;
      this._disabled = true;
      try {
        this.stopPicking();
      } catch {
      }
      this.comments = [];
      if (this.root) this.root.innerHTML = "";
    }
    _stylesReady() {
      if (this._stylesPromise) return this._stylesPromise;
      this._stylesPromise = new Promise((resolve) => {
        const link = this._styleLink;
        if (!link || link.sheet) return resolve();
        let done = false;
        const finish = () => {
          if (!done) {
            done = true;
            resolve();
          }
        };
        link.addEventListener("load", finish, { once: true });
        link.addEventListener("error", async () => {
          try {
            const fetchOpts = { mode: "cors" };
            if (CSS_INTEGRITY) {
              fetchOpts.integrity = CSS_INTEGRITY;
            }
            const cssUrl = link.href || CSS_URL;
            const res = await rawFetch(cssUrl, fetchOpts);
            if (res.ok) {
              const text = await res.text();
              if (typeof CSSStyleSheet !== "undefined") {
                const sheet = new CSSStyleSheet();
                if (typeof sheet.replace === "function") {
                  await sheet.replace(text);
                } else if (typeof sheet.replaceSync === "function") {
                  sheet.replaceSync(text);
                }
                if (this.shadowRoot) {
                  this.shadowRoot.adoptedStyleSheets = [sheet];
                }
              }
            }
          } catch {
          } finally {
            finish();
          }
        }, { once: true });
        setTimeout(finish, 1500);
      });
      return this._stylesPromise;
    }
    disconnectedCallback() {
      var _a2, _b;
      window.removeEventListener("scroll", this._reposition, true);
      window.removeEventListener("resize", this._reposition);
      window.removeEventListener("resize", this._scheduleBackdropUpdate);
      window.removeEventListener("scroll", this._scheduleBackdropUpdate, true);
      (_a2 = this.root) == null ? void 0 : _a2.removeEventListener("transitionend", this._scheduleBackdropUpdate);
      (_b = this._backdropObserver) == null ? void 0 : _b.disconnect();
      if (this._backdropRaf) cancelAnimationFrame(this._backdropRaf);
      document.removeEventListener("keydown", this._onShortcutKeydown);
      if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
      this.stopPicking();
      this.stopNotificationPolling();
      this.closeUpdatesMenu();
      stopPageContextCapture();
    }
    // --- "Add comment" keyboard shortcut --------------------------------------
    isEditableTarget(e) {
      const target = e.composedPath()[0];
      if (!target || !target.tagName) return false;
      const tag = target.tagName.toLowerCase();
      return tag === "input" || tag === "textarea" || tag === "select" || !!target.isContentEditable;
    }
    onShortcutKeydown(e) {
      if (this._recordingShortcut || this._disabled) return;
      if (this.isEditableTarget(e)) return;
      if (!matchesShortcut(e, this.shortcut)) return;
      e.preventDefault();
      this.activateAddComment();
    }
    // Shared by both the toolbar's "add" button and the keyboard shortcut — expands the widget
    // first if it's collapsed (the toolbar buttons don't exist in the DOM until then), then either
    // prompts login or toggles element-picking, exactly like clicking #fbk-add.
    activateAddComment() {
      if (this._collapsed) this.showOverlay();
      if (!this.token) {
        showLoginModal(this, () => {
          Promise.resolve(this.init()).then(() => this.togglePicking());
        });
        return;
      }
      this.togglePicking();
    }
    // Enters "recording" mode on the user-menu shortcut button: the next non-modifier keydown
    // (with at least one modifier held) becomes the new binding. Escape cancels.
    beginRecordingShortcut(btnEl) {
      this._recordingShortcut = true;
      const original = btnEl.textContent || "";
      btnEl.textContent = "Press keys… (Esc to cancel)";
      const onKey = (e) => {
        e.preventDefault();
        e.stopPropagation();
        if (e.key === "Escape") {
          btnEl.textContent = original;
          cleanup();
          return;
        }
        if (e.key === "Shift" || e.key === "Alt" || e.key === "Control" || e.key === "Meta") return;
        if (!(e.altKey || e.ctrlKey || e.metaKey || e.shiftKey)) {
          btnEl.textContent = "Add a modifier key (Alt/Shift/Ctrl/⌘)…";
          return;
        }
        const binding = {
          code: e.code,
          alt: e.altKey,
          shift: e.shiftKey,
          ctrl: e.ctrlKey,
          meta: e.metaKey
        };
        btnEl.textContent = "Saving…";
        cleanup();
        this.saveShortcutPreference(binding).then((ok) => {
          btnEl.textContent = formatShortcut(this.shortcut);
          this.toast(ok ? "Shortcut updated" : "Failed to save — try again", ok ? "" : "error");
        });
      };
      const cleanup = () => {
        this._recordingShortcut = false;
        this._shortcutRecordingCleanup = null;
        document.removeEventListener("keydown", onKey, true);
      };
      this._shortcutRecordingCleanup = cleanup;
      document.addEventListener("keydown", onKey, true);
    }
    // Persists a new binding to the account (PATCH /api/me/preferences) so it follows the user
    // across browsers/machines — an empty string resets to the widget's built-in default. Updates
    // the cached `pointer_user` mirror on success so a page reload reflects it instantly, without
    // waiting for the next fresh login.
    async saveShortcutPreference(binding) {
      try {
        const r = await this.api("/api/me/preferences", {
          method: "PATCH",
          body: JSON.stringify({ addCommentShortcut: binding ? serializeShortcut(binding) : "" })
        });
        if (!r.ok) return false;
        this.shortcut = binding ? binding : parseShortcut(void 0);
        if (this.user) {
          this.user = { ...this.user, addCommentShortcut: binding ? serializeShortcut(binding) : void 0 };
          localStorage.setItem("pointer_user", JSON.stringify(this.user));
        }
        this.updateAddButtonTooltip();
        return true;
      } catch {
        return false;
      }
    }
    // --- Auth helpers --------------------------------------------------------
    loadAuth() {
      var _a2;
      try {
        this.token = typeof localStorage !== "undefined" ? localStorage.getItem("pointer_token") || null : null;
        const raw = typeof localStorage !== "undefined" ? localStorage.getItem("pointer_user") : null;
        this.user = raw ? JSON.parse(raw) : null;
      } catch {
        this.token = null;
        this.user = null;
      }
      this.shortcut = parseShortcut((_a2 = this.user) == null ? void 0 : _a2.addCommentShortcut);
    }
    saveAuth(token, user) {
      this.token = token;
      this.user = user;
      this.shortcut = parseShortcut(user == null ? void 0 : user.addCommentShortcut);
      localStorage.setItem("pointer_token", token);
      localStorage.setItem("pointer_user", JSON.stringify(user));
      this.startNotificationPolling();
    }
    clearAuth() {
      this.token = null;
      this.user = null;
      this.unreadNotifyCount = 0;
      this.stopNotificationPolling();
      this.closeUpdatesMenu();
      localStorage.removeItem("pointer_token");
      localStorage.removeItem("pointer_user");
    }
    handle401() {
      this.clearAuth();
      showLoginModal(this);
    }
    // --- Theme ------------------------------------------------------------
    // Deliberately WIDGET-LOCAL, not account-wide: `User.theme`/`/api/me/preferences` is the same
    // field the dashboard's own theme toggle reads to paint the entire admin app, so persisting the
    // widget's choice there would silently flip the dashboard's site-wide theme too. An explicit
    // per-browser override (localStorage, set from the user menu below) wins; otherwise the host
    // page's own rendered theme; otherwise the OS preference. See theme.ts.
    resolveTheme() {
      try {
        const stored = localStorage.getItem("pointer_widget_theme");
        if (stored === "light" || stored === "dark") return stored;
      } catch {
      }
      return detectSiteTheme();
    }
    // Reflects the resolved mode onto the host element (light DOM, not shadowRoot) as
    // `data-fbk-theme` — _theme.scss's `:host([data-fbk-theme="dark"])` block reads it to swap the
    // shadow UI's token values, and a consuming app can target the same attribute from its own CSS
    // to override any single token per project, exactly like the light defaults.
    applyTheme() {
      this.setAttribute("data-fbk-theme", this.resolveTheme());
    }
    // Sets the widget's own per-browser theme override — never touches the account (see the note
    // on resolveTheme above), so it can never bleed into the host dashboard's own theme.
    setThemeOverride(mode) {
      try {
        localStorage.setItem("pointer_widget_theme", mode);
      } catch {
      }
      this.applyTheme();
    }
    // Persists the account's language preference — the widget's own UI text stays English (see
    // the language-scope decision); this only keeps the account in sync with the dashboard's
    // language switcher, which reads the same field.
    async saveLanguagePreference(lang) {
      try {
        const r = await this.api("/api/me/preferences", {
          method: "PATCH",
          body: JSON.stringify({ language: lang })
        });
        if (!r.ok) return false;
        if (this.user) {
          this.user = { ...this.user, language: lang };
          localStorage.setItem("pointer_user", JSON.stringify(this.user));
        }
        return true;
      } catch {
        return false;
      }
    }
    async init() {
      await loadStatusCatalog(this.server);
      this.renderChrome();
      await Promise.all([this.fetchComments(), this.fetchPredefinedActions(), this.fetchCaptureConfig()]);
      if (this.token) this.startNotificationPolling();
      this.renderSidebar();
      this.renderPins();
      if (this._collapsed) this.renderChrome();
    }
    // Fetch the project's predefined-action options for the comment popover picker.
    // Silently no-ops on failure — the picker simply won't appear.
    async fetchPredefinedActions() {
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/predefined-actions`);
        if (!r.ok) {
          this.predefinedActions = [];
          return;
        }
        const envelope = await r.json();
        this.predefinedActions = envelope && envelope.data || [];
      } catch {
        this.predefinedActions = [];
      }
    }
    // Read the project's page-context capture toggle and, if on, start buffering
    // console/network events. Silently no-ops on failure (feature stays off).
    async fetchCaptureConfig() {
      var _a2, _b, _c, _d, _e;
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/capture-config`);
        if (!r.ok) {
          this.pageContextCaptureEnabled = false;
          return;
        }
        const envelope = await r.json();
        this.pageContextCaptureEnabled = !!(envelope && envelope.data && envelope.data.pageContextCaptureEnabled);
        if ((envelope == null ? void 0 : envelope.data) && typeof envelope.data.captureTextContent === "boolean") {
          this.captureTextContent = envelope.data.captureTextContent;
        }
        this.projectName = envelope && envelope.data && envelope.data.name || this.project;
        const showSelector = (_a2 = envelope == null ? void 0 : envelope.data) == null ? void 0 : _a2.showEnvironmentSelector;
        this.showEnvironmentSelector = showSelector !== false;
        this.projectId = typeof ((_b = envelope == null ? void 0 : envelope.data) == null ? void 0 : _b.id) === "number" ? envelope.data.id : null;
        const resolved = (_c = envelope == null ? void 0 : envelope.data) == null ? void 0 : _c.resolvedEnvironment;
        if (!this.environmentExplicit && typeof resolved === "number" && ENV_NAME[resolved]) {
          this.environmentInt = resolved;
          this.environmentAttr = ENV_NAME[resolved];
          const envSel = this.root && this.root.querySelector("#fbk-env");
          if (envSel && "value" in envSel) envSel.value = this.environmentAttr;
          const envLabel = this.root && this.root.querySelector(".fbk-env-label");
          if (envLabel) envLabel.textContent = "· " + this.environmentAttr;
        }
        this.commitStyle = typeof ((_d = envelope == null ? void 0 : envelope.data) == null ? void 0 : _d.commitStyle) === "number" ? envelope.data.commitStyle : 1;
        this.canEditSettings = !!((_e = envelope == null ? void 0 : envelope.data) == null ? void 0 : _e.canEditSettings);
        this.updateProjectNameLabel();
        this.updateEnvironmentSelectorVisibility();
        this.renderCommitStyleControl();
        if (this.pageContextCaptureEnabled) startPageContextCapture(this.server, SCRIPT_SRC);
      } catch {
        this.pageContextCaptureEnabled = false;
        this.captureTextContent = true;
      }
    }
    // Patches the already-rendered header label in place rather than a full renderChrome() —
    // re-rendering chrome here would drop the sidebar's open/closed state mid-session.
    updateProjectNameLabel() {
      const el = this.root && this.root.querySelector("#fbk-project-name");
      if (el) {
        el.textContent = this.projectName;
        el.setAttribute("title", this.projectName);
      }
    }
    // /capture-config resolves AFTER the first renderChrome() (which assumed the switcher was
    // visible), so if it turns out this caller should NOT see it, swap the already-rendered
    // <select id="fbk-env"> for the same read-only label used for a host-fixed environment — same
    // reasoning as updateProjectNameLabel() above (no full re-render, mid-session state stays put).
    // A no-op when the toolbar isn't open yet or the switcher was already hidden — the NEXT
    // renderChrome() (e.g. when the visitor opens the toolbar) already reads the updated flag.
    updateEnvironmentSelectorVisibility() {
      if (this.showEnvironmentSelector || this.hasFixedEnvironment) return;
      const sel = this.root && this.root.querySelector("#fbk-env");
      if (!sel) return;
      const label = document.createElement("span");
      label.className = "fbk-env-label";
      label.title = "Environment";
      label.textContent = "· " + (this.environmentAttr || ENV_NAME[this.environmentInt] || "unknown");
      sel.replaceWith(label);
    }
    // Patches #fbk-commit-style in place (same reasoning as updateEnvironmentSelectorVisibility) —
    // hidden entirely unless the current caller is authorized to change it (canEditSettings), so a
    // stakeholder who couldn't save the PATCH never sees a control that would just 403.
    renderCommitStyleControl() {
      const host = this.root && this.root.querySelector("#fbk-commit-style");
      if (!host) return;
      if (!this.canEditSettings) {
        host.classList.add("fbk-hidden");
        return;
      }
      host.classList.remove("fbk-hidden");
      host.innerHTML = TPL.commitStyleControl(this.commitStyle);
      const sel = this.root.querySelector("#fbk-commit-style-select");
      if (sel) sel.addEventListener("change", () => this.setCommitStyle(Number(sel.value)));
    }
    async setCommitStyle(value) {
      if (this.projectId == null || value !== 1 && value !== 2) return;
      const previous = this.commitStyle;
      this.commitStyle = value;
      try {
        const r = await this.api(`/api/admin/projects/${this.projectId}`, {
          method: "PATCH",
          body: JSON.stringify({ commitStyle: value })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        this.toast("Commit style updated");
      } catch (e) {
        this.commitStyle = previous;
        this.renderCommitStyleControl();
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Update failed", "error");
      }
    }
    // Keeps the "Comment on an element" button's tooltip showing the current shortcut after it's
    // changed from the user menu — same in-place-patch reasoning as updateProjectNameLabel().
    updateAddButtonTooltip() {
      const btn = this.root && this.root.querySelector("#fbk-add");
      if (!btn) return;
      const label = formatShortcut(this.shortcut);
      btn.setAttribute("title", `Comment on an element (${label})`);
      btn.setAttribute("aria-label", `Comment on an element, shortcut ${label}`);
    }
    // --- API ----------------------------------------------------------------
    async apiLogin(email, password) {
      return pfFetch(`${this.server}/api/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password })
      });
    }
    // Anonymous: active non-admin roles for the signup / re-apply dropdowns.
    async apiRoles() {
      const r = await pfFetch(`${this.server}/api/roles?project=${encodeURIComponent(this.project)}`, {
        headers: { "Content-Type": "application/json" }
      });
      const envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || "Could not load roles.");
      return envelope.data || [];
    }
    // Anonymous: self-signup AND re-apply (one endpoint). No token returned.
    async apiRegister(body) {
      return pfFetch(`${this.server}/api/auth/register`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    }
    api(path, opts = {}) {
      const headers = {
        "Content-Type": "application/json",
        // Declares which kind of client this is, so the server knows it may include the advisory
        // payload flags (R2-06). Not an auth signal — it is trivially forgeable and nothing
        // security-critical depends on it. Its job is that the documented AI paths, which never send
        // it, never receive the flag.
        "X-Pointer-Client": "widget",
        ...this.token ? { Authorization: `Bearer ${this.token}` } : {},
        ...opts.headers || {}
      };
      return pfFetch(`${this.server}${path}`, { ...opts, headers }).then((r) => {
        if (r.status === 401) {
          this.handle401();
          throw new Error("HTTP 401 Unauthorized");
        }
        return r;
      });
    }
    /**
     * Removes `?pointer_invite=` from the address bar and returns the token it held.
     *
     * SYNCHRONOUS AND FIRST, on every path including failure. Every comment captures
     * `window.location.href` and the route into its element capture, so a token still in the URL when
     * someone comments is persisted into the database and handed to anyone who can read that comment.
     * Stripping before any await — and before the redemption can fail — is what makes that
     * impossible. Other query params and the hash are preserved.
     */
    stripInviteTokenFromUrl() {
      try {
        const url = new URL(window.location.href);
        const token = url.searchParams.get("pointer_invite");
        if (!token) return null;
        url.searchParams.delete("pointer_invite");
        window.history.replaceState({}, "", url.toString());
        return token;
      } catch {
        return null;
      }
    }
    /**
     * Exchanges a stripped magic-link token for a normal session.
     *
     * Awaited in _boot() before the `if (this.token)` branch, because a first-time client has nothing
     * in storage — init() never runs for them, so the exchange has to complete before that decision.
     */
    async redeemInviteToken(token) {
      var _a2, _b;
      try {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 3e3);
        const res = await pfFetch(`${this.server}/api/auth/login-with-invite`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token }),
          signal: controller.signal
        }).finally(() => clearTimeout(timer));
        const envelope = await res.json();
        const data = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope;
        if (res.ok && (data == null ? void 0 : data.status) === "ok" && data.token) {
          this.saveAuth(data.token, (_b = data.user) != null ? _b : null);
          return true;
        }
      } catch {
      }
      return false;
    }
    async fetchComments() {
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments?environment=${this.environmentInt}`);
        if (r.status === 409 || r.status === 404) {
          this.disableSilently();
          return;
        }
        if (!r.ok) throw new Error("HTTP " + r.status);
        const envelope = await r.json();
        const items = envelope.data && envelope.data.items || [];
        this.hiddenPrivateCount = envelope.data && Number(envelope.data.hiddenPrivateCount) || 0;
        this.comments = items.map((c) => ({
          ...c,
          status: STATUS_STR[c.status] || "open"
        }));
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") {
          this.toast(`Could not reach ${getBrandName()} server`, "error", "Retry", () => {
            this.fetchComments().then(() => {
              this.renderSidebar();
              this.renderPins();
            });
          });
        }
        this.comments = [];
        this.hiddenPrivateCount = 0;
      }
    }
    // All comments returned belong to the project (no page_url filtering).
    pageComments() {
      return this.comments;
    }
    // --- Chrome (toolbar + sidebar shell) -----------------------------------
    renderChrome() {
      if (this._disabled) return;
      if (this._collapsed) {
        const n = (this.comments || []).filter((c) => c.status !== "archived" && c.status !== "applied").length;
        this.root.innerHTML = TPL.launcher(n, this.launcherPosition, pageIsRtl(), this.unreadNotifyCount);
        const launcher = this.root.querySelector("#fbk-launcher");
        if (launcher) launcher.addEventListener("click", () => this.showOverlay());
        return;
      }
      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "";
      const roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "";
      const avatarInitials = this.user ? escapeHtml(initials(this.user.displayName || this.user.email || "")) : "";
      const fixedEnvLabel = this.hasFixedEnvironment || !this.showEnvironmentSelector ? this.environmentAttr || ENV_NAME[this.environmentInt] || "staging" : null;
      this.root.innerHTML = TPL.chrome(displayName, roleLabel, fixedEnvLabel, this.projectName || this.project, formatShortcut(this.shortcut), this.unreadNotifyCount, avatarInitials, ariaKeyshortcuts(this.shortcut));
      const hideBtn = this.root.querySelector("#fbk-hide");
      if (hideBtn) hideBtn.addEventListener("click", () => this.hideOverlay());
      const userBtn = this.root.querySelector("#fbk-user");
      if (userBtn) userBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this.toggleUserMenu();
      });
      const updatesBtn = this.root.querySelector("#fbk-updates");
      if (updatesBtn) updatesBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this.toggleUpdatesMenu();
      });
      this.root.querySelector("#fbk-add").addEventListener("click", () => this.activateAddComment());
      this.root.querySelector("#fbk-toggle").addEventListener("click", () => {
        if (!this.token) {
          showLoginModal(this, () => {
            Promise.resolve(this.init()).then(() => this.toggleSidebar(true));
          });
          return;
        }
        this.toggleSidebar();
      });
      this.root.querySelector("#fbk-refresh").addEventListener("click", async () => {
        if (!this.token) {
          showLoginModal(this, () => this.init());
          return;
        }
        await this.fetchComments();
        this.renderSidebar();
        this.renderPins();
        this.toast("Refreshed");
      });
      this.root.querySelector("#fbk-close").addEventListener("click", () => this.toggleSidebar(false));
      const envSel = this.root.querySelector("#fbk-env");
      if (envSel) {
        envSel.value = (this.environmentAttr || ENV_NAME[this.environmentInt] || "staging").toLowerCase();
        envSel.addEventListener("change", () => this.setEnvironment(envSel.value));
      }
      const resetBtn = this.root.querySelector("#fbk-reset-pos");
      if (resetBtn) resetBtn.addEventListener("click", () => this.resetToolbarPos());
      this.restoreToolbarPos();
      this.enableToolbarDrag();
      this.updateMenuSide();
    }
    // Switch the active environment from the toolbar. Comments are environment-scoped, so this
    // re-queries the server and re-renders; the choice is remembered per project on this origin.
    setEnvironment(env) {
      const key = (env || "").toLowerCase();
      if (!ENV_MAP[key] || key === this.environmentAttr.toLowerCase()) return;
      this.environmentAttr = key;
      this.environmentInt = ENV_MAP[key];
      try {
        localStorage.setItem("pointer_env_" + this.project, key);
      } catch (e) {
      }
      if (!this.token) return;
      this.fetchComments().then(() => {
        this.renderSidebar();
        this.renderPins();
      });
    }
    // --- Draggable toolbar ---------------------------------------------------
    // The toolbar's default corner is bottom-right (CSS inset-block-end/inset-inline-end); let the
    // user drag it by its grip so it never covers the element they want to comment on. Dragging
    // sets a `translate(dx, dy)` offset (--fbk-toolbar-dx/dy) rather than switching the anchor
    // to absolute left/top, so the anchor itself never changes — only the offset from it does.
    // Position persists per tab.
    restoreToolbarPos() {
      const tb = this.root.querySelector(".fbk-toolbar");
      if (!tb) return;
      let saved = null;
      try {
        saved = JSON.parse(localStorage.getItem("pointer_toolbar_pos") || "null");
      } catch {
      }
      if (!saved || typeof saved.dx !== "number" || typeof saved.dy !== "number") return;
      const rect = tb.getBoundingClientRect();
      const maxDx = Math.max(0, window.innerWidth - rect.width) - rect.left;
      const maxDy = Math.max(0, window.innerHeight - rect.height) - rect.top;
      this._toolbarDx = Math.min(Math.max(saved.dx, -rect.left), maxDx);
      this._toolbarDy = Math.min(Math.max(saved.dy, -rect.top), maxDy);
      this.applyToolbarOffset(tb);
      tb.classList.add("is-moved");
    }
    applyToolbarOffset(tb) {
      tb.style.setProperty("--fbk-toolbar-dx", `${this._toolbarDx}px`);
      tb.style.setProperty("--fbk-toolbar-dy", `${this._toolbarDy}px`);
    }
    // Restore the toolbar to its default corner and forget the saved position.
    resetToolbarPos() {
      try {
        localStorage.removeItem("pointer_toolbar_pos");
      } catch {
      }
      this._toolbarDx = 0;
      this._toolbarDy = 0;
      const tb = this.root.querySelector(".fbk-toolbar");
      if (tb) {
        tb.style.removeProperty("--fbk-toolbar-dx");
        tb.style.removeProperty("--fbk-toolbar-dy");
        tb.classList.remove("is-moved");
      }
      this.updateMenuSide();
    }
    // The toolbar's own dropdowns (account/updates/pin-cluster) all open BELOW their trigger by
    // default — fine when the toolbar sits at the top of the screen, but the default corner is
    // bottom-right, and a dropdown opening downward from something already near the bottom edge
    // renders mostly or entirely off-screen (unreachable — this was the actual bug, not just a
    // dragged-toolbar edge case). Recorded once here as an attribute on the toolbar itself, rather
    // than remeasured by each menu, since all three anchor off the same toolbar and the answer is
    // the same for all of them; toggleUserMenu/toggleUpdatesMenu/toggleClusterMenu read it.
    updateMenuSide() {
      const tb = this.root.querySelector(".fbk-toolbar");
      if (!tb) return;
      const rect = tb.getBoundingClientRect();
      const spaceBelow = window.innerHeight - rect.bottom;
      tb.dataset.fbkMenuSide = spaceBelow < 340 ? "top" : "bottom";
    }
    // Vertically anchors a toolbar dropdown (account/updates/pin-cluster) above or below `rect`
    // per updateMenuSide()'s reading of the toolbar's own position — shared by all three so a
    // toolbar sitting near the bottom edge doesn't open a menu that renders off-screen below it.
    positionMenuVertically(menu, rect, gap = 6) {
      var _a2;
      const openUp = ((_a2 = this.root.querySelector(".fbk-toolbar")) == null ? void 0 : _a2.dataset.fbkMenuSide) === "top";
      if (openUp) {
        menu.style.top = "auto";
        menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - rect.top + gap))}px`;
      } else {
        menu.style.bottom = "auto";
        menu.style.top = `${Math.round(rect.bottom + gap)}px`;
      }
    }
    enableToolbarDrag() {
      const tb = this.root.querySelector(".fbk-toolbar");
      const grip = this.root.querySelector("#fbk-grip");
      if (!tb || !grip) return;
      let sx = 0, sy = 0, startDx = 0, startDy = 0, baseLeft = 0, baseTop = 0, baseWidth = 0, baseHeight = 0, dragging = false;
      const onMove = (e) => {
        if (!dragging) return;
        const maxDx = Math.max(0, window.innerWidth - baseWidth) - baseLeft;
        const maxDy = Math.max(0, window.innerHeight - baseHeight) - baseTop;
        this._toolbarDx = Math.min(Math.max(startDx + (e.clientX - sx), -baseLeft), maxDx);
        this._toolbarDy = Math.min(Math.max(startDy + (e.clientY - sy), -baseTop), maxDy);
        this.applyToolbarOffset(tb);
      };
      const onUp = (e) => {
        if (!dragging) return;
        dragging = false;
        tb.classList.remove("is-dragging");
        try {
          grip.releasePointerCapture(e.pointerId);
        } catch {
        }
        try {
          localStorage.setItem("pointer_toolbar_pos", JSON.stringify({ dx: this._toolbarDx, dy: this._toolbarDy }));
        } catch {
        }
        tb.classList.add("is-moved");
        this.updateMenuSide();
      };
      grip.addEventListener("pointerdown", (e) => {
        e.preventDefault();
        const rect = tb.getBoundingClientRect();
        baseLeft = rect.left - this._toolbarDx;
        baseTop = rect.top - this._toolbarDy;
        baseWidth = rect.width;
        baseHeight = rect.height;
        startDx = this._toolbarDx;
        startDy = this._toolbarDy;
        sx = e.clientX;
        sy = e.clientY;
        dragging = true;
        tb.classList.add("is-dragging");
        try {
          grip.setPointerCapture(e.pointerId);
        } catch {
        }
      });
      grip.addEventListener("pointermove", onMove);
      grip.addEventListener("pointerup", onUp);
      grip.addEventListener("pointercancel", onUp);
    }
    // --- User menu (identity + sign out) ------------------------------------
    toggleUserMenu() {
      var _a2;
      this.closeUpdatesMenu();
      this.closeClusterMenu();
      const host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-user-menu")) {
        this.closeUserMenu();
        return;
      }
      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "";
      const roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "";
      host.innerHTML = TPL.userMenu(
        displayName,
        roleLabel,
        formatShortcut(this.shortcut),
        this.authOwnedByHost,
        this.resolveTheme(),
        ((_a2 = this.user) == null ? void 0 : _a2.language) === "ar" ? "ar" : "en"
      );
      const menu = host.querySelector("#fbk-user-menu");
      const btn = this.root.querySelector("#fbk-user");
      if (btn) {
        btn.setAttribute("aria-expanded", "true");
        const r = btn.getBoundingClientRect();
        this.positionMenuVertically(menu, r);
        menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
      }
      const signoutBtn = host.querySelector("#fbk-signout");
      if (signoutBtn) signoutBtn.addEventListener("click", () => this.signOut());
      host.querySelector("#fbk-shortcut-edit").addEventListener("click", (e) => {
        e.stopPropagation();
        this.beginRecordingShortcut(host.querySelector("#fbk-shortcut-edit"));
      });
      host.querySelector("#fbk-shortcut-reset").addEventListener("click", async (e) => {
        e.stopPropagation();
        const editBtn = host.querySelector("#fbk-shortcut-edit");
        if (editBtn) editBtn.textContent = "Resetting…";
        const ok = await this.saveShortcutPreference(null);
        if (editBtn) editBtn.textContent = formatShortcut(this.shortcut);
        this.toast(ok ? "Shortcut reset to default" : "Failed to reset — try again", ok ? "" : "error");
      });
      const reopenUserMenu = () => {
        this.closeUserMenu();
        this.toggleUserMenu();
      };
      const wireThemeBtn = (id, mode) => {
        host.querySelector(id).addEventListener("click", (e) => {
          e.stopPropagation();
          if (this.resolveTheme() === mode) return;
          this.setThemeOverride(mode);
          reopenUserMenu();
        });
      };
      wireThemeBtn("#fbk-theme-light", "light");
      wireThemeBtn("#fbk-theme-dark", "dark");
      const wireLangBtn = (id, lang) => {
        host.querySelector(id).addEventListener("click", async (e) => {
          var _a3;
          e.stopPropagation();
          if ((((_a3 = this.user) == null ? void 0 : _a3.language) === "ar" ? "ar" : "en") === lang) return;
          const ok = await this.saveLanguagePreference(lang);
          if (ok) reopenUserMenu();
          else this.toast("Failed to save language — try again", "error");
        });
      };
      wireLangBtn("#fbk-lang-en", "en");
      wireLangBtn("#fbk-lang-ar", "ar");
      this._userMenuClose = (e) => {
        const path = e.composedPath();
        if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUserMenu();
      };
      setTimeout(() => {
        if (this._userMenuClose) document.addEventListener("click", this._userMenuClose, true);
      }, 0);
    }
    closeUserMenu() {
      var _a2;
      const host = this.root.querySelector("#fbk-menu-host");
      if (host && host.querySelector("#fbk-user-menu")) host.innerHTML = "";
      (_a2 = this.root.querySelector("#fbk-user")) == null ? void 0 : _a2.setAttribute("aria-expanded", "false");
      if (this._userMenuClose) {
        document.removeEventListener("click", this._userMenuClose, true);
        this._userMenuClose = null;
      }
      if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
    }
    // --- Updates menu (in-app notifications) --------------------------------
    async toggleUpdatesMenu() {
      const host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-notifications-menu")) {
        this.closeUpdatesMenu();
        return;
      }
      this.closeUserMenu();
      this.closeClusterMenu();
      const items = await this.apiNotifications();
      if (this.unreadNotifyCount > 0) {
        await this.apiMarkAllNotificationsRead();
        this.unreadNotifyCount = 0;
        this.updateNotifyBadges();
      }
      host.innerHTML = TPL.notificationsMenu(items);
      const menu = host.querySelector("#fbk-notifications-menu");
      if (!menu) return;
      const btn = this.root.querySelector("#fbk-updates");
      if (btn) {
        btn.setAttribute("aria-expanded", "true");
        const r = btn.getBoundingClientRect();
        this.positionMenuVertically(menu, r);
        menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 330, Math.round(r.left)))}px`;
      }
      menu.querySelectorAll(".fbk-notification-item").forEach((el) => {
        el.addEventListener("click", () => {
          const commentId = el.getAttribute("data-id");
          this.closeUpdatesMenu();
          if (commentId) {
            const c = this.comments.find((x) => String(x.id) === String(commentId));
            if (c && this.statusFilter !== "all" && this.statusFilter !== c.status) {
              this.statusFilter = "all";
            }
            this.toggleSidebar(true);
            this.renderSidebar();
            setTimeout(() => {
              const card = this.root.querySelector(`.fbk-card[data-id="${commentId}"]`);
              if (card) {
                card.scrollIntoView({ behavior: "smooth", block: "center" });
                card.classList.add("highlight");
                setTimeout(() => card.classList.remove("highlight"), 2e3);
              }
            }, 100);
          }
        });
      });
      this._updatesMenuClose = (e) => {
        const path = e.composedPath();
        if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUpdatesMenu();
      };
      setTimeout(() => {
        if (this._updatesMenuClose) document.addEventListener("click", this._updatesMenuClose, true);
      }, 0);
    }
    closeUpdatesMenu() {
      var _a2;
      const host = this.root.querySelector("#fbk-menu-host");
      if (host && host.querySelector("#fbk-notifications-menu")) host.innerHTML = "";
      (_a2 = this.root.querySelector("#fbk-updates")) == null ? void 0 : _a2.setAttribute("aria-expanded", "false");
      if (this._updatesMenuClose) {
        document.removeEventListener("click", this._updatesMenuClose, true);
        this._updatesMenuClose = null;
      }
    }
    // --- Notification Polling & Verification ---------------------------------
    startNotificationPolling() {
      var _a2, _b;
      this.stopNotificationPolling();
      this.fetchUnreadNotifyCount();
      const pollInterval = (_b = (_a2 = window.__POINTER_CONFIG__) == null ? void 0 : _a2.notifyPollMs) != null ? _b : 6e4;
      this._notifyPollTimer = window.setInterval(() => {
        if (document.visibilityState === "visible") {
          this.fetchUnreadNotifyCount();
        }
      }, pollInterval);
      this._onVisibilityChange = () => {
        if (document.visibilityState === "visible") {
          this.fetchUnreadNotifyCount();
        }
      };
      document.addEventListener("visibilitychange", this._onVisibilityChange);
    }
    stopNotificationPolling() {
      if (this._notifyPollTimer !== null) {
        window.clearInterval(this._notifyPollTimer);
        this._notifyPollTimer = null;
      }
      if (this._onVisibilityChange) {
        document.removeEventListener("visibilitychange", this._onVisibilityChange);
        this._onVisibilityChange = null;
      }
    }
    async fetchUnreadNotifyCount() {
      var _a2;
      if (!this.token) return;
      try {
        const r = await this.api("/api/me/notifications/unread-count");
        if (!r.ok) return;
        const envelope = await r.json();
        const count = typeof ((_a2 = envelope == null ? void 0 : envelope.data) == null ? void 0 : _a2.count) === "number" ? envelope.data.count : typeof (envelope == null ? void 0 : envelope.count) === "number" ? envelope.count : 0;
        this.unreadNotifyCount = count;
        this.updateNotifyBadges();
      } catch {
      }
    }
    updateNotifyBadges() {
      if (this._collapsed) {
        this.renderChrome();
        return;
      }
      const dot = this.root.querySelector("#fbk-notify-count");
      if (dot) dot.classList.toggle("fbk-hidden", this.unreadNotifyCount <= 0);
      const updatesBtn = this.root.querySelector("#fbk-updates");
      if (updatesBtn) {
        const n = this.unreadNotifyCount;
        updatesBtn.setAttribute("aria-label", `Updates${n > 0 ? `, ${n > 99 ? "99+" : n} unread` : ""}`);
      }
    }
    async apiNotifications(unread = false) {
      var _a2;
      try {
        const r = await this.api(`/api/me/notifications${unread ? "?unread=true" : ""}`);
        if (!r.ok) return [];
        const envelope = await r.json();
        const items = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope;
        return Array.isArray(items) ? items : [];
      } catch {
        return [];
      }
    }
    async apiMarkAllNotificationsRead() {
      try {
        await this.api("/api/me/notifications/read-all", { method: "POST" });
      } catch {
      }
    }
    async apiVerify(id, ok, note) {
      var _a2, _b;
      try {
        const r = await this.api(`/api/comments/${id}/verify`, {
          method: "POST",
          body: JSON.stringify({ ok, note: note || null })
        });
        if (!r.ok) {
          let errMessage = "Failed to verify comment";
          try {
            const err = await r.json();
            if (err == null ? void 0 : err.message) errMessage = err.message;
          } catch {
          }
          this.toast(errMessage, "error");
          return false;
        }
        const envelope = await r.json();
        const updated = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope;
        const idx = this.comments.findIndex((c) => String(c.id) === String(id));
        if (idx !== -1 && updated) {
          const normalizedComment = {
            ...this.comments[idx],
            ...updated,
            status: typeof updated.status === "number" ? STATUS_STR[updated.status] || "open" : updated.status || "open",
            verifiedAt: (_b = updated.verifiedAt) != null ? _b : null
          };
          this.comments[idx] = normalizedComment;
        } else {
          await this.fetchComments();
        }
        this.renderSidebar();
        this.renderPins();
        this.toast(ok ? "Comment verified" : "Comment re-opened");
        return true;
      } catch {
        this.toast("Failed to verify comment", "error");
        return false;
      }
    }
    // Clear the session and reset the widget to its logged-out (deferred-login) state.
    signOut() {
      this.closeUserMenu();
      this.closeUpdatesMenu();
      this.stopNotificationPolling();
      this.unreadNotifyCount = 0;
      if (this.picking) this.stopPicking();
      this.clearAuth();
      this.comments = [];
      this.hiddenPrivateCount = 0;
      this.sidebarOpen = false;
      this.mineOnly = false;
      this.authorFilter = null;
      this.statusFilter = "all";
      this.renderChrome();
      this.renderSidebar();
      this.renderPins();
      this.toast("Signed out");
    }
    // Collapse the overlay to the floating launcher (remembered for this tab session).
    hideOverlay() {
      if (this.picking) this.stopPicking();
      this.sidebarOpen = false;
      this._collapsed = true;
      try {
        sessionStorage.removeItem("pointer_visible");
      } catch (e) {
      }
      this.renderChrome();
      this.toast(`${getBrandName()} hidden — click the button to reopen`);
    }
    // Restore the full overlay from the launcher; remembered for this tab session.
    showOverlay() {
      this._collapsed = false;
      try {
        sessionStorage.setItem("pointer_visible", "1");
      } catch (e) {
      }
      this.renderChrome();
      if (this.token) {
        this.fetchComments().then(() => {
          this.renderSidebar();
          this.renderPins();
        });
      }
    }
    toggleSidebar(force) {
      var _a2;
      this.sidebarOpen = force === void 0 ? !this.sidebarOpen : force;
      this.root.querySelector("#fbk-sidebar").classList.toggle("open", this.sidebarOpen);
      (_a2 = this.root.querySelector("#fbk-toggle")) == null ? void 0 : _a2.setAttribute("aria-expanded", String(this.sidebarOpen));
      if (this.sidebarOpen) {
        this.fetchComments().then(() => {
          this.renderSidebar();
          this.renderPins();
        });
      }
    }
    // --- Staying clickable under host-app modals ------------------------------
    // The rects our own UI currently occupies on screen — every top-level container that can be
    // visible at once. Used to punch matching holes in any modal backdrop so those areas stay
    // clickable. Elements not currently rendered/visible in this.root simply aren't found and are
    // skipped; no need to check display/visibility explicitly.
    ownUiRects() {
      var _a2;
      const selectors = [".fbk-launcher", ".fbk-toolbar", ".fbk-sidebar", ".fbk-modal-overlay", ".fbk-popover", ".fbk-menu"];
      const rects = [];
      for (const sel of selectors) {
        const el = (_a2 = this.root) == null ? void 0 : _a2.querySelector(sel);
        if (!el) continue;
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.height > 0) rects.push(r);
      }
      return rects;
    }
    // Clips a hole out of every full-viewport modal backdrop AND dialog/panel content pane currently
    // open (BACKDROP_SELECTOR, DIALOG_CONTENT_SELECTOR), exactly where our own UI sits — see the
    // connectedCallback comment for why the backdrop needs this: no z-index, however high, makes an
    // element clickable through it, because Chromium's native hit-testing can award the click to the
    // backdrop regardless of paint order. The content pane needs the SAME treatment for a separate
    // reason, confirmed empirically against a real Angular Material dialog: its content pane
    // out-ranks our max-z-index shadow content in actual paint order despite every ancestor on both
    // sides having no stacking-context-creating property that would explain it per spec — whatever
    // the underlying browser mechanism, clip-path fixes it identically. Everywhere else on the page,
    // the backdrop/dialog keeps blocking/rendering normally — only our own footprint becomes
    // click-through.
    punchBackdropHoles() {
      const targets = document.querySelectorAll(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
      if (targets.length === 0) return;
      const rects = this.ownUiRects();
      targets.forEach((t) => {
        const clipPath = rects.length === 0 ? "" : buildClipPathWithHoles(rects, t.getBoundingClientRect());
        if (t.style.clipPath !== clipPath) t.style.clipPath = clipPath;
      });
    }
    // --- Element picking -----------------------------------------------------
    togglePicking() {
      this.picking ? this.stopPicking() : this.startPicking();
    }
    startPicking() {
      var _a2;
      this.picking = true;
      (_a2 = this.root.querySelector("#fbk-pins-layer")) == null ? void 0 : _a2.classList.add("picking");
      const tb = this.root.querySelector(".fbk-toolbar");
      tb == null ? void 0 : tb.classList.add("is-dim");
      const addBtn = this.root.querySelector("#fbk-add");
      addBtn.setAttribute("aria-pressed", "true");
      addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.close}</span>`;
      addBtn.title = "Cancel";
      addBtn.setAttribute("aria-label", "Cancel");
      document.addEventListener("mousemove", this._onHover, true);
      document.addEventListener("click", this._onPick, true);
      document.addEventListener("keydown", this._onPickKey, true);
      this.toast("Click any element to comment on it — or press Esc to cancel");
    }
    stopPicking() {
      var _a2, _b, _c, _d;
      this.picking = false;
      (_b = (_a2 = this.root) == null ? void 0 : _a2.querySelector("#fbk-pins-layer")) == null ? void 0 : _b.classList.remove("picking");
      (_d = (_c = this.root) == null ? void 0 : _c.querySelector(".fbk-toolbar")) == null ? void 0 : _d.classList.remove("is-dim");
      const addBtn = this.root && this.root.querySelector("#fbk-add");
      if (addBtn) {
        addBtn.setAttribute("aria-pressed", "false");
        addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span>`;
        addBtn.title = "Comment on an element";
        addBtn.setAttribute("aria-label", "Comment on an element");
      }
      document.removeEventListener("mousemove", this._onHover, true);
      document.removeEventListener("click", this._onPick, true);
      document.removeEventListener("keydown", this._onPickKey, true);
      this.clearHover();
    }
    // Esc cancels element-picking (deselects the pointer) without placing a comment.
    onPickKey(e) {
      if (e.key !== "Escape" && e.key !== "Esc") return;
      e.preventDefault();
      e.stopPropagation();
      this.stopPicking();
      this.toast("Cancelled");
    }
    clearHover() {
      if (this.hovered) {
        this.hovered.classList.remove(HL_CLASS);
        this.hovered = null;
      }
    }
    isOwnElement(el) {
      return el === this || !!el && el.tagName === "POINTER-FEEDBACK";
    }
    // Resolves the real element a pointer event is over, seeing through any full-viewport modal
    // backdrop the host app has open (BACKDROP_SELECTOR) — a dialog opened WHILE picking is active
    // would otherwise swallow the hover/click itself, since it deliberately covers the whole
    // viewport to catch clicks that dismiss it.
    //
    // e.target is the primary source of truth (NOT document.elementsFromPoint): our own UI lives in
    // a Shadow DOM behind a host element with zero intrinsic size, so point-based hit-testing can
    // never land on the host itself — only the browser's own composed-event retargeting (which sets
    // e.target to the host for any event landing on our shadow content) reliably identifies "this
    // was our own UI". elementsFromPoint is used only as a fallback, and only once e.target has
    // already proven to be a recognized backdrop — never to re-derive "is this our own UI".
    resolveHitTarget(e) {
      const target = e.target;
      if (!target || this.isOwnElement(target)) return null;
      if (!target.matches(BACKDROP_SELECTOR)) return target;
      for (const el of document.elementsFromPoint(e.clientX, e.clientY)) {
        if (this.isOwnElement(el)) continue;
        if (el.matches(BACKDROP_SELECTOR)) continue;
        return el;
      }
      return null;
    }
    onHover(e) {
      const el = this.resolveHitTarget(e);
      if (!el) return;
      if (el === this.hovered) return;
      this.clearHover();
      this.hovered = el;
      el.classList.add(HL_CLASS);
    }
    onPick(e) {
      const el = this.resolveHitTarget(e);
      if (!el) return;
      e.preventDefault();
      e.stopPropagation();
      const x = e.clientX, y = e.clientY;
      this.clearHover();
      this.stopPicking();
      this._pendingShotPromise = null;
      this.openCommentPopover(x, y, el);
    }
    // Kick off a best-effort screenshot capture for `el` (resolves null on failure).
    // Idempotent per popover: reuses an in-flight capture if one already started.
    beginScreenshotCapture(el) {
      if (!this.screenshotEnabled) return;
      if (this._pendingShotPromise) return;
      this._pendingShotPromise = captureScreenshot(el).catch((err) => {
        console.warn("[pointer-feedback] screenshot capture failed", err);
        return null;
      });
    }
    // Upload a screenshot Blob to /api/uploads via multipart/form-data. Returns the
    // absolute URL on success, or null on failure. Deliberately NOT using api() —
    // for FormData we must let the browser set the multipart boundary itself.
    async uploadToServer(blob) {
      try {
        const ext = blob.type === "image/jpeg" ? "jpg" : "webp";
        const fd = new FormData();
        fd.append("file", blob, `screenshot.${ext}`);
        fd.append("project", this.project);
        const r = await pfFetch(`${this.server}/api/uploads`, {
          method: "POST",
          headers: { ...this.token ? { Authorization: `Bearer ${this.token}` } : {} },
          body: fd
        });
        if (r.status === 401) {
          this.handle401();
          return null;
        }
        if (!r.ok) throw new Error("HTTP " + r.status);
        const envelope = await r.json();
        if (!envelope || !envelope.isSuccess || !envelope.data || !envelope.data.url) {
          throw new Error("upload response missing data.url");
        }
        return envelope.data.url;
      } catch (err) {
        console.warn("[pointer-feedback] screenshot upload failed", err);
        return null;
      }
    }
    // --- Comment popover -----------------------------------------------------
    // (named openCommentPopover, not showPopover, to avoid clashing with the
    //  built-in HTMLElement.showPopover() from the Popover API.)
    openCommentPopover(x, y, el) {
      const meta = captureMetadata(el, this.sourceAttr, { captureText: this.captureTextContent });
      const host = this.root.querySelector("#fbk-popover-host");
      host.innerHTML = TPL.popover(meta, x, y, this.screenshotEnabled, this.predefinedActions, this.pageContextCaptureEnabled);
      applyDataPosition(host, ".fbk-popover");
      const popoverEl = host.querySelector(".fbk-popover");
      if (popoverEl) {
        const rect = popoverEl.getBoundingClientRect();
        const margin = 8;
        const left = Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin));
        const top = Math.max(margin, Math.min(y, window.innerHeight - rect.height - margin));
        popoverEl.style.left = `${Math.round(left)}px`;
        popoverEl.style.top = `${Math.round(top)}px`;
      }
      const ta = host.querySelector("#fbk-comment-text");
      ta.focus();
      let isPrivateComment = false;
      const privateToggle = host.querySelector("#fbk-comment-private");
      if (privateToggle) privateToggle.addEventListener("click", () => {
        isPrivateComment = !isPrivateComment;
        privateToggle.classList.toggle("is-active", isPrivateComment);
        privateToggle.setAttribute("aria-pressed", String(isPrivateComment));
        privateToggle.title = isPrivateComment ? "Private — only me" : "Keep private — only me";
        privateToggle.innerHTML = isPrivateComment ? ICON.lock : ICON.unlock;
      });
      const shotToggle = host.querySelector("#fbk-comment-shot");
      if (shotToggle) shotToggle.addEventListener("change", () => {
        if (shotToggle.checked) this.beginScreenshotCapture(el);
      });
      const cancelPopover = () => {
        host.innerHTML = "";
        this._pendingShotPromise = null;
      };
      host.querySelector("#fbk-cancel").addEventListener("click", cancelPopover);
      host.addEventListener("keydown", (e) => {
        if (e.key !== "Escape") return;
        e.preventDefault();
        e.stopPropagation();
        cancelPopover();
      });
      host.querySelector("#fbk-submit").addEventListener("click", async () => {
        const text = ta.value.trim();
        if (!text) return this.toast("Comment cannot be empty", "error");
        const isPrivate = isPrivateComment;
        const shotEl = host.querySelector("#fbk-comment-shot");
        const attachShot = !!(shotEl && shotEl.checked);
        const bugEl = host.querySelector("#fbk-comment-bug");
        const isBugReport = !!(bugEl && bugEl.checked);
        const shotPromise = this._pendingShotPromise;
        this._pendingShotPromise = null;
        const predefinedActionIds = Array.from(
          host.querySelectorAll(".fbk-action-opt:checked")
        ).map((el2) => Number(el2.value));
        const submitBtn = host.querySelector("#fbk-submit");
        submitBtn.disabled = true;
        submitBtn.textContent = "Saving…";
        const saved = await this.createComment({ ...meta, text, isPrivate, attachShot, shotPromise, predefinedActionIds, isBugReport });
        if (saved) host.innerHTML = "";
        else {
          submitBtn.disabled = false;
          submitBtn.textContent = "Add";
        }
      });
    }
    // Returns true on success (popover should close), false on failure (popover stays open).
    async createComment(data) {
      var _a2, _b;
      const vw = window.innerWidth;
      const vh = window.innerHeight;
      const deviceType = vw < 768 ? "mobile" : vw < 1024 ? "tablet" : "desktop";
      const element = {
        selector: data.selector,
        snapshot: data.snapshot,
        classes: data.classes,
        computedStyles: data.computedStyles,
        appliedCssRules: data.appliedCssRules,
        sourcePath: data.sourcePath,
        parentInfo: data.parentInfo,
        // Page the comment was left on — gives the apply step the route/page,
        // essential for multi-page apps (window.location reflects the current page).
        pageUrl: window.location.href,
        // Active route relative to the origin: path + query params (+ hash, so
        // hash-routed SPAs are covered too).
        route: window.location.pathname + window.location.search + window.location.hash,
        pageTitle: document.documentElement.hasAttribute("data-snapshot-mask") || !this.captureTextContent ? "•••" : document.title,
        viewportWidth: vw,
        viewportHeight: vh,
        deviceType,
        devicePixelRatio: window.devicePixelRatio || 1,
        userAgent: navigator.userAgent
      };
      if (data.attachShot && data.shotPromise) {
        const blob = await Promise.resolve(data.shotPromise).catch(() => null);
        if (blob) {
          const url = await this.uploadToServer(blob);
          if (url) element.screenshotUrl = url;
          else this.toast("Screenshot upload failed — saving without it", "error");
        }
      }
      const bodyObj = {
        body: data.text,
        environment: this.environmentInt,
        isPrivate: !!data.isPrivate,
        element,
        isBugReport: !!data.isBugReport
      };
      if (data.predefinedActionIds && data.predefinedActionIds.length) bodyObj.predefinedActionIds = data.predefinedActionIds;
      if (data.isBugReport) {
        const pageContext = getPageContextPayload();
        if (pageContext) bodyObj.pageContext = pageContext;
      }
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments`, {
          method: "POST",
          body: JSON.stringify({ ...bodyObj, projectKey: this.project })
        });
        if (r.status === 409 || r.status === 404) {
          this.disableSilently();
          return true;
        }
        if (!r.ok) {
          const errEnv = await r.json().catch(() => null);
          const msg = errEnv && errEnv.message || "";
          if (data.predefinedActionIds && data.predefinedActionIds.length && msg.toLowerCase().includes("action")) {
            await this.fetchPredefinedActions();
            this.toast("That action is no longer available — please choose another and try again.", "error");
            return false;
          }
          if (r.status === 403) {
            this.toast("Comments are not allowed from this address", "error");
            return false;
          }
          if (r.status === 429) {
            const retryAfter = Number((_b = (_a2 = r.headers) == null ? void 0 : _a2.get) == null ? void 0 : _b.call(_a2, "retry-after"));
            this.toast(
              retryAfter > 0 ? `Too many comments — try again in ${retryAfter} second${retryAfter === 1 ? "" : "s"}.` : "Too many comments — please wait a moment and try again.",
              "error"
            );
            return false;
          }
          throw new Error("HTTP " + r.status);
        }
        const envelope = await r.json();
        const comment = envelope.data;
        if (comment) {
          this.comments.push({ ...comment, status: STATUS_STR[comment.status] || "open" });
        }
        const newId = comment ? String(comment.id) : "";
        this._newPinId = newId || null;
        this.renderSidebar();
        this.renderPins();
        if (newId) setTimeout(() => {
          if (String(this._newPinId) === newId) this._newPinId = null;
        }, 3e3);
        this.toast("Comment added", "success", newId ? "Undo" : void 0, newId ? () => this.deleteComment(newId) : void 0);
        return true;
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") {
          this.toast("Failed to save comment", "error");
        }
        return false;
      }
    }
    // --- Mutations -----------------------------------------------------------
    async addReply(id, text) {
      try {
        const r = await this.api(`/api/comments/${id}/replies`, {
          method: "POST",
          body: JSON.stringify({ body: text })
        });
        if (!r.ok) throw new Error();
        await this.fetchComments();
        this.renderSidebar();
        this.renderPins();
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Failed to reply", "error");
      }
    }
    async toggleApply(comment) {
      const nextStr = comment.status === "pending-apply" ? "open" : "pending-apply";
      const nextInt = STATUS_INT[nextStr];
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: nextInt })
        });
        if (!r.ok) throw new Error();
        comment.status = nextStr;
        this.renderSidebar();
        this.renderPins();
        this.toast(nextStr === "pending-apply" ? "Marked for apply" : "Unmarked");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Update failed", "error");
      }
    }
    // Generic status change (Re-open → open, Archive → archived).
    async setStatus(comment, nextStr, toastMsg) {
      const nextInt = STATUS_INT[nextStr];
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: nextInt })
        });
        if (!r.ok) throw new Error();
        comment.status = nextStr;
        this.renderSidebar();
        this.renderPins();
        this.toast(toastMsg || "Updated");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Update failed", "error");
      }
    }
    // Toggle a comment's privacy — author-only (enforced server-side too).
    async setVisibility(comment, isPrivate) {
      try {
        const r = await this.api(`/api/comments/${comment.id}/visibility`, {
          method: "PATCH",
          body: JSON.stringify({ isPrivate })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        comment.isPrivate = isPrivate;
        this.renderSidebar();
        this.renderPins();
        this.toast(isPrivate ? "Marked private" : "Made public");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Update failed", "error");
      }
    }
    async markCompleted(comment) {
      const label = this.user ? this.user.displayName || this.user.email : null;
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: STATUS_INT["applied"], appliedByLabel: label })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        comment.status = "applied";
        if (label) comment.appliedByLabel = label;
        this.renderSidebar();
        this.renderPins();
        this.toast("Marked completed");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast("Update failed", "error");
      }
    }
    /**
     * Two-step delete: replaces the whole end-cluster it lives in (the visibility toggle sits
     * alongside it) with a "Delete this comment? ✓ ✕" confirmation, so nothing else in that
     * cluster can be mis-clicked while confirming. Confirms on ✓, cancels on ✕, and
     * auto-dismisses after a few seconds. Other buttons are hidden (not removed), so their
     * existing click listeners survive once restored.
     */
    confirmDelete(btn) {
      const id = btn.dataset.id;
      const row = btn.closest(".fbk-actions-end");
      if (!id || !row || row.querySelector(".fbk-confirm")) return;
      const others = Array.from(row.children);
      others.forEach((el) => {
        el.style.display = "none";
      });
      const wrap = document.createElement("div");
      wrap.className = "fbk-confirm fbk-confirm-row";
      wrap.innerHTML = `<span class="fbk-confirm-q">Delete this comment?</span><span class="fbk-confirm-btns"><button type="button" class="fbk-mini danger fbk-icon" data-c="yes" title="Confirm delete" aria-label="Confirm delete">${ICON.checkPlain}</button><button type="button" class="fbk-mini fbk-icon" data-c="no" title="Cancel" aria-label="Cancel">&#x2715;</button></span>`;
      row.appendChild(wrap);
      let closed = false;
      const close = () => {
        if (closed) return;
        closed = true;
        clearTimeout(timer);
        wrap.remove();
        others.forEach((el) => {
          el.style.display = "";
        });
      };
      const timer = setTimeout(close, 4e3);
      wrap.querySelector('[data-c="yes"]').addEventListener("click", (e) => {
        e.stopPropagation();
        close();
        this.deleteComment(id);
      });
      wrap.querySelector('[data-c="no"]').addEventListener("click", (e) => {
        e.stopPropagation();
        close();
      });
    }
    async deleteComment(id) {
      try {
        const r = await this.api(`/api/comments/${id}`, { method: "DELETE" });
        if (!r.ok) {
          const body = await r.json().catch(() => null);
          throw new Error(body && body.message || "HTTP " + r.status);
        }
        this.comments = this.comments.filter((c) => String(c.id) !== String(id));
        this.renderSidebar();
        this.renderPins();
        this.toast("Deleted");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast(e.message || "Delete failed", "error");
      }
    }
    // Inline edit (own comments only): swap the body text for a textarea + controls.
    startEdit(id) {
      const card = this.root && this.root.querySelector(`.fbk-card[data-id="${id}"]`);
      if (!card || card.querySelector(".fbk-edit")) return;
      const comment = (this.comments || []).find((x) => String(x.id) === String(id));
      if (!comment) return;
      const textEl = card.querySelector(".fbk-text");
      if (!textEl) return;
      const hasShot = !!(comment.element && comment.element.screenshotUrl);
      const editor = document.createElement("div");
      editor.className = "fbk-edit";
      editor.style.margin = "6px 0";
      editor.innerHTML = `
        <textarea class="fbk-textarea fbk-edit-body">${escapeHtml(comment.body || "")}</textarea>
        ${hasShot ? `<label class="fbk-edit-option"><input type="checkbox" class="fbk-edit-rmshot" /> Remove image</label>` : ""}
        <div class="fbk-reply-row">
          <button class="fbk-btn primary fbk-btn-fill fbk-edit-save">Save</button>
          <button class="fbk-mini fbk-edit-cancel">Cancel</button>
        </div>`;
      textEl.style.display = "none";
      textEl.insertAdjacentElement("afterend", editor);
      const ta = editor.querySelector(".fbk-edit-body");
      ta.focus();
      editor.querySelector(".fbk-edit-cancel").addEventListener("click", () => {
        editor.remove();
        textEl.style.display = "";
      });
      editor.querySelector(".fbk-edit-save").addEventListener("click", () => {
        const body = ta.value.trim();
        if (!body) {
          this.toast("Comment cannot be empty", "error");
          return;
        }
        const rm = editor.querySelector(".fbk-edit-rmshot");
        const removeScreenshot = !!(rm && rm.checked);
        this.saveEdit(id, body, removeScreenshot);
      });
    }
    async saveEdit(id, body, removeScreenshot) {
      try {
        const r = await this.api(`/api/comments/${id}`, {
          method: "PUT",
          body: JSON.stringify({ body, removeScreenshot })
        });
        if (!r.ok) {
          const b = await r.json().catch(() => null);
          throw new Error(b && b.message || "HTTP " + r.status);
        }
        await this.fetchComments();
        this.renderSidebar();
        this.renderPins();
        this.toast("Comment updated", "success");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") this.toast(e.message || "Failed to update comment", "error");
      }
    }
    // True when comment `c` was authored by the current logged-in user.
    /**
     * Completes `this.user` with the fields the card template needs (`id`, `isAdmin`, `isQuickAccess`)
     * when the session came from a host that only supplied a display name. One `GET /api/auth/me`,
     * in memory only for host-owned sessions — the host re-injects its own user object on every
     * activation, and persisting a wider profile than it chose to share would defeat that choice.
     */
    async hydrateIdentity() {
      if (!this.token || this.user && this.user.id) return;
      try {
        const r = await this.api("/api/auth/me");
        if (!r.ok) return;
        const env = await r.json().catch(() => null);
        const me = env && env.data ? env.data : env;
        if (!me || !me.id) return;
        this.user = {
          ...this.user || {},
          id: String(me.id),
          displayName: this.user && this.user.displayName || me.displayName,
          isAdmin: !!me.isAdmin,
          isQuickAccess: !!me.isQuickAccess,
          language: this.user && this.user.language || me.language
        };
        if (!this.authOwnedByHost) {
          try {
            localStorage.setItem("pointer_user", JSON.stringify(this.user));
          } catch {
          }
        }
      } catch {
      }
    }
    isMine(c) {
      const uid = this.user && this.user.id;
      if (!uid) return false;
      return String(c.authorId || "").toLowerCase() === String(uid).toLowerCase();
    }
    // Distinct comment authors in the current project list.
    distinctAuthors(comments) {
      const seen = /* @__PURE__ */ new Set();
      const out = [];
      for (const c of comments) {
        const id = String(c.authorId || "");
        if (id && !seen.has(id)) {
          seen.add(id);
          out.push({ id, name: c.authorName || id });
        }
      }
      return out;
    }
    // Apply the "who" filters in priority order: Mine wins; else a chosen author.
    scopeByWho(comments) {
      if (this.mineOnly) return comments.filter((c) => this.isMine(c));
      if (this.authorFilter) return comments.filter((c) => String(c.authorId || "") === this.authorFilter);
      return comments;
    }
    // --- Sidebar render ------------------------------------------------------
    renderSidebar() {
      var _a2, _b;
      const all = this.pageComments();
      const canMine = !!(this.user && this.user.id);
      if (!canMine) this.mineOnly = false;
      const authors = this.distinctAuthors(all);
      if (this.authorFilter && !authors.some((a) => a.id === this.authorFilter)) this.authorFilter = null;
      const scoped = this.scopeByWho(all);
      const counts = {
        // "All" means active (non-archived, non-completed); those move out to their own chips.
        all: scoped.filter((c) => c.status !== "archived" && c.status !== "applied").length,
        open: scoped.filter((c) => c.status === "open").length,
        "pending-apply": scoped.filter((c) => c.status === "pending-apply").length,
        applied: scoped.filter((c) => c.status === "applied").length,
        archived: scoped.filter((c) => c.status === "archived").length
      };
      const countEl = this.root.querySelector("#fbk-count");
      if (countEl) countEl.textContent = String(all.filter((c) => c.status !== "archived" && c.status !== "applied").length);
      const filtersEl = this.root.querySelector("#fbk-filters");
      if (filtersEl) {
        const activeFilters = catalogToFilters();
        filtersEl.innerHTML = TPL.statusFilterSelect(activeFilters, this.statusFilter, counts) + (authors.length > 1 && !this.mineOnly ? TPL.authorFilter(authors, this.authorFilter || "") : "") + (canMine ? TPL.mineToggle(this.mineOnly) : "");
        const statusSel = filtersEl.querySelector("#fbk-status-filter");
        if (statusSel) statusSel.addEventListener("change", () => {
          this.statusFilter = statusSel.value;
          this.renderSidebar();
        });
        const mineBtn = filtersEl.querySelector("#fbk-mine-toggle");
        if (mineBtn) mineBtn.addEventListener("click", () => {
          this.mineOnly = !this.mineOnly;
          this.renderSidebar();
          this.renderPins();
        });
        const authorSel = filtersEl.querySelector("#fbk-author-filter");
        if (authorSel) authorSel.addEventListener("change", () => {
          this.authorFilter = authorSel.value || null;
          this.renderSidebar();
          this.renderPins();
        });
      }
      const list = this.root.querySelector("#fbk-list");
      if (!list) return;
      const awaitingMyVerification = (c) => c.status === "applied" && !c.verifiedAt && this.isMine(c);
      const shown = this.statusFilter === "all" ? scoped.filter((c) => c.status !== "archived" && (c.status !== "applied" || awaitingMyVerification(c))) : scoped.filter((c) => c.status === this.statusFilter);
      if (!scoped.length) {
        list.innerHTML = TPL.empty(this.mineOnly ? "You haven't left any comments yet." : "No comments on this project yet.<br/>Click the inspect icon, then click an element.");
        return;
      }
      if (!shown.length) {
        const activeFilters = catalogToFilters();
        const filterLabel = ((_a2 = activeFilters.find((f) => f.key === this.statusFilter)) != null ? _a2 : { label: this.statusFilter }).label;
        list.innerHTML = TPL.empty(`No ${filterLabel.toLowerCase()} comments${this.mineOnly ? " of yours" : ""}.`);
        return;
      }
      const isQuickAccess = !!((_b = this.user) == null ? void 0 : _b.isQuickAccess);
      list.innerHTML = shown.map((c, i) => {
        c._mine = this.isMine(c);
        c._canVerify = c._mine || !!(this.user && this.user.isAdmin);
        return TPL.card(c, i, isQuickAccess);
      }).join("");
      list.querySelectorAll('[data-act="apply"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c && c.status !== "applied") this.toggleApply(c);
      }));
      list.querySelectorAll('[data-act="complete"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c && c.status !== "applied") this.markCompleted(c);
      }));
      list.querySelectorAll('[data-act="delete"]').forEach((b) => b.addEventListener("click", () => this.confirmDelete(b)));
      list.querySelectorAll('[data-act="edit"]').forEach((b) => b.addEventListener("click", () => this.startEdit(b.dataset.id)));
      list.querySelectorAll('[data-act="visibility"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c) this.setVisibility(c, b.dataset.private === "true");
      }));
      list.querySelectorAll('[data-act="reopen"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c) this.setStatus(c, "open", "Re-opened");
      }));
      list.querySelectorAll('[data-act="archive"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c) this.setStatus(c, "archived", "Archived");
      }));
      list.querySelectorAll(".fbk-reply-input").forEach((inp) => inp.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && inp.value.trim()) {
          this.addReply(inp.dataset.id, inp.value.trim());
          inp.value = "";
        }
      }));
      list.querySelectorAll('[data-act="verify-ok"]').forEach((b) => b.addEventListener("click", () => {
        const id = b.dataset.id;
        if (id) this.apiVerify(id, true);
      }));
      list.querySelectorAll('[data-act="verify-reject"]').forEach((b) => b.addEventListener("click", () => {
        const id = b.dataset.id;
        if (!id) return;
        const box = list.querySelector(`#fbk-verify-box-${id}`);
        if (box) {
          const show = box.classList.contains("fbk-hidden");
          box.classList.toggle("fbk-hidden", !show);
          const input = box.querySelector(`#fbk-verify-note-${id}`);
          if (input && show) input.focus();
        }
      }));
      list.querySelectorAll('[data-act="verify-cancel"]').forEach((b) => b.addEventListener("click", () => {
        const id = b.dataset.id;
        if (!id) return;
        const box = list.querySelector(`#fbk-verify-box-${id}`);
        if (box) {
          box.classList.add("fbk-hidden");
          const input = box.querySelector(`#fbk-verify-note-${id}`);
          if (input) input.value = "";
        }
      }));
      list.querySelectorAll('[data-act="verify-submit"]').forEach((b) => b.addEventListener("click", () => {
        var _a3;
        const id = b.dataset.id;
        if (!id) return;
        const input = list.querySelector(`#fbk-verify-note-${id}`);
        const note = (_a3 = input == null ? void 0 : input.value) == null ? void 0 : _a3.trim();
        if (!note) {
          input == null ? void 0 : input.focus();
          this.toast("Please provide a note explaining what is not fixed", "error");
          return;
        }
        this.apiVerify(id, false, note);
      }));
      list.querySelectorAll(".fbk-verify-note-input").forEach((inp) => inp.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
          const id = inp.id.replace("fbk-verify-note-", "");
          const note = inp.value.trim();
          if (!note) {
            inp.focus();
            this.toast("Please provide a note explaining what is not fixed", "error");
            return;
          }
          this.apiVerify(id, false, note);
        }
      }));
    }
    // --- Pins ----------------------------------------------------------------
    renderPins() {
      const wrap = this.root && this.root.querySelector("#fbk-pins-layer");
      if (!wrap) return;
      const all = this.pageComments().filter((c) => c.status !== "archived" && c.status !== "applied");
      const here = this.scopeByWho(all);
      const items = [];
      here.forEach((c, i) => {
        const el = matchElement(c);
        if (!el) return;
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) return;
        items.push({ c, i, x: rect.left, y: rect.top });
      });
      const groups = [];
      for (const item of items) {
        const group = groups.find((g) => Math.hypot(item.x - g[0].x, item.y - g[0].y) <= PIN_CLUSTER_RADIUS);
        if (group) group.push(item);
        else groups.push([item]);
      }
      wrap.innerHTML = groups.map((g) => {
        const cx = g.reduce((sum, it) => sum + it.x, 0) / g.length;
        const cy = g.reduce((sum, it) => sum + it.y, 0) / g.length;
        const clampedCx = cx >= 0 && cx < PIN_HALF_WIDTH ? PIN_HALF_WIDTH : cx <= window.innerWidth && cx > window.innerWidth - PIN_HALF_WIDTH ? window.innerWidth - PIN_HALF_WIDTH : cx;
        const clampedCy = cy >= 0 && cy < PIN_HEIGHT ? PIN_HEIGHT : cy;
        const rect = { left: clampedCx, top: clampedCy };
        if (g.length > 1) return TPL.pinCluster(g.map((it) => it.c), rect);
        const tipSide = clampedCy - PIN_HEIGHT - PIN_TOOLTIP_HEIGHT_ESTIMATE < 0 ? "bottom" : "top";
        return TPL.pin(g[0].c, g[0].i, rect, String(g[0].c.id) === String(this._newPinId), tipSide);
      }).join("");
      applyDataPosition(wrap, ".fbk-pin-wrapper");
      wrap.querySelectorAll(".fbk-pin-cluster").forEach((btn) => btn.addEventListener("click", (e) => {
        e.stopPropagation();
        const wrapper = btn.closest(".fbk-pin-wrapper");
        const ids = ((wrapper == null ? void 0 : wrapper.dataset.ids) || "").split(",").filter(Boolean);
        const clustered = ids.map((id) => this.comments.find((c) => String(c.id) === id)).filter((c) => !!c);
        this.toggleClusterMenu(btn, clustered);
      }));
      wrap.querySelectorAll(".fbk-pin").forEach((btn) => {
        if (btn.classList.contains("fbk-pin-cluster")) return;
        btn.addEventListener("click", () => {
          const wrapper = btn.closest(".fbk-pin-wrapper");
          const id = wrapper == null ? void 0 : wrapper.dataset.id;
          if (id) this.highlightCommentCard(id);
        });
      });
    }
    // Opens the sidebar (if needed) and scrolls/highlights one comment's card — shared by a
    // standalone pin's click and the expanded cluster menu's item clicks.
    highlightCommentCard(id) {
      this.toggleSidebar(true);
      this.renderSidebar();
      setTimeout(() => {
        const card = this.root.querySelector(`.fbk-card[data-id="${id}"]`);
        if (card) {
          card.scrollIntoView({ behavior: "smooth", block: "center" });
          card.classList.add("highlight");
          setTimeout(() => card.classList.remove("highlight"), 2e3);
        }
      }, 100);
    }
    // --- Pin cluster menu ------------------------------------------------------
    toggleClusterMenu(btn, comments) {
      const host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-pin-cluster-menu")) {
        this.closeClusterMenu();
        return;
      }
      this.closeUserMenu();
      this.closeUpdatesMenu();
      if (comments.length === 0) return;
      host.innerHTML = TPL.pinClusterMenu(comments);
      const menu = host.querySelector("#fbk-pin-cluster-menu");
      if (!menu) return;
      btn.setAttribute("aria-expanded", "true");
      const r = btn.getBoundingClientRect();
      const menuHeight = menu.offsetHeight;
      const spaceBelow = window.innerHeight - r.bottom;
      if (spaceBelow < menuHeight + 6 && r.top > spaceBelow) {
        menu.style.top = "auto";
        menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - r.top + 6))}px`;
      } else {
        menu.style.bottom = "auto";
        menu.style.top = `${Math.round(r.bottom + 6)}px`;
      }
      menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 248, Math.round(r.left - 100)))}px`;
      menu.querySelectorAll(".fbk-pin-cluster-item").forEach((item) => {
        item.addEventListener("click", () => {
          const id = item.dataset.id;
          this.closeClusterMenu();
          if (id) this.highlightCommentCard(id);
        });
      });
      this._clusterMenuClose = (e) => {
        const path = e.composedPath();
        if (!path.includes(menu) && !path.includes(btn)) this.closeClusterMenu();
      };
      setTimeout(() => {
        if (this._clusterMenuClose) document.addEventListener("click", this._clusterMenuClose, true);
      }, 0);
    }
    closeClusterMenu() {
      const host = this.root.querySelector("#fbk-menu-host");
      if (host && host.querySelector("#fbk-pin-cluster-menu")) {
        host.innerHTML = "";
        this.root.querySelectorAll('.fbk-pin-cluster[aria-expanded="true"]').forEach((b) => b.setAttribute("aria-expanded", "false"));
      }
      if (this._clusterMenuClose) {
        document.removeEventListener("click", this._clusterMenuClose, true);
        this._clusterMenuClose = null;
      }
    }
    // --- Toast ---------------------------------------------------------------
    // `type` keeps every existing call site's convention ('', 'success', 'error') working
    // unchanged — '' and 'success' both render the success (green check) variant; 'error' maps to
    // the danger (red) variant. 'warn' (amber) is available for a future call site; nothing emits
    // it yet. `actionLabel`/`onAction` add an optional inline button (e.g. "Undo", "Retry") —
    // `onAction` runs, then the toast dismisses either way.
    toast(msg, type = "", actionLabel, onAction) {
      var _a2, _b;
      const variant = type === "error" ? "danger" : type === "warn" ? "warn" : "success";
      const host = this.ensureToastContainer();
      const wrap = document.createElement("div");
      wrap.innerHTML = TPL.toast(variant, msg, actionLabel);
      const el = wrap.firstElementChild;
      host.appendChild(el);
      let dismissed = false;
      const dismiss = () => {
        if (dismissed) return;
        dismissed = true;
        el.classList.add("dismissing");
        setTimeout(() => el.remove(), 160);
      };
      (_a2 = el.querySelector(".fbk-toast-close")) == null ? void 0 : _a2.addEventListener("click", dismiss);
      if (actionLabel) {
        (_b = el.querySelector(".fbk-toast-action")) == null ? void 0 : _b.addEventListener("click", () => {
          onAction == null ? void 0 : onAction();
          dismiss();
        });
      }
      setTimeout(dismiss, 2200);
    }
    // Toasts stack in their own fixed container (see _toast.scss) rather than as loose siblings —
    // otherwise two toasts shown close together would render on top of each other. Created lazily
    // and reused; renderChrome()'s full innerHTML swap can wipe it (same as any other overlay it
    // doesn't own), which only matters if a re-render happens to land inside a toast's ~2s life.
    ensureToastContainer() {
      let host = this.root.querySelector("#fbk-toast-container");
      if (!host) {
        host = document.createElement("div");
        host.id = "fbk-toast-container";
        host.className = "fbk-toast-container";
        host.setAttribute("role", "region");
        host.setAttribute("aria-label", "Notifications");
        host.setAttribute("aria-live", "polite");
        this.root.appendChild(host);
      }
      return host;
    }
  };
  /**
   * Module-level, not per-instance: "once per page load" has to hold even if the host page mounts
   * two widgets, which is exactly when a duplicate beacon would be least expected.
   */
  _PointerFeedback._buildShaReported = false;
  var PointerFeedback = _PointerFeedback;

  // src/index.ts
  if (!(window.customElements && window.customElements.get("pointer-feedback"))) {
    customElements.define("pointer-feedback", PointerFeedback);
  }
})();
