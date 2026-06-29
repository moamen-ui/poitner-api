/* GENERATED from web-component/src — DO NOT EDIT. Run `npm run build` in web-component/. */
"use strict";
(() => {
  // src/constants.ts
  var HL_CLASS = "pointer-feedback-hl";
  var ENV_MAP = { local: 1, staging: 2, production: 3 };
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
  var STATUS_LABEL = {
    open: "open",
    "pending-apply": "pending",
    applied: "completed",
    archived: "archived"
  };
  var STATUS_FALLBACK = [
    { value: 1, name: "Open", label: "Open", color: "#2563eb", order: 1 },
    { value: 2, name: "ReadyToApply", label: "Ready", color: "#d97706", order: 2 },
    { value: 3, name: "Applied", label: "Completed", color: "#16a34a", order: 3 },
    { value: 4, name: "Archived", label: "Archived", color: "#6b7280", order: 4 }
  ];
  var _catalog = STATUS_FALLBACK;
  async function loadStatusCatalog(server) {
    var _a2;
    try {
      const res = await fetch(`${server.replace(/\/$/, "")}/api/statuses`);
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
  var POSITIONS = ["top-start", "top-end", "bottom-start", "bottom-end"];
  var SHOT_MAX_WIDTH = 1280;
  var SHOT_HIGHLIGHT = "#2563eb";
  var _a;
  var SCRIPT_SRC = ((_a = document.currentScript) == null ? void 0 : _a.src) || "";
  var CSS_URL = SCRIPT_SRC ? new URL("pointer.css", SCRIPT_SRC).href : "pointer.css";
  var SNAPDOM_URL = SCRIPT_SRC ? new URL("vendor/snapdom.js", SCRIPT_SRC).href : "vendor/snapdom.js";

  // src/dom.ts
  var escapeHtml = (s) => String(s == null ? "" : s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  var ensureHighlightStyle = () => {
    if (document.getElementById("pointer-feedback-hl-style")) return;
    const s = document.createElement("style");
    s.id = "pointer-feedback-hl-style";
    s.textContent = `.${HL_CLASS}{outline:2px dashed #2563eb!important;outline-offset:1px!important;cursor:crosshair!important;}`;
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

  // src/icons.ts
  var ICON = {
    flag: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><line x1="4" y1="22" x2="4" y2="15"/></svg>',
    check: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>',
    trash: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><line x1="10" y1="11" x2="10" y2="17"/><line x1="14" y1="11" x2="14" y2="17"/></svg>',
    pencil: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
    reopen: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/></svg>',
    archive: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="21 8 21 21 3 21 3 8"/><rect x="1" y="3" width="22" height="5"/><line x1="10" y1="12" x2="14" y2="12"/></svg>',
    eyeOff: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24"/><line x1="1" y1="1" x2="23" y2="23"/></svg>',
    pin: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z"/><circle cx="12" cy="10" r="3"/></svg>',
    user: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>',
    lock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
    unlock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>',
    logout: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>'
  };

  // src/templates.ts
  var TPL = {
    // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
    // shell. showLoginModal() renders the shell once and then swaps #pf-auth-body
    // between loginBody and signupBody. The shell keeps the Skip control so
    // deferred-login dismissal works from either view.
    loginModal: (project) => `
        <div class="pf-modal-overlay">
          <div class="pf-modal">
            <h2>Pointer</h2>
            <p>Leave feedback on <b>${escapeHtml(project)}</b>.</p>
            <div id="pf-auth-body"></div>
            <button class="pf-btn pf-link" id="pf-login-skip" style="width:100%; justify-content:center; margin-top:8px;">Skip for now</button>
          </div>
        </div>`,
    // Sign-in body. After a "rejected" login it also renders an inline re-apply
    // block (role select + "Request again"); pass rejected=true to show it.
    loginBody: (rejected) => `
        <input class="pf-input" id="pf-email" type="email" placeholder="Email" style="margin-bottom:8px;" />
        <input class="pf-input" id="pf-password" type="password" placeholder="Password" style="margin-bottom:8px;" />
        <div class="pf-modal-error" id="pf-login-error"></div>
        <button class="pf-btn primary" id="pf-login-submit" style="width:100%; justify-content:center;">Sign in</button>
        ${rejected ? `
        <div class="pf-reapply" id="pf-reapply">
          <label class="pf-field-label" for="pf-reapply-role">Choose a role to request again</label>
          <select class="pf-input" id="pf-reapply-role" style="margin-bottom:8px;"></select>
          <button class="pf-btn primary" id="pf-reapply-submit" style="width:100%; justify-content:center;">Request again</button>
        </div>` : ""}
        <div class="pf-auth-foot">
          No account? <button class="pf-btn pf-link pf-link-inline" id="pf-show-signup">Create account</button>
        </div>`,
    // Sign-up body. The role <select> is populated at runtime from GET /api/roles.
    signupBody: () => `
        <input class="pf-input" id="pf-su-name" type="text" placeholder="Name" style="margin-bottom:8px;" />
        <input class="pf-input" id="pf-su-email" type="email" placeholder="Email" style="margin-bottom:8px;" />
        <input class="pf-input" id="pf-su-password" type="password" placeholder="Password" style="margin-bottom:8px;" />
        <label class="pf-field-label" for="pf-su-role">Role</label>
        <select class="pf-input" id="pf-su-role" style="margin-bottom:8px;"></select>
        <div class="pf-modal-error" id="pf-signup-error"></div>
        <div class="pf-modal-success" id="pf-signup-success"></div>
        <button class="pf-btn primary" id="pf-signup-submit" style="width:100%; justify-content:center;">Create account</button>
        <div class="pf-auth-foot">
          Already have an account? <button class="pf-btn pf-link pf-link-inline" id="pf-show-login">Back to sign in</button>
        </div>`,
    chrome: (displayName, roleLabel) => `
        <div class="pf-toolbar">
          <button class="pf-btn primary" id="pf-add" title="Add a comment on an element">+ Comment</button>
          <button class="pf-btn" id="pf-toggle" title="Show comments">Comments <span class="pf-badge" id="pf-count">0</span></button>
          <button class="pf-btn" id="pf-refresh" title="Refresh comments">&#8635;</button>
          ${displayName ? `<button class="pf-btn pf-icon-btn" id="pf-user" title="Signed in as ${displayName}${roleLabel ? " · " + roleLabel : ""}" aria-label="Signed in as ${displayName}">${ICON.user}</button>` : ""}
          <button class="pf-btn pf-icon-btn" id="pf-hide" title="Hide Pointer" aria-label="Hide Pointer">${ICON.eyeOff}</button>
        </div>
        <div class="pf-sidebar" id="pf-sidebar">
          <div class="pf-sidebar-head">
            <h2>Comments</h2>
            <button class="pf-mini" id="pf-close">&#x2715;</button>
          </div>
          <div class="pf-filters" id="pf-filters"></div>
          <div class="pf-sidebar-body" id="pf-list"></div>
        </div>
        <div id="pf-pins"></div>
        <div id="pf-popover-host"></div>
        <div id="pf-menu-host"></div>`,
    // Dropdown under the user icon: shows identity + a Sign out action.
    userMenu: (displayName, roleLabel) => `
        <div class="pf-menu" id="pf-user-menu" role="menu">
          <div class="pf-menu-id">
            <span>${displayName}</span>
            ${roleLabel ? `<span class="pf-menu-role">${roleLabel}</span>` : ""}
          </div>
          <button class="pf-menu-item" id="pf-signout" role="menuitem">${ICON.logout}<span>Sign out</span></button>
        </div>`,
    // Collapsed state: a small floating launcher that re-opens the overlay.
    // `rtl` makes start/end resolve against the host page direction (the shadow
    // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
    launcher: (count, position, rtl) => `
        <button class="pf-launcher pf-pos-${position || "bottom-end"}${rtl ? " pf-rtl" : ""}" id="pf-launcher" title="Open Pointer feedback" aria-label="Open Pointer feedback">
          ${ICON.pin}
          ${count ? `<span class="pf-launcher-badge">${count > 99 ? "99+" : count}</span>` : ""}
        </button>`,
    empty: (msg) => `<div class="pf-empty">${msg}</div>`,
    filterChip: (f, active, count) => {
      const colorStyle = f.color ? ` style="--chip-color:${f.color}"` : "";
      return `<button class="pf-chip ${active ? "active" : ""} chip-${STATUS_LABEL[f.key] || "all"}"${colorStyle} data-filter="${f.key}">
             ${f.label} <span class="pf-chip-n">${count}</span>
           </button>`;
    },
    // "Mine only" toggle — a chip that composes with the status chips above.
    // Rendered only when a user is logged in.
    mineToggle: (active) => `<button class="pf-chip pf-mine ${active ? "active" : ""}" id="pf-mine-toggle" title="Show only my comments" aria-pressed="${active ? "true" : "false"}">
             &#x1f464; Mine only
           </button>`,
    // User filter — only rendered when the list has comments from >1 author.
    authorFilter: (authors, selectedId) => `<select class="pf-userfilter" id="pf-author-filter" title="Filter by user">
             <option value="">&#x1f465; All users</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? "selected" : ""}>${escapeHtml(a.name)}</option>`).join("")}
           </select>`,
    card: (c, i) => {
      const cls = c.status === "pending-apply" ? "pending" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "";
      const statusPill = c.status === "applied" ? '<span class="pf-pill status-applied">&#x2713; completed</span>' : c.status === "pending-apply" ? '<span class="pf-pill status-pending">pending</span>' : c.status === "archived" ? '<span class="pf-pill status-archived">&#x1f4e6; archived</span>' : "";
      const replies = (c.replies || []).map((r) => `<div class="pf-reply ${r.isAi ? "ai" : ""}"><b>${escapeHtml(r.authorName || r.authorLabel || "User")}:</b> ${escapeHtml(r.body || r.text || "")}</div>`).join("");
      const envInt = c.environment;
      const envLabel = envInt === 1 ? "Local" : envInt === 2 ? "Staging" : envInt === 3 ? "Production" : envInt ? String(envInt) : "";
      const authorLabel = c.authorName || "";
      const shotUrl = c.element && c.element.screenshotUrl;
      const shot = shotUrl ? `<a class="pf-shot-link" href="${escapeHtml(shotUrl)}" target="_blank" rel="noopener noreferrer" title="Open full screenshot">
            <img class="pf-shot" src="${escapeHtml(shotUrl)}" alt="Element screenshot" loading="lazy" />
          </a>` : "";
      return `
          <div class="pf-card ${cls}" data-id="${c.id}">
            <div class="pf-meta">
              <span class="pf-badge">${i + 1}</span>
              ${envLabel ? `<span class="pf-pill env">${escapeHtml(envLabel)}</span>` : ""}
              ${statusPill}
            </div>
            <div class="pf-text">${escapeHtml(c.body || c.text || "")}</div>
            ${shot}
            <div class="pf-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ""}${c.editedAt ? ' &middot; <span style="font-style:italic;">edited</span>' : ""}</div>
            ${replies ? `<div class="pf-replies">${replies}</div>` : ""}
            <div class="pf-reply-row">
              <input class="pf-input pf-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="pf-actions">
              ${c.status === "applied" || c.status === "archived" ? "" : `<button class="pf-mini ${c.status === "pending-apply" ? "apply" : "ready"}" data-act="apply" data-id="${c.id}" title="${c.status === "pending-apply" ? "Marked ready — click to unmark" : "Mark ready to apply"}">
                ${ICON.flag}<span>Ready</span>
              </button>`}
              ${c.status === "open" || c.status === "pending-apply" ? `<button class="pf-mini done pf-icon" data-act="complete" data-id="${c.id}" title="Mark completed" aria-label="Mark completed">${ICON.check}</button>` : ""}
              ${c.status === "applied" ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>
              <button class="pf-mini pf-icon" data-act="archive" data-id="${c.id}" title="Archive" aria-label="Archive">${ICON.archive}</button>` : ""}
              ${c.status === "archived" ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>` : ""}
              <div class="pf-actions-end">
                ${c._mine ? `<button class="pf-mini pf-icon${c.isPrivate ? " private-on" : ""}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? "false" : "true"}" title="${c.isPrivate ? "Private — click to make public" : "Make private (only you)"}" aria-label="${c.isPrivate ? "Make public" : "Make private"}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ""}
                ${c._mine ? `<button class="pf-mini pf-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button>` : ""}
                ${c.status === "open" ? `<button class="pf-mini danger pf-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ""}
              </div>
            </div>
          </div>`;
    },
    popover: (meta, left, top, shotEnabled) => `
        <div class="pf-popover" style="left:${left}px; top:${top}px;">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ""}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          ${shotEnabled ? `<label class="pf-check"><input type="checkbox" id="pf-comment-shot" /> &#x1f4f7; Attach screenshot</label>` : ""}
          <label class="pf-check"><input type="checkbox" id="pf-comment-private" /> &#x1f512; Keep private — only me</label>
          <div class="pf-reply-row">
            <button class="pf-btn primary" id="pf-submit" style="flex:1; justify-content:center;">Add</button>
            <button class="pf-mini" id="pf-cancel">Cancel</button>
          </div>
        </div>`,
    pin: (c, i, rect) => {
      const cls = c.status === "pending-apply" ? "pending" : c.status === "applied" ? "applied" : "";
      return `<div class="pf-pin ${cls}" data-id="${c.id}" style="left:${rect.left}px; top:${rect.top}px;"><span>${i + 1}</span></div>`;
    }
  };

  // src/capture.ts
  var snapdomPromise = null;
  function loadSnapdom() {
    if (window.snapdom) return Promise.resolve(window.snapdom);
    if (snapdomPromise) return snapdomPromise;
    snapdomPromise = new Promise((resolve, reject) => {
      const s = document.createElement("script");
      s.src = SNAPDOM_URL;
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
  function captureMetadata(el, sourceAttr) {
    const selector = generateSelector(el);
    const snapshot = el.outerHTML.length > 2e3 ? el.outerHTML.slice(0, 2e3) : el.outerHTML;
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
    for (const sheet of Array.from(document.styleSheets)) {
      let rules;
      try {
        rules = sheet.cssRules || sheet.rules;
      } catch (e) {
        continue;
      }
      if (!rules) continue;
      for (const rule of Array.from(rules)) {
        const styleRule = rule;
        if (!styleRule.selectorText) continue;
        try {
          if (el.matches(styleRule.selectorText)) {
            applied.push({ selector: styleRule.selectorText, styles: styleRule.style.cssText });
          }
        } catch (e) {
        }
      }
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

  // src/auth-ui.ts
  function showLoginModal(host, afterLogin) {
    host.afterLogin = afterLogin || null;
    host.root.innerHTML = TPL.loginModal(host.project);
    const skipBtn = host.root.querySelector("#pf-login-skip");
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
    const body = host.root.querySelector("#pf-auth-body");
    if (!body) return;
    body.innerHTML = TPL.loginBody(!!opts.rejected);
    const emailEl = body.querySelector("#pf-email");
    const passEl = body.querySelector("#pf-password");
    const errEl = body.querySelector("#pf-login-error");
    const submitBtn = body.querySelector("#pf-login-submit");
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
          const re = host.root.querySelector("#pf-auth-body");
          re.querySelector("#pf-email").value = email;
          re.querySelector("#pf-password").value = password;
          re.querySelector("#pf-login-error").textContent = envelope.message || "Your request was rejected.";
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
    body.querySelector("#pf-show-signup").addEventListener("click", () => renderSignupView(host));
    if (opts.rejected) {
      const roleEl = body.querySelector("#pf-reapply-role");
      const reBtn = body.querySelector("#pf-reapply-submit");
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
          const reBody = host.root.querySelector("#pf-auth-body");
          reBody.querySelector("#pf-email").value = email;
          reBody.querySelector("#pf-login-error").textContent = envelope.message || "Request submitted — an admin will review it.";
        } catch (e) {
          errEl.textContent = "Network error. Please try again.";
          reBtn.disabled = false;
          reBtn.textContent = "Request again";
        }
      });
    }
  }
  function renderSignupView(host) {
    const body = host.root.querySelector("#pf-auth-body");
    if (!body) return;
    body.innerHTML = TPL.signupBody();
    const nameEl = body.querySelector("#pf-su-name");
    const emailEl = body.querySelector("#pf-su-email");
    const passEl = body.querySelector("#pf-su-password");
    const roleEl = body.querySelector("#pf-su-role");
    const errEl = body.querySelector("#pf-signup-error");
    const okEl = body.querySelector("#pf-signup-success");
    const submitBtn = body.querySelector("#pf-signup-submit");
    populateRoles(host, roleEl, errEl);
    body.querySelector("#pf-show-login").addEventListener("click", () => renderLoginView(host));
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
  var PointerFeedback = class extends HTMLElement {
    constructor() {
      super(...arguments);
      this._mounted = false;
      this.project = "";
      this.environmentAttr = "";
      this.sourceAttr = "data-component-source";
      this.screenshotEnabled = true;
      this.launcherPosition = "bottom-end";
      this.server = "";
      this.environmentInt = 2;
      this.comments = [];
      this.statusFilter = "all";
      this.mineOnly = false;
      this.authorFilter = null;
      this.hiddenPrivateCount = 0;
      this._collapsed = true;
      this.picking = false;
      this.sidebarOpen = false;
      this.hovered = null;
      this.token = null;
      this.user = null;
      this.afterLogin = null;
      this._pendingShotPromise = null;
      this._userMenuClose = null;
    }
    connectedCallback() {
      if (this._mounted) return;
      this._mounted = true;
      this.project = this.getAttribute("project") || "";
      this.environmentAttr = this.getAttribute("environment") || "";
      this.sourceAttr = this.getAttribute("source-attr") || "data-component-source";
      this.screenshotEnabled = (this.getAttribute("screenshot") || "").toLowerCase() !== "false";
      const pos = (this.getAttribute("launcher-position") || "").toLowerCase();
      this.launcherPosition = POSITIONS.includes(pos) ? pos : "bottom-end";
      this.server = (this.getAttribute("server") || (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, "");
      this.environmentInt = ENV_MAP[this.environmentAttr.toLowerCase()] || 2;
      this._collapsed = (() => {
        try {
          return sessionStorage.getItem("pointer_visible") !== "1";
        } catch (e) {
          return true;
        }
      })();
      this.loadAuth();
      this.style.position = "fixed";
      this.style.zIndex = "2147483647";
      this.style.top = "0";
      this.style.left = "0";
      this.style.pointerEvents = "none";
      this.attachShadow({ mode: "open" });
      this._styleLink = document.createElement("link");
      this._styleLink.rel = "stylesheet";
      this._styleLink.href = CSS_URL || `${this.server}/pointer.css`;
      this.shadowRoot.appendChild(this._styleLink);
      this.root = document.createElement("div");
      this.shadowRoot.appendChild(this.root);
      ensureHighlightStyle();
      if (!this.project) {
        console.error("[pointer-feedback] Missing required `project` attribute. Component disabled.");
        return;
      }
      this._onHover = this.onHover.bind(this);
      this._onPick = this.onPick.bind(this);
      this._reposition = () => this.renderPins();
      window.addEventListener("scroll", this._reposition, true);
      window.addEventListener("resize", this._reposition);
      this._boot();
    }
    // Wait for the stylesheet to load, then render the first view (avoids a flash
    // of unstyled UI). A short timeout guarantees we never hang on slow CSS.
    async _boot() {
      await this._stylesReady();
      if (this.token) this.init();
      else this.renderChrome();
    }
    _stylesReady() {
      return new Promise((resolve) => {
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
        link.addEventListener("error", finish, { once: true });
        setTimeout(finish, 1500);
      });
    }
    disconnectedCallback() {
      window.removeEventListener("scroll", this._reposition, true);
      window.removeEventListener("resize", this._reposition);
      this.stopPicking();
    }
    // --- Auth helpers --------------------------------------------------------
    loadAuth() {
      this.token = localStorage.getItem("pointer_token") || null;
      try {
        const raw = localStorage.getItem("pointer_user");
        this.user = raw ? JSON.parse(raw) : null;
      } catch (e) {
        this.user = null;
      }
    }
    saveAuth(token, user) {
      this.token = token;
      this.user = user;
      localStorage.setItem("pointer_token", token);
      localStorage.setItem("pointer_user", JSON.stringify(user));
    }
    clearAuth() {
      this.token = null;
      this.user = null;
      localStorage.removeItem("pointer_token");
      localStorage.removeItem("pointer_user");
    }
    handle401() {
      this.clearAuth();
      showLoginModal(this);
    }
    async init() {
      await loadStatusCatalog(this.server);
      this.renderChrome();
      await this.fetchComments();
      this.renderSidebar();
      this.renderPins();
      if (this._collapsed) this.renderChrome();
    }
    // --- API ----------------------------------------------------------------
    async apiLogin(email, password) {
      return fetch(`${this.server}/api/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password })
      });
    }
    // Anonymous: active non-admin roles for the signup / re-apply dropdowns.
    async apiRoles() {
      const r = await fetch(`${this.server}/api/roles?project=${encodeURIComponent(this.project)}`, {
        headers: { "Content-Type": "application/json" }
      });
      const envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || "Could not load roles.");
      return envelope.data || [];
    }
    // Anonymous: self-signup AND re-apply (one endpoint). No token returned.
    async apiRegister(body) {
      return fetch(`${this.server}/api/auth/register`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    }
    api(path, opts = {}) {
      const headers = {
        "Content-Type": "application/json",
        ...this.token ? { Authorization: `Bearer ${this.token}` } : {},
        ...opts.headers || {}
      };
      return fetch(`${this.server}${path}`, { ...opts, headers }).then((r) => {
        if (r.status === 401) {
          this.handle401();
          throw new Error("HTTP 401 Unauthorized");
        }
        return r;
      });
    }
    async fetchComments() {
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments?environment=${this.environmentInt}`);
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
          this.toast("Could not reach Pointer server", "error");
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
      if (this._collapsed) {
        const n = (this.comments || []).filter((c) => c.status !== "archived").length;
        this.root.innerHTML = TPL.launcher(n, this.launcherPosition, pageIsRtl());
        const launcher = this.root.querySelector("#pf-launcher");
        if (launcher) launcher.addEventListener("click", () => this.showOverlay());
        return;
      }
      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "";
      const roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "";
      this.root.innerHTML = TPL.chrome(displayName, roleLabel);
      const hideBtn = this.root.querySelector("#pf-hide");
      if (hideBtn) hideBtn.addEventListener("click", () => this.hideOverlay());
      const userBtn = this.root.querySelector("#pf-user");
      if (userBtn) userBtn.addEventListener("click", (e) => {
        e.stopPropagation();
        this.toggleUserMenu();
      });
      this.root.querySelector("#pf-add").addEventListener("click", () => {
        if (!this.token) {
          showLoginModal(this, () => {
            Promise.resolve(this.init()).then(() => this.togglePicking());
          });
          return;
        }
        this.togglePicking();
      });
      this.root.querySelector("#pf-toggle").addEventListener("click", () => {
        if (!this.token) {
          showLoginModal(this, () => {
            Promise.resolve(this.init()).then(() => this.toggleSidebar(true));
          });
          return;
        }
        this.toggleSidebar();
      });
      this.root.querySelector("#pf-refresh").addEventListener("click", async () => {
        if (!this.token) {
          showLoginModal(this, () => this.init());
          return;
        }
        await this.fetchComments();
        this.renderSidebar();
        this.renderPins();
        this.toast("Refreshed");
      });
      this.root.querySelector("#pf-close").addEventListener("click", () => this.toggleSidebar(false));
    }
    // --- User menu (identity + sign out) ------------------------------------
    toggleUserMenu() {
      const host = this.root.querySelector("#pf-menu-host");
      if (!host) return;
      if (host.querySelector("#pf-user-menu")) {
        this.closeUserMenu();
        return;
      }
      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "";
      const roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "";
      host.innerHTML = TPL.userMenu(displayName, roleLabel);
      const menu = host.querySelector("#pf-user-menu");
      const btn = this.root.querySelector("#pf-user");
      if (btn) {
        const r = btn.getBoundingClientRect();
        menu.style.top = `${Math.round(r.bottom + 6)}px`;
        menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
      }
      host.querySelector("#pf-signout").addEventListener("click", () => this.signOut());
      this._userMenuClose = (e) => {
        const path = e.composedPath();
        if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUserMenu();
      };
      setTimeout(() => {
        if (this._userMenuClose) document.addEventListener("click", this._userMenuClose, true);
      }, 0);
    }
    closeUserMenu() {
      const host = this.root.querySelector("#pf-menu-host");
      if (host) host.innerHTML = "";
      if (this._userMenuClose) {
        document.removeEventListener("click", this._userMenuClose, true);
        this._userMenuClose = null;
      }
    }
    // Clear the session and reset the widget to its logged-out (deferred-login) state.
    signOut() {
      this.closeUserMenu();
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
      this.toast("Pointer hidden — click the button to reopen");
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
      this.sidebarOpen = force === void 0 ? !this.sidebarOpen : force;
      this.root.querySelector("#pf-sidebar").classList.toggle("open", this.sidebarOpen);
      if (this.sidebarOpen) {
        this.fetchComments().then(() => {
          this.renderSidebar();
          this.renderPins();
        });
      }
    }
    // --- Element picking -----------------------------------------------------
    togglePicking() {
      this.picking ? this.stopPicking() : this.startPicking();
    }
    startPicking() {
      this.picking = true;
      const addBtn = this.root.querySelector("#pf-add");
      addBtn.classList.add("active");
      addBtn.textContent = "✕ Cancel";
      document.addEventListener("mousemove", this._onHover, true);
      document.addEventListener("click", this._onPick, true);
      this.toast("Click any element to comment on it");
    }
    stopPicking() {
      this.picking = false;
      const addBtn = this.root && this.root.querySelector("#pf-add");
      if (addBtn) {
        addBtn.classList.remove("active");
        addBtn.textContent = "+ Comment";
      }
      document.removeEventListener("mousemove", this._onHover, true);
      document.removeEventListener("click", this._onPick, true);
      this.clearHover();
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
    onHover(e) {
      const el = e.target;
      if (this.isOwnElement(el)) return;
      if (el === this.hovered) return;
      this.clearHover();
      this.hovered = el;
      el.classList.add(HL_CLASS);
    }
    onPick(e) {
      if (this.isOwnElement(e.target)) return;
      e.preventDefault();
      e.stopPropagation();
      const el = e.target;
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
        const r = await fetch(`${this.server}/api/uploads`, {
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
      const meta = captureMetadata(el, this.sourceAttr);
      const host = this.root.querySelector("#pf-popover-host");
      const left = Math.min(x, window.innerWidth - 300);
      const top = Math.min(y, window.innerHeight - 220);
      host.innerHTML = TPL.popover(meta, left, top, this.screenshotEnabled);
      const ta = host.querySelector("#pf-comment-text");
      ta.focus();
      const shotToggle = host.querySelector("#pf-comment-shot");
      if (shotToggle) shotToggle.addEventListener("change", () => {
        if (shotToggle.checked) this.beginScreenshotCapture(el);
      });
      host.querySelector("#pf-cancel").addEventListener("click", () => {
        host.innerHTML = "";
        this._pendingShotPromise = null;
      });
      host.querySelector("#pf-submit").addEventListener("click", async () => {
        const text = ta.value.trim();
        if (!text) return this.toast("Comment cannot be empty", "error");
        const privateEl = host.querySelector("#pf-comment-private");
        const isPrivate = !!(privateEl && privateEl.checked);
        const shotEl = host.querySelector("#pf-comment-shot");
        const attachShot = !!(shotEl && shotEl.checked);
        const shotPromise = this._pendingShotPromise;
        this._pendingShotPromise = null;
        const submitBtn = host.querySelector("#pf-submit");
        submitBtn.disabled = true;
        submitBtn.textContent = "Saving…";
        await this.createComment({ ...meta, text, isPrivate, attachShot, shotPromise });
        host.innerHTML = "";
      });
    }
    async createComment(data) {
      const element = {
        selector: data.selector,
        snapshot: data.snapshot,
        classes: data.classes,
        computedStyles: data.computedStyles,
        appliedCssRules: data.appliedCssRules,
        sourcePath: data.sourcePath,
        parentInfo: data.parentInfo
      };
      if (data.attachShot && data.shotPromise) {
        const blob = await Promise.resolve(data.shotPromise).catch(() => null);
        if (blob) {
          const url = await this.uploadToServer(blob);
          if (url) element.screenshotUrl = url;
          else this.toast("Screenshot upload failed — saving without it", "error");
        }
      }
      const body = {
        body: data.text,
        environment: this.environmentInt,
        isPrivate: !!data.isPrivate,
        element
      };
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments`, {
          method: "POST",
          body: JSON.stringify({ ...body, projectKey: this.project })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        const envelope = await r.json();
        const comment = envelope.data;
        if (comment) {
          this.comments.push({ ...comment, status: STATUS_STR[comment.status] || "open" });
        }
        this.renderSidebar();
        this.renderPins();
        this.toast("Comment added", "success");
      } catch (e) {
        if (e.message !== "HTTP 401 Unauthorized") {
          this.toast("Failed to save comment", "error");
        }
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
      const card = this.root && this.root.querySelector(`.pf-card[data-id="${id}"]`);
      if (!card || card.querySelector(".pf-edit")) return;
      const comment = (this.comments || []).find((x) => String(x.id) === String(id));
      if (!comment) return;
      const textEl = card.querySelector(".pf-text");
      if (!textEl) return;
      const hasShot = !!(comment.element && comment.element.screenshotUrl);
      const editor = document.createElement("div");
      editor.className = "pf-edit";
      editor.style.margin = "6px 0";
      editor.innerHTML = `
        <textarea class="pf-textarea pf-edit-body">${escapeHtml(comment.body || "")}</textarea>
        ${hasShot ? `<label style="display:flex;gap:6px;align-items:center;font-size:12px;color:#475569;margin:6px 0;"><input type="checkbox" class="pf-edit-rmshot" /> Remove image</label>` : ""}
        <div class="pf-reply-row">
          <button class="pf-btn primary pf-edit-save" style="flex:1;justify-content:center;">Save</button>
          <button class="pf-mini pf-edit-cancel">Cancel</button>
        </div>`;
      textEl.style.display = "none";
      textEl.insertAdjacentElement("afterend", editor);
      const ta = editor.querySelector(".pf-edit-body");
      ta.focus();
      editor.querySelector(".pf-edit-cancel").addEventListener("click", () => {
        editor.remove();
        textEl.style.display = "";
      });
      editor.querySelector(".pf-edit-save").addEventListener("click", () => {
        const body = ta.value.trim();
        if (!body) {
          this.toast("Comment cannot be empty", "error");
          return;
        }
        const rm = editor.querySelector(".pf-edit-rmshot");
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
      var _a2;
      const all = this.pageComments();
      const canMine = !!(this.user && this.user.id);
      if (!canMine) this.mineOnly = false;
      const authors = this.distinctAuthors(all);
      if (this.authorFilter && !authors.some((a) => a.id === this.authorFilter)) this.authorFilter = null;
      const scoped = this.scopeByWho(all);
      const counts = {
        // "All" means active (non-archived); archived move out to their own chip.
        all: scoped.filter((c) => c.status !== "archived").length,
        open: scoped.filter((c) => c.status === "open").length,
        "pending-apply": scoped.filter((c) => c.status === "pending-apply").length,
        applied: scoped.filter((c) => c.status === "applied").length,
        archived: scoped.filter((c) => c.status === "archived").length
      };
      const countEl = this.root.querySelector("#pf-count");
      if (countEl) countEl.textContent = String(all.filter((c) => c.status !== "archived").length);
      const filtersEl = this.root.querySelector("#pf-filters");
      if (filtersEl) {
        const activeFilters = catalogToFilters();
        filtersEl.innerHTML = activeFilters.map((f) => {
          var _a3;
          return TPL.filterChip(f, this.statusFilter === f.key, (_a3 = counts[f.key]) != null ? _a3 : 0);
        }).join("") + (authors.length > 1 && !this.mineOnly ? TPL.authorFilter(authors, this.authorFilter || "") : "") + (canMine ? TPL.mineToggle(this.mineOnly) : "");
        filtersEl.querySelectorAll("[data-filter]").forEach((b) => b.addEventListener("click", () => {
          this.statusFilter = b.dataset.filter;
          this.renderSidebar();
        }));
        const mineBtn = filtersEl.querySelector("#pf-mine-toggle");
        if (mineBtn) mineBtn.addEventListener("click", () => {
          this.mineOnly = !this.mineOnly;
          this.renderSidebar();
          this.renderPins();
        });
        const authorSel = filtersEl.querySelector("#pf-author-filter");
        if (authorSel) authorSel.addEventListener("change", () => {
          this.authorFilter = authorSel.value || null;
          this.renderSidebar();
          this.renderPins();
        });
      }
      const list = this.root.querySelector("#pf-list");
      if (!list) return;
      const shown = this.statusFilter === "all" ? scoped.filter((c) => c.status !== "archived") : scoped.filter((c) => c.status === this.statusFilter);
      if (!scoped.length) {
        list.innerHTML = TPL.empty(this.mineOnly ? "You haven't left any comments yet." : 'No comments on this project yet.<br/>Click "+ Comment", then click an element.');
        return;
      }
      if (!shown.length) {
        const activeFilters = catalogToFilters();
        const filterLabel = ((_a2 = activeFilters.find((f) => f.key === this.statusFilter)) != null ? _a2 : { label: this.statusFilter }).label;
        list.innerHTML = TPL.empty(`No ${filterLabel.toLowerCase()} comments${this.mineOnly ? " of yours" : ""}.`);
        return;
      }
      list.innerHTML = shown.map((c, i) => {
        c._mine = this.isMine(c);
        return TPL.card(c, i);
      }).join("");
      list.querySelectorAll('[data-act="apply"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c && c.status !== "applied") this.toggleApply(c);
      }));
      list.querySelectorAll('[data-act="complete"]').forEach((b) => b.addEventListener("click", () => {
        const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        if (c && c.status !== "applied") this.markCompleted(c);
      }));
      list.querySelectorAll('[data-act="delete"]').forEach((b) => b.addEventListener("click", () => this.deleteComment(b.dataset.id)));
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
      list.querySelectorAll(".pf-reply-input").forEach((inp) => inp.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && inp.value.trim()) {
          this.addReply(inp.dataset.id, inp.value.trim());
          inp.value = "";
        }
      }));
    }
    // --- Pins ----------------------------------------------------------------
    renderPins() {
      const wrap = this.root && this.root.querySelector("#pf-pins");
      if (!wrap) return;
      const all = this.pageComments().filter((c) => c.status !== "archived");
      const here = this.scopeByWho(all);
      wrap.innerHTML = here.map((c, i) => {
        const el = matchElement(c);
        if (!el) return "";
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) return "";
        return TPL.pin(c, i, rect);
      }).join("");
      wrap.querySelectorAll(".pf-pin").forEach((p) => p.addEventListener("click", () => {
        this.toggleSidebar(true);
        const card = this.root.querySelector(`.pf-card[data-id="${p.dataset.id}"]`);
        if (card) card.scrollIntoView({ behavior: "smooth", block: "center" });
      }));
    }
    // --- Toast ---------------------------------------------------------------
    toast(msg, type = "") {
      const t = document.createElement("div");
      t.className = `pf-toast ${type}`;
      t.textContent = msg;
      this.root.appendChild(t);
      setTimeout(() => t.remove(), 2200);
    }
  };

  // src/index.ts
  if (!(window.customElements && window.customElements.get("pointer-feedback"))) {
    customElements.define("pointer-feedback", PointerFeedback);
  }
})();
