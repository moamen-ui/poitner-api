import {
  HL_CLASS, BACKDROP_SELECTOR, DIALOG_CONTENT_SELECTOR, ENV_MAP, ENV_NAME, STATUS_STR, STATUS_INT, POSITIONS, CSS_URL, SCRIPT_SRC,
  loadStatusCatalog, catalogToFilters, pfFetch, loadBranding, getBrandName, CSS_INTEGRITY,
} from './constants';
import { escapeHtml, initials, ensureHighlightStyle, matchElement, pageIsRtl, buildClipPathWithHoles, applyDataPosition } from './dom';
import { TPL } from './templates';
import { ICON } from './icons';
import { captureScreenshot, captureMetadata } from './capture';
import { startPageContextCapture, stopPageContextCapture, getPageContextPayload, rawFetch } from './pagecontext';
import {
  type ShortcutBinding, parseShortcut, serializeShortcut, matchesShortcut, formatShortcut, ariaKeyshortcuts,
} from './shortcut';
import { type ThemeMode, detectSiteTheme } from './theme';
import { type Lang, t, setLang, detectTextLanguageAsync } from './i18n';
import { showLoginModal } from './auth-ui';
import type { AuthorOption, Comment, Meta, NotificationItem, PointerHost, PredefinedActionOption, RoleOption, StatusStr, User } from './types';

// Two pin anchors this close together (px) collide visually — the 28px pin plus its border
// already covers most of that gap — so renderPins() merges them into one expandable cluster
// instead of stacking indistinguishable pins on top of each other.
const PIN_CLUSTER_RADIUS = 24;
// The pin's own rendered footprint — half its width and its full height (28px pin + a couple px
// of border/shadow) — used to nudge a pin back on-screen when its target sits right at a
// viewport edge (see the clamp in renderPins()).
const PIN_HALF_WIDTH = 16;
const PIN_HEIGHT = 30;
// Conservative estimate of the hover tooltip's rendered height, used only to decide whether it
// has room to open upward — doesn't need to be exact, just enough to flip it early rather than
// late.
const PIN_TOOLTIP_HEIGHT_ESTIMATE = 150;

interface CreateCommentData extends Meta {
  text: string;
  isPrivate: boolean;
  attachShot: boolean;
  shotPromise: Promise<Blob | null> | null;
  predefinedActionIds?: number[];
  isBugReport: boolean;
  language?: string;
}

export class PointerFeedback extends HTMLElement implements PointerHost {
  private _mounted = false;
  project = '';
  environmentAttr = '';
  sourceAttr = 'data-component-source';
  screenshotEnabled = true;
  launcherPosition = 'bottom-end';
  server = '';
  environmentInt = 0;
  /** True when the page, the host config or a saved choice named an environment — the server's
   *  origin-resolved answer is then advisory and must not override it. */
  environmentExplicit = false;
  /** True when the toolbar's environment select is on "All" — comments are fetched unfiltered
   *  (no `?environment=` query param) across every environment. Independent of environmentInt/
   *  environmentAttr, which keep tracking the actual (resolved or last-picked) environment so a
   *  NEW comment composed while viewing "All" still gets tagged with a real environment, not "all". */
  viewAllEnvironments = false;

  comments: Comment[] = [];
  statusFilter = 'all';
  mineOnly = false;
  authorFilter: string | null = null;
  hiddenPrivateCount = 0;
  private _collapsed = true;
  private _disabled = false;
  picking = false;
  /** A magic-link token stripped from the URL, awaiting redemption in _boot(). */
  private _pendingInviteToken: string | null = null;

  /**
   * Module-level, not per-instance: "once per page load" has to hold even if the host page mounts
   * two widgets, which is exactly when a duplicate beacon would be least expected.
   */
  private static _buildShaReported = false;
  sidebarOpen = false;
  hovered: Element | null = null;

  token: string | null = null;
  user: User | null = null;
  afterLogin: (() => void) | null = null;
  predefinedActions: PredefinedActionOption[] = [];
  // Whether switching is hard-locked off regardless of role (an explicit `fixed-environment="true"`
  // attribute, or host-injected config like the browser extension) — when true, the toolbar shows a
  // read-only label instead of a switcher, no matter what /capture-config's role check says. Plain
  // `environment="..."` alone no longer implies this — it only seeds the starting value now, so a
  // normal install (which always sets `environment` from *_POINTER_ENV) doesn't silently defeat the
  // role-gated switcher below.
  hasFixedEnvironment = false;
  // Per-project, per-role: whether THIS logged-in caller may switch environments at all (vs a
  // read-only label). Defaults true (matches pre-existing behavior) until /capture-config
  // resolves post-login and possibly turns it off (e.g. for a Client/QuickAccess role by default,
  // or any role the project owner excluded). See ProjectService.ShowEnvironmentSelectorFor.
  showEnvironmentSelector = true;
  // Project-level opt-in (default off), read once at init via /capture-config. Gates both whether
  // the widget buffers console/network events at all and whether "Report as a bug" is shown.
  pageContextCaptureEnabled = false;
  // Per-project text capture toggle (default true until /capture-config resolves).
  // When false, the widget emits no text content in the DOM snapshot and masks pageTitle.
  captureTextContent = true;
  // Whether the AI apply flow (skill.md) bundles applied comments into one commit or commits each
  // one separately — 1=Single, 2=Separate (backend CommitStyle enum, read as-is like
  // environmentInt already is). Changeable via a small widget control, but only rendered when
  // canEditSettings is true (admin or the project's creator — same gate as the PATCH itself).
  commitStyle = 1;
  canEditSettings = false;
  // The numeric project id (distinct from the `project` key attribute) — needed to PATCH
  // /api/admin/projects/{id} for the commit-style control; resolved once via /capture-config.
  projectId: number | null = null;
  // Display name resolved from /capture-config (falls back to the raw `project` key attribute
  // until it loads). Shown next to the environment indicator so a visitor can immediately tell
  // which project an install is actually bound to — project keys aren't unique across a workspace.
  projectName = '';
  // Per-user "add comment" keyboard shortcut, synced to the account (User.AddCommentShortcut,
  // not localStorage) — set from `this.user.addCommentShortcut` in loadAuth()/saveAuth() below.
  // Default is Ctrl+Alt+Shift+C / Control+Option+Shift+C — see shortcut.ts for why.
  shortcut: ShortcutBinding = parseShortcut(undefined);
  // True when a host (the browser extension) injected a token: auth is entirely owned by that
  // host, re-applied on every reload (see connectedCallback), so the widget's own sign-out would
  // be immediately overwritten and must not be offered — switching accounts happens in the
  // extension popup instead.
  authOwnedByHost = false;

  root!: HTMLElement;
  private _styleLink!: HTMLLinkElement;
  private _onHover!: (e: MouseEvent) => void;
  private _onPick!: (e: MouseEvent) => void;
  private _onPickKey!: (e: KeyboardEvent) => void;
  private _onShortcutKeydown!: (e: KeyboardEvent) => void;
  private _reposition!: () => void;
  private _pendingShotPromise: Promise<Blob | null> | null = null;
  // The comment id whose pin should show the attention ripple on its NEXT renderPins() — cleared
  // shortly after so re-renders (env switch, poll, etc.) don't replay it forever.
  private _newPinId: string | number | null = null;
  // The toolbar's drag offset from its default bottom-right anchor (see enableToolbarDrag).
  private _toolbarDx = 0;
  private _toolbarDy = 0;
  unreadNotifyCount = 0;
  private _notifyPollTimer: number | null = null;
  private _updatesMenuClose: ((e: MouseEvent) => void) | null = null;
  private _onVisibilityChange: (() => void) | null = null;
  private _userMenuClose: ((e: MouseEvent) => void) | null = null;
  private _clusterMenuClose: ((e: MouseEvent) => void) | null = null;
  private _cardMenuClose: ((e: MouseEvent) => void) | null = null;
  private _recordingShortcut = false;
  private _shortcutRecordingCleanup: (() => void) | null = null;
  private _backdropObserver: MutationObserver | null = null;
  private _backdropRaf = 0;
  private _scheduleBackdropUpdate!: () => void;

  connectedCallback(): void {
    try { performance.mark('pf:boot:start'); } catch { /* ignore */ }
    if (this._mounted) return;
    this._mounted = true;

    this.project = this.getAttribute('project') || '';
    this.environmentAttr = this.getAttribute('environment') || '';
    // `environment` alone only seeds the starting value — see `hasFixedEnvironment`'s field doc.
    // Hard-lock is a separate, explicit opt-in for deployments that must never offer switching
    // (e.g. a server-rendered embed pinned to one environment on purpose).
    this.hasFixedEnvironment = (this.getAttribute('fixed-environment') || '').toLowerCase() === 'true';
    this.sourceAttr = this.getAttribute('source-attr') || 'data-component-source';
    // Screenshot capture is available by default; opt out with screenshot="false".
    this.screenshotEnabled = (this.getAttribute('screenshot') || '').toLowerCase() !== 'false';
    // Collapsed-launcher corner: top-start | top-end | bottom-start | bottom-end (default bottom-end).
    const pos = (this.getAttribute('launcher-position') || '').toLowerCase();
    this.launcherPosition = (POSITIONS as readonly string[]).includes(pos) ? pos : 'bottom-end';
    this.server = (this.getAttribute('server') ||
      (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, '');

    // An absent `environment` attribute now means "server, you tell me": the deployment's own
    // origin decides, matched against the URLs registered for the project. An app does not have an
    // environment, a deployment does — and a value baked into the markup tags staging and
    // production feedback identically forever.
    //
    // An attribute that IS present still wins, so every existing install behaves exactly as before.
    this.environmentExplicit = !!this.environmentAttr;
    this.environmentInt = this.environmentAttr ? (ENV_MAP[this.environmentAttr.toLowerCase()] ?? 2) : 0;

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
        this.environmentExplicit = true;
      }
      // A host-supplied environment is a STARTING value, not a lock. The extension's popup says
      // "switch environment inside the widget", yet this used to set hasFixedEnvironment and the
      // switcher rendered as plain text for every role. Only an explicit fixedEnvironment locks it —
      // the same opt-in the `fixed-environment="true"` attribute gives an embed.
      if (injected.fixedEnvironment === true) this.hasFixedEnvironment = true;
    }

    // The environment is switchable IN the widget (toolbar select) and remembered per project on
    // this origin — it takes precedence over the attribute/injected default so the viewer's choice
    // sticks across reloads. Comments are environment-scoped, so switching re-queries per env.
    // Skipped entirely when the environment is fixed at install time — a saved choice from before
    // the host added the attribute must never override it.
    if (!this.hasFixedEnvironment) {
      try {
        const savedEnv = (localStorage.getItem('pointer_env_' + this.project) || '').toLowerCase();
        if (savedEnv === 'all') {
          this.viewAllEnvironments = true;
        } else if (savedEnv && ENV_MAP[savedEnv]) {
          this.environmentAttr = savedEnv;
          this.environmentInt = ENV_MAP[savedEnv];
          this.environmentExplicit = true;
        }
      } catch (e) { /* ignore */ }
    }
    // Normalize the display string so the toolbar select always has a matching option. Left as
    // `unknown` until /capture-config answers, rather than guessing `staging` and briefly showing a
    // label that may be wrong.
    if (!this.environmentAttr) this.environmentAttr = ENV_NAME[this.environmentInt] || 'unknown';

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
    // Strip the magic-link token SYNCHRONOUSLY, before anything else can read the URL. Redemption
    // itself is awaited later in _boot(), where the result can actually gate the render.
    this._pendingInviteToken = this.stripInviteTokenFromUrl();

    this.loadAuth();
    if (injected?.token) {
      this.token = injected.token;
      if (injected.user !== undefined) this.user = injected.user;
      this.shortcut = parseShortcut(this.user?.addCommentShortcut);
      this.authOwnedByHost = true;
    }
    this.applyTheme();
    setLang(this.resolveLang());

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
    if (CSS_INTEGRITY) {
      this._styleLink.integrity = CSS_INTEGRITY;
      this._styleLink.crossOrigin = 'anonymous';
    }
    // A host may bundle the CSS (extension) and pass its URL; otherwise resolve from the script's
    // own origin, falling back to the API server.
    this._styleLink.href = injected?.cssUrl || CSS_URL || `${this.server}/pointer.css`;
    this.shadowRoot!.appendChild(this._styleLink);
    this.root = document.createElement('div');
    this.shadowRoot!.appendChild(this.root);

    this._stylesPromise = this._stylesReady();

    ensureHighlightStyle();

    if (!this.project) {
      console.error('[pointer-feedback] Missing required `project` attribute. Component disabled.');
      return;
    }

    this._onHover = this.onHover.bind(this);
    this._onPick = this.onPick.bind(this);
    this._onPickKey = this.onPickKey.bind(this);
    this._onShortcutKeydown = this.onShortcutKeydown.bind(this);
    this._reposition = () => { this.renderPins(); this.updateMenuSide(); };
    window.addEventListener('scroll', this._reposition, true);
    window.addEventListener('resize', this._reposition);

