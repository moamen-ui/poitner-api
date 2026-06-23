/**
 * Pointer — <pointer-feedback> web component
 *
 * Drop-in element-level feedback for any app. Single install:
 *
 *   <script src="https://your-pointer-server/pointer.js"></script>
 *   <pointer-feedback
 *      project="checkout-app"              // required: which project this feedback belongs to
 *      environment="staging"               // optional: "local"|"staging"|"production" (default Staging)
 *      source-attr="data-component-source" // optional: DOM attribute carrying the source file path
 *      server="https://pointer.example.com"></pointer-feedback>  // optional: defaults to this script's origin
 *
 * The UI lives inside a Shadow DOM so it never collides with the host app's CSS.
 * Comments are sent to the Pointer API, partitioned by project.
 *
 * Auth: email + password login against POST /api/auth/login.
 * Token stored in localStorage['pointer_token']; user profile in localStorage['pointer_user'].
 * Every non-login request sends Authorization: Bearer <token>.
 * HTTP 401 clears storage and re-opens the login modal.
 */
(() => {
  if (window.customElements && window.customElements.get('pointer-feedback')) return;

  const SCRIPT_SRC = (document.currentScript && document.currentScript.src) || '';
  const HL_CLASS = 'pointer-feedback-hl';

  // Environment string → int mapping (API contract: 1=Local, 2=Staging, 3=Production)
  const ENV_MAP = { local: 1, staging: 2, production: 3 };

  // Status int → string mapping (API contract: 1=Open, 2=ReadyToApply, 3=Applied)
  const STATUS_STR = { 1: 'open', 2: 'pending-apply', 3: 'applied' };
  const STATUS_INT = { 'open': 1, 'pending-apply': 2, 'applied': 3 };

  // One global style for the host-page hover highlight (lives in light DOM by
  // necessity — it decorates the host app's own elements, not our shadow UI).
  const ensureHighlightStyle = () => {
    if (document.getElementById('pointer-feedback-hl-style')) return;
    const s = document.createElement('style');
    s.id = 'pointer-feedback-hl-style';
    s.textContent = `.${HL_CLASS}{outline:2px dashed #2563eb!important;outline-offset:1px!important;cursor:crosshair!important;}`;
    document.head.appendChild(s);
  };

  // --- Element selector (ported from the original inject.js) ----------------
  const generateSelector = (el) => {
    if (el === document.documentElement) return 'html';
    if (el === document.body) return 'body';

    if (el.id) {
      try {
        if (document.querySelector('#' + CSS.escape(el.id)) === el) return '#' + el.id;
      } catch (e) {}
    }

    const parts = [];
    let cur = el;
    while (cur && cur !== document.body && cur !== document.documentElement) {
      let selector = cur.tagName.toLowerCase();
      if (cur.id) {
        selector += '#' + cur.id;
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
    if (cur === document.body) parts.unshift('body');
    else if (cur === document.documentElement) parts.unshift('html');
    return parts.join(' > ');
  };

  // Re-find an element from a stored comment (selector first, snapshot fallback).
  const matchElement = (comment) => {
    const selector = comment.element && comment.element.selector;
    const snapshot = comment.element && comment.element.snapshot;
    if (selector) {
      try {
        const el = document.querySelector(selector);
        if (el) return el;
      } catch (e) {}
    }
    if (snapshot) {
      const all = document.querySelectorAll('*');
      for (const el of all) {
        if (el.outerHTML === snapshot) return el;
      }
    }
    return null;
  };

  const escapeHtml = (s) => String(s == null ? '' : s)
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;').replace(/'/g, '&#39;');

  // Status model: open | pending-apply | applied. The UI groups these as the
  // filter chips All / Open / Pending / Completed.
  const STATUS_LABEL = { 'open': 'open', 'pending-apply': 'pending', 'applied': 'completed' };
  const FILTERS = [
    { key: 'all', label: 'All' },
    { key: 'open', label: 'Open' },
    { key: 'pending-apply', label: 'Pending' },
    { key: 'applied', label: 'Completed' },
  ];

  const STYLES = `
    :host{ all: initial; }
    *{ box-sizing: border-box; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
    .pf-toolbar{ position: fixed; top: 16px; right: 16px; z-index: 2147483646; display: flex; gap: 8px;
      background: #fff; border: 1px solid #e2e8f0; border-radius: 12px; padding: 8px;
      box-shadow: 0 6px 24px rgba(0,0,0,.12); pointer-events: auto; }
    .pf-btn{ display: inline-flex; align-items: center; gap: 6px; border: none; border-radius: 8px;
      padding: 8px 12px; font-size: 13px; font-weight: 600; cursor: pointer; background: #f1f5f9; color: #0f172a; }
    .pf-btn:hover{ background: #e2e8f0; }
    .pf-btn.primary{ background: #2563eb; color: #fff; }
    .pf-btn.primary:hover{ background: #1d4ed8; }
    .pf-btn.active{ background: #1d4ed8; color: #fff; }
    .pf-badge{ background: #2563eb; color:#fff; border-radius: 999px; padding: 1px 7px; font-size: 11px; }

    .pf-sidebar{ position: fixed; top: 0; right: 0; height: 100vh; width: 360px; max-width: 92vw; z-index: 2147483646;
      background: #fff; border-left: 1px solid #e2e8f0; box-shadow: -8px 0 24px rgba(0,0,0,.08);
      transform: translateX(100%); transition: transform .2s ease; display: flex; flex-direction: column; pointer-events: auto; }
    .pf-sidebar.open{ transform: translateX(0); }
    .pf-sidebar-head{ padding: 16px; border-bottom: 1px solid #eef2f7; display:flex; align-items:center; justify-content: space-between; }
    .pf-sidebar-head h2{ margin:0; font-size: 16px; color:#0f172a; }
    .pf-filters{ display:flex; gap:6px; flex-wrap:wrap; padding: 10px 12px; border-bottom: 1px solid #eef2f7; }
    .pf-chip{ border:1px solid #e2e8f0; background:#fff; color:#475569; border-radius:999px; padding:4px 10px;
      font-size:12px; font-weight:600; cursor:pointer; display:inline-flex; align-items:center; gap:6px; }
    .pf-chip:hover{ background:#f8fafc; }
    .pf-chip-n{ background:#eef2f7; color:#475569; border-radius:999px; padding:0 6px; font-size:11px; }
    .pf-chip.active{ background:#0f172a; color:#fff; border-color:#0f172a; }
    .pf-chip.active .pf-chip-n{ background:rgba(255,255,255,.25); color:#fff; }
    .pf-chip.chip-pending.active{ background:#92400e; border-color:#92400e; }
    .pf-chip.chip-completed.active{ background:#166534; border-color:#166534; }
    .pf-sidebar-body{ overflow-y: auto; padding: 12px; flex: 1; }
    .pf-empty{ color:#64748b; font-size: 13px; text-align:center; padding: 32px 12px; }

    .pf-card{ border: 1px solid #eef2f7; border-radius: 10px; padding: 12px; margin-bottom: 10px; }
    .pf-card.pending{ border-color: #f59e0b; background: #fffbeb; }
    .pf-card.applied{ border-color: #16a34a; background: #f0fdf4; }
    .pf-meta{ display:flex; gap:6px; align-items:center; flex-wrap: wrap; margin-bottom: 6px; }
    .pf-pill{ font-size: 10px; font-weight: 700; text-transform: uppercase; letter-spacing:.03em; padding: 2px 6px; border-radius: 6px; background:#eef2f7; color:#475569; }
    .pf-pill.role{ background:#dbeafe; color:#1e40af; }
    .pf-pill.env{ background:#f3e8ff; color:#7c3aed; }
    .pf-pill.status-applied{ background:#dcfce7; color:#166534; }
    .pf-pill.status-pending{ background:#fef3c7; color:#92400e; }
    .pf-text{ font-size: 14px; color:#0f172a; margin: 4px 0 8px; white-space: pre-wrap; }
    .pf-sub{ font-size: 11px; color:#94a3b8; }
    .pf-src{ font-size: 11px; color:#0369a1; font-family: ui-monospace, monospace; word-break: break-all; }
    .pf-actions{ display:flex; gap:6px; flex-wrap: wrap; margin-top: 8px; }
    .pf-mini{ border:none; background:#f1f5f9; color:#0f172a; border-radius:6px; padding:5px 9px; font-size:12px; cursor:pointer; }
    .pf-mini:hover{ background:#e2e8f0; }
    .pf-mini.apply{ background:#fef3c7; color:#92400e; }
    .pf-mini.applied{ background:#dcfce7; color:#166534; }
    .pf-mini.danger:hover{ background:#fee2e2; color:#b91c1c; }
    .pf-replies{ margin-top: 8px; border-top: 1px dashed #e2e8f0; padding-top: 8px; }
    .pf-reply{ font-size: 13px; color:#334155; padding: 4px 0; }
    .pf-reply.ai{ color:#166534; }
    .pf-reply-row{ display:flex; gap:6px; margin-top: 6px; }
    .pf-input, .pf-textarea{ width:100%; border:1px solid #cbd5e1; border-radius:8px; padding:8px; font-size:13px; }
    .pf-textarea{ resize: vertical; min-height: 64px; }

    .pf-pin{ position: fixed; z-index: 2147483645; width: 24px; height: 24px; border-radius: 50% 50% 50% 0;
      background:#2563eb; color:#fff; font-size: 12px; font-weight: 700; display:flex; align-items:center; justify-content:center;
      transform: rotate(-45deg); box-shadow: 0 2px 6px rgba(0,0,0,.3); cursor: pointer; pointer-events: auto; }
    .pf-pin span{ transform: rotate(45deg); }
    .pf-pin.pending{ background:#f59e0b; }
    .pf-pin.applied{ background:#16a34a; }

    .pf-popover{ position: fixed; z-index: 2147483647; width: 280px; background:#fff; border:1px solid #e2e8f0;
      border-radius: 12px; box-shadow: 0 12px 32px rgba(0,0,0,.18); padding: 12px; pointer-events: auto; }
    .pf-popover h3{ margin:0 0 8px; font-size: 13px; color:#0f172a; }
    .pf-snippet{ font-size: 11px; font-family: ui-monospace, monospace; color:#475569; background:#f8fafc; border-radius:6px; padding:6px; margin-bottom:8px; max-height: 60px; overflow:auto; }

    .pf-modal-overlay{ position: fixed; inset: 0; z-index: 2147483647; background: rgba(15,23,42,.5);
      display:flex; align-items:center; justify-content:center; pointer-events: auto; }
    .pf-modal{ background:#fff; border-radius: 14px; padding: 24px; width: 340px; max-width: 92vw; box-shadow: 0 20px 50px rgba(0,0,0,.3); }
    .pf-modal h2{ margin:0 0 6px; font-size: 18px; color:#0f172a; }
    .pf-modal p{ margin:0 0 14px; font-size: 13px; color:#64748b; }
    .pf-modal-error{ color:#b91c1c; font-size: 12px; margin: 0 0 10px; min-height: 16px; }

    .pf-toast{ position: fixed; bottom: 20px; left: 50%; transform: translateX(-50%); z-index: 2147483647;
      background:#0f172a; color:#fff; padding: 10px 16px; border-radius: 8px; font-size: 13px; box-shadow: 0 8px 24px rgba(0,0,0,.3); pointer-events: none; }
    .pf-toast.error{ background:#b91c1c; }
    .pf-toast.success{ background:#16a34a; }
  `;

  class PointerFeedback extends HTMLElement {
    connectedCallback() {
      if (this._mounted) return;
      this._mounted = true;

      this.project = this.getAttribute('project');
      this.environmentAttr = this.getAttribute('environment') || '';
      this.sourceAttr = this.getAttribute('source-attr') || 'data-component-source';
      this.server = (this.getAttribute('server') ||
        (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, '');

      // Resolve environment int from attribute string (default Staging = 2)
      this.environmentInt = ENV_MAP[this.environmentAttr.toLowerCase()] || 2;

      this.comments = [];
      this.statusFilter = 'all';
      this.picking = false;
      this.sidebarOpen = false;
      this.hovered = null;

      // Load persisted auth
      this._loadAuth();

      // Host element must not block page clicks; only inner panels are interactive.
      this.style.position = 'fixed';
      this.style.zIndex = '2147483647';
      this.style.top = '0';
      this.style.left = '0';
      this.style.pointerEvents = 'none';

      this.attachShadow({ mode: 'open' });
      const style = document.createElement('style');
      style.textContent = STYLES;
      this.shadowRoot.appendChild(style);
      this.root = document.createElement('div');
      this.shadowRoot.appendChild(this.root);

      ensureHighlightStyle();

      if (!this.project) {
        console.error('[pointer-feedback] Missing required `project` attribute. Component disabled.');
        return;
      }

      this._onHover = this.onHover.bind(this);
      this._onPick = this.onPick.bind(this);
      this._reposition = () => this.renderPins();
      window.addEventListener('scroll', this._reposition, true);
      window.addEventListener('resize', this._reposition);

      if (!this.token) this.showLoginModal();
      else this.init();
    }

    disconnectedCallback() {
      window.removeEventListener('scroll', this._reposition, true);
      window.removeEventListener('resize', this._reposition);
      this.stopPicking();
    }

    // --- Auth helpers --------------------------------------------------------
    _loadAuth() {
      this.token = localStorage.getItem('pointer_token') || null;
      try {
        const raw = localStorage.getItem('pointer_user');
        this.user = raw ? JSON.parse(raw) : null;
      } catch (e) {
        this.user = null;
      }
    }

    _saveAuth(token, user) {
      this.token = token;
      this.user = user;
      localStorage.setItem('pointer_token', token);
      localStorage.setItem('pointer_user', JSON.stringify(user));
    }

    _clearAuth() {
      this.token = null;
      this.user = null;
      localStorage.removeItem('pointer_token');
      localStorage.removeItem('pointer_user');
    }

    _handle401() {
      this._clearAuth();
      this.showLoginModal();
    }

    async init() {
      this.renderChrome();
      await this.fetchComments();
      this.renderSidebar();
      this.renderPins();
    }

    // --- API ----------------------------------------------------------------
    // Login has its own method (no Bearer header, different path).
    // All other calls go through api() which injects the Bearer token and
    // handles 401 by clearing auth and opening the login modal.

    async apiLogin(email, password) {
      const r = await fetch(`${this.server}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password })
      });
      return r;
    }

    api(path, opts = {}) {
      const headers = {
        'Content-Type': 'application/json',
        ...(this.token ? { 'Authorization': `Bearer ${this.token}` } : {}),
        ...(opts.headers || {})
      };
      return fetch(`${this.server}${path}`, { ...opts, headers })
        .then(r => {
          if (r.status === 401) {
            this._handle401();
            throw new Error('HTTP 401 Unauthorized');
          }
          return r;
        });
    }

    async fetchComments() {
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments?environment=${this.environmentInt}`);
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const envelope = await r.json();
        const items = (envelope.data && envelope.data.items) || [];
        // Normalize int status → string for internal UI use
        this.comments = items.map(c => ({
          ...c,
          status: STATUS_STR[c.status] || 'open'
        }));
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') {
          this.toast('Could not reach Pointer server', 'error');
        }
        this.comments = [];
      }
    }

    // All comments returned from the list endpoint belong to the project
    // (no page_url filtering in the new API — show all project comments).
    pageComments() {
      return this.comments;
    }

    // --- Identity / Login modal ----------------------------------------------
    showLoginModal(afterLogin) {
      // afterLogin: optional callback to run after successful login
      this._afterLogin = afterLogin || null;
      this.root.innerHTML = `
        <div class="pf-modal-overlay">
          <div class="pf-modal">
            <h2>Pointer</h2>
            <p>Sign in to leave feedback on <b>${escapeHtml(this.project)}</b>.</p>
            <input class="pf-input" id="pf-email" type="email" placeholder="Email" style="margin-bottom:8px;" />
            <input class="pf-input" id="pf-password" type="password" placeholder="Password" style="margin-bottom:8px;" />
            <div class="pf-modal-error" id="pf-login-error"></div>
            <button class="pf-btn primary" id="pf-login-submit" style="width:100%; justify-content:center;">Sign in</button>
          </div>
        </div>`;

      const emailEl = this.root.querySelector('#pf-email');
      const passEl = this.root.querySelector('#pf-password');
      const errEl = this.root.querySelector('#pf-login-error');
      const submitBtn = this.root.querySelector('#pf-login-submit');

      const doLogin = async () => {
        const email = emailEl.value.trim();
        const password = passEl.value;
        if (!email) { errEl.textContent = 'Please enter your email.'; return; }
        if (!password) { errEl.textContent = 'Please enter your password.'; return; }
        errEl.textContent = '';
        submitBtn.disabled = true;
        submitBtn.textContent = 'Signing in…';
        try {
          const r = await this.apiLogin(email, password);
          const envelope = await r.json();
          if (!r.ok || !envelope.isSuccess) {
            errEl.textContent = envelope.message || 'Invalid email or password.';
            submitBtn.disabled = false;
            submitBtn.textContent = 'Sign in';
            return;
          }
          this._saveAuth(envelope.data.token, envelope.data.user);
          this.root.innerHTML = '';
          if (this._afterLogin) {
            const cb = this._afterLogin;
            this._afterLogin = null;
            cb();
          } else {
            this.init();
          }
        } catch (e) {
          errEl.textContent = 'Network error. Please try again.';
          submitBtn.disabled = false;
          submitBtn.textContent = 'Sign in';
        }
      };

      submitBtn.addEventListener('click', doLogin);
      passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin(); });
    }

    // --- Chrome (toolbar + sidebar shell) -----------------------------------
    renderChrome() {
      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
      const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
      this.root.innerHTML = `
        <div class="pf-toolbar">
          <button class="pf-btn primary" id="pf-add">+ Comment</button>
          <button class="pf-btn" id="pf-toggle">Comments <span class="pf-badge" id="pf-count">0</span></button>
          <button class="pf-btn" id="pf-refresh" title="Refresh comments">&#8635;</button>
          ${displayName ? `<span class="pf-btn" style="cursor:default;" title="${roleLabel}">${displayName}</span>` : ''}
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
        <div id="pf-popover-host"></div>`;

      this.root.querySelector('#pf-add').addEventListener('click', () => {
        if (!this.token) {
          this.showLoginModal(() => { this.init().then(() => this.togglePicking()); });
          return;
        }
        this.togglePicking();
      });
      this.root.querySelector('#pf-toggle').addEventListener('click', () => this.toggleSidebar());
      this.root.querySelector('#pf-refresh').addEventListener('click', async () => {
        await this.fetchComments(); this.renderSidebar(); this.renderPins(); this.toast('Refreshed');
      });
      this.root.querySelector('#pf-close').addEventListener('click', () => this.toggleSidebar(false));
    }

    toggleSidebar(force) {
      this.sidebarOpen = force === undefined ? !this.sidebarOpen : force;
      this.root.querySelector('#pf-sidebar').classList.toggle('open', this.sidebarOpen);
      // Opening → pull fresh server state so applied/"completed" comments show
      // even if they were applied by the AI while this page was open.
      if (this.sidebarOpen) {
        this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
      }
    }

    // --- Element picking -----------------------------------------------------
    togglePicking() {
      this.picking ? this.stopPicking() : this.startPicking();
    }
    startPicking() {
      this.picking = true;
      this.root.querySelector('#pf-add').classList.add('active');
      this.root.querySelector('#pf-add').textContent = '✕ Cancel';
      document.addEventListener('mousemove', this._onHover, true);
      document.addEventListener('click', this._onPick, true);
      this.toast('Click any element to comment on it');
    }
    stopPicking() {
      this.picking = false;
      const addBtn = this.root && this.root.querySelector('#pf-add');
      if (addBtn) { addBtn.classList.remove('active'); addBtn.textContent = '+ Comment'; }
      document.removeEventListener('mousemove', this._onHover, true);
      document.removeEventListener('click', this._onPick, true);
      this.clearHover();
    }
    clearHover() {
      if (this.hovered) { this.hovered.classList.remove(HL_CLASS); this.hovered = null; }
    }
    isOwnElement(el) { return el === this || (el && el.tagName === 'POINTER-FEEDBACK'); }

    onHover(e) {
      const el = e.target;
      if (this.isOwnElement(el)) return;
      if (el === this.hovered) return;
      this.clearHover();
      this.hovered = el;
      el.classList.add(HL_CLASS);
    }
    onPick(e) {
      if (this.isOwnElement(e.target)) return; // clicks on our own UI pass through
      e.preventDefault();
      e.stopPropagation();
      const el = e.target;
      this.clearHover();
      this.stopPicking();
      this.showPopover(e.clientX, e.clientY, el);
    }

    // --- Metadata capture (ported from inject.js) ---------------------------
    captureMetadata(el) {
      const selector = generateSelector(el);
      const snapshot = el.outerHTML.length > 2000 ? el.outerHTML.slice(0, 2000) : el.outerHTML;
      const classes = (el.className && typeof el.className === 'string')
        ? el.className.split(/\s+/).filter(Boolean) : [];

      const computed = {};
      const applied = [];
      const cs = window.getComputedStyle(el);
      ['color', 'background-color', 'font-size', 'font-weight', 'margin', 'padding', 'border', 'text-align', 'display', 'flex-direction']
        .forEach(p => { const v = cs.getPropertyValue(p); if (v) computed[p] = v.trim(); });
      if (el.style && el.style.cssText) computed['inline-style'] = el.style.cssText;

      for (const sheet of Array.from(document.styleSheets)) {
        let rules;
        try { rules = sheet.cssRules || sheet.rules; } catch (e) { continue; } // cross-origin
        if (!rules) continue;
        for (const rule of Array.from(rules)) {
          if (!rule.selectorText) continue;
          try { if (el.matches(rule.selectorText)) applied.push({ selector: rule.selectorText, styles: rule.style.cssText }); } catch (e) {}
        }
      }

      let parent = {};
      if (el.parentElement) {
        const p = el.parentElement;
        parent = {
          tag: p.tagName.toLowerCase(),
          classes: (p.className && typeof p.className === 'string') ? p.className.split(/\s+/).filter(Boolean) : [],
          id: p.id || null
        };
      }

      // Source path: nearest ancestor carrying the configured attribute (e.g. data-component-source).
      let sourcePath = null;
      let node = el;
      while (node && node.getAttribute) {
        const v = node.getAttribute(this.sourceAttr);
        if (v) { sourcePath = v; break; }
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

    // --- Comment popover -----------------------------------------------------
    showPopover(x, y, el) {
      const meta = this.captureMetadata(el);
      const host = this.root.querySelector('#pf-popover-host');
      const left = Math.min(x, window.innerWidth - 300);
      const top = Math.min(y, window.innerHeight - 220);
      host.innerHTML = `
        <div class="pf-popover" style="left:${left}px; top:${top}px;">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ''}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          <div class="pf-reply-row">
            <button class="pf-btn primary" id="pf-submit" style="flex:1; justify-content:center;">Add</button>
            <button class="pf-mini" id="pf-cancel">Cancel</button>
          </div>
        </div>`;
      const ta = host.querySelector('#pf-comment-text');
      ta.focus();
      host.querySelector('#pf-cancel').addEventListener('click', () => { host.innerHTML = ''; });
      host.querySelector('#pf-submit').addEventListener('click', async () => {
        const text = ta.value.trim();
        if (!text) return this.toast('Comment cannot be empty', 'error');
        host.innerHTML = '';
        await this.createComment({ ...meta, text });
      });
    }

    async createComment(data) {
      const body = {
        body: data.text,
        environment: this.environmentInt,
        element: {
          selector: data.selector,
          snapshot: data.snapshot,
          classes: data.classes,
          computedStyles: data.computedStyles,
          appliedCssRules: data.appliedCssRules,
          sourcePath: data.sourcePath,
          parentInfo: data.parentInfo
        }
      };
      try {
        const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments`, {
          method: 'POST',
          body: JSON.stringify(body)
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const envelope = await r.json();
        const comment = envelope.data;
        if (comment) {
          this.comments.push({ ...comment, status: STATUS_STR[comment.status] || 'open' });
        }
        this.renderSidebar();
        this.renderPins();
        this.toast('Comment added', 'success');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') {
          this.toast('Failed to save comment', 'error');
        }
      }
    }

    // --- Mutations -----------------------------------------------------------
    async addReply(id, text) {
      try {
        const r = await this.api(`/api/comments/${id}/replies`, {
          method: 'POST',
          body: JSON.stringify({ body: text })
        });
        if (!r.ok) throw new Error();
        await this.fetchComments(); this.renderSidebar(); this.renderPins();
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast('Failed to reply', 'error');
      }
    }

    async toggleApply(comment) {
      // Cycle: open (1) → pending-apply (2); pending-apply (2) → open (1)
      const nextStr = comment.status === 'pending-apply' ? 'open' : 'pending-apply';
      const nextInt = STATUS_INT[nextStr];
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ status: nextInt })
        });
        if (!r.ok) throw new Error();
        comment.status = nextStr;
        this.renderSidebar(); this.renderPins();
        this.toast(nextStr === 'pending-apply' ? 'Marked for apply' : 'Unmarked');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
      }
    }

    // --- Sidebar render ------------------------------------------------------
    renderSidebar() {
      const all = this.pageComments();
      const counts = {
        all: all.length,
        open: all.filter((c) => c.status === 'open').length,
        'pending-apply': all.filter((c) => c.status === 'pending-apply').length,
        applied: all.filter((c) => c.status === 'applied').length,
      };

      const countEl = this.root.querySelector('#pf-count');
      if (countEl) countEl.textContent = all.length;

      // Status filter chips
      const filtersEl = this.root.querySelector('#pf-filters');
      if (filtersEl) {
        filtersEl.innerHTML = FILTERS.map((f) =>
          `<button class="pf-chip ${this.statusFilter === f.key ? 'active' : ''} chip-${STATUS_LABEL[f.key] || 'all'}" data-filter="${f.key}">
             ${f.label} <span class="pf-chip-n">${counts[f.key]}</span>
           </button>`).join('');
        filtersEl.querySelectorAll('[data-filter]').forEach((b) =>
          b.addEventListener('click', () => { this.statusFilter = b.dataset.filter; this.renderSidebar(); }));
      }

      const list = this.root.querySelector('#pf-list');
      if (!list) return;

      const shown = this.statusFilter === 'all' ? all : all.filter((c) => c.status === this.statusFilter);
      if (!all.length) {
        list.innerHTML = `<div class="pf-empty">No comments on this project yet.<br/>Click "+ Comment", then click an element.</div>`;
        return;
      }
      if (!shown.length) {
        list.innerHTML = `<div class="pf-empty">No ${FILTERS.find((f) => f.key === this.statusFilter).label.toLowerCase()} comments.</div>`;
        return;
      }

      list.innerHTML = shown.map((c, i) => {
        const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : '';
        const statusPill = c.status === 'applied'
          ? '<span class="pf-pill status-applied">&#x2713; completed</span>'
          : c.status === 'pending-apply' ? '<span class="pf-pill status-pending">pending</span>' : '';
        // Replies: from GET /api/comments/{id} response; list items may not include them
        const replies = (c.replies || []).map(r =>
          `<div class="pf-reply ${r.isAi ? 'ai' : ''}"><b>${escapeHtml(r.authorLabel || r.authorId || 'User')}:</b> ${escapeHtml(r.body || r.text || '')}</div>`).join('');
        // Environment label
        const envInt = c.environment;
        const envLabel = envInt === 1 ? 'Local' : envInt === 2 ? 'Staging' : envInt === 3 ? 'Production' : (envInt ? String(envInt) : '');
        // Author label from appliedByLabel or authorId
        const authorLabel = c.appliedByLabel || c.authorId || '';
        return `
          <div class="pf-card ${cls}" data-id="${c.id}">
            <div class="pf-meta">
              <span class="pf-badge">${i + 1}</span>
              ${envLabel ? `<span class="pf-pill env">${escapeHtml(envLabel)}</span>` : ''}
              ${statusPill}
            </div>
            <div class="pf-text">${escapeHtml(c.body || c.text || '')}</div>
            <div class="pf-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ''}</div>
            ${replies ? `<div class="pf-replies">${replies}</div>` : ''}
            <div class="pf-reply-row">
              <input class="pf-input pf-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="pf-actions">
              <button class="pf-mini ${c.status === 'pending-apply' ? 'apply' : c.status === 'applied' ? 'applied' : ''}" data-act="apply" data-id="${c.id}" ${c.status === 'applied' ? 'disabled' : ''}>
                ${c.status === 'applied' ? '&#x2713; Completed' : c.status === 'pending-apply' ? '&#x23F3; Pending &#x2014; unmark' : 'Ready to Apply'}
              </button>
            </div>
          </div>`;
      }).join('');

      list.querySelectorAll('[data-act="apply"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c && c.status !== 'applied') this.toggleApply(c);
      }));
      list.querySelectorAll('.pf-reply-input').forEach(inp => inp.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && inp.value.trim()) { this.addReply(inp.dataset.id, inp.value.trim()); inp.value = ''; }
      }));
    }

    // --- Pins ----------------------------------------------------------------
    renderPins() {
      const wrap = this.root && this.root.querySelector('#pf-pins');
      if (!wrap) return;
      const here = this.pageComments();
      wrap.innerHTML = here.map((c, i) => {
        const el = matchElement(c);
        if (!el) return '';
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) return '';
        const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : '';
        return `<div class="pf-pin ${cls}" data-id="${c.id}" style="left:${rect.left}px; top:${rect.top}px;"><span>${i + 1}</span></div>`;
      }).join('');
      wrap.querySelectorAll('.pf-pin').forEach(p => p.addEventListener('click', () => {
        this.toggleSidebar(true);
        const card = this.root.querySelector(`.pf-card[data-id="${p.dataset.id}"]`);
        if (card) card.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }));
    }

    // --- Toast ---------------------------------------------------------------
    toast(msg, type = '') {
      const t = document.createElement('div');
      t.className = `pf-toast ${type}`;
      t.textContent = msg;
      this.root.appendChild(t);
      setTimeout(() => t.remove(), 2200);
    }
  }

  customElements.define('pointer-feedback', PointerFeedback);
})();
