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
 *      launcher-position="bottom-end"      // optional: top-start | top-end | bottom-start | bottom-end (collapsed launcher corner)
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

  // Status int → string mapping (API contract: 1=Open, 2=ReadyToApply, 3=Applied, 4=Archived)
  const STATUS_STR = { 1: 'open', 2: 'pending-apply', 3: 'applied', 4: 'archived' };
  const STATUS_INT = { 'open': 1, 'pending-apply': 2, 'applied': 3, 'archived': 4 };

  // Inline SVG icons (monochrome, inherit currentColor) for compact card actions.
  const ICON = {
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
  };

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
  const STATUS_LABEL = { 'open': 'open', 'pending-apply': 'pending', 'applied': 'completed', 'archived': 'archived' };
  const FILTERS = [
    { key: 'all', label: 'All' },
    { key: 'open', label: 'Open' },
    { key: 'pending-apply', label: 'Pending' },
    { key: 'applied', label: 'Completed' },
    { key: 'archived', label: 'Archived' },
  ];

  // --- Styles: loaded at runtime from pointer.css (sibling of this script) ---
  // Kept in a separate .css file so styles can be edited directly (with editor
  // tooling) without touching this JS. Loaded via <link> inside the shadow root
  // — deliberately NOT fetch(): a <link> is not subject to CORS, so it works
  // cross-origin (host app → Pointer server) even though static files aren't
  // served with CORS headers. To change styles: edit pointer.css and refresh —
  // no build step.
  const CSS_URL = SCRIPT_SRC ? new URL('pointer.css', SCRIPT_SRC).href : 'pointer.css';

  // --- Screenshot capture library (lazy-loaded) ------------------------------
  // We vendor snapdom (https://github.com/zumerlab/snapdom, MIT) under
  // vendor/snapdom.js and self-host it — never a CDN, so it works offline and
  // under strict host-app CSPs. The UMD build assigns `window.snapdom`. It is
  // injected ONLY when a capture actually happens (see _loadSnapdom): a normal
  // page load never pulls it in. snapdom is a single ~123KB no-dependency file
  // and is markedly faster/smaller than html2canvas while reconstructing the
  // DOM with high fidelity.
  const SNAPDOM_URL = SCRIPT_SRC ? new URL('vendor/snapdom.js', SCRIPT_SRC).href : 'vendor/snapdom.js';
  const SHOT_MAX_WIDTH = 1280;   // downscale cap for the exported screenshot
  const SHOT_HIGHLIGHT = '#2563eb';

  // --- Templates: all component markup lives here (pure string builders) ------
  // Edit these to change the UI's HTML. Event wiring stays in the class methods,
  // which call these and then attach listeners to the rendered nodes. Values
  // interpolated here are pre-escaped by callers via escapeHtml where needed.
  const TPL = {
    // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
    // shell. showLoginModal() renders the shell once and then swaps #pf-auth-body
    // between TPL.loginBody and TPL.signupBody. The shell keeps the Skip control
    // so deferred-login dismissal works from either view.
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
        </div>` : ''}
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
          ${displayName ? `<button class="pf-btn pf-icon-btn" id="pf-user" title="Signed in as ${displayName}${roleLabel ? ' · ' + roleLabel : ''}" aria-label="Signed in as ${displayName}">${ICON.user}</button>` : ''}
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
        <div id="pf-popover-host"></div>`,

    // Collapsed state: a small floating launcher that re-opens the overlay.
    // `rtl` makes start/end resolve against the host page direction (the shadow
    // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
    launcher: (count, position, rtl) => `
        <button class="pf-launcher pf-pos-${position || 'bottom-end'}${rtl ? ' pf-rtl' : ''}" id="pf-launcher" title="Open Pointer feedback" aria-label="Open Pointer feedback">
          ${ICON.pin}
          ${count ? `<span class="pf-launcher-badge">${count > 99 ? '99+' : count}</span>` : ''}
        </button>`,

    empty: (msg) => `<div class="pf-empty">${msg}</div>`,

    filterChip: (f, active, count) =>
      `<button class="pf-chip ${active ? 'active' : ''} chip-${STATUS_LABEL[f.key] || 'all'}" data-filter="${f.key}">
             ${f.label} <span class="pf-chip-n">${count}</span>
           </button>`,

    // "Mine only" toggle — a chip that composes with the status chips above.
    // Rendered only when a user is logged in.
    mineToggle: (active) =>
      `<button class="pf-chip pf-mine ${active ? 'active' : ''}" id="pf-mine-toggle" title="Show only my comments" aria-pressed="${active ? 'true' : 'false'}">
             &#x1f464; Mine only
           </button>`,

    // User filter — only rendered when the list has comments from >1 author.
    authorFilter: (authors, selectedId) =>
      `<select class="pf-userfilter" id="pf-author-filter" title="Filter by user">
             <option value="">&#x1f465; All users</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? 'selected' : ''}>${escapeHtml(a.name)}</option>`).join('')}
           </select>`,

    card: (c, i) => {
      const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : '';
      const statusPill = c.status === 'applied'
        ? '<span class="pf-pill status-applied">&#x2713; completed</span>'
        : c.status === 'pending-apply' ? '<span class="pf-pill status-pending">pending</span>'
        : c.status === 'archived' ? '<span class="pf-pill status-archived">&#x1f4e6; archived</span>' : '';
      const replies = (c.replies || []).map(r =>
        `<div class="pf-reply ${r.isAi ? 'ai' : ''}"><b>${escapeHtml(r.authorName || r.authorLabel || 'User')}:</b> ${escapeHtml(r.body || r.text || '')}</div>`).join('');
      const envInt = c.environment;
      const envLabel = envInt === 1 ? 'Local' : envInt === 2 ? 'Staging' : envInt === 3 ? 'Production' : (envInt ? String(envInt) : '');
      const authorLabel = c.authorName || '';
      const shotUrl = c.element && c.element.screenshotUrl;
      const shot = shotUrl
        ? `<a class="pf-shot-link" href="${escapeHtml(shotUrl)}" target="_blank" rel="noopener noreferrer" title="Open full screenshot">
            <img class="pf-shot" src="${escapeHtml(shotUrl)}" alt="Element screenshot" loading="lazy" />
          </a>`
        : '';
      return `
          <div class="pf-card ${cls}" data-id="${c.id}">
            <div class="pf-meta">
              <span class="pf-badge">${i + 1}</span>
              ${envLabel ? `<span class="pf-pill env">${escapeHtml(envLabel)}</span>` : ''}
              ${statusPill}
            </div>
            <div class="pf-text">${escapeHtml(c.body || c.text || '')}</div>
            ${shot}
            <div class="pf-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ''}${c.editedAt ? ' &middot; <span style="font-style:italic;">edited</span>' : ''}</div>
            ${replies ? `<div class="pf-replies">${replies}</div>` : ''}
            <div class="pf-reply-row">
              <input class="pf-input pf-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="pf-actions">
              ${(c.status === 'applied' || c.status === 'archived') ? '' : `<button class="pf-mini ${c.status === 'pending-apply' ? 'apply' : 'ready'}" data-act="apply" data-id="${c.id}" title="${c.status === 'pending-apply' ? 'Marked ready — click to unmark' : 'Mark ready to apply'}">
                ${ICON.flag}<span>Ready</span>
              </button>`}
              ${(c.status === 'open' || c.status === 'pending-apply') ? `<button class="pf-mini done pf-icon" data-act="complete" data-id="${c.id}" title="Mark completed" aria-label="Mark completed">${ICON.check}</button>` : ''}
              ${c.status === 'applied' ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>
              <button class="pf-mini pf-icon" data-act="archive" data-id="${c.id}" title="Archive" aria-label="Archive">${ICON.archive}</button>` : ''}
              ${c.status === 'archived' ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>` : ''}
              <div class="pf-actions-end">
                ${c._mine ? `<button class="pf-mini pf-icon${c.isPrivate ? ' private-on' : ''}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? 'false' : 'true'}" title="${c.isPrivate ? 'Private — click to make public' : 'Make private (only you)'}" aria-label="${c.isPrivate ? 'Make public' : 'Make private'}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ''}
                ${c._mine ? `<button class="pf-mini pf-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button>` : ''}
                ${c.status === 'open' ? `<button class="pf-mini danger pf-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ''}
              </div>
            </div>
          </div>`;
    },

    popover: (meta, left, top, shotEnabled) => `
        <div class="pf-popover" style="left:${left}px; top:${top}px;">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ''}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          ${shotEnabled ? `<label class="pf-check"><input type="checkbox" id="pf-comment-shot" checked /> &#x1f4f7; Attach screenshot</label>` : ''}
          <label class="pf-check"><input type="checkbox" id="pf-comment-private" /> &#x1f512; Keep private — only me</label>
          <div class="pf-reply-row">
            <button class="pf-btn primary" id="pf-submit" style="flex:1; justify-content:center;">Add</button>
            <button class="pf-mini" id="pf-cancel">Cancel</button>
          </div>
        </div>`,

    pin: (c, i, rect) => {
      const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : '';
      return `<div class="pf-pin ${cls}" data-id="${c.id}" style="left:${rect.left}px; top:${rect.top}px;"><span>${i + 1}</span></div>`;
    },
  };

  class PointerFeedback extends HTMLElement {
    connectedCallback() {
      if (this._mounted) return;
      this._mounted = true;

      this.project = this.getAttribute('project');
      this.environmentAttr = this.getAttribute('environment') || '';
      this.sourceAttr = this.getAttribute('source-attr') || 'data-component-source';
      // Screenshot capture is ON by default; opt out with screenshot="false".
      this.screenshotEnabled = (this.getAttribute('screenshot') || '').toLowerCase() !== 'false';
      // Collapsed-launcher corner: top-start | top-end | bottom-start | bottom-end (default bottom-end).
      const POSITIONS = ['top-start', 'top-end', 'bottom-start', 'bottom-end'];
      const pos = (this.getAttribute('launcher-position') || '').toLowerCase();
      this.launcherPosition = POSITIONS.includes(pos) ? pos : 'bottom-end';
      this.server = (this.getAttribute('server') ||
        (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, '');

      // Resolve environment int from attribute string (default Staging = 2)
      this.environmentInt = ENV_MAP[this.environmentAttr.toLowerCase()] || 2;

      this.comments = [];
      this.statusFilter = 'all';
      this.mineOnly = false;
      this.authorFilter = null;   // when set (an authorId), show only that user's comments
      this.hiddenPrivateCount = 0;
      // Whether the overlay is collapsed to a small launcher (persisted per browser).
      this._collapsed = (() => { try { return localStorage.getItem('pointer_hidden') === '1'; } catch (e) { return false; } })();
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
      // External stylesheet loaded into the shadow root (see _boot for the
      // load gate). Using <link> avoids CORS issues with cross-origin fetch.
      this._styleLink = document.createElement('link');
      this._styleLink.rel = 'stylesheet';
      this._styleLink.href = CSS_URL || `${this.server}/pointer.css`;
      this.shadowRoot.appendChild(this._styleLink);
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

      this._boot();
    }

    // Wait for the stylesheet to load, then render the first view. Gating the
    // first render on the stylesheet avoids a flash of unstyled UI. A short
    // timeout guarantees we never hang if the CSS is slow/unreachable.
    async _boot() {
      await this._stylesReady();
      // Login is deferred: on load just show the toolbar. The popup only appears
      // when the user acts (+ Comment / Comments) and there's no token yet.
      if (this.token) this.init();
      else this.renderChrome();
    }

    _stylesReady() {
      return new Promise((resolve) => {
        const link = this._styleLink;
        if (!link || link.sheet) return resolve();
        let done = false;
        const finish = () => { if (!done) { done = true; resolve(); } };
        link.addEventListener('load', finish, { once: true });
        link.addEventListener('error', finish, { once: true });
        setTimeout(finish, 1500);
      });
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
      // When collapsed, re-render so the launcher badge reflects the loaded count.
      if (this._collapsed) this.renderChrome();
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

    // Anonymous: fetch active non-admin roles for the signup / re-apply dropdowns.
    // Plain fetch (no Bearer) — the endpoint allows anonymous. Returns
    // [{ id, name }]; resolves [] on any failure (caller surfaces an error).
    async apiRoles() {
      const r = await fetch(`${this.server}/api/roles`, {
        headers: { 'Content-Type': 'application/json' }
      });
      const envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || 'Could not load roles.');
      return envelope.data || [];
    }

    // Anonymous: self-signup AND re-apply (one endpoint). No token returned.
    async apiRegister(body) {
      const r = await fetch(`${this.server}/api/auth/register`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
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
        // Count of others' private comments hidden from this viewer (server-side).
        this.hiddenPrivateCount = (envelope.data && Number(envelope.data.hiddenPrivateCount)) || 0;
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
        this.hiddenPrivateCount = 0;
      }
    }

    // All comments returned from the list endpoint belong to the project
    // (no page_url filtering in the new API — show all project comments).
    pageComments() {
      return this.comments;
    }

    // --- Identity / Auth modal -----------------------------------------------
    // One modal shell, two swappable bodies (sign-in / sign-up). The shell owns
    // the Skip control (deferred-login dismissal); renderLoginView /
    // renderSignupView fill #pf-auth-body and wire their own events.
    showLoginModal(afterLogin) {
      // afterLogin: optional callback to run after successful login
      this._afterLogin = afterLogin || null;
      this.root.innerHTML = TPL.loginModal(this.project);

      // Skip → dismiss the popup without logging in; restore the toolbar so the
      // user can come back to it later by clicking the tool again.
      const skipBtn = this.root.querySelector('#pf-login-skip');
      if (skipBtn) skipBtn.addEventListener('click', () => { this._afterLogin = null; this.renderChrome(); });

      this.renderLoginView();
    }

    // Populate a <select> with [{id,name}] roles fetched anonymously. Disables
    // the element while loading and on failure (shows a placeholder option).
    async _populateRoles(selectEl, errEl) {
      if (!selectEl) return;
      selectEl.disabled = true;
      selectEl.innerHTML = '<option value="">Loading roles…</option>';
      try {
        const roles = await this.apiRoles();
        if (!roles.length) {
          selectEl.innerHTML = '<option value="">No roles available</option>';
          return;
        }
        selectEl.innerHTML = roles.map(r =>
          `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}</option>`).join('');
        selectEl.disabled = false;
      } catch (e) {
        selectEl.innerHTML = '<option value="">Could not load roles</option>';
        if (errEl) errEl.textContent = e.message || 'Could not load roles.';
      }
    }

    // Finish a successful login: persist auth, clear the modal, run the deferred
    // callback (or init the full UI).
    _afterAuthOk(token, user) {
      this._saveAuth(token, user);
      this.root.innerHTML = '';
      if (this._afterLogin) {
        const cb = this._afterLogin;
        this._afterLogin = null;
        cb();
      } else {
        this.init();
      }
    }

    // --- Sign-in view --------------------------------------------------------
    renderLoginView(opts = {}) {
      // opts.rejected → render the re-apply block (role select + Request again).
      const body = this.root.querySelector('#pf-auth-body');
      if (!body) return;
      body.innerHTML = TPL.loginBody(!!opts.rejected);

      const emailEl = body.querySelector('#pf-email');
      const passEl = body.querySelector('#pf-password');
      const errEl = body.querySelector('#pf-login-error');
      const submitBtn = body.querySelector('#pf-login-submit');

      const doLogin = async () => {
        const email = emailEl.value.trim();
        const password = passEl.value;
        if (!email) { errEl.textContent = 'Please enter your email.'; return; }
        if (!password) { errEl.textContent = 'Please enter your password.'; return; }
        errEl.textContent = '';
        submitBtn.disabled = true;
        submitBtn.textContent = 'Signing in…';
        const restore = () => { submitBtn.disabled = false; submitBtn.textContent = 'Sign in'; };
        try {
          const r = await this.apiLogin(email, password);
          const envelope = await r.json();
          const data = envelope.data || null;
          const status = data && data.status;
          if (status === 'ok' && data.token) {
            this._afterAuthOk(data.token, data.user);
            return;
          }
          if (status === 'pending') {
            errEl.textContent = envelope.message || 'Your request is awaiting admin approval.';
            restore();
            return;
          }
          if (status === 'disabled') {
            errEl.textContent = envelope.message || 'Your account is disabled.';
            restore();
            return;
          }
          if (status === 'rejected') {
            // Re-render the sign-in view with the re-apply block, preserving the
            // typed credentials so "Request again" can reuse them.
            this.renderLoginView({ rejected: true });
            const re = this.root.querySelector('#pf-auth-body');
            re.querySelector('#pf-email').value = email;
            re.querySelector('#pf-password').value = password;
            re.querySelector('#pf-login-error').textContent =
              envelope.message || 'Your request was rejected.';
            return;
          }
          // Missing/unknown status with failure → generic message.
          errEl.textContent = envelope.message || 'Invalid email or password.';
          restore();
        } catch (e) {
          errEl.textContent = 'Network error. Please try again.';
          restore();
        }
      };

      submitBtn.addEventListener('click', doLogin);
      passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doLogin(); });

      body.querySelector('#pf-show-signup').addEventListener('click', () => this.renderSignupView());

      // Rejected re-apply block: populate roles and wire "Request again".
      if (opts.rejected) {
        const roleEl = body.querySelector('#pf-reapply-role');
        const reBtn = body.querySelector('#pf-reapply-submit');
        this._populateRoles(roleEl, errEl);
        reBtn.addEventListener('click', async () => {
          const email = emailEl.value.trim();
          const password = passEl.value;
          const roleId = roleEl.value;
          if (!roleId) { errEl.textContent = 'Please choose a role.'; return; }
          if (!email || !password) { errEl.textContent = 'Enter your email and password to request again.'; return; }
          errEl.textContent = '';
          reBtn.disabled = true;
          reBtn.textContent = 'Submitting…';
          try {
            const r = await this.apiRegister({ email, password, displayName: '', roleId });
            const envelope = await r.json();
            if (!r.ok || !envelope.isSuccess) {
              errEl.textContent = envelope.message || 'Could not submit your request.';
              reBtn.disabled = false;
              reBtn.textContent = 'Request again';
              return;
            }
            // Success → collapse the re-apply block; show the submitted message.
            this.renderLoginView();
            this.root.querySelector('#pf-auth-body').querySelector('#pf-email').value = email;
            this.root.querySelector('#pf-auth-body').querySelector('#pf-login-error').textContent =
              envelope.message || 'Request submitted — an admin will review it.';
          } catch (e) {
            errEl.textContent = 'Network error. Please try again.';
            reBtn.disabled = false;
            reBtn.textContent = 'Request again';
          }
        });
      }
    }

    // --- Sign-up view --------------------------------------------------------
    renderSignupView() {
      const body = this.root.querySelector('#pf-auth-body');
      if (!body) return;
      body.innerHTML = TPL.signupBody();

      const nameEl = body.querySelector('#pf-su-name');
      const emailEl = body.querySelector('#pf-su-email');
      const passEl = body.querySelector('#pf-su-password');
      const roleEl = body.querySelector('#pf-su-role');
      const errEl = body.querySelector('#pf-signup-error');
      const okEl = body.querySelector('#pf-signup-success');
      const submitBtn = body.querySelector('#pf-signup-submit');

      // Populate the role <select> from /api/roles when the form opens.
      this._populateRoles(roleEl, errEl);

      body.querySelector('#pf-show-login').addEventListener('click', () => this.renderLoginView());

      const doSignup = async () => {
        const displayName = nameEl.value.trim();
        const email = emailEl.value.trim();
        const password = passEl.value;
        const roleId = roleEl.value;
        errEl.textContent = '';
        okEl.textContent = '';
        if (!displayName) { errEl.textContent = 'Please enter your name.'; return; }
        if (!email) { errEl.textContent = 'Please enter your email.'; return; }
        if (!password) { errEl.textContent = 'Please choose a password.'; return; }
        if (!roleId) { errEl.textContent = 'Please choose a role.'; return; }
        submitBtn.disabled = true;
        submitBtn.textContent = 'Submitting…';
        const restore = () => { submitBtn.disabled = false; submitBtn.textContent = 'Create account'; };
        try {
          const r = await this.apiRegister({ email, password, displayName, roleId });
          const envelope = await r.json();
          if (!r.ok || !envelope.isSuccess) {
            errEl.textContent = envelope.message || 'Could not create your account.';
            restore();
            return;
          }
          // Success: lock the form, show the inline message + a way back to sign in.
          okEl.textContent = envelope.message || 'Request submitted — an admin will review it.';
          submitBtn.textContent = 'Request submitted';
          submitBtn.disabled = true;
          [nameEl, emailEl, passEl, roleEl].forEach(el => { el.disabled = true; });
        } catch (e) {
          errEl.textContent = 'Network error. Please try again.';
          restore();
        }
      };

      submitBtn.addEventListener('click', doSignup);
      passEl.addEventListener('keydown', (e) => { if (e.key === 'Enter') doSignup(); });
    }

    // --- Chrome (toolbar + sidebar shell) -----------------------------------
    // True when the host page renders right-to-left. Read live (not cached) so
    // the launcher corner tracks a page that toggles direction at runtime.
    _pageIsRtl() {
      try {
        const html = document.documentElement;
        const attr = (html.getAttribute('dir') || document.body?.getAttribute('dir') || '').toLowerCase();
        if (attr === 'rtl' || attr === 'ltr') return attr === 'rtl';
        return getComputedStyle(html).direction === 'rtl';
      } catch (e) {
        return false;
      }
    }

    renderChrome() {
      // Collapsed: show only a small launcher that re-opens the overlay.
      if (this._collapsed) {
        const n = (this.comments || []).filter((c) => c.status !== 'archived').length;
        this.root.innerHTML = TPL.launcher(n, this.launcherPosition, this._pageIsRtl());
        const launcher = this.root.querySelector('#pf-launcher');
        if (launcher) launcher.addEventListener('click', () => this.showOverlay());
        return;
      }

      const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
      const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
      this.root.innerHTML = TPL.chrome(displayName, roleLabel);

      const hideBtn = this.root.querySelector('#pf-hide');
      if (hideBtn) hideBtn.addEventListener('click', () => this.hideOverlay());

      // User icon — placeholder action for now (identity is in the tooltip);
      // ready to grow into a profile/sign-out menu later.
      const userBtn = this.root.querySelector('#pf-user');
      if (userBtn) userBtn.addEventListener('click', () => {
        if (this.user) this.toast(`Signed in as ${this.user.displayName || this.user.email}${this.user.roleName ? ' · ' + this.user.roleName : ''}`);
      });

      this.root.querySelector('#pf-add').addEventListener('click', () => {
        if (!this.token) {
          this.showLoginModal(() => { this.init().then(() => this.togglePicking()); });
          return;
        }
        this.togglePicking();
      });
      this.root.querySelector('#pf-toggle').addEventListener('click', () => {
        if (!this.token) { this.showLoginModal(() => { this.init().then(() => this.toggleSidebar(true)); }); return; }
        this.toggleSidebar();
      });
      this.root.querySelector('#pf-refresh').addEventListener('click', async () => {
        if (!this.token) { this.showLoginModal(() => this.init()); return; }
        await this.fetchComments(); this.renderSidebar(); this.renderPins(); this.toast('Refreshed');
      });
      this.root.querySelector('#pf-close').addEventListener('click', () => this.toggleSidebar(false));
    }

    // Collapse the overlay to the floating launcher (persists across reloads).
    hideOverlay() {
      if (this.picking) this.stopPicking();
      this.sidebarOpen = false;
      this._collapsed = true;
      try { localStorage.setItem('pointer_hidden', '1'); } catch (e) {}
      this.renderChrome();
      this.toast('Pointer hidden — click the pin to reopen');
    }

    // Restore the full overlay from the launcher.
    showOverlay() {
      this._collapsed = false;
      try { localStorage.removeItem('pointer_hidden'); } catch (e) {}
      this.renderChrome();
      if (this.token) {
        this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
      }
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
      const x = e.clientX, y = e.clientY;
      this.clearHover();
      this.stopPicking();
      // Open the popover IMMEDIATELY — no waiting on the screenshot. Capture runs
      // in the background and is held as a promise; the submit handler attaches it
      // after the comment is created. The Pointer UI (popover included) lives in
      // the Shadow DOM, which snapdom excludes, so opening the popover first does
      // not put it in the shot. Capture is best-effort (resolves null on failure).
      this._pendingShotPromise = null;
      this.showPopover(x, y, el);
      if (this.screenshotEnabled) {
        this._pendingShotPromise = this.captureScreenshot(el).catch((err) => {
          console.warn('[pointer-feedback] screenshot capture failed', err);
          return null;
        });
      }
    }

    // --- Screenshot capture --------------------------------------------------
    // Lazily inject the vendored snapdom UMD build exactly once. Returns a
    // promise that resolves to the global capture fn. Self-hosted (no CDN), so
    // it survives offline use and strict CSPs on the host app.
    _loadSnapdom() {
      if (window.snapdom) return Promise.resolve(window.snapdom);
      if (this._snapdomPromise) return this._snapdomPromise;
      this._snapdomPromise = new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = SNAPDOM_URL;
        s.async = true;
        s.onload = () => window.snapdom
          ? resolve(window.snapdom)
          : reject(new Error('snapdom loaded but window.snapdom missing'));
        s.onerror = () => reject(new Error('failed to load ' + SNAPDOM_URL));
        document.head.appendChild(s);
      });
      return this._snapdomPromise;
    }

    // Capture the current viewport, draw a highlight over the picked element,
    // downscale to <= SHOT_MAX_WIDTH and export a WebP (JPEG fallback) Blob.
    // Resolves null on any failure — capture must never block commenting.
    async captureScreenshot(el) {
      try {
        const snapdom = await this._loadSnapdom();
        // snapdom reconstructs the DOM into an image; capturing <body> gives us
        // the rendered page. We then crop to the current viewport.
        const full = await snapdom.toCanvas(document.body, {
          backgroundColor: '#fff',
          // Skip our own shadow host entirely as a belt-and-braces measure.
          exclude: ['pointer-feedback'],
          fast: true
        });

        const dpr = window.devicePixelRatio || 1;
        // Map viewport (CSS px) → full-page canvas pixels. snapdom rasterizes at
        // dpr, and the body canvas origin aligns with the document origin, so
        // the scroll offset (×dpr) is where the viewport begins.
        const sx = Math.max(0, Math.round(window.scrollX * dpr));
        const sy = Math.max(0, Math.round(window.scrollY * dpr));
        const vw = Math.round(window.innerWidth * dpr);
        const vh = Math.round(window.innerHeight * dpr);
        const cw = Math.min(vw, full.width - sx);
        const ch = Math.min(vh, full.height - sy);

        // Crop to viewport.
        const view = document.createElement('canvas');
        view.width = Math.max(1, cw);
        view.height = Math.max(1, ch);
        const vctx = view.getContext('2d');
        vctx.drawImage(full, sx, sy, view.width, view.height, 0, 0, view.width, view.height);

        // Highlight the picked element at its viewport-relative rect.
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

        // Downscale so max width ~SHOT_MAX_WIDTH.
        let out = view;
        if (view.width > SHOT_MAX_WIDTH) {
          const scale = SHOT_MAX_WIDTH / view.width;
          const small = document.createElement('canvas');
          small.width = SHOT_MAX_WIDTH;
          small.height = Math.max(1, Math.round(view.height * scale));
          small.getContext('2d').drawImage(view, 0, 0, small.width, small.height);
          out = small;
        }

        return await this._canvasToBlob(out);
      } catch (err) {
        console.warn('[pointer-feedback] screenshot capture failed', err);
        return null;
      }
    }

    // Export a canvas to a WebP Blob, falling back to JPEG where unsupported.
    _canvasToBlob(canvas) {
      return new Promise((resolve) => {
        const done = (b) => resolve(b || null);
        try {
          canvas.toBlob((b) => {
            if (b) return done(b);
            // WebP unsupported (older Safari) → JPEG fallback.
            canvas.toBlob(done, 'image/jpeg', 0.6);
          }, 'image/webp', 0.6);
        } catch (e) {
          try { canvas.toBlob(done, 'image/jpeg', 0.6); } catch (e2) { done(null); }
        }
      });
    }

    // Upload a screenshot Blob to /api/uploads via multipart/form-data. Returns
    // the absolute URL on success, or null on failure (caller proceeds without
    // a screenshot). NOTE: deliberately NOT using api() — it forces
    // application/json; for FormData we must let the browser set the multipart
    // boundary itself.
    async uploadToServer(blob) {
      try {
        const ext = blob.type === 'image/jpeg' ? 'jpg' : 'webp';
        const fd = new FormData();
        fd.append('file', blob, `screenshot.${ext}`);
        fd.append('project', this.project);
        const r = await fetch(`${this.server}/api/uploads`, {
          method: 'POST',
          headers: { ...(this.token ? { 'Authorization': `Bearer ${this.token}` } : {}) },
          body: fd
        });
        if (r.status === 401) { this._handle401(); return null; }
        if (!r.ok) throw new Error('HTTP ' + r.status);
        const envelope = await r.json();
        if (!envelope || !envelope.isSuccess || !envelope.data || !envelope.data.url) {
          throw new Error('upload response missing data.url');
        }
        return envelope.data.url;
      } catch (err) {
        console.warn('[pointer-feedback] screenshot upload failed', err);
        return null;
      }
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
      host.innerHTML = TPL.popover(meta, left, top, this.screenshotEnabled);
      const ta = host.querySelector('#pf-comment-text');
      ta.focus();
      host.querySelector('#pf-cancel').addEventListener('click', () => { host.innerHTML = ''; this._pendingShotPromise = null; });
      host.querySelector('#pf-submit').addEventListener('click', async () => {
        const text = ta.value.trim();
        if (!text) return this.toast('Comment cannot be empty', 'error');
        const privateEl = host.querySelector('#pf-comment-private');
        const isPrivate = !!(privateEl && privateEl.checked);
        const shotEl = host.querySelector('#pf-comment-shot');
        const attachShot = !!(shotEl && shotEl.checked);   // toggle: on by default
        const shotPromise = this._pendingShotPromise;
        this._pendingShotPromise = null;
        const submitBtn = host.querySelector('#pf-submit');
        submitBtn.disabled = true; submitBtn.textContent = 'Saving…';
        await this.createComment({ ...meta, text, isPrivate, attachShot, shotPromise });
        host.innerHTML = '';
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

      // Screenshot is opt-in (toggle, on by default). When on, await the capture
      // that started on pick, upload it via /api/uploads, and put the returned
      // URL on the comment so it's created in one shot. When off, the whole
      // upload is skipped. A failed upload is non-fatal — comment still saves.
      if (data.attachShot && data.shotPromise) {
        const blob = await Promise.resolve(data.shotPromise).catch(() => null);
        if (blob) {
          const url = await this.uploadToServer(blob);
          if (url) element.screenshotUrl = url;
          else this.toast('Screenshot upload failed — saving without it', 'error');
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

    // Generic status change (used by completed-item actions: Re-open → open, Archive → archived).
    async setStatus(comment, nextStr, toastMsg) {
      const nextInt = STATUS_INT[nextStr];
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ status: nextInt })
        });
        if (!r.ok) throw new Error();
        comment.status = nextStr;
        this.renderSidebar(); this.renderPins();
        this.toast(toastMsg || 'Updated');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
      }
    }

    // Toggle a comment's privacy — author-only (enforced server-side too). A
    // private comment is visible only to its author (and their AI tool).
    async setVisibility(comment, isPrivate) {
      try {
        const r = await this.api(`/api/comments/${comment.id}/visibility`, {
          method: 'PATCH',
          body: JSON.stringify({ isPrivate })
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        comment.isPrivate = isPrivate;
        this.renderSidebar(); this.renderPins();
        this.toast(isPrivate ? 'Marked private' : 'Made public');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
      }
    }

    async markCompleted(comment) {
      // Mark done directly — for changes already applied outside Pointer.
      // PATCH status → Applied (3), stamping who marked it.
      const label = this.user ? (this.user.displayName || this.user.email) : null;
      try {
        const r = await this.api(`/api/comments/${comment.id}`, {
          method: 'PATCH',
          body: JSON.stringify({ status: STATUS_INT['applied'], appliedByLabel: label })
        });
        if (!r.ok) throw new Error('HTTP ' + r.status);
        comment.status = 'applied';
        if (label) comment.appliedByLabel = label;
        this.renderSidebar(); this.renderPins();
        this.toast('Marked completed');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
      }
    }

    async deleteComment(id) {
      // DELETE /api/comments/{id} — soft-deletes on the server (JWT-scoped).
      try {
        const r = await this.api(`/api/comments/${id}`, { method: 'DELETE' });
        if (!r.ok) {
          // Surface the API's reason (e.g. permission denied) instead of a generic error.
          const body = await r.json().catch(() => null);
          throw new Error((body && body.message) || ('HTTP ' + r.status));
        }
        this.comments = this.comments.filter(c => String(c.id) !== String(id));
        this.renderSidebar(); this.renderPins();
        this.toast('Deleted');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast(e.message || 'Delete failed', 'error');
      }
    }

    // Inline edit (own comments only): swap the body text for a textarea + controls.
    startEdit(id) {
      const card = this.root && this.root.querySelector(`.pf-card[data-id="${id}"]`);
      if (!card || card.querySelector('.pf-edit')) return;
      const comment = (this.comments || []).find(x => String(x.id) === String(id));
      if (!comment) return;
      const textEl = card.querySelector('.pf-text');
      if (!textEl) return;
      const hasShot = !!(comment.element && comment.element.screenshotUrl);
      const editor = document.createElement('div');
      editor.className = 'pf-edit';
      editor.style.margin = '6px 0';
      editor.innerHTML = `
        <textarea class="pf-textarea pf-edit-body">${escapeHtml(comment.body || '')}</textarea>
        ${hasShot ? `<label style="display:flex;gap:6px;align-items:center;font-size:12px;color:#475569;margin:6px 0;"><input type="checkbox" class="pf-edit-rmshot" /> Remove image</label>` : ''}
        <div class="pf-reply-row">
          <button class="pf-btn primary pf-edit-save" style="flex:1;justify-content:center;">Save</button>
          <button class="pf-mini pf-edit-cancel">Cancel</button>
        </div>`;
      textEl.style.display = 'none';
      textEl.insertAdjacentElement('afterend', editor);
      const ta = editor.querySelector('.pf-edit-body');
      ta.focus();
      editor.querySelector('.pf-edit-cancel').addEventListener('click', () => { editor.remove(); textEl.style.display = ''; });
      editor.querySelector('.pf-edit-save').addEventListener('click', () => {
        const body = ta.value.trim();
        if (!body) { this.toast('Comment cannot be empty', 'error'); return; }
        const removeScreenshot = !!(editor.querySelector('.pf-edit-rmshot') && editor.querySelector('.pf-edit-rmshot').checked);
        this.saveEdit(id, body, removeScreenshot);
      });
    }

    async saveEdit(id, body, removeScreenshot) {
      // PUT /api/comments/{id} — author-only edit (enforced server-side).
      try {
        const r = await this.api(`/api/comments/${id}`, {
          method: 'PUT',
          body: JSON.stringify({ body, removeScreenshot })
        });
        if (!r.ok) {
          const b = await r.json().catch(() => null);
          throw new Error((b && b.message) || ('HTTP ' + r.status));
        }
        await this.fetchComments();
        this.renderSidebar();
        this.renderPins();
        this.toast('Comment updated', 'success');
      } catch (e) {
        if (e.message !== 'HTTP 401 Unauthorized') this.toast(e.message || 'Failed to update comment', 'error');
      }
    }

    // True when comment `c` was authored by the current logged-in user.
    // Compared case-insensitively as strings (authorId vs user.id GUIDs).
    isMine(c) {
      const uid = this.user && this.user.id;
      if (!uid) return false;
      return String(c.authorId || '').toLowerCase() === String(uid).toLowerCase();
    }

    // Distinct comment authors in the current project list: [{ id, name }].
    distinctAuthors(comments) {
      const seen = new Set();
      const out = [];
      for (const c of comments) {
        const id = String(c.authorId || '');
        if (id && !seen.has(id)) { seen.add(id); out.push({ id, name: c.authorName || id }); }
      }
      return out;
    }

    // Apply the "who" filters in priority order: Mine wins; else a chosen author.
    scopeByWho(comments) {
      if (this.mineOnly) return comments.filter((c) => this.isMine(c));
      if (this.authorFilter) return comments.filter((c) => String(c.authorId || '') === this.authorFilter);
      return comments;
    }

    // --- Sidebar render ------------------------------------------------------
    renderSidebar() {
      const all = this.pageComments();
      // "Mine only" composes with the status chips: when on, every downstream
      // view (counts, list, empty-state) is restricted to the user's own comments.
      const canMine = !!(this.user && this.user.id);
      if (!canMine) this.mineOnly = false;
      const authors = this.distinctAuthors(all);
      // Drop a stale author selection (e.g. after refetch) so the filter can't get stuck.
      if (this.authorFilter && !authors.some((a) => a.id === this.authorFilter)) this.authorFilter = null;
      const scoped = this.scopeByWho(all);
      const counts = {
        // "All" means active (non-archived); archived are moved out to their own chip.
        all: scoped.filter((c) => c.status !== 'archived').length,
        open: scoped.filter((c) => c.status === 'open').length,
        'pending-apply': scoped.filter((c) => c.status === 'pending-apply').length,
        applied: scoped.filter((c) => c.status === 'applied').length,
        archived: scoped.filter((c) => c.status === 'archived').length,
      };

      const countEl = this.root.querySelector('#pf-count');
      if (countEl) countEl.textContent = all.filter((c) => c.status !== 'archived').length;

      // Status filter chips + (optional) "Mine only" toggle.
      const filtersEl = this.root.querySelector('#pf-filters');
      if (filtersEl) {
        filtersEl.innerHTML = FILTERS.map((f) =>
          TPL.filterChip(f, this.statusFilter === f.key, counts[f.key])).join('')
          + ((authors.length > 1 && !this.mineOnly) ? TPL.authorFilter(authors, this.authorFilter || '') : '')
          + (canMine ? TPL.mineToggle(this.mineOnly) : '');
        filtersEl.querySelectorAll('[data-filter]').forEach((b) =>
          b.addEventListener('click', () => { this.statusFilter = b.dataset.filter; this.renderSidebar(); }));
        const mineBtn = filtersEl.querySelector('#pf-mine-toggle');
        if (mineBtn) mineBtn.addEventListener('click', () => {
          this.mineOnly = !this.mineOnly; this.renderSidebar(); this.renderPins();
        });
        const authorSel = filtersEl.querySelector('#pf-author-filter');
        if (authorSel) authorSel.addEventListener('change', () => {
          this.authorFilter = authorSel.value || null;
          this.renderSidebar(); this.renderPins();
        });
      }

      const list = this.root.querySelector('#pf-list');
      if (!list) return;

      // "All" shows active comments only (archived live under their own chip).
      const shown = this.statusFilter === 'all'
        ? scoped.filter((c) => c.status !== 'archived')
        : scoped.filter((c) => c.status === this.statusFilter);
      if (!scoped.length) {
        list.innerHTML = TPL.empty(this.mineOnly
          ? 'You haven\'t left any comments yet.'
          : 'No comments on this project yet.<br/>Click "+ Comment", then click an element.');
        return;
      }
      if (!shown.length) {
        list.innerHTML = TPL.empty(`No ${FILTERS.find((f) => f.key === this.statusFilter).label.toLowerCase()} comments${this.mineOnly ? ' of yours' : ''}.`);
        return;
      }

      list.innerHTML = shown.map((c, i) => { c._mine = this.isMine(c); return TPL.card(c, i); }).join('');

      list.querySelectorAll('[data-act="apply"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c && c.status !== 'applied') this.toggleApply(c);
      }));
      list.querySelectorAll('[data-act="complete"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c && c.status !== 'applied') this.markCompleted(c);
      }));
      list.querySelectorAll('[data-act="delete"]').forEach(b => b.addEventListener('click', () => this.deleteComment(b.dataset.id)));
      list.querySelectorAll('[data-act="edit"]').forEach(b => b.addEventListener('click', () => this.startEdit(b.dataset.id)));
      list.querySelectorAll('[data-act="visibility"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c) this.setVisibility(c, b.dataset.private === 'true');
      }));
      list.querySelectorAll('[data-act="reopen"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c) this.setStatus(c, 'open', 'Re-opened');
      }));
      list.querySelectorAll('[data-act="archive"]').forEach(b => b.addEventListener('click', () => {
        const c = this.comments.find(x => String(x.id) === String(b.dataset.id));
        if (c) this.setStatus(c, 'archived', 'Archived');
      }));
      list.querySelectorAll('.pf-reply-input').forEach(inp => inp.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && inp.value.trim()) { this.addReply(inp.dataset.id, inp.value.trim()); inp.value = ''; }
      }));
    }

    // --- Pins ----------------------------------------------------------------
    renderPins() {
      const wrap = this.root && this.root.querySelector('#pf-pins');
      if (!wrap) return;
      const all = this.pageComments().filter((c) => c.status !== 'archived');
      const here = this.scopeByWho(all);
      wrap.innerHTML = here.map((c, i) => {
        const el = matchElement(c);
        if (!el) return '';
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 && rect.height === 0) return '';
        return TPL.pin(c, i, rect);
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