    // A max z-index only wins the browser's PAINT order — verified independently that Chromium's
    // native hit-testing (elementFromPoint / real click dispatch) can still award a full-viewport
    // modal backdrop (Angular CDK, Bootstrap, etc.) the click even when our UI paints visibly above
    // it, no matter how high z-index goes. The only reliable fix is punching a hole in the
    // backdrop's own hit-testable area (see punchBackdropHoles) exactly where our UI sits, so the
    // rest of the backdrop keeps working normally (dismiss-on-click for everything else on the page).
    this._scheduleBackdropUpdate = () => {
      if (this._backdropRaf) return;
      this._backdropRaf = requestAnimationFrame(() => {
        this._backdropRaf = 0;
        this.punchBackdropHoles();
      });
    };
    window.addEventListener('resize', this._scheduleBackdropUpdate);
    window.addEventListener('scroll', this._scheduleBackdropUpdate, true);
    // The mutation that opens .fbk-sidebar/.fbk-popover (a class toggle) fires at the START of their
    // CSS transition (translateX/opacity), not at the end — so the rAF this schedules measures a
    // MID-TRANSITION rect and never gets recomputed again once the panel settles, leaving a
    // permanently-misaligned hole (confirmed: sidebar slides from x:1100→740 over 200ms, but the
    // punched hole freezes wherever it happened to be on the very next frame, e.g. x:951). Catch the
    // transition's end too so the hole gets one final, correct recompute.
    this.root.addEventListener('transitionend', this._scheduleBackdropUpdate);
    // Ignore mutations WE caused (setting clip-path on a backdrop/dialog pane) — otherwise punching
    // a hole would itself re-trigger this observer forever, once per animation frame indefinitely.
    this._backdropObserver = new MutationObserver((mutations) => {
      const isOwnMutation = (m: MutationRecord) =>
        m.target instanceof Element && m.target.matches(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
      if (mutations.some((m) => !isOwnMutation(m))) this._scheduleBackdropUpdate();
    });
    this._backdropObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class'] });
    // Our own shadow tree toggles (launcher ↔ toolbar, sidebar/popover/modal open-close) change
    // which of our elements need a hole, but never touch document.body — observe separately.
    this._backdropObserver.observe(this.root, { childList: true, subtree: true, attributes: true, attributeFilter: ['style', 'class'] });
    // The observer only reports FUTURE mutations — catch a backdrop already open when we load.
    this._scheduleBackdropUpdate();
    // Global "add comment" shortcut — listens on the whole document (not just our shadow root)
    // since it must fire no matter where on the host page the visitor's focus currently is.
    document.addEventListener('keydown', this._onShortcutKeydown);

    this._boot();
  }

