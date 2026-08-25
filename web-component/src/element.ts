import {
  HL_CLASS, ENV_MAP, ENV_NAME, STATUS_STR, STATUS_INT, POSITIONS, CSS_URL, SCRIPT_SRC,
  loadStatusCatalog, catalogToFilters, pfFetch, loadBranding, getBrandName,
} from './constants';
import { escapeHtml, ensureHighlightStyle, matchElement, pageIsRtl } from './dom';
import { TPL } from './templates';
import { ICON } from './icons';
import { captureScreenshot, captureMetadata } from './capture';
import { startPageContextCapture, stopPageContextCapture, getPageContextPayload } from './pagecontext';
import {
  type ShortcutBinding, parseShortcut, serializeShortcut, matchesShortcut, formatShortcut,
} from './shortcut';
import { showLoginModal } from './auth-ui';
import type { AuthorOption, Comment, Meta, PointerHost, PredefinedActionOption, RoleOption, StatusStr, User } from './types';

interface CreateCommentData extends Meta {
  text: string;
  isPrivate: boolean;
  attachShot: boolean;
  shotPromise: Promise<Blob | null> | null;
  predefinedActionIds?: number[];
  isBugReport: boolean;
}

export class PointerFeedback extends HTMLElement implements PointerHost {
  private _mounted = false;
  project = '';
  environmentAttr = '';
  sourceAttr = 'data-component-source';
  screenshotEnabled = true;
  launcherPosition = 'bottom-end';
  server = '';
  environmentInt = 2;

  comments: Comment[] = [];
  statusFilter = 'all';
  mineOnly = false;
  authorFilter: string | null = null;
  hiddenPrivateCount = 0;
  private _collapsed = true;
  private _disabled = false;
  picking = false;
  sidebarOpen = false;
  hovered: Element | null = null;

  token: string | null = null;
  user: User | null = null;
  afterLogin: (() => void) | null = null;
  predefinedActions: PredefinedActionOption[] = [];
  // Whether the environment was explicitly fixed at install time (HTML attribute or injected
  // config) — when true, the toolbar shows a read-only label instead of a switcher.
  hasFixedEnvironment = false;
  // Project-level opt-in (default off), read once at init via /capture-config. Gates both whether
  // the widget buffers console/network events at all and whether "Report as a bug" is shown.
  pageContextCaptureEnabled = false;
  // Display name resolved from /capture-config (falls back to the raw `project` key attribute
  // until it loads). Shown next to the environment indicator so a visitor can immediately tell
  // which project an install is actually bound to — project keys aren't unique across a workspace.
  projectName = '';
  // Per-user "add comment" keyboard shortcut, synced to the account (User.AddCommentShortcut,
  // not localStorage) — set from `this.user.addCommentShortcut` in loadAuth()/saveAuth() below.
  // Default is Alt+Shift+C / Option+Shift+C — see shortcut.ts for why not Ctrl+Shift+C / Cmd+Option+C.
  shortcut: ShortcutBinding = parseShortcut(undefined);

  root!: HTMLElement;
  private _styleLink!: HTMLLinkElement;
  private _onHover!: (e: MouseEvent) => void;
  private _onPick!: (e: MouseEvent) => void;
  private _onPickKey!: (e: KeyboardEvent) => void;
  private _onShortcutKeydown!: (e: KeyboardEvent) => void;
  private _reposition!: () => void;
  private _pendingShotPromise: Promise<Blob | null> | null = null;
  private _userMenuClose: ((e: MouseEvent) => void) | null = null;
  private _recordingShortcut = false;
  private _shortcutRecordingCleanup: (() => void) | null = null;

