import { escapeHtml, timeAgo } from './dom';
import { ICON } from './icons';
import { getBrandName } from './constants';
import type { AuthorOption, Comment, Meta, NotificationItem, PredefinedActionOption } from './types';

// All component markup lives here (pure string builders). Event wiring stays in
// the element / UI modules, which call these then attach listeners to the nodes.
// Values interpolated here are pre-escaped via escapeHtml where needed.
export const TPL = {
  // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
  // shell. showLoginModal() renders the shell once and then swaps #fbk-auth-body
  // between loginBody and signupBody. The shell keeps the Skip control so
  // deferred-login dismissal works from either view.
  loginModal: (project: string) => `
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
  loginBody: (rejected: boolean) => `
        <input class="fbk-input fbk-stack-gap" id="fbk-email" type="email" placeholder="Email" />
        <input class="fbk-input fbk-stack-gap" id="fbk-password" type="password" placeholder="Password" />
        <div class="fbk-modal-error" id="fbk-login-error"></div>
        <button class="fbk-btn primary fbk-btn-block" id="fbk-login-submit">Sign in</button>
        ${rejected ? `
        <div class="fbk-reapply" id="fbk-reapply">
          <label class="fbk-field-label" for="fbk-reapply-role">Choose a role to request again</label>
          <select class="fbk-input fbk-stack-gap" id="fbk-reapply-role"></select>
          <button class="fbk-btn primary fbk-btn-block" id="fbk-reapply-submit">Request again</button>
        </div>` : ''}
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
  chrome: (displayName: string, roleLabel: string, fixedEnvLabel?: string | null, projectName = '', shortcutLabel = '', unreadNotifyCount = 0, avatarInitials = '', ariaShortcut = '') => `
        <aside class="fbk-toolbar" id="fbk-toolbar" role="toolbar" aria-label="${escapeHtml(getBrandName())}" part="toolbar">
          <span class="fbk-toolbar__grip" id="fbk-grip" data-fbk-drag data-toggle="tooltip" data-placement="top" title="Drag to reposition" aria-hidden="true">${ICON.grip}</span>
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--primary fbk-toolbar-btn--icon" id="fbk-add" data-fbk-act="inspect" aria-pressed="false" data-toggle="tooltip" data-placement="top" title="Comment on an element${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ''}" aria-label="Comment on an element"${ariaShortcut ? ` aria-keyshortcuts="${escapeHtml(ariaShortcut)}"` : ''}><span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--comments" id="fbk-toggle" data-fbk-act="comments" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="View comments list" aria-label="Comments"><span class="fbk-toolbar-btn__icon">${ICON.bubble}</span> <span class="fbk-toolbar-count" id="fbk-count" data-fbk-count>0</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-updates" data-fbk-act="updates" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="Recent activity &amp; updates" aria-label="Updates${unreadNotifyCount > 0 ? `, ${unreadNotifyCount > 99 ? '99+' : unreadNotifyCount} unread` : ''}"><span class="fbk-toolbar-btn__icon">${ICON.bell}</span><span class="fbk-toolbar-dot${unreadNotifyCount > 0 ? '' : ' fbk-hidden'}" id="fbk-notify-count" data-fbk-unread aria-hidden="true"></span></button>
          ${displayName ? `
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--avatar" id="fbk-user" data-fbk-act="account" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="Signed in as ${displayName}${roleLabel ? ' · ' + roleLabel : ''}" aria-label="Account, ${displayName}">${avatarInitials}</button>` : ''}
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon fbk-toolbar-btn--brand" id="fbk-hide" data-fbk-act="hide" data-toggle="tooltip" data-placement="top" title="Hide ${escapeHtml(getBrandName())}" aria-label="Hide ${escapeHtml(getBrandName())}"><span class="fbk-toolbar-btn__icon">${ICON.eyeOff}</span></button>
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
                ${fixedEnvLabel
                  ? `<span class="fbk-env-label fbk-caption" title="Environment — fixed for this install">&middot; ${escapeHtml(fixedEnvLabel)}</span>`
                  : `<select class="fbk-input fbk-env-select" id="fbk-env" title="Environment — comments are scoped per environment">
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
  commitStyleControl: (commitStyle: number) => `
        <span class="fbk-caption">Commit style</span>
        <select class="fbk-input fbk-commit-style-select" id="fbk-commit-style-select" title="How the AI apply flow commits applied comments">
          <option value="1" ${commitStyle === 1 ? 'selected' : ''}>One commit</option>
          <option value="2" ${commitStyle === 2 ? 'selected' : ''}>Separate commits</option>
        </select>`,

  // Dropdown under the user icon: shows identity, the per-user "add comment" shortcut
  // (click to rebind, ↺ to reset), theme + language toggles, and a Sign out action.
  userMenu: (
    displayName: string,
    roleLabel: string,
    shortcutLabel: string,
    authOwnedByHost?: boolean,
    theme: string = 'light',
    language: string = 'en',
  ) => `
        <div class="fbk-menu" id="fbk-user-menu" role="menu">
          <div class="fbk-menu-id">
            <span>${displayName}</span>
            ${roleLabel ? `<span class="fbk-menu-role">${roleLabel}</span>` : ''}
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Add comment</span>
            <button type="button" id="fbk-shortcut-edit" class="fbk-mini" title="Click, then press a new key combo">${escapeHtml(shortcutLabel)}</button>
            <button type="button" id="fbk-shortcut-reset" class="fbk-mini fbk-icon" title="Reset to default">&#8635;</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Theme</span>
            <button type="button" id="fbk-theme-light" class="fbk-mini fbk-icon${theme === 'light' ? ' is-active' : ''}" title="Light" aria-label="Light theme" aria-pressed="${theme === 'light'}">${ICON.sun}</button>
            <button type="button" id="fbk-theme-dark" class="fbk-mini fbk-icon${theme === 'dark' ? ' is-active' : ''}" title="Dark" aria-label="Dark theme" aria-pressed="${theme === 'dark'}">${ICON.moon}</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">Language</span>
            <button type="button" id="fbk-lang-en" class="fbk-mini${language === 'en' ? ' is-active' : ''}" aria-pressed="${language === 'en'}">EN</button>
            <button type="button" id="fbk-lang-ar" class="fbk-mini${language === 'ar' ? ' is-active' : ''}" aria-pressed="${language === 'ar'}">AR</button>
          </div>
          ${authOwnedByHost
            ? `<div class="fbk-menu-note fbk-caption">Signed in via the browser extension — sign out from its popup.</div>`
            : `<button class="fbk-menu-item" id="fbk-signout" role="menuitem">${ICON.logout}<span>Sign out</span></button>`}
        </div>`,

  // Collapsed state: a small floating launcher that re-opens the overlay.
  // `rtl` makes start/end resolve against the host page direction (the shadow
  // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
  launcher: (count: number, position: string, rtl: boolean, unreadNotifyCount = 0) => {
    const hasUnread = unreadNotifyCount > 0;
    const badgeCount = hasUnread ? unreadNotifyCount : count;
    return `
        <button class="fbk-launcher fbk-pos-${position || 'bottom-end'}${rtl ? ' fbk-rtl' : ''}" id="fbk-launcher" title="Open ${escapeHtml(getBrandName())} feedback" aria-label="Open ${escapeHtml(getBrandName())} feedback">
          <span class="fbk-launcher-ring" aria-hidden="true"></span>
          ${ICON.bubbleLg}
          ${badgeCount ? `<span class="fbk-launcher-badge${hasUnread ? ' fbk-notify-badge' : ''}">${badgeCount > 99 ? '99+' : badgeCount}</span>` : ''}
        </button>`;
  },

  empty: (msg: string) => `<div class="fbk-empty">${msg}</div>`,

  // One toast card. `role="alert"` for danger (assertive — interrupts) vs `role="status"` for
  // success/warn (polite — announced without interrupting); the shared container around these
  // already carries `aria-live="polite"`, so screen readers pick either up without extra wiring.
  // `element.ts`'s toast() owns the click listeners (Undo/Retry callback + dismiss); this is a
  // pure string builder like the rest of TPL.
  toast: (variant: 'success' | 'warn' | 'danger', message: string, actionLabel?: string) => {
    const icon = variant === 'success' ? ICON.checkPlain : variant === 'warn' ? ICON.warnTriangle : ICON.dangerCircle;
    const role = variant === 'danger' ? 'alert' : 'status';
    return `
        <div class="fbk-toast fbk-toast-${variant}" role="${role}">
          <div class="fbk-toast-icon" aria-hidden="true">${icon}</div>
          <div class="fbk-toast-content"><span class="fbk-toast-message">${escapeHtml(message)}</span></div>
          ${actionLabel ? `<button type="button" class="fbk-toast-action">${escapeHtml(actionLabel)}</button>` : ''}
          <button type="button" class="fbk-toast-close" aria-label="Dismiss notification">${ICON.close}</button>
        </div>`;
  },

  // Status filter as a dropdown (rather than a row of chip buttons) — keeps the filter bar compact.
  statusFilterSelect: (filters: { key: string; label: string; color?: string }[], active: string, counts: Record<string, number>) =>
    `<select class="fbk-status-select" id="fbk-status-filter" title="Filter by status">
             ${filters.map((f) => `<option value="${f.key}" ${f.key === active ? 'selected' : ''}>${escapeHtml(f.label)} (${counts[f.key] ?? 0})</option>`).join('')}
           </select>`,

  // "Mine only" toggle — a chip that composes with the status chips above.
  // Rendered only when a user is logged in.
  mineToggle: (active: boolean) =>
    `<button class="fbk-chip fbk-mine ${active ? 'active' : ''}" id="fbk-mine-toggle" title="Show only my comments" aria-pressed="${active ? 'true' : 'false'}">
             &#x1f464; Mine only
           </button>`,

  // User filter — only rendered when the list has comments from >1 author.
  authorFilter: (authors: AuthorOption[], selectedId: string) =>
    `<select class="fbk-userfilter" id="fbk-author-filter" title="Filter by user">
             <option value="">&#x1f465; All users</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? 'selected' : ''}>${escapeHtml(a.name)}</option>`).join('')}
           </select>`,

  card: (c: Comment, i: number, isQuickAccess?: boolean) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : '';
    // "completed" means a developer applied it; "live" means it is actually on the site. Those
    // are different days for the person who left the comment, and telling them apart is the whole
    // point of deploy awareness — the title names the build so they can ask about a specific one.
    const statusPill = c.status === 'applied' && c.deployedAt
      ? `<span class="fbk-pill status-applied" title="Deployed in ${escapeHtml((c.deployedSha || '').slice(0, 7))}">&#x2713; live</span>`
      : c.status === 'applied'
      ? '<span class="fbk-pill status-applied">&#x2713; completed</span>'
      : c.status === 'pending-apply' ? '<span class="fbk-pill status-pending">pending</span>'
      : c.status === 'archived' ? '<span class="fbk-pill status-archived">&#x1f4e6; archived</span>' : '';
    const verifiedPill = (c.status === 'applied' && c.verifiedAt)
      ? '<span class="fbk-pill verified">&#x2713; Verified</span>'
      : '';
    const verifyGroup = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<span class="fbk-verify-group">
          <button class="fbk-mini fbk-verify-ok" data-act="verify-ok" data-id="${c.id}" title="Looks right">&#x1f44d; Looks right</button>
          <button class="fbk-mini fbk-verify-reject" data-act="verify-reject" data-id="${c.id}" title="Not fixed">&#x1f44e; Not fixed</button>
        </span>`
      : '';
    const verifyBox = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<div class="fbk-verify-box fbk-hidden" id="fbk-verify-box-${c.id}">
          <input class="fbk-input fbk-verify-note-input" id="fbk-verify-note-${c.id}" placeholder="Explain what is still not fixed…" />
          <div class="fbk-verify-actions">
            <button class="fbk-mini primary" data-act="verify-submit" data-id="${c.id}">Submit</button>
            <button class="fbk-mini" data-act="verify-cancel" data-id="${c.id}">Cancel</button>
          </div>
        </div>`
      : '';
    // A "#" href for comments with no tracked commit (applied before this field existed, or by a
    // flow that doesn't record one) — inert rather than a broken/missing link.
    const commitLink = c.status === 'applied'
      ? `<a class="fbk-pill" href="${c.commitUrl ? escapeHtml(c.commitUrl) : '#'}" ${c.commitUrl ? 'target="_blank" rel="noopener noreferrer"' : ''} title="${c.commitUrl ? 'View commit' : 'No commit recorded for this comment'}">&#x1f517; commit</a>`
      : '';
    // Advisory only: the server flagged this text as looking like a credential or payload, so a
    // reviewer notices before acting on it. Nothing is blocked and nothing is rewritten — and the
    // flag is absent entirely for AI callers, so this pill is the only place it ever appears.
    const payloadPill = c.hasPayloadFlag
      ? `<span class="fbk-pill fbk-payload-flag" title="${escapeHtml((c.payloadFlags || []).join(', '))}">&#x26a0; contains a secret/payload?</span>`
      : '';

    const replies = (c.replies || []).map((r) =>
      `<div class="fbk-reply ${r.isAi ? 'ai' : ''}"><b>${escapeHtml(r.authorName || r.authorLabel || 'User')}:</b> ${escapeHtml(r.body || r.text || '')}</div>`).join('');
    const envInt = c.environment;
    const envLabel = envInt === 1 ? 'Local' : envInt === 2 ? 'Staging' : envInt === 3 ? 'Production' : (envInt ? String(envInt) : '');
    const authorLabel = c.authorName || '';
    const shotUrl = c.element && c.element.screenshotUrl;
    const shot = shotUrl
      ? `<a class="fbk-shot-link" href="${escapeHtml(shotUrl)}" target="_blank" rel="noopener noreferrer" title="Open full screenshot">
            <img class="fbk-shot" src="${escapeHtml(shotUrl)}" alt="Element screenshot" loading="lazy" />
          </a>`
      : '';
    return `
          <div class="fbk-card ${cls}" data-id="${c.id}">
            <div class="fbk-meta">
              <span class="fbk-badge">${i + 1}</span>
              ${envLabel ? `<span class="fbk-pill env">${escapeHtml(envLabel)}</span>` : ''}
              ${payloadPill}
              ${statusPill}
              ${verifiedPill}
              ${verifyGroup}
              ${commitLink}
              <div class="fbk-actions-end">
                ${c._mine ? `<button class="fbk-mini fbk-icon${c.isPrivate ? ' private-on' : ''}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? 'false' : 'true'}" title="${c.isPrivate ? 'Private — click to make public' : 'Make private (only you)'}" aria-label="${c.isPrivate ? 'Make public' : 'Make private'}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ''}
                ${c.status === 'open' ? `<button class="fbk-mini danger fbk-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ''}
              </div>
            </div>
            <div class="fbk-text">${escapeHtml(c.body || c.text || '')}</div>
            ${shot}
            <div class="fbk-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ''}${c.editedAt ? ' &middot; <span class="fbk-edited">edited</span>' : ''}</div>
            ${verifyBox}
            ${replies ? `<div class="fbk-replies">${replies}</div>` : ''}
            <div class="fbk-reply-row">
              <input class="fbk-input fbk-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="fbk-actions">
              ${isQuickAccess ? '' : (c.status === 'applied' || c.status === 'archived') ? '' : `<button class="fbk-mini ${c.status === 'pending-apply' ? 'apply' : 'ready'}" data-act="apply" data-id="${c.id}" title="${c.status === 'pending-apply' ? 'Marked ready — click to unmark' : 'Mark ready to apply'}">
                ${ICON.flag}<span>Ready</span>
              </button>`}
              ${(!isQuickAccess && (c.status === 'open' || c.status === 'pending-apply')) ? `<button class="fbk-mini done fbk-icon" data-act="complete" data-id="${c.id}" title="Mark completed" aria-label="Mark completed">${ICON.check}</button>` : ''}
              ${(!isQuickAccess && c.status === 'applied') ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>
              <button class="fbk-mini fbk-icon" data-act="archive" data-id="${c.id}" title="Archive" aria-label="Archive">${ICON.archive}</button>` : ''}
              ${(!isQuickAccess && c.status === 'archived') ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>` : ''}
              ${c._mine ? `<div class="fbk-actions-end"><button class="fbk-mini fbk-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button></div>` : ''}
            </div>
          </div>`;
  },

  // `bugReportEnabled`: only true when the project has page-context capture turned on — the
  // checkbox controls whether the console/network buffer already sitting in memory gets attached
  // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
  popover: (meta: Meta, left: number, top: number, shotEnabled: boolean, actions: PredefinedActionOption[] = [], bugReportEnabled = false) => `
        <div class="fbk-popover" data-fbk-left="${left}" data-fbk-top="${top}">
          <div class="fbk-popover-nav">
            <button type="button" class="fbk-popover-nav-btn" id="fbk-target-up" title="Select parent element" aria-label="Select parent element">${ICON.chevronUp}</button>
            <button type="button" class="fbk-popover-nav-btn" id="fbk-target-down" title="Select first child element" aria-label="Select first child element">${ICON.chevronDown}</button>
            <button type="button" class="fbk-popover-private-toggle" id="fbk-comment-private" title="Make private (only you)" aria-label="Make private" aria-pressed="false">${ICON.unlock}</button>
          </div>
          <h3 id="fbk-popover-title">Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="fbk-snippet" id="fbk-popover-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          <div class="fbk-src${meta._sourcePath ? '' : ' fbk-hidden'}" id="fbk-popover-src">&#x26ec; <span id="fbk-popover-src-path">${escapeHtml(meta._sourcePath || '')}</span></div>
          <textarea class="fbk-textarea" id="fbk-comment-text" placeholder="What should change here?"></textarea>
          ${actions.length ? `<div class="fbk-field-label">Predefined prompts</div>
          <div class="fbk-ms" id="fbk-action-ms">
            <div class="fbk-ms-control" id="fbk-action-ms-control">
              <div class="fbk-ms-chips" id="fbk-action-ms-chips"></div>
              <input type="text" class="fbk-ms-input" id="fbk-action-ms-input" placeholder="Search prompts…" autocomplete="off" role="combobox" aria-expanded="false" aria-haspopup="listbox" aria-label="Search predefined prompts" />
            </div>
            <div class="fbk-ms-list" id="fbk-action-ms-list" role="listbox" hidden></div>
          </div>` : ''}
          ${(shotEnabled || bugReportEnabled) ? `<div class="fbk-popover-toggles">
            ${shotEnabled ? `<button type="button" class="fbk-mini" id="fbk-comment-shot" aria-pressed="false">&#x1f4f7; Attach screenshot</button>` : ''}
            ${bugReportEnabled ? `<button type="button" class="fbk-mini" id="fbk-comment-bug" aria-pressed="false" title="Attaches any console errors/warnings and failed or slow network requests seen on this page">&#x1f41e; Report as a bug</button>` : ''}
          </div>` : ''}
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
  pin: (c: Comment, i: number, rect: { left: number; top: number }, isNew = false, tipSide: 'top' | 'bottom' = 'top') => {
    const status = c.status === 'pending-apply' ? 'ready' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : 'open';
    const statusLabel = status === 'ready' ? 'Ready' : status === 'applied' ? 'Applied' : status === 'archived' ? 'Archived' : 'Open';
    const author = c.authorName || '';
    const bodyText = c.body || c.text || '';
    const replyCount = (c.replies || []).length;
    const selector = (c.element && c.element.selector) || '';
    const label = `Comment #${i + 1}${author ? ` by ${author}` : ''}${bodyText ? `: ${bodyText}` : ''}`;
    const showMeta = !!(selector || replyCount);
    return `
        <div class="fbk-pin-wrapper" data-id="${c.id}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}" data-fbk-tip-side="${tipSide}">
          <button type="button" class="fbk-pin fbk-pin-${status}" aria-label="${escapeHtml(label)}">
            ${isNew ? '<span class="fbk-pin-ring" aria-hidden="true"></span>' : ''}
            ${status === 'applied' ? ICON.checkBold : `<span class="fbk-pin-number">${i + 1}</span>`}
          </button>
          <div class="fbk-pin-tooltip" role="tooltip">
            <div class="fbk-pin-tooltip-header">
              <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
              ${author ? `<span class="fbk-pin-author">${escapeHtml(author)}</span>` : ''}
              <span class="fbk-pin-time">${escapeHtml(timeAgo(c.createdAt))}</span>
            </div>
            ${bodyText ? `<p class="fbk-pin-tooltip-body">${escapeHtml(bodyText)}</p>` : ''}
            ${showMeta ? `
            <div class="fbk-pin-tooltip-meta">
              ${selector ? `<span class="fbk-pin-tag" title="${escapeHtml(selector)}">${escapeHtml(selector)}</span>` : '<span></span>'}
              ${replyCount ? `<span class="fbk-pin-reply-count">${ICON.bubbleSm} ${replyCount} ${replyCount === 1 ? 'reply' : 'replies'}</span>` : ''}
            </div>` : ''}
          </div>
        </div>`;
  },

  // 2+ pins within 24px of each other (see element.ts's renderPins clustering) render as one
  // expandable "N+" badge instead of stacking indistinguishable pins on top of each other.
  // Clicking it opens pinClusterMenu below, listing each one individually.
  pinCluster: (comments: Comment[], rect: { left: number; top: number }) => `
        <div class="fbk-pin-wrapper" data-ids="${comments.map((c) => c.id).join(',')}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}">
          <button type="button" class="fbk-pin fbk-pin-cluster" aria-label="${comments.length} overlapping comments at this location" aria-expanded="false">
            <span class="fbk-pin-cluster-count">${comments.length}+</span>
            <span class="fbk-pin-cluster-dots" aria-hidden="true">&bull;&bull;&bull;</span>
          </button>
        </div>`,

  // Expanded cluster — one row per clustered comment, click-through to the same
  // scroll+highlight behavior as clicking a standalone pin.
  pinClusterMenu: (comments: Comment[]) => `
        <div class="fbk-pin-cluster-menu" id="fbk-pin-cluster-menu" role="menu">
          ${comments.map((c) => {
            const status = c.status === 'pending-apply' ? 'ready' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : 'open';
            const statusLabel = status === 'ready' ? 'Ready' : status === 'applied' ? 'Applied' : status === 'archived' ? 'Archived' : 'Open';
            const bodyText = c.body || c.text || '';
            return `
          <button type="button" class="fbk-pin-cluster-item" data-id="${c.id}" role="menuitem">
            <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
            <span class="fbk-pin-cluster-item-text">${escapeHtml(bodyText)}</span>
          </button>`;
          }).join('')}
        </div>`,

  notificationsMenu: (items: NotificationItem[]) => `
        <div class="fbk-notifications-menu" id="fbk-notifications-menu" role="menu">
          <div class="fbk-notifications-head">
            <h3>Updates</h3>
          </div>
          <div class="fbk-notifications-body">
            ${items.length === 0
              ? '<div class="fbk-empty fbk-notifications-empty">No updates yet</div>'
              : items.map((item) => {
                  const isUnread = !item.readAt;
                  let typeLabel = 'Update';
                  let icon: string = ICON.pin;
                  let detail = '';
                  const typeNum = typeof item.type === 'string' ? (item.type === 'CommentApplied' ? 1 : item.type === 'CommentReopened' ? 2 : item.type === 'ReplyAdded' ? 3 : 0) : item.type;
                  if (typeNum === 1) {
                    typeLabel = 'Applied';
                    icon = ICON.check;
                    detail = item.payload?.commitUrl
                      ? `<div class="fbk-notification-item-commit"><a class="fbk-pill" href="${escapeHtml(item.payload.commitUrl)}" target="_blank" rel="noopener noreferrer" onclick="event.stopPropagation()">&#x1f517; Commit</a></div>`
                      : '';
                  } else if (typeNum === 2) {
                    typeLabel = 'Reopened';
                    icon = ICON.reopen;
                  } else if (typeNum === 3) {
                    typeLabel = 'New reply';
                    icon = ICON.inspect;
                    if (item.payload?.replyExcerpt) {
                      detail = `<div class="fbk-notification-reply fbk-caption">"${escapeHtml(item.payload.replyExcerpt)}"</div>`;
                    }
                  }
                  const timeAgo = item.createdAt ? new Date(item.createdAt).toLocaleDateString() : '';
                  return `
                    <div class="fbk-notification-item${isUnread ? ' unread' : ''}" data-id="${item.commentId}" role="menuitem">
                      <div class="fbk-notification-item-head">
                        <span class="fbk-notification-item-type">${icon} ${escapeHtml(typeLabel)}</span>
                        <span class="fbk-notification-item-time">${escapeHtml(timeAgo)}</span>
                      </div>
                      <div class="fbk-notification-item-body">
                        ${escapeHtml(item.commentBodyExcerpt || '')}
                      </div>
                      ${detail}
                    </div>`;
                }).join('')}
          </div>
        </div>`,
};