  // Wait for the stylesheet to load, then render the first view (avoids a flash
  // of unstyled UI). A short timeout guarantees we never hang on slow CSS.
  private async _boot(): Promise<void> {
    // Hidden by default until the server confirms this project+origin is active — nothing
    // renders (not even the launcher) before this resolves, and nothing renders at all if it
    // resolves false or the request fails. This is the ONLY check that gates rendering itself
    // (as opposed to comment submission); it runs for every visitor, logged in or not.
    if (!(await this._checkWidgetActive())) return;

    // Resolve styles + product branding before the first render so the toolbar/login modal
    // show the configured product name (not the "Pointer" default) from the very first paint.
    await Promise.all([this._stylesReady(), loadBranding(this.server)]);
    // Redeem a magic link before deciding what to render: a first-time client has no stored token,
    // so this is the difference between signing them in and showing them a login they cannot pass.
    let inviteFailed = false;
    if (this._pendingInviteToken) {
      inviteFailed = !(await this.redeemInviteToken(this._pendingInviteToken));
      this._pendingInviteToken = null;
    }

    // Login is deferred: on load just show the toolbar/launcher. The popup only
    // appears when the user acts (inspect / Comments) and there's no token yet.
    // A host-injected session (browser extension) hands over a display name only, so the widget
    // cannot tell which comments are the viewer's own — and never shows them the verify buttons.
    // Fill in id/isAdmin from the server before the first comment list renders.
    if (this.token) await this.hydrateIdentity();
    setLang(this.resolveLang());
    if (this.token) this.init();
    else this.renderChrome();

    try { performance.mark('pf:boot:end'); } catch { /* ignore */ }

    // Tell the server which build this page is, so comments fixed in it can show as live.
    // After the token check, because it is an authenticated call and an anonymous visitor has
    // nothing to report with.
    if (this.token) void this._reportBuildSha();

    // Once per tab session, tell the server which languages this visit actually involved (widget
    // UI, host page, browser) — see _reportWidgetLanguage. Same authenticated-only gating as the
    // build-sha beacon above: an anonymous visitor has no token to post an event with.
    if (this.token) void this._reportWidgetLanguage();

    if (inviteFailed) this.toast('This invite link is invalid or expired — ask for a new one.', 'error');
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
  private async _reportBuildSha(): Promise<void> {
    if (PointerFeedback._buildShaReported) return;
    try {
      const sha = document.documentElement?.dataset?.buildSha;
      if (!sha || !/^[0-9a-f]{7,40}$/.test(sha)) return;
      PointerFeedback._buildShaReported = true;
      await pfFetch(`${this.server}/api/projects/${encodeURIComponent(this.project)}/builds`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${this.token}` },
        body: JSON.stringify({ sha }),
      });
    } catch {
      /* never surface a build-beacon failure to a visitor */
    }
  }

  /**
   * Reports the widget's own UI language, the host page's `<html lang>`, and the visiting
   * browser's locale — once per tab session (sessionStorage guard `pointer_lang_reported`), so a
   * future widget-translation priority list can be based on real usage instead of guesswork. Fire-
   * and-forget and failure-silent, same as `_reportBuildSha`: a visitor must never see (or wait on)
   * a usage beacon. Posted through the existing authenticated `POST /api/events` pipeline, so an
   * anonymous visitor (no token) simply never reports — acceptable, see the plan.
   */
  private async _reportWidgetLanguage(): Promise<void> {
    try {
      if (sessionStorage.getItem('pointer_lang_reported') === '1') return;
      sessionStorage.setItem('pointer_lang_reported', '1');
    } catch { /* ignore — worst case this posts more than once per tab */ }
    try {
      await pfFetch(`${this.server}/api/events`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${this.token}` },
        body: JSON.stringify({
          type: 'widget_language',
          source: 'web-component',
          projectKey: this.project,
          meta: {
            ui: this.resolveLang(),
            browser: typeof navigator !== 'undefined' ? navigator.language : null,
            page: (typeof document !== 'undefined' && document.documentElement?.lang) || null,
          },
        }),
      });
    } catch {
      /* never surface a usage-beacon failure to a visitor */
    }
  }

  // Anonymous, pre-auth: asks the server whether this project should render on this page's
  // origin at all (gates on the project's overall activation AND, if this origin matches a
  // configured "other environment" URL, that specific mapping's own active flag). A network
  // failure or non-OK response is treated as "keep hidden," not "fail open" — an outage in
  // this check must not accidentally show the widget where it was explicitly deactivated.
  private async _checkWidgetActive(): Promise<boolean> {
    try {
      const origin = typeof window !== 'undefined' ? window.location.origin : '';
      const url = `${this.server}/api/public/projects/${encodeURIComponent(this.project)}/widget-status`
        + `?origin=${encodeURIComponent(origin)}`;
      const res = await pfFetch(url);
      if (!res.ok) return false;
      const body = await res.json();
      const data = body?.data ?? body;
      return data?.active === true;
    } catch {
      return false;
    }
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
    if (this.root) this.root.innerHTML = ''; // removes toolbar, launcher, and pins (#fbk-pins-layer lives here)
  }

  private _stylesPromise: Promise<void> | null = null;

  private _stylesReady(): Promise<void> {
    if (this._stylesPromise) return this._stylesPromise;
    this._stylesPromise = new Promise((resolve) => {
      const link = this._styleLink;
      if (!link || link.sheet) return resolve();
      let done = false;
      const finish = () => { if (!done) { done = true; resolve(); } };
      link.addEventListener('load', finish, { once: true });
      link.addEventListener('error', async () => {
        try {
          const fetchOpts: RequestInit = { mode: 'cors' };
          if (CSS_INTEGRITY) {
            fetchOpts.integrity = CSS_INTEGRITY;
          }
          const cssUrl = link.href || CSS_URL;
          const res = await rawFetch(cssUrl, fetchOpts);
          if (res.ok) {
            const text = await res.text();
            if (typeof CSSStyleSheet !== 'undefined') {
              const sheet = new CSSStyleSheet();
              if (typeof sheet.replace === 'function') {
                await sheet.replace(text);
              } else if (typeof (sheet as any).replaceSync === 'function') {
                (sheet as any).replaceSync(text);
              }
              if (this.shadowRoot) {
                this.shadowRoot.adoptedStyleSheets = [sheet];
              }
            }
          }
        } catch {
          // fallback failed, resolve anyway
        } finally {
          finish();
        }
      }, { once: true });
      setTimeout(finish, 1500);
    });
    return this._stylesPromise;
  }

  disconnectedCallback(): void {
    window.removeEventListener('scroll', this._reposition, true);
    window.removeEventListener('resize', this._reposition);
    window.removeEventListener('resize', this._scheduleBackdropUpdate);
    window.removeEventListener('scroll', this._scheduleBackdropUpdate, true);
    this.root?.removeEventListener('transitionend', this._scheduleBackdropUpdate);
    this._backdropObserver?.disconnect();
    if (this._backdropRaf) cancelAnimationFrame(this._backdropRaf);
    document.removeEventListener('keydown', this._onShortcutKeydown);
    if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
    this.stopPicking();
    this.stopNotificationPolling();
    this.closeUpdatesMenu();
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
  // prompts login or toggles element-picking, exactly like clicking #fbk-add.
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
    btnEl.textContent = t('menu.pressKeysToCancel');

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
        btnEl.textContent = t('menu.addModifierKey');
        return;
      }
      const binding: ShortcutBinding = {
        code: e.code,
        alt: e.altKey,
        shift: e.shiftKey,
        ctrl: e.ctrlKey,
        meta: e.metaKey,
      };
      btnEl.textContent = t('menu.saving');
      cleanup();
      this.saveShortcutPreference(binding).then((ok) => {
        btnEl.textContent = formatShortcut(this.shortcut);
        this.toast(ok ? t('menu.shortcutUpdated') : t('menu.failedToSaveTryAgain'), ok ? '' : 'error');
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
      this.updateAddButtonTooltip();
      return true;
    } catch {
      return false;
    }
  }

  // --- Auth helpers --------------------------------------------------------
  private loadAuth(): void {
    try {
      this.token = typeof localStorage !== 'undefined' ? localStorage.getItem('pointer_token') || null : null;
      const raw = typeof localStorage !== 'undefined' ? localStorage.getItem('pointer_user') : null;
      this.user = raw ? JSON.parse(raw) : null;
    } catch {
      this.token = null;
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
    this.startNotificationPolling();
  }

  private clearAuth(): void {
    this.token = null;
    this.user = null;
    this.unreadNotifyCount = 0;
    this.stopNotificationPolling();
    this.closeUpdatesMenu();
    localStorage.removeItem('pointer_token');
    localStorage.removeItem('pointer_user');
  }

  private handle401(): void {
    this.clearAuth();
    showLoginModal(this);
  }

  // --- Theme ------------------------------------------------------------
  // Deliberately WIDGET-LOCAL, not account-wide: `User.theme`/`/api/me/preferences` is the same
  // field the dashboard's own theme toggle reads to paint the entire admin app, so persisting the
  // widget's choice there would silently flip the dashboard's site-wide theme too. An explicit
  // per-browser override (localStorage, set from the user menu below) wins; otherwise the host
  // page's own rendered theme; otherwise the OS preference. See theme.ts.
  private resolveTheme(): ThemeMode {
    try {
      const stored = localStorage.getItem('pointer_widget_theme');
      if (stored === 'light' || stored === 'dark') return stored;
    } catch { /* ignore */ }
    return detectSiteTheme();
  }

  // Reflects the resolved mode onto the host element (light DOM, not shadowRoot) as
  // `data-fbk-theme` — _theme.scss's `:host([data-fbk-theme="dark"])` block reads it to swap the
  // shadow UI's token values, and a consuming app can target the same attribute from its own CSS
  // to override any single token per project, exactly like the light defaults.
  private applyTheme(): void {
    this.setAttribute('data-fbk-theme', this.resolveTheme());
  }

  // Sets the widget's own per-browser theme override — never touches the account (see the note
  // on resolveTheme above), so it can never bleed into the host dashboard's own theme.
  private setThemeOverride(mode: ThemeMode): void {
    try { localStorage.setItem('pointer_widget_theme', mode); } catch { /* ignore */ }
    this.applyTheme();
  }

  // --- Language ---------------------------------------------------------
  // Same widget-local shape as theme above, for the same reason: `User.language` is the SAME
  // field the dashboard's own language switcher writes to paint the whole admin app, so an
  // explicit per-browser override (localStorage, set from the user menu below) must win without
  // ever being written back to the account — otherwise picking a language in the widget would
  // silently flip the dashboard's language too. Falling back to the account's EXISTING value
  // (read-only) when there is no local override yet is fine — the widget just never sets it.
  private resolveLang(): Lang {
    try {
      const stored = localStorage.getItem('pointer_widget_language');
      if (stored === 'ar' || stored === 'en') return stored;
    } catch { /* ignore */ }
    const accountLang = this.user?.language;
    if (accountLang === 'ar') return 'ar';
    if (accountLang === 'en') return 'en';
    try { return (navigator.language || '').toLowerCase().startsWith('ar') ? 'ar' : 'en'; } catch { return 'en'; }
  }

  // Sets the widget's own per-browser language override — never touches the account (see the
  // note on resolveLang above). The caller (wireLangBtn in toggleUserMenu) still needs to
  // re-render everything language-bearing afterward — unlike theme, a language change can't be
  // reflected by a CSS attribute flip alone, since the text itself is baked into rendered markup.
  private setLanguageOverride(lang: Lang): void {
    try { localStorage.setItem('pointer_widget_language', lang); } catch { /* ignore */ }
    setLang(lang);
  }

  async init(): Promise<void> {
    // Load the status catalog from the server before the first render so that
    // filter chips and status labels reflect server-configured values.
    // Falls back to STATUS_FALLBACK silently if the fetch fails.
    await loadStatusCatalog(this.server);
    this.renderChrome();
    await Promise.all([this.fetchComments(), this.fetchPredefinedActions(), this.fetchCaptureConfig()]);
    if (this.token) this.startNotificationPolling();
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
      if (envelope?.data && typeof envelope.data.captureTextContent === 'boolean') {
        this.captureTextContent = envelope.data.captureTextContent;
      }
      this.projectName = (envelope && envelope.data && envelope.data.name) || this.project;
      // Missing/malformed → true (matches the pre-existing, always-switchable behavior).
      const showSelector = envelope?.data?.showEnvironmentSelector;
      this.showEnvironmentSelector = showSelector !== false;
      this.projectId = typeof envelope?.data?.id === 'number' ? envelope.data.id : null;

      // The server resolved this request's origin against the project's registered URLs. Applied
      // only when the page did not state an environment itself — see `environmentExplicit`.
      const resolved = envelope?.data?.resolvedEnvironment;
      if (!this.environmentExplicit && typeof resolved === 'number' && ENV_NAME[resolved] && resolved !== this.environmentInt) {
        this.environmentInt = resolved;
        this.environmentAttr = ENV_NAME[resolved];
        // Skipped while viewing "All": environmentInt/environmentAttr still need the real resolved
        // environment (a NEW comment composed in this state must be tagged with it, not "all"),
        // but a refetch here would silently narrow the list down to one environment, undoing the
        // viewer's choice — the renderSidebar() below still picks up the corrected value for
        // composing without needing to touch what's currently shown.
        if (!this.viewAllEnvironments) {
          // init() fires this request CONCURRENTLY with the very first fetchComments() (Promise.all)
          // — that first call always ran with the pre-resolution environment (0, "unknown"), which
          // the server has no comments for, so the sidebar/pins silently rendered empty until
          // something else (e.g. touching the environment dropdown) happened to trigger a refetch.
          // Correcting it here means init()'s OWN renderSidebar()/renderPins() calls — which run
          // after this whole Promise.all settles — already see the right data.
          await this.fetchComments();
        }
      }
      this.commitStyle = typeof envelope?.data?.commitStyle === 'number' ? envelope.data.commitStyle : 1;
      this.canEditSettings = !!envelope?.data?.canEditSettings;
      this.updateCommentsHeading();
      // Rebuilds the filters row (status + environment selects) with whatever we just learned —
      // showEnvironmentSelector, hasFixedEnvironment and the corrected environmentAttr all live
      // there now, same as the status/author filters already do on every render.
      this.renderSidebar();
      this.renderCommitStyleControl();
      if (this.pageContextCaptureEnabled) startPageContextCapture(this.server, SCRIPT_SRC);
    } catch {
      this.pageContextCaptureEnabled = false;
      this.captureTextContent = true;
    }
  }

  // Patches the already-rendered "{project} comments" heading in place rather than a full
  // renderChrome() — re-rendering chrome here would drop the sidebar's open/closed state
  // mid-session. Needed because the initial renderChrome() runs before fetchCaptureConfig()
  // resolves the real project name, so the heading starts out showing the raw project key as a
  // fallback. The project name is shown only in this heading — not duplicated elsewhere in the
  // header, so there's nothing else to keep in step with it.
  private updateCommentsHeading(): void {
    const heading = this.root && this.root.querySelector('#fbk-comments-heading');
    if (heading) heading.textContent = t('toolbar.commentsHeading', { project: this.projectName });
  }

  // Translated display text for an environment key ('local'/'staging'/'production') — falls back
  // to the raw key for anything else (e.g. 'unknown', before the server has resolved one).
  private envDisplayLabel(key: string): string {
    const k = (key || '').toLowerCase();
    if (k === 'local') return t('toolbar.envLocal');
    if (k === 'staging') return t('toolbar.envStaging');
    if (k === 'production') return t('toolbar.envProduction');
    return key;
  }

  // Patches #fbk-commit-style in place (same reasoning as updateEnvironmentSelectorVisibility) —
  // hidden entirely unless the current caller is authorized to change it (canEditSettings), so a
  // stakeholder who couldn't save the PATCH never sees a control that would just 403.
  private renderCommitStyleControl(): void {
    const host = this.root && this.root.querySelector('#fbk-commit-style');
    if (!host) return;
    if (!this.canEditSettings) { host.classList.add('fbk-hidden'); return; }
    host.classList.remove('fbk-hidden');
    host.innerHTML = TPL.commitStyleControl(this.commitStyle);
    const sel = this.root!.querySelector('#fbk-commit-style-select') as HTMLSelectElement | null;
    if (sel) sel.addEventListener('change', () => this.setCommitStyle(Number(sel.value)));
  }

  private async setCommitStyle(value: number): Promise<void> {
    if (this.projectId == null || (value !== 1 && value !== 2)) return;
    const previous = this.commitStyle;
    this.commitStyle = value;
    try {
      const r = await this.api(`/api/admin/projects/${this.projectId}`, {
        method: 'PATCH',
        body: JSON.stringify({ commitStyle: value }),
      });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      this.toast(t('toast.commitStyleUpdated'));
    } catch (e) {
      this.commitStyle = previous;
      this.renderCommitStyleControl();
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.updateFailed'), 'error');
    }
  }

  // Keeps the "Comment on an element" button's tooltip showing the current shortcut after it's
  // changed from the user menu — same in-place-patch reasoning as updateCommentsHeading().
  private updateAddButtonTooltip(): void {
    const btn = this.root && this.root.querySelector('#fbk-add');
    if (!btn) return;
    const label = formatShortcut(this.shortcut);
    btn.setAttribute('title', `${t('toolbar.commentOnElement')} (${label})`);
    btn.setAttribute('aria-label', t('toolbar.commentOnElementShortcut', { label }));
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
    if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || t('auth.couldNotLoadRoles'));
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
      // Declares which kind of client this is, so the server knows it may include the advisory
      // payload flags (R2-06). Not an auth signal — it is trivially forgeable and nothing
      // security-critical depends on it. Its job is that the documented AI paths, which never send
      // it, never receive the flag.
      'X-Pointer-Client': 'widget',
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

  /**
   * Removes `?pointer_invite=` from the address bar and returns the token it held.
   *
   * SYNCHRONOUS AND FIRST, on every path including failure. Every comment captures
   * `window.location.href` and the route into its element capture, so a token still in the URL when
   * someone comments is persisted into the database and handed to anyone who can read that comment.
   * Stripping before any await — and before the redemption can fail — is what makes that
   * impossible. Other query params and the hash are preserved.
   */
  stripInviteTokenFromUrl(): string | null {
    try {
      const url = new URL(window.location.href);
      const token = url.searchParams.get('pointer_invite');
      if (!token) return null;

      url.searchParams.delete('pointer_invite');
      window.history.replaceState({}, '', url.toString());
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
  async redeemInviteToken(token: string): Promise<boolean> {
    try {
      // A hanging server must not stop the widget booting into its normal login.
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 3000);
      const res = await pfFetch(`${this.server}/api/auth/login-with-invite`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token }),
        signal: controller.signal,
      }).finally(() => clearTimeout(timer));

      const envelope = await res.json();
      const data = envelope?.data ?? envelope;
      if (res.ok && data?.status === 'ok' && data.token) {
        this.saveAuth(data.token, data.user ?? null);
        return true;
      }
    } catch {
      // Fall through to the normal login modal.
    }
    return false;
  }

  async fetchComments(): Promise<void> {
    try {
      // Omitting `environment` entirely (rather than passing any int) is what asks the server for
      // every environment — the filter is nullable server-side (CommentFilter.Environment), and
      // 0 is a real value ("unknown"), not a wildcard.
      const envQuery = this.viewAllEnvironments ? '' : `?environment=${this.environmentInt}`;
      const r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments${envQuery}`);
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
        this.toast(t('toast.couldNotReachServer', { brand: getBrandName() }), 'error', t('toast.retry'), () => {
          this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
        });
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
      this.root.innerHTML = TPL.launcher(n, this.launcherPosition, pageIsRtl(), this.unreadNotifyCount);
      const launcher = this.root.querySelector('#fbk-launcher');
      if (launcher) launcher.addEventListener('click', () => this.showOverlay());
      return;
    }

    const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
    const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
    // Computed from the RAW name (before escaping above) — see the chrome() doc comment.
    const avatarInitials = this.user ? escapeHtml(initials(this.user.displayName || this.user.email || '')) : '';
    this.root.innerHTML = TPL.chrome(displayName, roleLabel, this.projectName || this.project, formatShortcut(this.shortcut), this.unreadNotifyCount, avatarInitials, ariaKeyshortcuts(this.shortcut));

    const hideBtn = this.root.querySelector('#fbk-hide');
    if (hideBtn) hideBtn.addEventListener('click', () => this.hideOverlay());

    const userBtn = this.root.querySelector('#fbk-user');
    if (userBtn) userBtn.addEventListener('click', (e) => { e.stopPropagation(); this.toggleUserMenu(); });

    const updatesBtn = this.root.querySelector('#fbk-updates');
    if (updatesBtn) updatesBtn.addEventListener('click', (e) => { e.stopPropagation(); this.toggleUpdatesMenu(); });

    this.root.querySelector('#fbk-add')!.addEventListener('click', () => this.activateAddComment());
    this.root.querySelector('#fbk-toggle')!.addEventListener('click', () => {
      if (!this.token) { showLoginModal(this, () => { Promise.resolve(this.init()).then(() => this.toggleSidebar(true)); }); return; }
      this.toggleSidebar();
    });
    this.root.querySelector('#fbk-refresh')!.addEventListener('click', async () => {
      if (!this.token) { showLoginModal(this, () => this.init()); return; }
      await this.fetchComments(); this.renderSidebar(); this.renderPins(); this.toast(t('toast.refreshed'));
    });
    this.root.querySelector('#fbk-close')!.addEventListener('click', () => this.toggleSidebar(false));

    const resetBtn = this.root.querySelector('#fbk-reset-pos');
    if (resetBtn) resetBtn.addEventListener('click', () => this.resetToolbarPos());

    this.restoreToolbarPos();
    this.enableToolbarDrag();
    this.updateMenuSide();
  }

  // Switch the active environment from the toolbar. Comments are environment-scoped, so this
  // re-queries the server and re-renders; the choice is remembered per project on this origin.
  // "all" is a widget-only filter state (see viewAllEnvironments' field doc) — it doesn't touch
  // environmentAttr/environmentInt, which keep tracking the real environment for tagging new
  // comments composed while every environment is shown.
  setEnvironment(env: string): void {
    const key = (env || '').toLowerCase();
    if (key === 'all') {
      if (this.viewAllEnvironments) return;
      this.viewAllEnvironments = true;
      try { localStorage.setItem('pointer_env_' + this.project, 'all'); } catch (e) { /* ignore */ }
      if (!this.token) return;
      this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
      return;
    }
    if (!ENV_MAP[key] || (!this.viewAllEnvironments && key === this.environmentAttr.toLowerCase())) return;
    this.viewAllEnvironments = false;
    this.environmentAttr = key;
    this.environmentInt = ENV_MAP[key];
    try { localStorage.setItem('pointer_env_' + this.project, key); } catch (e) { /* ignore */ }
    if (!this.token) return; // not signed in yet — the new env applies on next fetch
    this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
  }

  // --- Draggable toolbar ---------------------------------------------------
  // The toolbar's default corner is bottom-right (CSS inset-block-end/inset-inline-end); let the
  // user drag it by its grip so it never covers the element they want to comment on. Dragging
  // sets a `translate(dx, dy)` offset (--fbk-toolbar-dx/dy) rather than switching the anchor
  // to absolute left/top, so the anchor itself never changes — only the offset from it does.
  // Position persists per tab.
  private restoreToolbarPos(): void {
    const tb = this.root.querySelector('.fbk-toolbar') as HTMLElement | null;
    if (!tb) return;
    let saved: { dx: number; dy: number } | null = null;
    try { saved = JSON.parse(localStorage.getItem('pointer_toolbar_pos') || 'null'); } catch { /* ignore */ }
    if (!saved || typeof saved.dx !== 'number' || typeof saved.dy !== 'number') return;
    // The translate is still 0 here, so this rect IS the untranslated anchor position — clamp
    // the saved offset against it so a viewport that's shrunk since last time can't push the
    // toolbar off-screen.
    const rect = tb.getBoundingClientRect();
    const maxDx = Math.max(0, window.innerWidth - rect.width) - rect.left;
    const maxDy = Math.max(0, window.innerHeight - rect.height) - rect.top;
    this._toolbarDx = Math.min(Math.max(saved.dx, -rect.left), maxDx);
    this._toolbarDy = Math.min(Math.max(saved.dy, -rect.top), maxDy);
    this.applyToolbarOffset(tb);
    tb.classList.add('is-moved');
  }

  private applyToolbarOffset(tb: HTMLElement): void {
    tb.style.setProperty('--fbk-toolbar-dx', `${this._toolbarDx}px`);
    tb.style.setProperty('--fbk-toolbar-dy', `${this._toolbarDy}px`);
  }

  // Restore the toolbar to its default corner and forget the saved position.
  private resetToolbarPos(): void {
    try { localStorage.removeItem('pointer_toolbar_pos'); } catch { /* ignore */ }
    this._toolbarDx = 0;
    this._toolbarDy = 0;
    const tb = this.root.querySelector('.fbk-toolbar') as HTMLElement | null;
    if (tb) {
      tb.style.removeProperty('--fbk-toolbar-dx');
      tb.style.removeProperty('--fbk-toolbar-dy');
      tb.classList.remove('is-moved');
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
  private updateMenuSide(): void {
    const tb = this.root.querySelector('.fbk-toolbar') as HTMLElement | null;
    if (!tb) return;
    const rect = tb.getBoundingClientRect();
    const spaceBelow = window.innerHeight - rect.bottom;
    // 340px comfortably covers every dropdown's own max-height (the tallest, the notifications
    // menu, caps at 320px) plus the anchor gap.
    tb.dataset.fbkMenuSide = spaceBelow < 340 ? 'top' : 'bottom';
  }

  // Vertically anchors a toolbar dropdown (account/updates/pin-cluster) above or below `rect`
  // per updateMenuSide()'s reading of the toolbar's own position — shared by all three so a
  // toolbar sitting near the bottom edge doesn't open a menu that renders off-screen below it.
  private positionMenuVertically(menu: HTMLElement, rect: DOMRect, gap = 6): void {
    const openUp = (this.root.querySelector('.fbk-toolbar') as HTMLElement | null)?.dataset.fbkMenuSide === 'top';
    if (openUp) {
      menu.style.top = 'auto';
      menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - rect.top + gap))}px`;
    } else {
      menu.style.bottom = 'auto';
      menu.style.top = `${Math.round(rect.bottom + gap)}px`;
    }
  }

  private enableToolbarDrag(): void {
    const tb = this.root.querySelector('.fbk-toolbar') as HTMLElement | null;
    const grip = this.root.querySelector('#fbk-grip') as HTMLElement | null;
    if (!tb || !grip) return;
    let sx = 0, sy = 0, startDx = 0, startDy = 0, baseLeft = 0, baseTop = 0, baseWidth = 0, baseHeight = 0, dragging = false;
    const onMove = (e: PointerEvent) => {
      if (!dragging) return;
      const maxDx = Math.max(0, window.innerWidth - baseWidth) - baseLeft;
      const maxDy = Math.max(0, window.innerHeight - baseHeight) - baseTop;
      this._toolbarDx = Math.min(Math.max(startDx + (e.clientX - sx), -baseLeft), maxDx);
      this._toolbarDy = Math.min(Math.max(startDy + (e.clientY - sy), -baseTop), maxDy);
      this.applyToolbarOffset(tb);
    };
    const onUp = (e: PointerEvent) => {
      if (!dragging) return;
      dragging = false;
      tb.classList.remove('is-dragging');
      try { grip.releasePointerCapture(e.pointerId); } catch { /* ignore */ }
      try { localStorage.setItem('pointer_toolbar_pos', JSON.stringify({ dx: this._toolbarDx, dy: this._toolbarDy })); } catch { /* ignore */ }
      tb.classList.add('is-moved');
      this.updateMenuSide();
    };
    grip.addEventListener('pointerdown', (e: PointerEvent) => {
      e.preventDefault();
      // Baseline = the current rect with the CURRENT offset subtracted back out, so dragging
      // from an already-moved position still clamps against the true anchor, not the shifted one.
      const rect = tb.getBoundingClientRect();
      baseLeft = rect.left - this._toolbarDx;
      baseTop = rect.top - this._toolbarDy;
      baseWidth = rect.width;
      baseHeight = rect.height;
      startDx = this._toolbarDx; startDy = this._toolbarDy;
      sx = e.clientX; sy = e.clientY;
      dragging = true;
      tb.classList.add('is-dragging');
      try { grip.setPointerCapture(e.pointerId); } catch { /* ignore */ }
    });
    grip.addEventListener('pointermove', onMove);
    grip.addEventListener('pointerup', onUp);
    grip.addEventListener('pointercancel', onUp);
  }

  // --- User menu (identity + sign out) ------------------------------------
  private toggleUserMenu(): void {
    this.closeUpdatesMenu();
    this.closeClusterMenu();
    this.closeCardMenu();
    const host = this.root.querySelector('#fbk-menu-host') as HTMLElement | null;
    if (!host) return;
    if (host.querySelector('#fbk-user-menu')) { this.closeUserMenu(); return; }

    const displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : '';
    const roleLabel = this.user ? escapeHtml(this.user.roleName || '') : '';
    host.innerHTML = TPL.userMenu(
      displayName,
      roleLabel,
      formatShortcut(this.shortcut),
      this.authOwnedByHost,
      this.resolveTheme(),
      this.resolveLang(),
    );
    const menu = host.querySelector('#fbk-user-menu') as HTMLElement;

    // Anchor the dropdown under the user icon.
    const btn = this.root.querySelector('#fbk-user') as HTMLElement | null;
    if (btn) {
      btn.setAttribute('aria-expanded', 'true');
      const r = btn.getBoundingClientRect();
      this.positionMenuVertically(menu, r);
      menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
    }

    // No sign-out control to wire up when the extension owns auth (see authOwnedByHost).
    const signoutBtn = host.querySelector('#fbk-signout') as HTMLElement | null;
    if (signoutBtn) signoutBtn.addEventListener('click', () => this.signOut());
    (host.querySelector('#fbk-shortcut-edit') as HTMLElement).addEventListener('click', (e) => {
      e.stopPropagation();
      this.beginRecordingShortcut(host.querySelector('#fbk-shortcut-edit') as HTMLElement);
    });
    (host.querySelector('#fbk-shortcut-reset') as HTMLElement).addEventListener('click', async (e) => {
      e.stopPropagation();
      const editBtn = host.querySelector('#fbk-shortcut-edit') as HTMLElement | null;
      if (editBtn) editBtn.textContent = t('menu.resetting');
      const ok = await this.saveShortcutPreference(null);
      if (editBtn) editBtn.textContent = formatShortcut(this.shortcut);
      this.toast(ok ? t('menu.shortcutResetToDefault') : t('menu.failedToResetTryAgain'), ok ? '' : 'error');
    });

    // Re-opens the menu fresh (rather than patching two buttons' classes in place) so its
    // active/pressed state always matches what was actually persisted.
    const reopenUserMenu = () => { this.closeUserMenu(); this.toggleUserMenu(); };

    const wireThemeBtn = (id: string, mode: ThemeMode) => {
      (host.querySelector(id) as HTMLElement).addEventListener('click', (e) => {
        e.stopPropagation();
        if (this.resolveTheme() === mode) return;
        this.setThemeOverride(mode);
        reopenUserMenu();
      });
    };
    wireThemeBtn('#fbk-theme-light', 'light');
    wireThemeBtn('#fbk-theme-dark', 'dark');

    const wireLangBtn = (id: string, lang: Lang) => {
      (host.querySelector(id) as HTMLElement).addEventListener('click', (e) => {
        e.stopPropagation();
        if (this.resolveLang() === lang) return;
        this.setLanguageOverride(lang);
        // Unlike theme (a CSS attribute flip), every piece of UI text is baked into the rendered
        // markup — a language change needs a real re-render, not just re-opening the menu.
        // reopenUserMenu() runs AFTER since renderChrome() wipes and rebuilds
        // #fbk-menu-host/#fbk-user.
        this.renderChrome();
        this.renderSidebar();
        this.renderPins();
        reopenUserMenu();
      });
    };
    wireLangBtn('#fbk-lang-en', 'en');
    wireLangBtn('#fbk-lang-ar', 'ar');

    // Close on click outside (composedPath crosses the shadow boundary).
    this._userMenuClose = (e: MouseEvent) => {
      const path = e.composedPath();
      if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUserMenu();
    };
    setTimeout(() => { if (this._userMenuClose) document.addEventListener('click', this._userMenuClose, true); }, 0);
  }

  private closeUserMenu(): void {
    const host = this.root.querySelector('#fbk-menu-host');
    if (host && host.querySelector('#fbk-user-menu')) host.innerHTML = '';
    (this.root.querySelector('#fbk-user') as HTMLElement | null)?.setAttribute('aria-expanded', 'false');
    if (this._userMenuClose) {
      document.removeEventListener('click', this._userMenuClose, true);
      this._userMenuClose = null;
    }
    // The menu (and its shortcut-edit button) is about to be gone — tear down any in-flight
    // "press keys…" recording so it can't silently capture some unrelated future keystroke.
    if (this._shortcutRecordingCleanup) this._shortcutRecordingCleanup();
  }

  // --- Updates menu (in-app notifications) --------------------------------
  private async toggleUpdatesMenu(): Promise<void> {
    const host = this.root.querySelector('#fbk-menu-host') as HTMLElement | null;
    if (!host) return;
    if (host.querySelector('#fbk-notifications-menu')) {
      this.closeUpdatesMenu();
      return;
    }
    this.closeUserMenu();
    this.closeClusterMenu();
    this.closeCardMenu();

    const items = await this.apiNotifications();
    if (this.unreadNotifyCount > 0) {
      await this.apiMarkAllNotificationsRead();
      this.unreadNotifyCount = 0;
      this.updateNotifyBadges();
    }

    host.innerHTML = TPL.notificationsMenu(items);
    const menu = host.querySelector('#fbk-notifications-menu') as HTMLElement | null;
    if (!menu) return;

    // Anchor the dropdown under the Updates button.
    const btn = this.root.querySelector('#fbk-updates') as HTMLElement | null;
    if (btn) {
      btn.setAttribute('aria-expanded', 'true');
      const r = btn.getBoundingClientRect();
      this.positionMenuVertically(menu, r);
      menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 330, Math.round(r.left)))}px`;
    }

    // Clicking an item opens the sidebar, switches filter to 'all' if needed, and scrolls to/highlights card.
    menu.querySelectorAll('.fbk-notification-item').forEach((el) => {
      el.addEventListener('click', () => {
        const commentId = el.getAttribute('data-id');
        this.closeUpdatesMenu();
        if (commentId) {
          const c = this.comments.find((x) => String(x.id) === String(commentId));
          if (c && this.statusFilter !== 'all' && this.statusFilter !== c.status) {
            this.statusFilter = 'all';
          }
          this.toggleSidebar(true);
          this.renderSidebar();
          setTimeout(() => {
            const card = this.root.querySelector(`.fbk-card[data-id="${commentId}"]`) as HTMLElement | null;
            if (card) {
              card.scrollIntoView({ behavior: 'smooth', block: 'center' });
              card.classList.add('highlight');
              setTimeout(() => card.classList.remove('highlight'), 2000);
            }
          }, 100);
        }
      });
    });

    // Close on click outside (composedPath crosses the shadow boundary).
    this._updatesMenuClose = (e: MouseEvent) => {
      const path = e.composedPath();
      if (!path.includes(menu) && (!btn || !path.includes(btn))) this.closeUpdatesMenu();
    };
    setTimeout(() => { if (this._updatesMenuClose) document.addEventListener('click', this._updatesMenuClose, true); }, 0);
  }

  private closeUpdatesMenu(): void {
    const host = this.root.querySelector('#fbk-menu-host');
    if (host && host.querySelector('#fbk-notifications-menu')) host.innerHTML = '';
    (this.root.querySelector('#fbk-updates') as HTMLElement | null)?.setAttribute('aria-expanded', 'false');
    if (this._updatesMenuClose) {
      document.removeEventListener('click', this._updatesMenuClose, true);
      this._updatesMenuClose = null;
    }
  }

  // --- Notification Polling & Verification ---------------------------------
  startNotificationPolling(): void {
    this.stopNotificationPolling();
    this.fetchUnreadNotifyCount();
    const pollInterval = window.__POINTER_CONFIG__?.notifyPollMs ?? 60000;
    this._notifyPollTimer = window.setInterval(() => {
      if (document.visibilityState === 'visible') {
        this.fetchUnreadNotifyCount();
      }
    }, pollInterval);
    this._onVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        this.fetchUnreadNotifyCount();
      }
    };
    document.addEventListener('visibilitychange', this._onVisibilityChange);
  }

  stopNotificationPolling(): void {
    if (this._notifyPollTimer !== null) {
      window.clearInterval(this._notifyPollTimer);
      this._notifyPollTimer = null;
    }
    if (this._onVisibilityChange) {
      document.removeEventListener('visibilitychange', this._onVisibilityChange);
      this._onVisibilityChange = null;
    }
  }

  async fetchUnreadNotifyCount(): Promise<void> {
    if (!this.token) return;
    try {
      const r = await this.api('/api/me/notifications/unread-count');
      if (!r.ok) return;
      const envelope = await r.json();
      const count = typeof envelope?.data?.count === 'number'
        ? envelope.data.count
        : (typeof envelope?.count === 'number' ? envelope.count : 0);
      this.unreadNotifyCount = count;
      this.updateNotifyBadges();
    } catch {
      // Silently ignore
    }
  }

  updateNotifyBadges(): void {
    if (this._collapsed) {
      this.renderChrome();
      return;
    }
    // A plain dot, not a count — the exact number lives in the Updates dropdown itself, and in
    // the button's own aria-label for anyone who can't see the dot.
    const dot = this.root.querySelector('#fbk-notify-count') as HTMLElement | null;
    if (dot) dot.classList.toggle('fbk-hidden', this.unreadNotifyCount <= 0);
    const updatesBtn = this.root.querySelector('#fbk-updates') as HTMLElement | null;
    if (updatesBtn) {
      const n = this.unreadNotifyCount;
      updatesBtn.setAttribute('aria-label', `Updates${n > 0 ? `, ${n > 99 ? '99+' : n} unread` : ''}`);
    }
  }

  async apiNotifications(unread = false): Promise<NotificationItem[]> {
    try {
      const r = await this.api(`/api/me/notifications${unread ? '?unread=true' : ''}`);
      if (!r.ok) return [];
      const envelope = await r.json();
      const items = envelope?.data ?? envelope;
      return Array.isArray(items) ? items : [];
    } catch {
      return [];
    }
  }

  async apiMarkAllNotificationsRead(): Promise<void> {
    try {
      await this.api('/api/me/notifications/read-all', { method: 'POST' });
    } catch {
      // Silently ignore
    }
  }

  async apiVerify(id: number | string, ok: boolean, note?: string): Promise<boolean> {
    try {
      const r = await this.api(`/api/comments/${id}/verify`, {
        method: 'POST',
        body: JSON.stringify({ ok, note: note || null }),
      });
      if (!r.ok) {
        let errMessage = t('toast.failedToVerifyComment');
        try {
          const err = await r.json();
          if (err?.message) errMessage = err.message;
        } catch {}
        this.toast(errMessage, 'error');
        return false;
      }
      const envelope = await r.json();
      const updated = envelope?.data ?? envelope;
      const idx = this.comments.findIndex((c) => String(c.id) === String(id));
      if (idx !== -1 && updated) {
        const normalizedComment: Comment = {
          ...this.comments[idx],
          ...updated,
          status: typeof updated.status === 'number' ? (STATUS_STR[updated.status] || 'open') : (updated.status || 'open'),
          verifiedAt: updated.verifiedAt ?? null,
        };
        this.comments[idx] = normalizedComment;
      } else {
        await this.fetchComments();
      }
      this.renderSidebar();
      this.renderPins();
      this.toast(ok ? t('toast.commentVerified') : t('toast.commentReopened'));
      return true;
    } catch {
      this.toast(t('toast.failedToVerifyComment'), 'error');
      return false;
    }
  }

  // Clear the session and reset the widget to its logged-out (deferred-login) state.
  signOut(): void {
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
    this.statusFilter = 'all';
    this.renderChrome();
    this.renderSidebar();
    this.renderPins();
    this.toast(t('toast.signedOut'));
  }

  // Collapse the overlay to the floating launcher (remembered for this tab session).
  hideOverlay(): void {
    if (this.picking) this.stopPicking();
    this.sidebarOpen = false;
    this._collapsed = true;
    try { sessionStorage.removeItem('pointer_visible'); } catch (e) {}
    this.renderChrome();
    this.toast(t('toast.hiddenClickToReopen', { brand: getBrandName() }));
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
    this.root.querySelector('#fbk-sidebar')!.classList.toggle('open', this.sidebarOpen);
    (this.root.querySelector('#fbk-toggle') as HTMLElement | null)?.setAttribute('aria-expanded', String(this.sidebarOpen));
    // Opening → pull fresh server state so applied/"completed" comments show.
    if (this.sidebarOpen) {
      this.fetchComments().then(() => { this.renderSidebar(); this.renderPins(); });
    }
  }

  // --- Staying clickable under host-app modals ------------------------------
  // The rects our own UI currently occupies on screen — every top-level container that can be
  // visible at once. Used to punch matching holes in any modal backdrop so those areas stay
  // clickable. Elements not currently rendered/visible in this.root simply aren't found and are
  // skipped; no need to check display/visibility explicitly.
  private ownUiRects(): DOMRect[] {
    const selectors = ['.fbk-launcher', '.fbk-toolbar', '.fbk-sidebar', '.fbk-modal-overlay', '.fbk-popover', '.fbk-menu'];
    const rects: DOMRect[] = [];
    for (const sel of selectors) {
      const el = this.root?.querySelector(sel) as HTMLElement | null;
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
  private punchBackdropHoles(): void {
    const targets = document.querySelectorAll<HTMLElement>(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
    if (targets.length === 0) return;
    const rects = this.ownUiRects();
    // clip-path is relative to EACH element's own box (see buildClipPathWithHoles) — a full-viewport
    // backdrop and a small, positioned dialog pane need their holes computed separately, not shared.
    targets.forEach((t) => {
      const clipPath = rects.length === 0 ? '' : buildClipPathWithHoles(rects, t.getBoundingClientRect());
      if (t.style.clipPath !== clipPath) t.style.clipPath = clipPath;
    });
  }

  // --- Element picking -----------------------------------------------------
  togglePicking(): void {
    this.picking ? this.stopPicking() : this.startPicking();
  }
  startPicking(): void {
    this.picking = true;
    // Pins are `pointer-events: auto` so they can be clicked to open their comment — which means a
    // pin sitting over an element makes that element impossible to comment on: the click retargets
    // to our own host, resolveHitTarget() discards it as own-UI, and the pick silently does nothing.
    // The more pins a page accumulates, the more of it becomes un-commentable. Mark the pin layer
    // for the duration of picking so clicks fall through to the page underneath.
    this.root.querySelector('#fbk-pins-layer')?.classList.add('picking');
    const tb = this.root.querySelector('.fbk-toolbar') as HTMLElement | null;
    tb?.classList.add('is-dim'); // the rest of the toolbar steps back — only Cancel stays live
    // Stays `--primary` throughout — it's always the toolbar's main action; only its icon/label
    // and aria-pressed change to reflect "start picking" vs "cancel".
    const addBtn = this.root.querySelector('#fbk-add') as HTMLButtonElement;
    addBtn.setAttribute('aria-pressed', 'true');
    addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.close}</span>`;
    addBtn.title = t('toolbar.cancel');
    addBtn.setAttribute('aria-label', t('toolbar.cancel'));
    document.addEventListener('mousemove', this._onHover, true);
    document.addEventListener('click', this._onPick, true);
    document.addEventListener('keydown', this._onPickKey, true);
    this.toast(t('popover.clickAnyElementToComment'));
  }
  stopPicking(): void {
    this.picking = false;
    this.root?.querySelector('#fbk-pins-layer')?.classList.remove('picking');
    (this.root?.querySelector('.fbk-toolbar') as HTMLElement | null)?.classList.remove('is-dim');
    const addBtn = this.root && (this.root.querySelector('#fbk-add') as HTMLButtonElement | null);
    if (addBtn) {
      addBtn.setAttribute('aria-pressed', 'false');
      addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span>`;
    }
    // Restores the shortcut-suffixed tooltip (updateAddButtonTooltip), not a bare "Comment on an
    // element" — the button's title otherwise loses the "(⌃⌥⇧C)" hint the very first time picking
    // mode is entered and cancelled, since that hint isn't part of the icon-swap above.
    this.updateAddButtonTooltip();
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
    this.toast(t('popover.cancelled'));
  }
  clearHover(): void {
    if (this.hovered) { this.hovered.classList.remove(HL_CLASS); this.hovered = null; }
  }
  isOwnElement(el: EventTarget | null): boolean {
    return el === this || (!!el && (el as Element).tagName === 'POINTER-FEEDBACK');
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
  resolveHitTarget(e: MouseEvent): Element | null {
    const target = e.target as Element | null;
    if (!target || this.isOwnElement(target)) return null;
    if (!target.matches(BACKDROP_SELECTOR)) return target;
    for (const el of document.elementsFromPoint(e.clientX, e.clientY)) {
      if (this.isOwnElement(el)) continue;
      if (el.matches(BACKDROP_SELECTOR)) continue;
      return el;
    }
    return null;
  }

  onHover(e: MouseEvent): void {
    const el = this.resolveHitTarget(e);
    if (!el) return;
    if (el === this.hovered) return;
    this.clearHover();
    this.hovered = el;
    el.classList.add(HL_CLASS);
  }
  onPick(e: MouseEvent): void {
    const el = this.resolveHitTarget(e);
    if (!el) return; // clicks on our own UI pass through
    e.preventDefault();
    e.stopPropagation();
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
    // `currentEl`/`currentMeta` — mutable: the up/down nav buttons below let the viewer retarget
    // the comment to the clicked element's parent or first child without closing and re-picking,
    // for the "clicked the wrong element" case. Everything downstream (screenshot capture, the
    // highlight, and the eventual createComment call) reads these, not the original `el`.
    let currentEl: Element = el;
    let currentMeta = captureMetadata(currentEl, this.sourceAttr, { captureText: this.captureTextContent });
    const host = this.root.querySelector('#fbk-popover-host') as HTMLElement;
    // Render at the raw click point first so the REAL box can be measured below — a fixed
    // height guess here (this used to clamp against a hardcoded 220px) goes stale the moment the
    // popover gains a new field (predefined actions, screenshot preview, bug-report checkbox,
    // ...) and starts rendering off-screen below the fold for a click near the bottom of the
    // page — unreachable, can't type or submit. Width IS fixed by CSS (280px), but measuring it
    // too costs nothing and stays correct if that ever changes.
    host.innerHTML = TPL.popover(currentMeta, x, y, this.screenshotEnabled, this.predefinedActions, this.pageContextCaptureEnabled);
    // Position applied through the CSSOM, not a style attribute — see applyDataPosition.
    applyDataPosition(host, '.fbk-popover');
    const popoverEl = host.querySelector('.fbk-popover') as HTMLElement | null;
    if (popoverEl) {
      const rect = popoverEl.getBoundingClientRect();
      const margin = 8;
      const left = Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin));
      const top = Math.max(margin, Math.min(y, window.innerHeight - rect.height - margin));
      popoverEl.style.left = `${Math.round(left)}px`;
      popoverEl.style.top = `${Math.round(top)}px`;
    }
    // Highlight the current target on the actual page (same visual language as the picking-mode
    // hover) so retargeting via the nav buttons below is visible, not just reflected in the text.
    currentEl.classList.add(HL_CLASS);
    const ta = host.querySelector('#fbk-comment-text') as HTMLTextAreaElement;
    ta.focus();
    // Element nav — move the comment's target up to its parent or down to its first child, for
    // when the wrong element got picked. Disabled at either end (no parent left to go up to, no
    // child to go down into) rather than hidden, so the row's width stays stable.
    const upBtn = host.querySelector('#fbk-target-up') as HTMLButtonElement | null;
    const downBtn = host.querySelector('#fbk-target-down') as HTMLButtonElement | null;
    const titleEl = host.querySelector('#fbk-popover-title') as HTMLElement | null;
    const snippetEl = host.querySelector('#fbk-popover-snippet') as HTMLElement | null;
    const srcEl = host.querySelector('#fbk-popover-src') as HTMLElement | null;
    const srcPathEl = host.querySelector('#fbk-popover-src-path') as HTMLElement | null;
    const updateNavButtons = () => {
      if (upBtn) upBtn.disabled = !currentEl.parentElement;
      if (downBtn) downBtn.disabled = currentEl.children.length === 0;
    };
    const navigateTo = (nextEl: Element) => {
      currentEl.classList.remove(HL_CLASS);
      currentEl = nextEl;
      currentMeta = captureMetadata(currentEl, this.sourceAttr, { captureText: this.captureTextContent });
      currentEl.classList.add(HL_CLASS);
      currentEl.scrollIntoView({ block: 'nearest', inline: 'nearest' });
      if (titleEl) titleEl.innerHTML = `Comment on &lt;${escapeHtml(currentMeta._tag)}&gt;`;
      if (snippetEl) snippetEl.textContent = currentMeta._snapshotPreview.slice(0, 200);
      if (srcEl) srcEl.classList.toggle('fbk-hidden', !currentMeta._sourcePath);
      if (srcPathEl) srcPathEl.textContent = currentMeta._sourcePath || '';
      updateNavButtons();
    };
    if (upBtn) upBtn.addEventListener('click', () => {
      const parent = currentEl.parentElement;
      if (parent) navigateTo(parent);
    });
    if (downBtn) downBtn.addEventListener('click', () => {
      const child = currentEl.children[0];
      if (child) navigateTo(child);
    });
    updateNavButtons();
    // Private is opt-in (off by default) — a lock/unlock icon toggle rather than a checkbox, same
    // pattern as the card's own visibility toggle. Tracked here (not re-derived from the DOM at
    // submit time) so the click handler is the single place that updates both the icon and the
    // state together.
    let isPrivateComment = false;
    const privateToggle = host.querySelector('#fbk-comment-private') as HTMLButtonElement | null;
    if (privateToggle) privateToggle.addEventListener('click', () => {
      isPrivateComment = !isPrivateComment;
      privateToggle.classList.toggle('is-active', isPrivateComment);
      privateToggle.setAttribute('aria-pressed', String(isPrivateComment));
      // Same wording pattern as the card's own visibility toggle, for consistency.
      privateToggle.title = isPrivateComment ? t('card.privateClickToMakePublic') : t('card.makePrivateOnlyYou');
      privateToggle.setAttribute('aria-label', isPrivateComment ? t('card.makePublic') : t('card.makePrivate'));
      privateToggle.innerHTML = isPrivateComment ? ICON.lock : ICON.unlock;
    });
    // Predefined prompts — searchable multi-select combobox. `selectedActionIds` is read directly
    // at submit time (same pattern as isPrivateComment above) instead of re-deriving it from the
    // DOM. Declared here (not inside the `if` below) so cancelPopover/submit can always call
    // `stopMsListening` without a type error when the project has no predefined actions at all.
    const selectedActionIds = new Set<number>();
    let stopMsListening: (() => void) | null = null;
    const actionMsControl = host.querySelector('#fbk-action-ms-control') as HTMLElement | null;
    const actionMsInput = host.querySelector('#fbk-action-ms-input') as HTMLInputElement | null;
    const actionMsChips = host.querySelector('#fbk-action-ms-chips') as HTMLElement | null;
    const actionMsList = host.querySelector('#fbk-action-ms-list') as HTMLElement | null;
    if (actionMsControl && actionMsInput && actionMsChips && actionMsList) {
      const renderChips = () => {
        actionMsChips.innerHTML = Array.from(selectedActionIds).map((id) => {
          const a = this.predefinedActions.find((x) => x.id === id);
          if (!a) return '';
          return `<span class="fbk-ms-chip"><span class="fbk-ms-chip-label">${escapeHtml(a.text)}</span><button type="button" class="fbk-ms-chip-remove" data-id="${id}" aria-label="${t('popover.remove')} ${escapeHtml(a.text)}">&times;</button></span>`;
        }).join('');
        actionMsChips.querySelectorAll('.fbk-ms-chip-remove').forEach((btn) => {
          btn.addEventListener('click', (e) => {
            e.stopPropagation();
            selectedActionIds.delete(Number((btn as HTMLElement).dataset.id));
            renderChips();
            renderOptions(actionMsInput.value);
          });
        });
      };
      const renderOptions = (query: string) => {
        const q = query.trim().toLowerCase();
        const matches = this.predefinedActions.filter(
          (a) => !selectedActionIds.has(a.id) && (!q || a.text.toLowerCase().includes(q)),
        );
        actionMsList.innerHTML = matches.length
          ? matches.map((a) => `<div class="fbk-ms-option" role="option" data-id="${a.id}">${escapeHtml(a.text)}</div>`).join('')
          : `<div class="fbk-ms-empty">${t('popover.noMatches')}</div>`;
        actionMsList.querySelectorAll('.fbk-ms-option').forEach((opt) => {
          // mousedown (not click) so selection registers BEFORE the input's blur would otherwise
          // fire and close the list first.
          opt.addEventListener('mousedown', (e) => {
            e.preventDefault();
            selectedActionIds.add(Number((opt as HTMLElement).dataset.id));
            actionMsInput.value = '';
            renderChips();
            renderOptions('');
            actionMsInput.focus();
          });
        });
      };
      const openList = () => { actionMsList.hidden = false; actionMsInput.setAttribute('aria-expanded', 'true'); };
      const closeList = () => { actionMsList.hidden = true; actionMsInput.setAttribute('aria-expanded', 'false'); };
      actionMsInput.addEventListener('focus', () => { renderOptions(actionMsInput.value); openList(); });
      actionMsInput.addEventListener('input', () => { renderOptions(actionMsInput.value); openList(); });
      actionMsInput.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') {
          e.stopPropagation(); // don't also trigger the popover's own Escape-to-cancel below
          closeList();
        } else if (e.key === 'Enter') {
          e.preventDefault(); // don't submit the comment from this field
          const first = actionMsList.querySelector('.fbk-ms-option') as HTMLElement | null;
          const id = first?.dataset.id;
          if (id) {
            selectedActionIds.add(Number(id));
            actionMsInput.value = '';
            renderChips();
            renderOptions('');
          }
        } else if (e.key === 'Backspace' && !actionMsInput.value && selectedActionIds.size) {
          const last = Array.from(selectedActionIds).pop()!;
          selectedActionIds.delete(last);
          renderChips();
          renderOptions('');
        }
      });
      // Close on click outside the control/list (composedPath crosses the shadow boundary) — same
      // pattern as the toolbar's user/updates/cluster menus elsewhere in this file.
      const onDocClick = (e: MouseEvent) => {
        const path = e.composedPath();
        if (!path.includes(actionMsControl) && !path.includes(actionMsList)) closeList();
      };
      document.addEventListener('click', onDocClick, true);
      stopMsListening = () => document.removeEventListener('click', onDocClick, true);
      renderOptions('');
    }
    // Screenshot / bug-report are opt-in (off by default), press-to-toggle buttons rather than
    // checkboxes — same tracked-in-closure pattern as isPrivateComment above. Screenshot capture
    // only starts the moment the user turns it ON, not on every toggle, so it's never captured for
    // a comment that ends up not using one.
    let attachShotComment = false;
    const shotToggle = host.querySelector('#fbk-comment-shot') as HTMLButtonElement | null;
    if (shotToggle) shotToggle.addEventListener('click', () => {
      attachShotComment = !attachShotComment;
      shotToggle.classList.toggle('is-active', attachShotComment);
      shotToggle.setAttribute('aria-pressed', String(attachShotComment));
      if (attachShotComment) this.beginScreenshotCapture(currentEl);
    });
    let isBugReportComment = false;
    const bugToggle = host.querySelector('#fbk-comment-bug') as HTMLButtonElement | null;
    if (bugToggle) bugToggle.addEventListener('click', () => {
      isBugReportComment = !isBugReportComment;
      bugToggle.classList.toggle('is-active', isBugReportComment);
      bugToggle.setAttribute('aria-pressed', String(isBugReportComment));
    });
    const cancelPopover = () => {
      currentEl.classList.remove(HL_CLASS);
      host.innerHTML = '';
      this._pendingShotPromise = null;
      stopMsListening?.();
    };
    (host.querySelector('#fbk-cancel') as HTMLElement).addEventListener('click', cancelPopover);
    // Esc cancels the comment box, same as clicking Cancel — stopPropagation so it doesn't also
    // reach the document-level Esc handler for element-picking mode (picking already stopped by
    // the time this popover is open, but this keeps the two handlers unambiguous either way).
    host.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      e.preventDefault();
      e.stopPropagation();
      cancelPopover();
    });
    (host.querySelector('#fbk-submit') as HTMLButtonElement).addEventListener('click', async () => {
      const text = ta.value.trim();
      if (!text) return this.toast(t('popover.commentCannotBeEmpty'), 'error');
      const isPrivate = isPrivateComment;
      const attachShot = attachShotComment;
      const isBugReport = isBugReportComment;
      const shotPromise = this._pendingShotPromise;
      this._pendingShotPromise = null;
      const predefinedActionIds = Array.from(selectedActionIds);
      const submitBtn = host.querySelector('#fbk-submit') as HTMLButtonElement;
      submitBtn.disabled = true; submitBtn.textContent = t('menu.saving');
      const saved = await this.createComment({ ...currentMeta, text, isPrivate, attachShot, shotPromise, predefinedActionIds, isBugReport });
      if (saved) { currentEl.classList.remove(HL_CLASS); host.innerHTML = ''; stopMsListening?.(); }
      else { submitBtn.disabled = false; submitBtn.textContent = t('popover.add'); }
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
      pageTitle: (document.documentElement.hasAttribute('data-snapshot-mask') || !this.captureTextContent)
        ? '•••'
        : document.title,
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
        else this.toast(t('toast.screenshotUploadFailed'), 'error');
      }
    }

    const language = data.language ?? await detectTextLanguageAsync(data.text);
    const bodyObj: Record<string, unknown> = {
      body: data.text,
      environment: this.environmentInt,
      isPrivate: !!data.isPrivate,
      element,
      isBugReport: !!data.isBugReport,
      language,
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
          this.toast(t('toast.actionNoLongerAvailable'), 'error');
          return false;
        }
        // A blocked origin is the one failure the user can actually act on — their site is not on
        // the project's allowed list. "Failed to save comment" sends them hunting through the
        // console for a 403 they will read as a bug in Pointer.
        if (r.status === 403) {
          this.toast(t('toast.commentsNotAllowedFromAddress'), 'error');
          return false;
        }
        // Rate limited: also actionable, and self-resolving. Retry-After is seconds.
        if (r.status === 429) {
          // A host-supplied transport (window.__POINTER_FETCH__, used by the extension) may
          // hand back a synthesized Response without a real Headers object.
          const retryAfter = Number(r.headers?.get?.('retry-after'));
          this.toast(
            retryAfter > 0
              ? t('toast.tooManyCommentsRetryIn', { n: retryAfter, s: retryAfter === 1 ? '' : 's' })
              : t('toast.tooManyCommentsWait'),
            'error',
          );
          return false;
        }
        throw new Error('HTTP ' + r.status);
      }
      const envelope = await r.json();
      const comment = envelope.data;
      if (comment) {
        this.comments.push({ ...comment, status: STATUS_STR[comment.status as unknown as number] || 'open' });
      }
      const newId = comment ? String(comment.id) : '';
      this._newPinId = newId || null;
      this.renderSidebar();
      this.renderPins();
      if (newId) setTimeout(() => { if (String(this._newPinId) === newId) this._newPinId = null; }, 3000);
      this.toast(t('toast.commentAdded'), 'success', newId ? t('toast.undo') : undefined, newId ? () => this.deleteComment(newId) : undefined);
      return true;
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') {
        this.toast(t('toast.failedToSaveComment'), 'error');
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
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.failedToReply'), 'error');
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
      this.toast(nextStr === 'pending-apply' ? t('toast.markedForApply') : t('toast.unmarked'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.updateFailed'), 'error');
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
      this.toast(toastMsg || t('toast.updated'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.updateFailed'), 'error');
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
      this.toast(isPrivate ? t('toast.markedPrivate') : t('toast.madePublic'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.updateFailed'), 'error');
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
      this.toast(t('toast.markedCompleted'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast(t('toast.updateFailed'), 'error');
    }
  }

  /**
   * Delete confirmation for a comment's own card: an opaque overlay covering the WHOLE card
   * (not just the kebab menu — the menu is already closed by the time this runs), so nothing
   * else on the card can be mis-clicked while confirming. No auto-dismiss: unlike the old
   * in-menu confirm (which had to give the dropdown back for other uses), this is a deliberate
   * modal-style prompt that stays until the viewer explicitly confirms or cancels.
   */
  confirmDeleteCard(id: string): void {
    const card = this.root && (this.root.querySelector(`.fbk-card[data-id="${id}"]`) as HTMLElement | null);
    if (!card || card.querySelector('.fbk-card-delete-confirm')) return; // already confirming

    const overlay = document.createElement('div');
    overlay.className = 'fbk-card-delete-confirm';
    overlay.innerHTML =
      `<p class="fbk-card-delete-confirm-q">${t('card.deleteThisComment')}</p>` +
      `<div class="fbk-card-delete-confirm-actions">` +
      `<button type="button" class="fbk-mini danger" data-c="yes">${t('card.confirmDelete')}</button>` +
      `<button type="button" class="fbk-mini" data-c="no">${t('toolbar.cancel')}</button>` +
      `</div>`;
    card.appendChild(overlay);

    overlay.querySelector('[data-c="yes"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      this.deleteComment(id);
    });
    overlay.querySelector('[data-c="no"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      overlay.remove();
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
      this.closeCardMenu();
      this.renderSidebar(); this.renderPins();
      this.toast(t('toast.deleted'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || t('toast.deleteFailed'), 'error');
    }
  }

  // Inline edit (own comments only): swap the body text for a textarea + controls.
  startEdit(id: string): void {
    const card = this.root && this.root.querySelector(`.fbk-card[data-id="${id}"]`);
    if (!card || card.querySelector('.fbk-edit')) return;
    const comment = (this.comments || []).find((x) => String(x.id) === String(id));
    if (!comment) return;
    const textEl = card.querySelector('.fbk-text') as HTMLElement | null;
    if (!textEl) return;
    const hasShot = !!(comment.element && comment.element.screenshotUrl);
    const editor = document.createElement('div');
    editor.className = 'fbk-edit';
    editor.style.margin = '6px 0';
    editor.innerHTML = `
        <textarea class="fbk-textarea fbk-edit-body">${escapeHtml(comment.body || '')}</textarea>
        ${hasShot ? `<label class="fbk-edit-option"><input type="checkbox" class="fbk-edit-rmshot" /> ${t('card.removeImage')}</label>` : ''}
        <div class="fbk-reply-row">
          <button class="fbk-btn primary fbk-btn-fill fbk-edit-save">${t('card.save')}</button>
          <button class="fbk-mini fbk-edit-cancel">${t('toolbar.cancel')}</button>
        </div>`;
    textEl.style.display = 'none';
    textEl.insertAdjacentElement('afterend', editor);
    const ta = editor.querySelector('.fbk-edit-body') as HTMLTextAreaElement;
    ta.focus();
    (editor.querySelector('.fbk-edit-cancel') as HTMLElement).addEventListener('click', () => { editor.remove(); textEl.style.display = ''; });
    (editor.querySelector('.fbk-edit-save') as HTMLElement).addEventListener('click', () => {
      const body = ta.value.trim();
      if (!body) { this.toast(t('popover.commentCannotBeEmpty'), 'error'); return; }
      const rm = editor.querySelector('.fbk-edit-rmshot') as HTMLInputElement | null;
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
      this.toast(t('toast.commentUpdated'), 'success');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || t('toast.failedToUpdateComment'), 'error');
    }
  }

  // Inline edit for a single reply (own replies only) — same swap-body-for-a-textarea pattern
  // as the comment's own startEdit, scoped to one .fbk-reply row instead of the whole card.
  startEditReply(commentId: string, replyId: string): void {
    const row = this.root && this.root.querySelector(`.fbk-reply[data-reply-id="${replyId}"]`);
    if (!row || row.querySelector('.fbk-edit')) return;
    const comment = (this.comments || []).find((x) => String(x.id) === String(commentId));
    const reply = comment && (comment.replies || []).find((r) => String(r.id) === String(replyId));
    if (!reply) return;
    const mainEl = row.querySelector('.fbk-reply-main') as HTMLElement | null;
    const actionsEl = row.querySelector('.fbk-reply-actions') as HTMLElement | null;
    if (!mainEl) return;
    const editor = document.createElement('div');
    editor.className = 'fbk-edit';
    editor.style.flex = '1';
    editor.innerHTML = `
        <textarea class="fbk-textarea fbk-reply-edit-body">${escapeHtml(reply.body || reply.text || '')}</textarea>
        <div class="fbk-reply-row">
          <button class="fbk-btn primary fbk-btn-fill fbk-edit-save">${t('card.save')}</button>
          <button class="fbk-mini fbk-edit-cancel">${t('toolbar.cancel')}</button>
        </div>`;
    mainEl.style.display = 'none';
    if (actionsEl) actionsEl.style.display = 'none';
    mainEl.insertAdjacentElement('afterend', editor);
    const ta = editor.querySelector('.fbk-reply-edit-body') as HTMLTextAreaElement;
    ta.focus();
    const close = () => { editor.remove(); mainEl.style.display = ''; if (actionsEl) actionsEl.style.display = ''; };
    (editor.querySelector('.fbk-edit-cancel') as HTMLElement).addEventListener('click', close);
    (editor.querySelector('.fbk-edit-save') as HTMLElement).addEventListener('click', () => {
      const body = ta.value.trim();
      if (!body) { this.toast(t('popover.commentCannotBeEmpty'), 'error'); return; }
      this.saveReplyEdit(replyId, body);
    });
  }

  async saveReplyEdit(replyId: string, body: string): Promise<void> {
    // PUT /api/replies/{id} — author-only edit (enforced server-side).
    try {
      const r = await this.api(`/api/replies/${replyId}`, {
        method: 'PUT',
        body: JSON.stringify({ body }),
      });
      if (!r.ok) {
        const b = await r.json().catch(() => null);
        throw new Error((b && b.message) || ('HTTP ' + r.status));
      }
      await this.fetchComments();
      this.renderSidebar();
      this.toast(t('toast.commentUpdated'), 'success');
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || t('toast.failedToUpdateComment'), 'error');
    }
  }

  // Same inline "Delete this…?" confirm-row pattern as confirmDelete, scoped to one reply's own
  // actions row instead of the comment's.
  confirmDeleteReply(btn: HTMLElement): void {
    const commentId = btn.dataset.commentId;
    const replyId = btn.dataset.replyId;
    const row = btn.closest('.fbk-reply-actions') as HTMLElement | null;
    if (!commentId || !replyId || !row || row.querySelector('.fbk-confirm')) return;

    const others = Array.from(row.children) as HTMLElement[];
    others.forEach((el) => { el.style.display = 'none'; });

    const wrap = document.createElement('div');
    wrap.className = 'fbk-confirm fbk-confirm-row';
    wrap.innerHTML =
      `<span class="fbk-confirm-q">${t('card.deleteThisReply')}</span>` +
      `<span class="fbk-confirm-btns">` +
      `<button type="button" class="fbk-mini danger fbk-icon" data-c="yes" title="${t('card.confirmDelete')}" aria-label="${t('card.confirmDelete')}">${ICON.checkPlain}</button>` +
      `<button type="button" class="fbk-mini fbk-icon" data-c="no" title="${t('toolbar.cancel')}" aria-label="${t('toolbar.cancel')}">&#x2715;</button>` +
      `</span>`;
    row.appendChild(wrap);

    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      clearTimeout(timer);
      wrap.remove();
      others.forEach((el) => { el.style.display = ''; });
    };
    const timer = setTimeout(close, 4000);
    wrap.querySelector('[data-c="yes"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      close();
      this.deleteReply(replyId);
    });
    wrap.querySelector('[data-c="no"]')!.addEventListener('click', (e) => {
      e.stopPropagation();
      close();
    });
  }

  async deleteReply(replyId: string): Promise<void> {
    // DELETE /api/replies/{id} — author or workspace admin (enforced server-side).
    try {
      const r = await this.api(`/api/replies/${replyId}`, { method: 'DELETE' });
      if (!r.ok) {
        const b = await r.json().catch(() => null);
        throw new Error((b && b.message) || ('HTTP ' + r.status));
      }
      await this.fetchComments();
      this.renderSidebar();
      this.renderPins();
      this.toast(t('toast.deleted'));
    } catch (e) {
      if ((e as Error).message !== 'HTTP 401 Unauthorized') this.toast((e as Error).message || t('toast.deleteFailed'), 'error');
    }
  }

  // True when comment `c` was authored by the current logged-in user.
  /**
   * Completes `this.user` with the fields the card template needs (`id`, `isAdmin`, `isQuickAccess`)
   * when the session came from a host that only supplied a display name. One `GET /api/auth/me`,
   * in memory only for host-owned sessions — the host re-injects its own user object on every
   * activation, and persisting a wider profile than it chose to share would defeat that choice.
   */
  private async hydrateIdentity(): Promise<void> {
    if (!this.token || (this.user && this.user.id)) return;
    try {
      const r = await this.api('/api/auth/me');
      if (!r.ok) return;
      const env = await r.json().catch(() => null);
      const me = env && env.data ? env.data : env;
      if (!me || !me.id) return;
      this.user = {
        ...(this.user || {}),
        id: String(me.id),
        displayName: (this.user && this.user.displayName) || me.displayName,
        isAdmin: !!me.isAdmin,
        isQuickAccess: !!me.isQuickAccess,
        language: (this.user && this.user.language) || me.language,
      };
      if (!this.authOwnedByHost) {
        try { localStorage.setItem('pointer_user', JSON.stringify(this.user)); } catch { /* ignore */ }
      }
    } catch {
      /* 401 is handled by api(); anything else just leaves the buttons hidden */
    }
  }

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

    const countEl = this.root.querySelector('#fbk-count');
    if (countEl) countEl.textContent = String(all.filter((c) => c.status !== 'archived' && c.status !== 'applied').length);

    const filtersEl = this.root.querySelector('#fbk-filters');
    if (filtersEl) {
      const activeFilters = catalogToFilters();
      // Fixed/read-only when the install pinned the environment or the project turned the
      // switcher off for everyone — same condition renderChrome() used to compute this for the
      // now-removed sidebar-head select.
      const fixedEnvLabel = (this.hasFixedEnvironment || !this.showEnvironmentSelector)
        ? this.envDisplayLabel(this.environmentAttr || ENV_NAME[this.environmentInt] || 'staging')
        : null;
      const envValue = this.viewAllEnvironments ? 'all' : (this.environmentAttr || ENV_NAME[this.environmentInt] || 'staging').toLowerCase();
      filtersEl.innerHTML = TPL.statusFilterSelect(activeFilters, this.statusFilter, counts)
        + TPL.envFilterSelect(fixedEnvLabel, envValue)
        + ((authors.length > 1 && !this.mineOnly) ? TPL.authorFilter(authors, this.authorFilter || '') : '')
        + (canMine ? TPL.mineToggle(this.mineOnly) : '');
      const statusSel = filtersEl.querySelector('#fbk-status-filter') as HTMLSelectElement | null;
      if (statusSel) statusSel.addEventListener('change', () => {
        this.statusFilter = statusSel.value; this.renderSidebar();
      });
      const envSel = filtersEl.querySelector('#fbk-env') as HTMLSelectElement | null;
      if (envSel) envSel.addEventListener('change', () => this.setEnvironment(envSel.value));
      const mineBtn = filtersEl.querySelector('#fbk-mine-toggle');
      if (mineBtn) mineBtn.addEventListener('click', () => {
        this.mineOnly = !this.mineOnly; this.renderSidebar(); this.renderPins();
      });
      const authorSel = filtersEl.querySelector('#fbk-author-filter') as HTMLSelectElement | null;
      if (authorSel) authorSel.addEventListener('change', () => {
        this.authorFilter = authorSel.value || null;
        this.renderSidebar(); this.renderPins();
      });
    }

    const list = this.root.querySelector('#fbk-list');
    if (!list) return;

    // Applied comments are hidden from the default view — they are done, and leaving them there
    // makes the list grow forever. The ONE exception is your own applied comment that you have not
    // verified yet: R2-04 notifies you that it was applied and asks "does this look right?", and
    // the buttons that answer that live on the card. Hiding the card makes the notification a dead
    // end — you are told to check something you cannot reach.
    const awaitingMyVerification = (c: Comment) =>
      c.status === 'applied' && !c.verifiedAt && this.isMine(c);

    const shown = this.statusFilter === 'all'
      ? scoped.filter((c) => c.status !== 'archived' && (c.status !== 'applied' || awaitingMyVerification(c)))
      : scoped.filter((c) => c.status === this.statusFilter);
    if (!scoped.length) {
      list.innerHTML = TPL.empty(this.mineOnly
        ? t('sidebar.noOwnComments')
        : t('sidebar.noCommentsYet'));
      return;
    }
    if (!shown.length) {
      const activeFilters = catalogToFilters();
      const filterLabel = (activeFilters.find((f) => f.key === this.statusFilter) ?? { label: this.statusFilter }).label;
      list.innerHTML = TPL.empty(t('sidebar.noFilteredComments', {
        label: filterLabel.toLowerCase(),
        suffix: this.mineOnly ? t('sidebar.ofYours') : '',
      }));
      return;
    }

    const isQuickAccess = !!this.user?.isQuickAccess;
    const myId = this.user?.id ? String(this.user.id).toLowerCase() : null;
    list.innerHTML = shown.map((c, i) => {
      c._mine = this.isMine(c);
      c._canVerify = c._mine || !!(this.user && this.user.isAdmin);
      // Automated (AI) replies are always read-only — never "mine" for edit/delete purposes,
      // regardless of whose account posted them (enforced server-side too).
      (c.replies || []).forEach((r) => { r._mine = !r.isAi && !!(myId && r.authorId && String(r.authorId).toLowerCase() === myId); });
      return TPL.card(c, i, isQuickAccess);
    }).join('');

    list.querySelectorAll<HTMLElement>('[data-act="apply"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c && c.status !== 'applied') this.toggleApply(c);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="complete"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c && c.status !== 'applied') this.markCompleted(c);
    }));
    // Private/public, edit, and delete now live in the kebab menu at the top-end of the card
    // (see cardMenu template + toggleCardMenu) instead of separate inline buttons.
    list.querySelectorAll<HTMLElement>('[data-act="card-menu"]').forEach((b) => b.addEventListener('click', (e) => {
      e.stopPropagation();
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.toggleCardMenu(b, c);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="reopen"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.setStatus(c, 'open', t('toast.reopenedMsg'));
    }));
    list.querySelectorAll<HTMLElement>('[data-act="archive"]').forEach((b) => b.addEventListener('click', () => {
      const c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
      if (c) this.setStatus(c, 'archived', t('toast.archivedMsg'));
    }));
    list.querySelectorAll<HTMLTextAreaElement>('.fbk-reply-input').forEach((inp) => inp.addEventListener('keydown', (e) => {
      // Enter sends (matches the old single-line input's behavior); Shift+Enter inserts a
      // newline, now that this is a textarea instead of a text input.
      if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        if (inp.value.trim()) { this.addReply(inp.dataset.id!, inp.value.trim()); inp.value = ''; }
      }
    }));
    list.querySelectorAll<HTMLElement>('[data-act="reply-edit"]').forEach((b) => b.addEventListener('click', () => {
      if (b.dataset.commentId && b.dataset.replyId) this.startEditReply(b.dataset.commentId, b.dataset.replyId);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="reply-delete"]').forEach((b) => b.addEventListener('click', () => this.confirmDeleteReply(b)));

    // Verify actions (R2-04)
    list.querySelectorAll<HTMLElement>('[data-act="verify-ok"]').forEach((b) => b.addEventListener('click', () => {
      const id = b.dataset.id;
      if (id) this.apiVerify(id, true);
    }));
    list.querySelectorAll<HTMLElement>('[data-act="verify-reject"]').forEach((b) => b.addEventListener('click', () => {
      const id = b.dataset.id;
      if (!id) return;
      const box = list.querySelector<HTMLElement>(`#fbk-verify-box-${id}`);
      if (box) {
        const show = box.classList.contains('fbk-hidden');
        box.classList.toggle('fbk-hidden', !show);
        const input = box.querySelector<HTMLInputElement>(`#fbk-verify-note-${id}`);
        if (input && show) input.focus();
      }
    }));
    list.querySelectorAll<HTMLElement>('[data-act="verify-cancel"]').forEach((b) => b.addEventListener('click', () => {
      const id = b.dataset.id;
      if (!id) return;
      const box = list.querySelector<HTMLElement>(`#fbk-verify-box-${id}`);
      if (box) {
        box.classList.add('fbk-hidden');
        const input = box.querySelector<HTMLInputElement>(`#fbk-verify-note-${id}`);
        if (input) input.value = '';
      }
    }));
    list.querySelectorAll<HTMLElement>('[data-act="verify-submit"]').forEach((b) => b.addEventListener('click', () => {
      const id = b.dataset.id;
      if (!id) return;
      const input = list.querySelector<HTMLInputElement>(`#fbk-verify-note-${id}`);
      const note = input?.value?.trim();
      if (!note) {
        input?.focus();
        this.toast(t('toast.pleaseProvideNoteNotFixed'), 'error');
        return;
      }
      this.apiVerify(id, false, note);
    }));
    list.querySelectorAll<HTMLInputElement>('.fbk-verify-note-input').forEach((inp) => inp.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        const id = inp.id.replace('fbk-verify-note-', '');
        const note = inp.value.trim();
        if (!note) {
          inp.focus();
          this.toast(t('toast.pleaseProvideNoteNotFixed'), 'error');
          return;
        }
        this.apiVerify(id, false, note);
      }
    }));
  }

  // --- Pins ----------------------------------------------------------------
  renderPins(): void {
    const wrap = this.root && this.root.querySelector('#fbk-pins-layer');
    if (!wrap) return;
    const all = this.pageComments().filter((c) => c.status !== 'archived' && c.status !== 'applied');
    const here = this.scopeByWho(all);

    // `i` is each comment's position in `here` — kept per-item (not per-group) so a
    // non-clustered pin's number still matches its card's number in the sidebar, which iterates
    // this same `here` array in the same order; a cluster shows a count instead of numbers, so it
    // doesn't need one.
    type Item = { c: Comment; i: number; x: number; y: number };
    const items: Item[] = [];
    here.forEach((c, i) => {
      const el = matchElement(c);
      if (!el) return;
      const rect = el.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) return;
      items.push({ c, i, x: rect.left, y: rect.top });
    });

    // Greedy single-linkage clustering: each item joins the first existing group whose anchor
    // (its first item — good enough at this scale; not true nearest-centroid clustering) is
    // within PIN_CLUSTER_RADIUS px, else it starts a new group.
    const groups: Item[][] = [];
    for (const item of items) {
      const group = groups.find((g) => Math.hypot(item.x - g[0]!.x, item.y - g[0]!.y) <= PIN_CLUSTER_RADIUS);
      if (group) group.push(item); else groups.push([item]);
    }

    wrap.innerHTML = groups.map((g) => {
      // Centroid of the group's anchors — a single pin still lands exactly on its target;
      // a cluster sits at the middle of what it's standing in for, rather than snapped to
      // whichever comment happened to be scanned first.
      const cx = g.reduce((sum, it) => sum + it.x, 0) / g.length;
      const cy = g.reduce((sum, it) => sum + it.y, 0) / g.length;
      // The wrapper is anchored via `transform: translate(-50%, -100%)` so the pin's TIP (not its
      // own top-left corner) lands on the target pixel — which means a target genuinely visible
      // right at the top or left edge of the viewport had part of the pin pushed off-screen by
      // that same transform. Nudge it back on-screen in that case ONLY (a target actually
      // scrolled well past the edge, at a much more negative coordinate, is correctly left alone —
      // its pin isn't supposed to be visible until scrolled back into view).
      const clampedCx = (cx >= 0 && cx < PIN_HALF_WIDTH) ? PIN_HALF_WIDTH
        : (cx <= window.innerWidth && cx > window.innerWidth - PIN_HALF_WIDTH) ? window.innerWidth - PIN_HALF_WIDTH
        : cx;
      const clampedCy = (cy >= 0 && cy < PIN_HEIGHT) ? PIN_HEIGHT : cy;
      const rect = { left: clampedCx, top: clampedCy };
      if (g.length > 1) return TPL.pinCluster(g.map((it) => it.c), rect);
      // The hover tooltip opens ABOVE the pin by default; flip it below when there's not enough
      // room above (same failure this whole clamp exists for, one level up: a pin near the top
      // of the viewport has nowhere for a ~150px tooltip to open upward into).
      const tipSide: 'top' | 'bottom' = (clampedCy - PIN_HEIGHT - PIN_TOOLTIP_HEIGHT_ESTIMATE) < 0 ? 'bottom' : 'top';
      return TPL.pin(g[0]!.c, g[0]!.i, rect, String(g[0]!.c.id) === String(this._newPinId), tipSide);
    }).join('');
    applyDataPosition(wrap, '.fbk-pin-wrapper');

    wrap.querySelectorAll<HTMLElement>('.fbk-pin-cluster').forEach((btn) => btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const wrapper = btn.closest('.fbk-pin-wrapper') as HTMLElement | null;
      const ids = (wrapper?.dataset.ids || '').split(',').filter(Boolean);
      const clustered = ids.map((id) => this.comments.find((c) => String(c.id) === id)).filter((c): c is Comment => !!c);
      this.toggleClusterMenu(btn, clustered);
    }));
    wrap.querySelectorAll<HTMLElement>('.fbk-pin').forEach((btn) => {
      if (btn.classList.contains('fbk-pin-cluster')) return; // handled above
      btn.addEventListener('click', () => {
        const wrapper = btn.closest('.fbk-pin-wrapper') as HTMLElement | null;
        const id = wrapper?.dataset.id;
        if (id) this.highlightCommentCard(id);
      });
    });
  }

  // Opens the sidebar (if needed) and scrolls/highlights one comment's card — shared by a
  // standalone pin's click and the expanded cluster menu's item clicks.
  private highlightCommentCard(id: string): void {
    this.toggleSidebar(true);
    this.renderSidebar(); // synchronous, from the comments already in memory, so the card
                           // exists in the DOM immediately instead of waiting on
                           // toggleSidebar's own re-fetch.
    setTimeout(() => {
      const card = this.root.querySelector(`.fbk-card[data-id="${id}"]`) as HTMLElement | null;
      if (card) {
        card.scrollIntoView({ behavior: 'smooth', block: 'center' });
        card.classList.add('highlight');
        setTimeout(() => card.classList.remove('highlight'), 2000);
      }
    }, 100);
  }

  // --- Pin cluster menu ------------------------------------------------------
  private toggleClusterMenu(btn: HTMLElement, comments: Comment[]): void {
    const host = this.root.querySelector('#fbk-menu-host') as HTMLElement | null;
    if (!host) return;
    if (host.querySelector('#fbk-pin-cluster-menu')) { this.closeClusterMenu(); return; }
    this.closeUserMenu();
    this.closeUpdatesMenu();
    this.closeCardMenu();
    if (comments.length === 0) return;

    host.innerHTML = TPL.pinClusterMenu(comments);
    const menu = host.querySelector('#fbk-pin-cluster-menu') as HTMLElement | null;
    if (!menu) return;
    btn.setAttribute('aria-expanded', 'true');

    // Anchor the menu under the cluster pin — or above it, if there isn't room. Computed from
    // the PIN's own position (already rendered, so `menu.offsetHeight` is real, not guessed),
    // not the toolbar's: a cluster can sit anywhere on the page, unlike the account/updates
    // dropdowns which are always anchored to the toolbar itself (see positionMenuVertically).
    const r = btn.getBoundingClientRect();
    const menuHeight = menu.offsetHeight;
    const spaceBelow = window.innerHeight - r.bottom;
    if (spaceBelow < menuHeight + 6 && r.top > spaceBelow) {
      menu.style.top = 'auto';
      menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - r.top + 6))}px`;
    } else {
      menu.style.bottom = 'auto';
      menu.style.top = `${Math.round(r.bottom + 6)}px`;
    }
    menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 248, Math.round(r.left - 100)))}px`;

    menu.querySelectorAll<HTMLElement>('.fbk-pin-cluster-item').forEach((item) => {
      item.addEventListener('click', () => {
        const id = item.dataset.id;
        this.closeClusterMenu();
        if (id) this.highlightCommentCard(id);
      });
    });

    this._clusterMenuClose = (e: MouseEvent) => {
      const path = e.composedPath();
      if (!path.includes(menu) && !path.includes(btn)) this.closeClusterMenu();
    };
    setTimeout(() => { if (this._clusterMenuClose) document.addEventListener('click', this._clusterMenuClose, true); }, 0);
  }

  private closeClusterMenu(): void {
    const host = this.root.querySelector('#fbk-menu-host');
    if (host && host.querySelector('#fbk-pin-cluster-menu')) {
      host.innerHTML = '';
      this.root.querySelectorAll('.fbk-pin-cluster[aria-expanded="true"]').forEach((b) => b.setAttribute('aria-expanded', 'false'));
    }
    if (this._clusterMenuClose) {
      document.removeEventListener('click', this._clusterMenuClose, true);
      this._clusterMenuClose = null;
    }
  }

  // --- Card actions menu (kebab menu: private/public, edit, delete) --------
  private toggleCardMenu(btn: HTMLElement, c: Comment): void {
    const host = this.root.querySelector('#fbk-menu-host') as HTMLElement | null;
    if (!host) return;
    if (host.querySelector('#fbk-card-menu')) { this.closeCardMenu(); return; }
    this.closeUserMenu();
    this.closeUpdatesMenu();
    this.closeClusterMenu();

    host.innerHTML = TPL.cardMenu(c);
    const menu = host.querySelector('#fbk-card-menu') as HTMLElement | null;
    if (!menu) return;
    btn.setAttribute('aria-expanded', 'true');

    // Same anchor-above-or-below-the-trigger logic as the pin cluster menu — this button can sit
    // anywhere in a scrollable sidebar, so it's measured from its own rect, not the toolbar's.
    const r = btn.getBoundingClientRect();
    const menuHeight = menu.offsetHeight;
    const spaceBelow = window.innerHeight - r.bottom;
    if (spaceBelow < menuHeight + 6 && r.top > spaceBelow) {
      menu.style.top = 'auto';
      menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - r.top + 6))}px`;
    } else {
      menu.style.bottom = 'auto';
      menu.style.top = `${Math.round(r.bottom + 6)}px`;
    }
    menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;

    const visBtn = menu.querySelector('[data-menu-act="visibility"]') as HTMLElement | null;
    if (visBtn) {
      visBtn.addEventListener('click', () => {
        this.closeCardMenu();
        this.setVisibility(c, visBtn.dataset.private === 'true');
      });
    }
    const editBtn = menu.querySelector('[data-menu-act="edit"]') as HTMLElement | null;
    if (editBtn) {
      editBtn.addEventListener('click', () => {
        this.closeCardMenu();
        this.startEdit(String(c.id));
      });
    }
    // Delete closes the menu immediately (like edit/visibility above) and shows its confirmation
    // as an overlay on the card itself, not inside the dropdown — see confirmDeleteCard.
    const delBtn = menu.querySelector('[data-menu-act="delete"]') as HTMLElement | null;
    if (delBtn) {
      delBtn.addEventListener('click', () => {
        this.closeCardMenu();
        this.confirmDeleteCard(String(c.id));
      });
    }

    this._cardMenuClose = (e: MouseEvent) => {
      const path = e.composedPath();
      if (!path.includes(menu) && !path.includes(btn)) this.closeCardMenu();
    };
    setTimeout(() => { if (this._cardMenuClose) document.addEventListener('click', this._cardMenuClose, true); }, 0);
  }

  private closeCardMenu(): void {
    const host = this.root.querySelector('#fbk-menu-host');
    if (host && host.querySelector('#fbk-card-menu')) {
      host.innerHTML = '';
      this.root.querySelectorAll('.fbk-card-kebab[aria-expanded="true"]').forEach((b) => b.setAttribute('aria-expanded', 'false'));
    }
    if (this._cardMenuClose) {
      document.removeEventListener('click', this._cardMenuClose, true);
      this._cardMenuClose = null;
    }
  }

  // --- Toast ---------------------------------------------------------------
  // `type` keeps every existing call site's convention ('', 'success', 'error') working
  // unchanged — '' and 'success' both render the success (green check) variant; 'error' maps to
  // the danger (red) variant. 'warn' (amber) is available for a future call site; nothing emits
  // it yet. `actionLabel`/`onAction` add an optional inline button (e.g. "Undo", "Retry") —
  // `onAction` runs, then the toast dismisses either way.
  toast(msg: string, type = '', actionLabel?: string, onAction?: () => void): void {
    const variant: 'success' | 'warn' | 'danger' = type === 'error' ? 'danger' : type === 'warn' ? 'warn' : 'success';
    const host = this.ensureToastContainer();
    const wrap = document.createElement('div');
    wrap.innerHTML = TPL.toast(variant, msg, actionLabel);
    const el = wrap.firstElementChild as HTMLElement;
    host.appendChild(el);

    let dismissed = false;
    const dismiss = () => {
      if (dismissed) return;
      dismissed = true;
      el.classList.add('dismissing');
      // Matches --fbk-transition-fast (140ms) — long enough for the exit animation to play.
      setTimeout(() => el.remove(), 160);
    };
    el.querySelector('.fbk-toast-close')?.addEventListener('click', dismiss);
    if (actionLabel) {
      el.querySelector('.fbk-toast-action')?.addEventListener('click', () => {
        onAction?.();
        dismiss();
      });
    }
    setTimeout(dismiss, 2200);
  }

  // Toasts stack in their own fixed container (see _toast.scss) rather than as loose siblings —
  // otherwise two toasts shown close together would render on top of each other. Created lazily
  // and reused; renderChrome()'s full innerHTML swap can wipe it (same as any other overlay it
  // doesn't own), which only matters if a re-render happens to land inside a toast's ~2s life.
  private ensureToastContainer(): HTMLElement {
    let host = this.root.querySelector('#fbk-toast-container') as HTMLElement | null;
    if (!host) {
      host = document.createElement('div');
      host.id = 'fbk-toast-container';
      host.className = 'fbk-toast-container';
      host.setAttribute('role', 'region');
      host.setAttribute('aria-label', t('toast.notifications'));
      host.setAttribute('aria-live', 'polite');
      this.root.appendChild(host);
    }
    return host;
  }
}