  connectedCallback(): void {
    if (this._mounted) return;
    this._mounted = true;

    this.project = this.getAttribute('project') || '';
    this.environmentAttr = this.getAttribute('environment') || '';
    // Captured BEFORE the default-to-staging fallback below overwrites the "was it explicitly
    // set" signal — an injected environment (browser extension) also counts as fixed.
    this.hasFixedEnvironment = !!this.environmentAttr;
    this.sourceAttr = this.getAttribute('source-attr') || 'data-component-source';
    // Screenshot capture is available by default; opt out with screenshot="false".
    this.screenshotEnabled = (this.getAttribute('screenshot') || '').toLowerCase() !== 'false';
    // Collapsed-launcher corner: top-start | top-end | bottom-start | bottom-end (default bottom-end).
    const pos = (this.getAttribute('launcher-position') || '').toLowerCase();
    this.launcherPosition = (POSITIONS as readonly string[]).includes(pos) ? pos : 'bottom-end';
    this.server = (this.getAttribute('server') ||
      (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, '');

    // Resolve environment int from attribute string (default Staging = 2)
    this.environmentInt = ENV_MAP[this.environmentAttr.toLowerCase()] || 2;

    // Host-injected config (e.g. the browser extension) overrides attributes. Lets
    // a host set server/project/environment programmatically and, via `token`,
    // pre-authenticate so the widget skips its own login. Applied to the token
    // itself after loadAuth() below.
    const injected = typeof window !== 'undefined' ? window.__POINTER_CONFIG__ : undefined;
    if (injected) {
      if (injected.server) this.server = injected.server.replace(/\/$/, '');
      if (injected.project) this.project = injected.project;
      if (injected.environment) {
        this.environmentAttr = injected.environment;
        this.environmentInt = ENV_MAP[injected.environment.toLowerCase()] || this.environmentInt;
        this.hasFixedEnvironment = true;
      }
    }

    // The environment is switchable IN the widget (toolbar select) and remembered per project on
    // this origin — it takes precedence over the attribute/injected default so the viewer's choice
    // sticks across reloads. Comments are environment-scoped, so switching re-queries per env.
    // Skipped entirely when the environment is fixed at install time — a saved choice from before
    // the host added the attribute must never override it.
    if (!this.hasFixedEnvironment) {
      try {
        const savedEnv = localStorage.getItem('pointer_env_' + this.project);
        if (savedEnv && ENV_MAP[savedEnv.toLowerCase()]) {
          this.environmentAttr = savedEnv.toLowerCase();
          this.environmentInt = ENV_MAP[savedEnv.toLowerCase()];
        }
      } catch (e) { /* ignore */ }
    }
    // Normalize the display string so the toolbar select always has a matching option.
    if (!this.environmentAttr) this.environmentAttr = ENV_NAME[this.environmentInt] || 'staging';

    // Hidden by default on first load: collapsed to a small launcher until the user
    // opens it once, after which it stays shown for the rest of this browser-tab
    // session. State lives in sessionStorage (NOT localStorage / server) — the key is
    // set to '1' only after the user opens it, so a fresh tab always starts hidden.
    this._collapsed = (() => {
      try { return sessionStorage.getItem('pointer_visible') !== '1'; } catch (e) { return true; }
    })();

    // Load persisted auth, then let an injected token take precedence (the
    // extension logs in once and hands the widget a token, so it never shows
    // its own login on third-party pages).
    this.loadAuth();
    if (injected?.token) {
      this.token = injected.token;
      if (injected.user !== undefined) this.user = injected.user;
      this.shortcut = parseShortcut(this.user?.addCommentShortcut);
    }

    // Host element must not block page clicks; only inner panels are interactive.
    this.style.position = 'fixed';
    this.style.zIndex = '2147483647';
    this.style.top = '0';
    this.style.left = '0';
    this.style.pointerEvents = 'none';

    this.attachShadow({ mode: 'open' });
    // External stylesheet loaded into the shadow root. Using <link> avoids CORS
    // issues with cross-origin fetch.
    this._styleLink = document.createElement('link');
    this._styleLink.rel = 'stylesheet';
    // A host may bundle the CSS (extension) and pass its URL; otherwise resolve from the script's
    // own origin, falling back to the API server.
    this._styleLink.href = injected?.cssUrl || CSS_URL || `${this.server}/pointer.css`;
    this.shadowRoot!.appendChild(this._styleLink);
    this.root = document.createElement('div');
    this.shadowRoot!.appendChild(this.root);

    ensureHighlightStyle();

    if (!this.project) {
      console.error('[pointer-feedback] Missing required `project` attribute. Component disabled.');
      return;
    }

    this._onHover = this.onHover.bind(this);
    this._onPick = this.onPick.bind(this);
    this._onPickKey = this.onPickKey.bind(this);
    this._onShortcutKeydown = this.onShortcutKeydown.bind(this);
    this._reposition = () => this.renderPins();
    window.addEventListener('scroll', this._reposition, true);
    window.addEventListener('resize', this._reposition);
    // Global "add comment" shortcut — listens on the whole document (not just our shadow root)
    // since it must fire no matter where on the host page the visitor's focus currently is.
    document.addEventListener('keydown', this._onShortcutKeydown);

    this._boot();
  }

  // Wait for the stylesheet to load, then render the first view (avoids a flash
  // of unstyled UI). A short timeout guarantees we never hang on slow CSS.
  private async _boot(): Promise<void> {
    // Resolve styles + product branding before the first render so the toolbar/login modal
    // show the configured product name (not the "Pointer" default) from the very first paint.
    await Promise.all([this._stylesReady(), loadBranding(this.server)]);
    // Login is deferred: on load just show the toolbar/launcher. The popup only
    // appears when the user acts (inspect / Comments) and there's no token yet.
    if (this.token) this.init();
    else this.renderChrome();
  }

  // An admin disabled this project: tear the widget down silently — no toolbar,
  // no launcher, no toast/console error. The only trace is the 409 already visible
  // in the browser's network tab. Detected from the comments endpoint's
  // 409 "project disabled" response.
  private disableSilently(): void {
    if (this._disabled) return;
    this._disabled = true;
    try { this.stopPicking(); } catch { /* ignore */ }
    this.comments = [];
    if (this.root) this.root.innerHTML = ''; // removes toolbar, launcher, and pins (#pf-pins lives here)
  }

  private _stylesReady(): Promise<void> {
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

  disconnectedCallback(): void {
    window.removeEventListener('scroll', this._reposition, true);
    window.removeEventListener('resize', this._reposition);
    document.removeEventListener('keydown', this._onShortcutKeydown);
    if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
    this.stopPicking();
    stopPageContextCapture();
  }

  // --- "Add comment" keyboard shortcut --------------------------------------
  private isEditableTarget(e: KeyboardEvent): boolean {
    const target = e.composedPath()[0] as HTMLElement | undefined;
    if (!target || !target.tagName) return false;
    const tag = target.tagName.toLowerCase();
    return tag === 'input' || tag === 'textarea' || tag === 'select' || !!target.isContentEditable;
  }

  private onShortcutKeydown(e: KeyboardEvent): void {
    if (this._recordingShortcut || this._disabled) return;
    if (this.isEditableTarget(e)) return; // never hijack typing, on the host page or in our own UI
    if (!matchesShortcut(e, this.shortcut)) return;
    e.preventDefault();
    this.activateAddComment();
  }

  // Shared by both the toolbar's "add" button and the keyboard shortcut — expands the widget
  // first if it's collapsed (the toolbar buttons don't exist in the DOM until then), then either
  // prompts login or toggles element-picking, exactly like clicking #pf-add.
  activateAddComment(): void {
    if (this._collapsed) this.showOverlay();
    if (!this.token) {
      showLoginModal(this, () => { Promise.resolve(this.init()).then(() => this.togglePicking()); });
      return;
    }
    this.togglePicking();
  }

  // Enters "recording" mode on the user-menu shortcut button: the next non-modifier keydown
  // (with at least one modifier held) becomes the new binding. Escape cancels.
  private beginRecordingShortcut(btnEl: HTMLElement): void {
    this._recordingShortcut = true;
    const original = btnEl.textContent || '';
    btnEl.textContent = 'Press keys… (Esc to cancel)';

    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopPropagation();
      if (e.key === 'Escape') {
        btnEl.textContent = original;
        cleanup();
        return;
      }
      if (e.key === 'Shift' || e.key === 'Alt' || e.key === 'Control' || e.key === 'Meta') return;
      if (!(e.altKey || e.ctrlKey || e.metaKey || e.shiftKey)) {
        btnEl.textContent = 'Add a modifier key (Alt/Shift/Ctrl/⌘)…';
        return;
      }
      const binding: ShortcutBinding = {
        key: e.key.length === 1 ? e.key.toLowerCase() : e.key,
        alt: e.altKey,
        shift: e.shiftKey,
        ctrl: e.ctrlKey,
        meta: e.metaKey,
      };
      btnEl.textContent = 'Saving…';
      cleanup();
      this.saveShortcutPreference(binding).then((ok) => {
        btnEl.textContent = formatShortcut(this.shortcut);
        this.toast(ok ? 'Shortcut updated' : 'Failed to save — try again', ok ? '' : 'error');
      });
    };
    const cleanup = () => {
      this._recordingShortcut = false;
      this._shortcutRecordingCleanup = null;
      document.removeEventListener('keydown', onKey, true);
    };
    this._shortcutRecordingCleanup = cleanup;
    document.addEventListener('keydown', onKey, true);
  }

  // Persists a new binding to the account (PATCH /api/me/preferences) so it follows the user
  // across browsers/machines — an empty string resets to the widget's built-in default. Updates
  // the cached `pointer_user` mirror on success so a page reload reflects it instantly, without
  // waiting for the next fresh login.
  private async saveShortcutPreference(binding: ShortcutBinding | null): Promise<boolean> {
    try {
      const r = await this.api('/api/me/preferences', {
        method: 'PATCH',
        body: JSON.stringify({ addCommentShortcut: binding ? serializeShortcut(binding) : '' }),
      });
      if (!r.ok) return false;
      this.shortcut = binding ? binding : parseShortcut(undefined);
      if (this.user) {
        this.user = { ...this.user, addCommentShortcut: binding ? serializeShortcut(binding) : undefined };
        localStorage.setItem('pointer_user', JSON.stringify(this.user));
      }
      return true;
    } catch {
      return false;
    }
  }

  // --- Auth helpers --------------------------------------------------------
  private loadAuth(): void {
    this.token = localStorage.getItem('pointer_token') || null;
    try {
      const raw = localStorage.getItem('pointer_user');
      this.user = raw ? JSON.parse(raw) : null;
    } catch (e) {
      this.user = null;
    }
    this.shortcut = parseShortcut(this.user?.addCommentShortcut);
  }

  saveAuth(token: string, user: User | null): void {
    this.token = token;
    this.user = user;
    this.shortcut = parseShortcut(user?.addCommentShortcut);
    localStorage.setItem('pointer_token', token);
    localStorage.setItem('pointer_user', JSON.stringify(user));
  }

  private clearAuth(): void {
    this.token = null;
    this.user = null;
    localStorage.removeItem('pointer_token');
    localStorage.removeItem('pointer_user');
  }

  private handle401(): void {
    this.clearAuth();
    showLoginModal(this);
  }

  async init(): Promise<void> {
    // Load the status catalog from the server before the first render so that
    // filter chips and status labels reflect server-configured values.
    // Falls back to STATUS_FALLBACK silently if the fetch fails.
    await loadStatusCatalog(this.server);
    this.renderChrome();
    await Promise.all([this.fetchComments(), this.fetchPredefinedActions(), this.fetchCaptureConfig()]);
    this.renderSidebar();
    this.renderPins();
    // When collapsed, re-render so the launcher badge reflects the loaded count.
    if (this._collapsed) this.renderChrome();
  }

  // Fetch the project's predefined-action options for the comment popover picker.
  // Silently no-ops on failure — the picker simply won't appear.
  async fetchPredefinedActions(): Promise<void> {
    try {
      const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/predefined-actions`);
      if (!r.ok) { this.predefinedActions = []; return; }
      const envelope = await r.json();
      this.predefinedActions = (envelope && envelope.data) || [];
    } catch {
      this.predefinedActions = [];
    }
  }

  // Read the project's page-context capture toggle and, if on, start buffering
  // console/network events. Silently no-ops on failure (feature stays off).
  async fetchCaptureConfig(): Promise<void> {
    try {
      const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/capture-config`);
      if (!r.ok) { this.pageContextCaptureEnabled = false; return; }
      const envelope = await r.json();
      this.pageContextCaptureEnabled = !!(envelope && envelope.data && envelope.data.pageContextCaptureEnabled);
      this.projectName = (envelope && envelope.data && envelope.data.name) || this.project;
      this.updateProjectNameLabel();
      if (this.pageContextCaptureEnabled) startPageContextCapture(this.server, SCRIPT_SRC);
    } catch {
      this.pageContextCaptureEnabled = false;
    }
  }

  // Patches the already-rendered header label in place rather than a full renderChrome() —
  // re-rendering chrome here would drop the sidebar's open/closed state mid-session.
  private updateProjectNameLabel(): void {
    const el = this.root && this.root.querySelector('#pf-project-name');
    if (el) {
      el.textContent = this.projectName;
      el.setAttribute('title', this.projectName);
    }
  }

  // --- API ----------------------------------------------------------------
  async apiLogin(email: string, password: string): Promise<Response> {
    return pfFetch(`${this.server}/api/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
  }

  // Anonymous: active non-admin roles for the signup / re-apply dropdowns.
  async apiRoles(): Promise<RoleOption[]> {
    const r = await pfFetch(`${this.server}/api/roles?project=${encodeURIComponent(this.project)}`, {
      headers: { 'Content-Type': 'application/json' },
    });
    const envelope = await r.json();
    if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || 'Could not load roles.');
    return envelope.data || [];
  }

  // Anonymous: self-signup AND re-apply (one endpoint). No token returned.
  async apiRegister(body: Record<string, unknown>): Promise<Response> {
    return pfFetch(`${this.server}/api/auth/register`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
  }

  api(path: string, opts: RequestInit = {}): Promise<Response> {
    const headers = {
      'Content-Type': 'application/json',
      ...(this.token ? { Authorization: `Bearer ${this.token}` } : {}),
      ...(opts.headers || {}),
    };
    return pfFetch(`${this.server}${path}`, { ...opts, headers }).then((r) => {
      if (r.status === 401) {
        this.handle401();
        throw new Error('HTTP 401 Unauthorized');
      }
      return r;
    });
  }

  async fetchComments(): Promise<void> {
    try {
      const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments?environment=${this.environmentInt}`);
      // 409 = the project was disabled by an admin → tear the widget down silently.
      // 404 = unknown/undefined project (project must be dashboard-created) → also hide silently.
      if (r.status === 409 || r.status === 404) { this.disableSilently(); return; }
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const envelope = await r.json();
      const items: Comment[] = (envelope.data && envelope.data.items) || [];
      // Count of others' private comments hidden from this viewer (server-side).
      this.hiddenPrivateCount = (envelope.data && Number(envelope.data.hiddenPrivateCount)) || 0;
      // Normalize int status → string for internal UI use
      this.comments = items.map((c) => ({
        ...c,
        status: STATUS_STR[c.status as unknown as number] || 'open',
      }));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') {
        this.toast(`Could not reach ${getBrandName()} server`, 'error');
      }
      this.comments = [];
      this.hiddenPrivateCount = 0;
    }
  }

  // All comments returned belong to the project (no page_url filtering).
  pageComments(): Comment[] {
    return this.comments;
  }

  // --- Chrome (toolbar + sidebar shell) -----------------------------------
  renderChrome(): void {
    if (this._disabled) return; // project disabled — stay torn down
    // Collapsed: show only a small launcher that re-opens the overlay.
    if (this._collapsed) {
      const n = (this.comments || []).filter((c) => c.status !== 'archived' && c.status !== 'applied').length;
      this.root.innerHTML = TPL.launcher(n, this.launcherPosition, pageIsRtl());
      const launcher = this.root.querySelector('#pf-launcher');
      if (launcher) launcher.addEventListener('click', () => this.showOverlay());
      return;
    }

    const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
    const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
    const fixedEnvLabel = this.hasFixedEnvironment
      ? (this.environmentAttr || ENV_NAME[this.environmentInt] || 'staging')
      : null;
    this.root.innerHTML = TPL.chrome(displayName, roleLabel, fixedEnvLabel, this.projectName || this.project);

    const hideBtn = this.root.querySelector('#pf-hide');
    if (hideBtn) hideBtn.addEventListener('click', () => this.hideOverlay());

    const userBtn = this.root.querySelector('#pf-user');
    if (userBtn) userBtn.addEventListener('click', (e) => { e.stopPropagation(); this.toggleUserMenu(); });

    this.root.querySelector('#pf-add')!.addEventListener('click', () => this.activateAddComment());
    this.root.querySelector('#pf-toggle')!.addEventListener('click', () => {
      if (!this.token) { showLoginModal(this, () => { Promise.resolve(this.init()).then(() => this.toggleSidebar(true)); }); return; }
      this.toggleSidebar();
    });
    this.root.querySelector('#pf-refresh')!.addEventListener('click', async () => {
      if (!this.token) { showLoginModal(this, () => this.init()); return; }
      await this.fetchComments(); this.renderSidebar(); this.renderPins(); this.toast('Refreshed');
    });
    this.root.querySelector('#pf-close')!.addEventListener('click', () => this.toggleSidebar(false));

    // Environment switcher — comments are scoped per environment; switching re-queries + persists.
    const envSel = this.root.querySelector('#pf-env') as HTMLSelectElement | null;
    if (envSel) {
      envSel.value = (this.environmentAttr || ENV_NAME[this.environmentInt] || 'staging').toLowerCase();
      envSel.addEventListener('change', () => this.setEnvironment(envSel.value));
    }

    const resetBtn = this.root.querySelector('#pf-reset-pos');
    if (resetBtn) resetBtn.addEventListener('click', () => this.resetToolbarPos());

    this.restoreToolbarPos();
    this.enableToolbarDrag();
  }

  // Switch the active environment from the toolbar. Comments are environment-scoped, so this
  // re-queries the server and re-renders; the choice is remembered per project on this origin.
  setEnvironment(env: string): void {
    const key = (env || '').toLowerCase();
    if (!ENV_MAP[key] || key === this.environmentAttr.toLowerCase()) return;
    this.environmentAttr = key;
    this.environmentInt = ENV_MAP[key];
    try { localStorage.setItem('pointer_env_' + this.project, key); } catch (e) { /* ignore */ }
    if (!this.token) return; // not signed in yet — the new env applies on next fetch
    this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
  }

  // --- Draggable toolbar ---------------------------------------------------
  // The toolbar is fixed at the top; let the user drag it by its grip so it
  // never covers the element they want to comment on. Position persists per tab.
  private restoreToolbarPos(): void {
    const tb = this.root.querySelector('.pf-toolbar') as HTMLElement | null;
    if (!tb) return;
    let saved: { left: number; top: number } | null = null;
    try { saved = JSON.parse(localStorage.getItem('pointer_toolbar_pos') || 'null'); } catch { /* ignore */ }
    if (!saved || typeof saved.left !== 'number' || typeof saved.top !== 'number') return;
    const maxLeft = Math.max(0, window.innerWidth - tb.offsetWidth);
    const maxTop = Math.max(0, window.innerHeight - tb.offsetHeight);
    tb.style.left = Math.min(Math.max(0, saved.left), maxLeft) + 'px';
    tb.style.top = Math.min(Math.max(0, saved.top), maxTop) + 'px';
    tb.style.right = 'auto';
    this._setResetVisible(true);
  }

  // Show/hide the "reset position" button (it only makes sense once the toolbar has moved).
  private _setResetVisible(visible: boolean): void {
    const btn = this.root.querySelector('#pf-reset-pos') as HTMLElement | null;
    if (btn) btn.style.display = visible ? '' : 'none';
  }

  // Restore the toolbar to its default corner and forget the saved position.
  private resetToolbarPos(): void {
    try { localStorage.removeItem('pointer_toolbar_pos'); } catch { /* ignore */ }
    const tb = this.root.querySelector('.pf-toolbar') as HTMLElement | null;
    if (tb) { tb.style.left = ''; tb.style.top = ''; tb.style.right = ''; }
    this._setResetVisible(false);
  }

  private enableToolbarDrag(): void {
    const tb = this.root.querySelector('.pf-toolbar') as HTMLElement | null;
    const grip = this.root.querySelector('#pf-grip') as HTMLElement | null;
    if (!tb || !grip) return;
    let sx = 0, sy = 0, startLeft = 0, startTop = 0, dragging = false;
    const onMove = (e: PointerEvent) => {
      if (!dragging) return;
      const maxLeft = Math.max(0, window.innerWidth - tb.offsetWidth);
      const maxTop = Math.max(0, window.innerHeight - tb.offsetHeight);
      tb.style.left = Math.min(Math.max(0, startLeft + (e.clientX - sx)), maxLeft) + 'px';
      tb.style.top = Math.min(Math.max(0, startTop + (e.clientY - sy)), maxTop) + 'px';
      tb.style.right = 'auto';
    };
    const onUp = (e: PointerEvent) => {
      if (!dragging) return;
      dragging = false;
      grip.classList.remove('dragging');
      try { grip.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
      try {
        localStorage.setItem('pointer_toolbar_pos', JSON.stringify({
          left: parseInt(tb.style.left, 10) || 0,
          top: parseInt(tb.style.top, 10) || 0,
        }));
      } catch { /* ignore */ }
      this._setResetVisible(true);
    };
    grip.addEventListener('pointerdown', (e: PointerEvent) => {
      e.preventDefault();
      const rect = tb.getBoundingClientRect();
      startLeft = rect.left; startTop = rect.top; sx = e.clientX; sy = e.clientY;
      dragging = true;
      grip.classList.add('dragging');
      try { grip.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    });
    grip.addEventListener('pointermove', onMove);
    grip.addEventListener('pointerup', onUp);
    grip.addEventListener('pointercancel', onUp);
  }

  // --- User menu (identity + sign out) ------------------------------------
  private toggleUserMenu(): void {
    const host = this.root.querySelector('#pf-menu-host') as HTMLElement | null;
    if (!host) return;
    if (host.querySelector('#pf-user-menu')) { this.closeUserMenu(); return; }

    const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
    const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
    host.innerHTML = TPL.userMenu(displayName, roleLabel, formatShortcut(this.shortcut));
    const menu = host.querySelector('#pf-user-menu') as HTMLElement;

    // Anchor the dropdown under the user icon.
    const btn = this.root.querySelector('#pf-user') as HTMLElement | null;
    if (btn) {
      const r = btn.getBoundingClientRect();
      menu.style.top = `${Math.round(r.bottom + 6)}px`;
      menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
    }

    (host.querySelector('#pf-signout') as HTMLElement).addEventListener('click', () => this.signOut());
    (host.querySelector('#pf-shortcut-edit') as HTMLElement).addEventListener('click', (e) => {
      e.stopPropagation();
      this.beginRecordingShortcut(host.querySelector('#pf-shortcut-edit') as HTMLElement);
    });
    (host.querySelector('#pf-shortcut-reset') as HTMLElement).addEventListener('click', async (e) => {
      e.stopPropagation();
      const editBtn = host.querySelector('#pf-shortcut-edit') as HTMLElement | null;
      if (editBtn) editBtn.textContent = 'Resetting…';
      const ok = await this.saveShortcutPreference(null);
      if (editBtn) editBtn.textContent = formatShortcut(this.shortcut);
      this.toast(ok ? 'Shortcut reset to default' : 'Failed to reset — try again', ok ? '' : 'error');
    });

    // Close on click outside (composedPath crosses the shadow boundary).
    this._userMenuClose = (e: MouseEvent) => {
      const path = e.composedPath();
      if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUserMenu();
    };
    setTimeout(() => { if (this._userMenuClose) document.addEventListener('click', this._userMenuClose, true); }, 0);
  }

  private closeUserMenu(): void {
    const host = this.root.querySelector('#pf-menu-host');
    if (host) host.innerHTML = '';
    if (this._userMenuClose) {
      document.removeEventListener('click', this._userMenuClose, true);
      this._userMenuClose = null;
    }
    // The menu (and its shortcut-edit button) is about to be gone — tear down any in-flight
    // "press keys…" recording so it can't silently capture some unrelated future keystroke.
    if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
  }

  // Clear the session and reset the widget to its logged-out (deferred-login) state.
  signOut(): void {
    this.closeUserMenu();
    if (this.picking) this.stopPicking();
    this.clearAuth();
    this.comments = [];
    this.hiddenPrivateCount = 0;
    this.sidebarOpen = false;
    this.mineOnly = false;
    this.authorFilter = null;
    this.statusFilter = 'all';
    this.renderChrome();
    this.renderSidebar();
    this.renderPins();
    this.toast('Signed out');
  }

  // Collapse the overlay to the floating launcher (remembered for this tab session).
  hideOverlay(): void {
    if (this.picking) this.stopPicking();
    this.sidebarOpen = false;
    this._collapsed = true;
    try { sessionStorage.removeItem('pointer_visible'); } catch (e) {}
    this.renderChrome();
    this.toast(`${getBrandName()} hidden — click the button to reopen`);
  }

  // Restore the full overlay from the launcher; remembered for this tab session.
  showOverlay(): void {
    this._collapsed = false;
    try { sessionStorage.setItem('pointer_visible', '1'); } catch (e) {}
    this.renderChrome();
    if (this.token) {
      this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
    }
  }

  toggleSidebar(force?: boolean): void {
    this.sidebarOpen = force === undefined ? !this.sidebarOpen : force;
    this.root.querySelector('#pf-sidebar')!.classList.toggle('open', this.sidebarOpen);
    // Opening → pull fresh server state so applied/"completed" comments show.
    if (this.sidebarOpen) {
      this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
    }
  }

  // --- Element picking -----------------------------------------------------
  togglePicking(): void {
    this.picking ? this.stopPicking() : this.startPicking();
  }
  startPicking(): void {
    this.picking = true;
    const addBtn = this.root.querySelector('#pf-add') as HTMLButtonElement;
    addBtn.classList.add('active');
    addBtn.innerHTML = ICON.close;
    addBtn.title = 'Cancel';
    document.addEventListener('mousemove', this._onHover, true);
    document.addEventListener('click', this._onPick, true);
    document.addEventListener('keydown', this._onPickKey, true);
    this.toast('Click any element to comment on it — or press Esc to cancel');
  }
  stopPicking(): void {
    this.picking = false;
    const addBtn = this.root && (this.root.querySelector('#pf-add') as HTMLButtonElement | null);
    if (addBtn) { addBtn.classList.remove('active'); addBtn.innerHTML = ICON.inspect; addBtn.title = 'Comment on an element'; }
    document.removeEventListener('mousemove', this._onHover, true);
    document.removeEventListener('click', this._onPick, true);
    document.removeEventListener('keydown', this._onPickKey, true);
    this.clearHover();
  }
  // Esc cancels element-picking (deselects the pointer) without placing a comment.
  onPickKey(e: KeyboardEvent): void {
    if (e.key !== 'Escape' && e.key !== 'Esc') return;
    e.preventDefault();
    e.stopPropagation();
    this.stopPicking();
    this.toast('Cancelled');
  }
  clearHover(): void {
    if (this.hovered) { this.hovered.classList.remove(HL_CLASS); this.hovered = null; }
  }
  isOwnElement(el: EventTarget | null): boolean {
    return el === this || (!!el && (el as Element).tagName === 'POINTER-FEEDBACK');
  }

  onHover(e: MouseEvent): void {
    const el = e.target as Element;
    if (this.isOwnElement(el)) return;
    if (el === this.hovered) return;
    this.clearHover();
    this.hovered = el;
    el.classList.add(HL_CLASS);
  }
  onPick(e: MouseEvent): void {
    if (this.isOwnElement(e.target)) return; // clicks on our own UI pass through
    e.preventDefault();
    e.stopPropagation();
    const el = e.target as Element;
    const x = e.clientX, y = e.clientY;
    this.clearHover();
    this.stopPicking();
    // Open the popover IMMEDIATELY. The "Attach screenshot" toggle is OFF by
    // default, so capture is deferred: it only starts if/when the user ticks the
    // box (wired in showPopover), avoiding a wasted snapdom render on every
    // comment. The Pointer UI (popover included) lives in the Shadow DOM, which
    // snapdom excludes, so capturing later still doesn't put it in the shot.
    this._pendingShotPromise = null;
    this.openCommentPopover(x, y, el);
  }

  // Kick off a best-effort screenshot capture for `el` (resolves null on failure).
  // Idempotent per popover: reuses an in-flight capture if one already started.
  beginScreenshotCapture(el: Element): void {
    if (!this.screenshotEnabled) return;
    if (this._pendingShotPromise) return;
    this._pendingShotPromise = captureScreenshot(el).catch((err) => {
      console.warn('[pointer-feedback] screenshot capture failed', err);
      return null;
    });
  }

  // Upload a screenshot Blob to /api/uploads via multipart/form-data. Returns the
  // absolute URL on success, or null on failure. Deliberately NOT using api() —
  // for FormData we must let the browser set the multipart boundary itself.
  async uploadToServer(blob: Blob): Promise<string | null> {
    try {
      const ext = blob.type === 'image/jpeg' ? 'jpg' : 'webp';
      const fd = new FormData();
      fd.append('file', blob, `screenshot.${ext}`);
      fd.append('project', this.project);
      // Note: in extension/proxy mode the transport may handle multipart specially
      // (a Blob can't cross chrome.runtime messaging as-is); a failed upload is
      // non-fatal — the comment still saves.
      const r = await pfFetch(`${this.server}/api/uploads`, {
        method: 'POST',
        headers: { ...(this.token ? { Authorization: `Bearer ${this.token}` } : {}) },
        body: fd,
      });
      if (r.status === 401) { this.handle401(); return null; }
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

  // --- Comment popover -----------------------------------------------------
  // (named openCommentPopover, not showPopover, to avoid clashing with the
  //  built-in HTMLElement.showPopover() from the Popover API.)
  openCommentPopover(x: number, y: number, el: Element): void {
    const meta = captureMetadata(el, this.sourceAttr);
    const host = this.root.querySelector('#pf-popover-host') as HTMLElement;
    const left = Math.min(x, window.innerWidth - 300);
    const top = Math.min(y, window.innerHeight - 220);
    host.innerHTML = TPL.popover(meta, left, top, this.screenshotEnabled, this.predefinedActions, this.pageContextCaptureEnabled);
    const ta = host.querySelector('#pf-comment-text') as HTMLTextAreaElement;
    ta.focus();
    // Screenshot is opt-in (unchecked by default): only capture once the user ticks
    // the box, so we don't render a screenshot for comments that won't use one.
    const shotToggle = host.querySelector('#pf-comment-shot') as HTMLInputElement | null;
    if (shotToggle) shotToggle.addEventListener('change', () => {
      if (shotToggle.checked) this.beginScreenshotCapture(el);
    });
    (host.querySelector('#pf-cancel') as HTMLElement).addEventListener('click', () => { host.innerHTML = ''; this._pendingShotPromise = null; });
    (host.querySelector('#pf-submit') as HTMLButtonElement).addEventListener('click', async () => {
      const text = ta.value.trim();
      if (!text) return this.toast('Comment cannot be empty', 'error');
      const privateEl = host.querySelector('#pf-comment-private') as HTMLInputElement | null;
      const isPrivate = !!(privateEl && privateEl.checked);
      const shotEl = host.querySelector('#pf-comment-shot') as HTMLInputElement | null;
      const attachShot = !!(shotEl && shotEl.checked); // toggle: off by default
      const bugEl = host.querySelector('#pf-comment-bug') as HTMLInputElement | null;
      const isBugReport = !!(bugEl && bugEl.checked); // toggle: off by default
      const shotPromise = this._pendingShotPromise;
      this._pendingShotPromise = null;
      const predefinedActionIds = Array.from(
        host.querySelectorAll('.pf-action-opt:checked') as NodeListOf<HTMLInputElement>,
      ).map((el) => Number(el.value));
      const submitBtn = host.querySelector('#pf-submit') as HTMLButtonElement;
      submitBtn.disabled = true; submitBtn.textContent = 'Saving…';
      const saved = await this.createComment({ ...meta, text, isPrivate, attachShot, shotPromise, predefinedActionIds, isBugReport });
      if (saved) host.innerHTML = '';
      else { submitBtn.disabled = false; submitBtn.textContent = 'Add'; }
    });
  }

  // Returns true on success (popover should close), false on failure (popover stays open).
  async createComment(data: CreateCommentData): Promise<boolean> {
    // Capture the viewport so triage knows which device the feedback came from.
    // deviceType is the common mobile/tablet/desktop split by CSS-px width.
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const deviceType = vw < 768 ? 'mobile' : vw < 1024 ? 'tablet' : 'desktop';

    const element: Comment['element'] = {
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
      pageTitle: document.title,
      viewportWidth: vw,
      viewportHeight: vh,
      deviceType,
      devicePixelRatio: window.devicePixelRatio || 1,
      userAgent: navigator.userAgent,
    };

    // Screenshot is opt-in (toggle, off by default). When on, await the capture
    // that started when the box was ticked, upload it via /api/uploads, and put
    // the URL on the comment. A failed upload is non-fatal — comment still saves.
    if (data.attachShot && data.shotPromise) {
      const blob = await Promise.resolve(data.shotPromise).catch(() => null);
      if (blob) {
        const url = await this.uploadToServer(blob);
        if (url) element!.screenshotUrl = url;
        else this.toast('Screenshot upload failed — saving without it', 'error');
      }
    }

    const bodyObj: Record<string, unknown> = {
      body: data.text,
      environment: this.environmentInt,
      isPrivate: !!data.isPrivate,
      element,
      isBugReport: !!data.isBugReport,
    };
    if (data.predefinedActionIds && data.predefinedActionIds.length) bodyObj.predefinedActionIds = data.predefinedActionIds;
    // Only attach the buffered console/network snapshot when the box is checked — unchecked means
    // zero extra payload, regardless of what's been silently buffered in the browser.
    if (data.isBugReport) {
      const pageContext = getPageContextPayload();
      if (pageContext) bodyObj.pageContext = pageContext;
    }
    try {
      const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments`, {
        method: 'POST',
        body: JSON.stringify({ ...bodyObj, projectKey: this.project }),
      });
      // Project disabled by an admin mid-session → tear down silently, no consumer error.
      // 404 = unknown/undefined project → also hide silently.
      if (r.status === 409 || r.status === 404) { this.disableSilently(); return true; }
      if (!r.ok) {
        const errEnv = await r.json().catch(() => null);
        const msg: string = (errEnv && errEnv.message) || '';
        // Stale predefined action: the server rejected the action as invalid/unavailable.
        // Refetch the list once so the picker is fresh, then ask the user to retry.
        if (data.predefinedActionIds && data.predefinedActionIds.length && msg.toLowerCase().includes('action')) {
          await this.fetchPredefinedActions();
          this.toast('That action is no longer available — please choose another and try again.', 'error');
          return false;
        }
        throw new Error('HTTP ' + r.status);
      }
      const envelope = await r.json();
      const comment = envelope.data;
      if (comment) {
        this.comments.push({ ...comment, status: STATUS_STR[comment.status as unknown as number] || 'open' });
      }
      this.renderSidebar();
      this.renderPins();
      this.toast('Comment added', 'success');
      return true;
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') {
        this.toast('Failed to save comment', 'error');
      }
      return false;
    }
  }

  // --- Mutations -----------------------------------------------------------
  async addReply(id: string, text: string): Promise<void> {
    try {
      const r = await this.api(`/api/comments/${id}/replies`, {
        method: 'POST',
        body: JSON.stringify({ body: text }),
      });
      if (!r.ok) throw new Error();
      await this.fetchComments(); this.renderSidebar(); this.renderPins();
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast('Failed to reply', 'error');
    }
  }

  async toggleApply(comment: Comment): Promise<void> {
    // Cycle: open (1) → pending-apply (2); pending-apply (2) → open (1)
    const nextStr: StatusStr = comment.status === 'pending-apply' ? 'open' : 'pending-apply';
    const nextInt = STATUS_INT[nextStr];
    try {
      const r = await this.api(`/api/comments/${comment.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ status: nextInt }),
      });
      if (!r.ok) throw new Error();
      comment.status = nextStr;
      this.renderSidebar(); this.renderPins();
      this.toast(nextStr === 'pending-apply' ? 'Marked for apply' : 'Unmarked');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
    }
  }

  // Generic status change (Re-open → open, Archive → archived).
  async setStatus(comment: Comment, nextStr: StatusStr, toastMsg?: string): Promise<void> {
    const nextInt = STATUS_INT[nextStr];
    try {
      const r = await this.api(`/api/comments/${comment.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ status: nextInt }),
      });
      if (!r.ok) throw new Error();
      comment.status = nextStr;
      this.renderSidebar(); this.renderPins();
      this.toast(toastMsg || 'Updated');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
    }
  }

  // Toggle a comment's privacy — author-only (enforced server-side too).
  async setVisibility(comment: Comment, isPrivate: boolean): Promise<void> {
    try {
      const r = await this.api(`/api/comments/${comment.id}/visibility`, {
        method: 'PATCH',
        body: JSON.stringify({ isPrivate }),
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      comment.isPrivate = isPrivate;
      this.renderSidebar(); this.renderPins();
      this.toast(isPrivate ? 'Marked private' : 'Made public');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
    }
  }

  async markCompleted(comment: Comment): Promise<void> {
    // Mark done directly — for changes already applied outside Pointer.
    const label = this.user ? (this.user.displayName || this.user.email) : null;
    try {
      const r = await this.api(`/api/comments/${comment.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ status: STATUS_INT['applied'], appliedByLabel: label }),
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      comment.status = 'applied';
      if (label) comment.appliedByLabel = label;
      this.renderSidebar(); this.renderPins();
      this.toast('Marked completed');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast('Update failed', 'error');
    }
  }

  /**
   * Two-step delete: swap the trash button for a small inline "Delete? ✓ ✕"
   * confirmation so a single mis-click can't destroy a comment. Confirms on ✓,
   * cancels on ✕, and auto-dismisses after a few seconds.
   */
  confirmDelete(btn: HTMLElement): void {
    const id = btn.dataset.id;
    const host = btn.parentElement;
    if (!id || !host || host.querySelector('.pf-confirm')) return; // already confirming
    btn.style.display = 'none';
    const wrap = document.createElement('span');
    wrap.className = 'pf-confirm';
    wrap.innerHTML =
      `<span class="pf-confirm-q">Delete?</span>` +
      `<button type="button" class="pf-mini danger pf-icon" data-c="yes" title="Confirm delete" aria-label="Confirm delete">${ICON.check}</button>` +
      `<button type="button" class="pf-mini pf-icon" data-c="no" title="Cancel" aria-label="Cancel">&#x2715;</button>`;
    host.insertBefore(wrap, btn);

    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      clearTimeout(timer);
      wrap.remove();
      btn.style.display = '';
    };
    const timer = setTimeout(close, 4000);
    wrap.querySelector('[data-c="yes"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      close();
      this.deleteComment(id);
    });
    wrap.querySelector('[data-c="no"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      close();
    });
  }

  async deleteComment(id: string): Promise<void> {
    // DELETE /api/comments/{id} — soft-deletes on the server (JWT-scoped).
    try {
      const r = await this.api(`/api/comments/${id}`, { method: 'DELETE' });
      if (!r.ok) {
        const body = await r.json().catch(() => null);
        throw new Error((body && body.message) || ('HTTP ' + r.status));
      }
      this.comments = this.comments.filter((c) => String(c.id) !== String(id));
      this.renderSidebar(); this.renderPins();
      this.toast('Deleted');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || 'Delete failed', 'error');
    }
  }

  // Inline edit (own comments only): swap the body text for a textarea + controls.
  startEdit(id: string): void {
    const card = this.root && this.root.querySelector(`.pf-card[data-id="${id}"]`);
    if (!card || card.querySelector('.pf-edit')) return;
    const comment = (this.comments || []).find((x) => String(x.id) === String(id));
    if (!comment) return;
    const textEl = card.querySelector('.pf-text') as HTMLElement | null;
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
    const ta = editor.querySelector('.pf-edit-body') as HTMLTextAreaElement;
    ta.focus();
    (editor.querySelector('.pf-edit-cancel') as HTMLElement).addEventListener('click', () => { editor.remove(); textEl.style.display = ''; });
    (editor.querySelector('.pf-edit-save') as HTMLElement).addEventListener('click', () => {
      const body = ta.value.trim();
      if (!body) { this.toast('Comment cannot be empty', 'error'); return; }
      const rm = editor.querySelector('.pf-edit-rmshot') as HTMLInputElement | null;
      const removeScreenshot = !!(rm && rm.checked);
      this.saveEdit(id, body, removeScreenshot);
    });
  }

  async saveEdit(id: string, body: string, removeScreenshot: boolean): Promise<void> {
    // PUT /api/comments/{id} — author-only edit (enforced server-side).
    try {
      const r = await this.api(`/api/comments/${id}`, {
        method: 'PUT',
        body: JSON.stringify({ body, removeScreenshot }),
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
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || 'Failed to update comment', 'error');
    }
  }

  // True when comment `c` was authored by the current logged-in user.
  isMine(c: Comment): boolean {
    const uid = this.user && this.user.id;
    if (!uid) return false;
    return String(c.authorId || '').toLowerCase() === String(uid).toLowerCase();
  }

  // Distinct comment authors in the current project list.
  distinctAuthors(comments: Comment[]): AuthorOption[] {
    const seen = new Set<string>();
    const out: AuthorOption[] = [];
    for (const c of comments) {
      const id = String(c.authorId || '');
      if (id && !seen.has(id)) { seen.add(id); out.push({ id, name: c.authorName || id }); }
    }
    return out;
  }

  // Apply the "who" filters in priority order: Mine wins; else a chosen author.
  scopeByWho(comments: Comment[]): Comment[] {
    if (this.mineOnly) return comments.filter((c) => this.isMine(c));
    if (this.authorFilter) return comments.filter((c) => String(c.authorId || '') === this.authorFilter);
    return comments;
  }

  // --- Sidebar render ------------------------------------------------------
  renderSidebar(): void {
    const all = this.pageComments();
    const canMine = !!(this.user && this.user.id);
    if (!canMine) this.mineOnly = false;
    const authors = this.distinctAuthors(all);
    if (this.authorFilter && !authors.some((a) => a.id === this.authorFilter)) this.authorFilter = null;
    const scoped = this.scopeByWho(all);
    const counts: Record<string, number> = {
      // "All" means active (non-archived, non-completed); those move out to their own chips.
      all: scoped.filter((c) => c.status !== 'archived' && c.status !== 'applied').length,
      open: scoped.filter((c) => c.status === 'open').length,
      'pending-apply': scoped.filter((c) => c.status === 'pending-apply').length,
      applied: scoped.filter((c) => c.status === 'applied').length,
      archived: scoped.filter((c) => c.status === 'archived').length,
    };

    const countEl = this.root.querySelector('#pf-count');
    if (countEl) countEl.textContent = String(all.filter((c) => c.status !== 'archived' && c.status !== 'applied').length);

    const filtersEl = this.root.querySelector('#pf-filters');
    if (filtersEl) {
      const activeFilters = catalogToFilters();
      filtersEl.innerHTML = activeFilters.map((f) =>
        TPL.filterChip(f, this.statusFilter === f.key, counts[f.key] ?? 0)).join('')
        + ((authors.length > 1 && !this.mineOnly) ? TPL.authorFilter(authors, this.authorFilter || '') : '')
        + (canMine ? TPL.mineToggle(this.mineOnly) : '');
      filtersEl.querySelectorAll<HTMLElement>('[data-filter]').forEach((b) =>
        b.addEventListener('click', () => { this.statusFilter = b.dataset.filter!; this.renderSidebar(); }));
      const mineBtn = filtersEl.querySelector('#pf-mine-toggle');
      if (mineBtn) mineBtn.addEventListener('click', () => {
        this.mineOnly = !this.mineOnly; this.renderSidebar(); this.renderPins();
      });
      const authorSel = filtersEl.querySelector('#pf-author-filter') as HTMLSelectElement | null;
      if (authorSel) authorSel.addEventListener('change', () => {
        this.authorFilter = authorSel.value || null;
        this.renderSidebar(); this.renderPins();
      });
    }

    const list = this.root.querySelector('#pf-list');
    if (!list) return;

    const shown = this.statusFilter === 'all'
      ? scoped.filter((c) => c.status !== 'archived' && c.status !== 'applied')
      : scoped.filter((c) => c.status === this.statusFilter);
    if (!scoped.length) {
      list.innerHTML = TPL.empty(this.mineOnly
        ? "You haven't left any comments yet."
        : 'No comments on this project yet.<br/>Click the inspect icon, then click an element.');
      return;
    }
    if (!shown.length) {
      const activeFilters = catalogToFilters();
      const filterLabel = (activeFilters.find((f) => f.key === this.statusFilter) ?? { label: this.statusFilter }).label;
      list.innerHTML = TPL.empty(`No ${filterLabel.toLowerCase()} comments${this.mineOnly ? ' of yours' : ''}.`);
      return;
    }

    list.innerHTML = shown.map((c, i) => { c._mine = this.isMine(c); return TPL.card(c, i); }).join('');

    list.querySelectorAll<HTMLElement>('[data-act="apply"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c && c.status !== 'applied') this.toggleApply(c);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="complete"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c && c.status !== 'applied') this.markCompleted(c);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="delete"]').forEach((b) => b.addEventListener('click', () => this.confirmDelete(b)));
    list.querySelectorAll<HTMLElement>('[data-act="edit"]').forEach((b) => b.addEventListener('click', () => this.startEdit(b.dataset.id!)));
    list.querySelectorAll<HTMLElement>('[data-act="visibility"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.setVisibility(c, b.dataset.private === 'true');
    }));
    list.querySelectorAll<HTMLElement>('[data-act="reopen"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.setStatus(c, 'open', 'Re-opened');
    }));
    list.querySelectorAll<HTMLElement>('[data-act="archive"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.setStatus(c, 'archived', 'Archived');
    }));
    list.querySelectorAll<HTMLInputElement>('.pf-reply-input').forEach((inp) => inp.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && inp.value.trim()) { this.addReply(inp.dataset.id!, inp.value.trim()); inp.value = ''; }
    }));
  }

  // --- Pins ----------------------------------------------------------------
  renderPins(): void {
    const wrap = this.root && this.root.querySelector('#pf-pins');
    if (!wrap) return;
    const all = this.pageComments().filter((c) => c.status !== 'archived' && c.status !== 'applied');
    const here = this.scopeByWho(all);
    wrap.innerHTML = here.map((c, i) => {
      const el = matchElement(c);
      if (!el) return '';
      const rect = el.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) return '';
      return TPL.pin(c, i, rect);
    }).join('');
    wrap.querySelectorAll<HTMLElement>('.pf-pin').forEach((p) => p.addEventListener('click', () => {
      this.toggleSidebar(true);
      const card = this.root.querySelector(`.pf-card[data-id="${p.dataset.id}"]`);
      if (card) card.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }));
  }

  // --- Toast ---------------------------------------------------------------
  toast(msg: string, type = ''): void {
    const t = document.createElement('div');
    t.className = `pf-toast ${type}`;
    t.textContent = msg;
    this.root.appendChild(t);
    setTimeout(() => t.remove(), 2200);
  }
}
