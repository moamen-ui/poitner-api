import { escapeHtml, timeAgo } from './dom';
import { ICON } from './icons';
import { getBrandName } from './constants';
import { t } from './i18n';
import type { AuthorOption, Comment, Meta, NotificationItem, PredefinedActionOption } from './types';

// All component markup lives here (pure string builders). Event wiring stays in
// the element / UI modules, which call these then attach listeners to the nodes.
// Values interpolated here are pre-escaped via escapeHtml where needed.
// Static English text is pulled through t('key') (see ./i18n) so a switch to Arabic re-renders
// the same markup with translated strings; the shadow UI itself stays LTR regardless (see
// _base.scss) — this is a text-only translation, not a layout mirror.
export const TPL = {
  // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
  // shell. showLoginModal() renders the shell once and then swaps #fbk-auth-body
  // between loginBody and signupBody. The shell keeps the Skip control so
  // deferred-login dismissal works from either view.
  loginModal: (project: string) => `
        <div class="fbk-modal-overlay">
          <div class="fbk-modal">
            <h2>${escapeHtml(getBrandName())}</h2>
            <p>${t('auth.leaveFeedbackOn')} <b>${escapeHtml(project)}</b>.</p>
            <div id="fbk-auth-body"></div>
            <button class="fbk-btn fbk-link fbk-btn-block fbk-auth-skip" id="fbk-login-skip">${t('auth.skipForNow')}</button>
          </div>
        </div>`,

  // Sign-in body. After a "rejected" login it also renders an inline re-apply
  // block (role select + "Request again"); pass rejected=true to show it.
  loginBody: (rejected: boolean) => `
        <input class="fbk-input fbk-stack-gap" id="fbk-email" type="email" placeholder="${t('auth.email')}" />
        <input class="fbk-input fbk-stack-gap" id="fbk-password" type="password" placeholder="${t('auth.password')}" />
        <div class="fbk-modal-error" id="fbk-login-error"></div>
        <button class="fbk-btn primary fbk-btn-block" id="fbk-login-submit">${t('auth.signIn')}</button>
        ${rejected ? `
        <div class="fbk-reapply" id="fbk-reapply">
          <label class="fbk-field-label" for="fbk-reapply-role">${t('auth.chooseRoleToRequestAgain')}</label>
          <select class="fbk-input fbk-stack-gap" id="fbk-reapply-role"></select>
          <button class="fbk-btn primary fbk-btn-block" id="fbk-reapply-submit">${t('auth.requestAgain')}</button>
        </div>` : ''}
        <div class="fbk-auth-foot">
          ${t('auth.noAccount')} <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-signup">${t('auth.createAccount')}</button>
        </div>`,

  // Sign-up body. The role <select> is populated at runtime from GET /api/roles.
  signupBody: () => `
        <input class="fbk-input fbk-stack-gap" id="fbk-su-name" type="text" placeholder="${t('auth.name')}" />
        <input class="fbk-input fbk-stack-gap" id="fbk-su-email" type="email" placeholder="${t('auth.email')}" />
        <input class="fbk-input fbk-stack-gap" id="fbk-su-password" type="password" placeholder="${t('auth.password')}" />
        <label class="fbk-field-label" for="fbk-su-role">${t('auth.role')}</label>
        <select class="fbk-input fbk-stack-gap" id="fbk-su-role"></select>
        <div class="fbk-modal-error" id="fbk-signup-error"></div>
        <div class="fbk-modal-success" id="fbk-signup-success"></div>
        <button class="fbk-btn primary fbk-btn-block" id="fbk-signup-submit">${t('auth.createAccount')}</button>
        <div class="fbk-auth-foot">
          ${t('auth.alreadyHaveAccount')} <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-login">${t('auth.backToSignIn')}</button>
        </div>`,

  // `projectName`: embedded in the "{project} comments" heading so a visitor can immediately tell
  // which project this install is bound to — project keys aren't unique across a workspace, so two
  // different installs can easily look identical without this.
  // `avatarInitials`: 1-2 letters (see dom.ts's initials()) for the account button, already
  // safe to interpolate as-is — computed by the caller from the RAW display name (before
  // displayName below gets HTML-escaped), so a name containing "&"/"<" can't leak into it.
  // `ariaShortcut`: the ARIA-format string ("Control+Alt+Shift+C" — see shortcut.ts's
  // ariaKeyshortcuts), separate from `shortcutLabel` (the human-display form, "Ctrl+Alt+Shift+C"
  // or "⌃⌥⇧C" on Mac) since ARIA wants full, platform-independent modifier names.
  // The environment select/label used to live in this shell too; it's now rendered inside
  // #fbk-filters (see envFilterSelect), beside the status filter — both scoping controls together.
  chrome: (displayName: string, roleLabel: string, projectName = '', shortcutLabel = '', unreadNotifyCount = 0, avatarInitials = '', ariaShortcut = '') => `
        <aside class="fbk-toolbar" id="fbk-toolbar" role="toolbar" aria-label="${escapeHtml(getBrandName())}" part="toolbar">
          <span class="fbk-toolbar__grip" id="fbk-grip" data-fbk-drag data-toggle="tooltip" data-placement="top" title="${t('toolbar.dragToReposition')}" aria-hidden="true">${ICON.grip}</span>
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--primary fbk-toolbar-btn--icon fbk-toolbar-btn--brand" id="fbk-add" data-fbk-act="inspect" aria-pressed="false" data-toggle="tooltip" data-placement="top" title="${t('toolbar.commentOnElement')}${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ''}" aria-label="${t('toolbar.commentOnElement')}"${ariaShortcut ? ` aria-keyshortcuts="${escapeHtml(ariaShortcut)}"` : ''}><span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--comments" id="fbk-toggle" data-fbk-act="comments" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t('toolbar.viewCommentsList')}" aria-label="${t('toolbar.comments')}"><span class="fbk-toolbar-btn__icon">${ICON.bubble}</span> <span class="fbk-toolbar-count" id="fbk-count" data-fbk-count>0</span></button>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-updates" data-fbk-act="updates" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t('toolbar.recentActivityUpdates')}" aria-label="${t('toolbar.updates')}${unreadNotifyCount > 0 ? `, ${unreadNotifyCount > 99 ? '99+' : unreadNotifyCount} unread` : ''}"><span class="fbk-toolbar-btn__icon">${ICON.bell}</span><span class="fbk-toolbar-dot${unreadNotifyCount > 0 ? '' : ' fbk-hidden'}" id="fbk-notify-count" data-fbk-unread aria-hidden="true"></span></button>
          ${displayName ? `
          <span class="fbk-toolbar__divider" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--avatar" id="fbk-user" data-fbk-act="account" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t('toolbar.signedInAs')} ${displayName}${roleLabel ? ' · ' + roleLabel : ''}" aria-label="${t('toolbar.account')}, ${displayName}">${avatarInitials}</button>` : ''}
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-hide" data-fbk-act="hide" data-toggle="tooltip" data-placement="top" title="${t('toolbar.hideBrand', { brand: escapeHtml(getBrandName()) })}" aria-label="${t('toolbar.hideBrand', { brand: escapeHtml(getBrandName()) })}"><span class="fbk-toolbar-btn__icon">${ICON.eyeOff}</span></button>
          <span class="fbk-toolbar__divider fbk-toolbar__divider--moved" aria-hidden="true"></span>
          <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon fbk-toolbar__reset" id="fbk-reset-pos" data-fbk-act="reset-position" data-toggle="tooltip" data-placement="top" title="${t('toolbar.resetToolbarPosition')}" aria-label="${t('toolbar.resetToolbarPosition')}"><span class="fbk-toolbar-btn__icon">${ICON.restore}</span></button>
        </aside>
        <div class="fbk-sidebar" id="fbk-sidebar">
          <div class="fbk-sidebar-head">
            <div class="fbk-sidebar-head-row">
              <h2 id="fbk-comments-heading">${t('toolbar.commentsHeading', { project: escapeHtml(projectName) })}</h2>
              <button class="fbk-mini fbk-icon" id="fbk-close" title="${t('toolbar.close')}" aria-label="${t('toolbar.close')}">&#x2715;</button>
            </div>
            <div class="fbk-sidebar-head-row">
              <button class="fbk-mini fbk-icon fbk-refresh-btn" id="fbk-refresh" title="${t('toolbar.refreshComments')}" aria-label="${t('toolbar.refreshComments')}">&#8635;</button>
            </div>
            <div class="fbk-mine-row" id="fbk-mine-row"></div>
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
        <span class="fbk-caption">${t('toolbar.commitStyle')}</span>
        <select class="fbk-input fbk-commit-style-select" id="fbk-commit-style-select" title="${t('toolbar.commitStyleTitle')}">
          <option value="1" ${commitStyle === 1 ? 'selected' : ''}>${t('toolbar.oneCommit')}</option>
          <option value="2" ${commitStyle === 2 ? 'selected' : ''}>${t('toolbar.separateCommits')}</option>
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
            <span class="fbk-menu-shortcut-label">${t('menu.addComment')}</span>
            <button type="button" id="fbk-shortcut-edit" class="fbk-mini" title="${t('menu.clickThenPressKeyCombo')}">${escapeHtml(shortcutLabel)}</button>
            <button type="button" id="fbk-shortcut-reset" class="fbk-mini fbk-icon" title="${t('menu.resetToDefault')}">&#8635;</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">${t('menu.theme')}</span>
            <button type="button" id="fbk-theme-light" class="fbk-mini fbk-icon${theme === 'light' ? ' is-active' : ''}" title="${t('menu.light')}" aria-label="${t('menu.lightTheme')}" aria-pressed="${theme === 'light'}">${ICON.sun}</button>
            <button type="button" id="fbk-theme-dark" class="fbk-mini fbk-icon${theme === 'dark' ? ' is-active' : ''}" title="${t('menu.dark')}" aria-label="${t('menu.darkTheme')}" aria-pressed="${theme === 'dark'}">${ICON.moon}</button>
          </div>
          <div class="fbk-menu-shortcut">
            <span class="fbk-menu-shortcut-label">${t('menu.language')}</span>
            <button type="button" id="fbk-lang-en" class="fbk-mini${language === 'en' ? ' is-active' : ''}" aria-pressed="${language === 'en'}">EN</button>
            <button type="button" id="fbk-lang-ar" class="fbk-mini${language === 'ar' ? ' is-active' : ''}" aria-pressed="${language === 'ar'}">AR</button>
          </div>
          ${authOwnedByHost
            ? `<div class="fbk-menu-note fbk-caption">${t('menu.extensionSignedInNote')}</div>`
            : `<button class="fbk-menu-item" id="fbk-signout" role="menuitem">${ICON.logout}<span>${t('menu.signOut')}</span></button>`}
        </div>`,

  // Collapsed state: a small floating launcher that re-opens the overlay.
  // `rtl` makes start/end resolve against the host page direction (the shadow
  // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
  launcher: (count: number, position: string, rtl: boolean, unreadNotifyCount = 0) => {
    const hasUnread = unreadNotifyCount > 0;
    const badgeCount = hasUnread ? unreadNotifyCount : count;
    const openLabel = t('launcher.openFeedbackFor', { brand: escapeHtml(getBrandName()) });
    return `
        <button class="fbk-launcher fbk-pos-${position || 'bottom-end'}${rtl ? ' fbk-rtl' : ''}" id="fbk-launcher" title="${openLabel}" aria-label="${openLabel}">
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
  // pure string builder like the rest of TPL. `message`/`actionLabel` arrive already resolved
  // through t() by the caller — this function itself does no translation.
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
  // Filter labels themselves are server-provided status-catalog text (customizable per project) —
  // never translated by the widget, which cannot know what language an admin wrote them in.
  // Wrapped in a <label> with a visible text label (not just the select's own title tooltip) —
  // sits beside envFilterSelect, which follows the same fbk-filter-field shape.
  statusFilterSelect: (filters: { key: string; label: string; color?: string }[], active: string, counts: Record<string, number>) =>
    `<label class="fbk-filter-field">
             <span class="fbk-filter-field-label">${t('sidebar.status')}</span>
             <select class="fbk-status-select" id="fbk-status-filter" title="${t('sidebar.filterByStatus')}">
               ${filters.map((f) => `<option value="${f.key}" ${f.key === active ? 'selected' : ''}>${escapeHtml(f.label)} (${counts[f.key] ?? 0})</option>`).join('')}
             </select>
           </label>`,

  // Environment filter — sits beside the status filter (see statusFilterSelect) rather than in the
  // sidebar head, so both scoping controls live together. `fixedEnvLabel` renders a read-only
  // value instead of a select when the install pinned the environment, or the project turned the
  // switcher off for everyone (see `showEnvironmentSelector`); null/undefined renders the switcher.
  envFilterSelect: (fixedEnvLabel: string | null | undefined, currentValue: string) =>
    `<label class="fbk-filter-field">
             <span class="fbk-filter-field-label">${t('toolbar.environment')}</span>
             ${fixedEnvLabel
               ? `<span class="fbk-env-label fbk-caption" title="${t('toolbar.envFixedTitle')}">${escapeHtml(fixedEnvLabel)}</span>`
               : `<select class="fbk-input fbk-env-select" id="fbk-env" title="${t('toolbar.envSwitchTitle')}">
                 <option value="all" ${currentValue === 'all' ? 'selected' : ''}>${t('toolbar.envAll')}</option>
                 <option value="local" ${currentValue === 'local' ? 'selected' : ''}>${t('toolbar.envLocal')}</option>
                 <option value="staging" ${currentValue === 'staging' ? 'selected' : ''}>${t('toolbar.envStaging')}</option>
                 <option value="production" ${currentValue === 'production' ? 'selected' : ''}>${t('toolbar.envProduction')}</option>
               </select>`}
           </label>`,

  // "Mine only" — a real switch (track + thumb), not a filter chip: it's a single on/off
  // setting, not one choice among several (unlike the status/environment selects beside it),
  // so it gets its own row rather than living inside #fbk-filters. Rendered only when a user is
  // logged in (see renderSidebar's canMine).
  mineToggle: (active: boolean) =>
    `<div class="fbk-toggle-row">
             <span class="fbk-toggle-row-label">&#x1f464; ${t('sidebar.mineOnly')}</span>
             <button type="button" class="fbk-toggle-switch${active ? ' active' : ''}" id="fbk-mine-toggle" role="switch" aria-checked="${active ? 'true' : 'false'}" title="${t('sidebar.showOnlyMyComments')}" aria-label="${t('sidebar.showOnlyMyComments')}">
               <span class="fbk-toggle-switch-thumb"></span>
             </button>
           </div>`,

  // User filter — only rendered when the list has comments from >1 author.
  authorFilter: (authors: AuthorOption[], selectedId: string) =>
    `<select class="fbk-userfilter" id="fbk-author-filter" title="${t('sidebar.filterByUser')}">
             <option value="">&#x1f465; ${t('sidebar.allUsers')}</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? 'selected' : ''}>${escapeHtml(a.name)}</option>`).join('')}
           </select>`,

  // Kebab-menu dropdown for a comment card's own actions (private/public, edit, delete) —
  // rendered into the shared portal host (#fbk-menu-host), anchored under the card's kebab
  // button by toggleCardMenu. Visibility/edit are owner-only; delete only while still open
  // (matches the previous inline buttons' conditions exactly, just relocated).
  cardMenu: (c: Comment) => `
        <div class="fbk-card-menu" id="fbk-card-menu" role="menu">
          ${c._mine ? `<button type="button" class="fbk-card-menu-item" data-menu-act="visibility" data-private="${c.isPrivate ? 'false' : 'true'}" role="menuitem">${c.isPrivate ? ICON.unlock : ICON.lock}<span>${c.isPrivate ? t('card.makePublic') : t('card.makePrivate')}</span></button>` : ''}
          ${c._mine ? `<button type="button" class="fbk-card-menu-item" data-menu-act="edit" role="menuitem">${ICON.pencil}<span>${t('card.edit')}</span></button>` : ''}
          ${c.status === 'open' ? `<button type="button" class="fbk-card-menu-item danger" data-menu-act="delete" role="menuitem">${ICON.trash}<span>${t('card.delete')}</span></button>` : ''}
        </div>`,

  card: (c: Comment, i: number, isQuickAccess?: boolean) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : '';
    // "completed" means a developer applied it; "live" means it is actually on the site. Those
    // are different days for the person who left the comment, and telling them apart is the whole
    // point of deploy awareness — the title names the build so they can ask about a specific one.
    const statusPill = c.status === 'applied' && c.deployedAt
      ? `<span class="fbk-pill status-applied" title="${escapeHtml(t('card.deployedIn', { sha: (c.deployedSha || '').slice(0, 7) }))}">&#x2713; ${t('card.live')}</span>`
      : c.status === 'applied'
      ? `<span class="fbk-pill status-applied">&#x2713; ${t('card.completed')}</span>`
      : c.status === 'pending-apply' ? `<span class="fbk-pill status-pending">${t('card.pending')}</span>`
      : c.status === 'archived' ? `<span class="fbk-pill status-archived">&#x1f4e6; ${t('card.archived')}</span>` : '';
    const verifiedPill = (c.status === 'applied' && c.verifiedAt)
      ? `<span class="fbk-pill verified">&#x2713; ${t('card.verified')}</span>`
      : '';
    const verifyGroup = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<span class="fbk-verify-group">
          <button class="fbk-mini fbk-verify-ok" data-act="verify-ok" data-id="${c.id}" title="${t('card.looksRight')}">&#x1f44d; ${t('card.looksRight')}</button>
          <button class="fbk-mini fbk-verify-reject" data-act="verify-reject" data-id="${c.id}" title="${t('card.notFixed')}">&#x1f44e; ${t('card.notFixed')}</button>
        </span>`
      : '';
    const verifyBox = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<div class="fbk-verify-box fbk-hidden" id="fbk-verify-box-${c.id}">
          <input class="fbk-input fbk-verify-note-input" id="fbk-verify-note-${c.id}" placeholder="${t('card.explainNotFixed')}" />
          <div class="fbk-verify-actions">
            <button class="fbk-mini primary" data-act="verify-submit" data-id="${c.id}">${t('card.submit')}</button>
            <button class="fbk-mini" data-act="verify-cancel" data-id="${c.id}">${t('toolbar.cancel')}</button>
          </div>
        </div>`
      : '';
    // A "#" href for comments with no tracked commit (applied before this field existed, or by a
    // flow that doesn't record one) — inert rather than a broken/missing link.
    const commitLink = c.status === 'applied'
      ? `<a class="fbk-pill" href="${c.commitUrl ? escapeHtml(c.commitUrl) : '#'}" ${c.commitUrl ? 'target="_blank" rel="noopener noreferrer"' : ''} title="${c.commitUrl ? t('card.viewCommit') : t('card.noCommitRecorded')}">&#x1f517; ${t('card.commit')}</a>`
      : '';
    // Advisory only: the server flagged this text as looking like a credential or payload, so a
    // reviewer notices before acting on it. Nothing is blocked and nothing is rewritten — and the
    // flag is absent entirely for AI callers, so this pill is the only place it ever appears.
    const payloadPill = c.hasPayloadFlag
      ? `<span class="fbk-pill fbk-payload-flag" title="${escapeHtml((c.payloadFlags || []).join(', '))}">&#x26a0; ${t('card.containsSecretPayload')}</span>`
      : '';

    const replies = (c.replies || []).map((r) => {
      const body = escapeHtml(r.body || r.text || '');
      const authorName = escapeHtml(r.authorName || r.authorLabel || t('card.defaultReplyAuthor'));
      // Automated (AI apply flow) replies are always read-only — no edit/delete regardless of
      // author or admin, enforced server-side too (see CommentService.EditReplyAsync/
      // DeleteReplyAsync) — and collapsed by default, since they tend to be long changelogs.
      if (r.isAi) {
        // "claude-code · claude-sonnet-5 · via Moamen" — tool and model are absent on rows written
        // before this field existed, and "via {name}" only when the author is actually known (not
        // the generic defaultReplyAuthor fallback), so older/anonymous AI replies degrade gracefully.
        const attributionParts: string[] = [];
        if (r.aiTool) attributionParts.push(escapeHtml(r.aiTool));
        if (r.aiModel) attributionParts.push(escapeHtml(r.aiModel));
        const knownAuthorName = r.authorName || r.authorLabel;
        if (knownAuthorName) attributionParts.push(t('card.aiVia', { name: escapeHtml(knownAuthorName) }));
        const attribution = attributionParts.length > 0
          ? `<span class="fbk-reply-ai-author">${attributionParts.join(' &middot; ')}</span>`
          : '';
        return `<details class="fbk-reply fbk-reply-ai" data-reply-id="${r.id ?? ''}">
            <summary class="fbk-reply-ai-summary">&#x1f916; <b>${t('card.automatedReply')}</b> ${attribution}</summary>
            <div class="fbk-reply-main"><span class="fbk-reply-body">${body}</span></div>
          </details>`;
      }
      const actions = r._mine
        ? `<span class="fbk-reply-actions">
            <button type="button" class="fbk-mini fbk-icon" data-act="reply-edit" data-comment-id="${c.id}" data-reply-id="${r.id}" title="${t('card.edit')}" aria-label="${t('card.edit')}">${ICON.pencil}</button>
            <button type="button" class="fbk-mini danger fbk-icon" data-act="reply-delete" data-comment-id="${c.id}" data-reply-id="${r.id}" title="${t('card.delete')}" aria-label="${t('card.delete')}">${ICON.trash}</button>
          </span>`
        : '';
      return `<div class="fbk-reply" data-reply-id="${r.id ?? ''}">
          <div class="fbk-reply-main"><b>${authorName}:</b> <span class="fbk-reply-body">${body}</span></div>
          ${actions}
        </div>`;
    }).join('');
    const envInt = c.environment;
    const envLabel = envInt === 1 ? t('card.envLocal') : envInt === 2 ? t('card.envStaging') : envInt === 3 ? t('card.envProduction') : (envInt ? String(envInt) : '');
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
              ${(c._mine || c.status === 'open') ? `<div class="fbk-actions-end">
                <button class="fbk-mini fbk-icon fbk-card-kebab" data-act="card-menu" data-id="${c.id}" title="${t('card.moreActions')}" aria-label="${t('card.moreActions')}" aria-haspopup="true" aria-expanded="false">${ICON.kebab}</button>
              </div>` : ''}
            </div>
            <div class="fbk-text">${escapeHtml(c.body || c.text || '')}</div>
            ${shot}
            <div class="fbk-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ''}${c.editedAt ? ` &middot; <span class="fbk-edited">${t('card.edited')}</span>` : ''}</div>
            ${verifyBox}
            ${replies ? `<div class="fbk-replies">${replies}</div>` : ''}
            <div class="fbk-reply-row">
              <textarea class="fbk-textarea fbk-reply-input" placeholder="${t('card.replyPlaceholder')}" data-id="${c.id}" rows="1"></textarea>
            </div>
            <div class="fbk-actions">
              ${isQuickAccess ? '' : (c.status === 'applied' || c.status === 'archived') ? '' : `<button class="fbk-mini ${c.status === 'pending-apply' ? 'apply' : 'ready'}" data-act="apply" data-id="${c.id}" title="${c.status === 'pending-apply' ? t('card.markedReadyClickToUnmark') : t('card.markReadyToApply')}">
                ${ICON.flag}<span>${t('card.ready')}</span>
              </button>`}
              ${(!isQuickAccess && (c.status === 'open' || c.status === 'pending-apply')) ? `<button class="fbk-mini done fbk-icon" data-act="complete" data-id="${c.id}" title="${t('card.markCompleted')}" aria-label="${t('card.markCompleted')}">${ICON.check}</button>` : ''}
              ${(!isQuickAccess && c.status === 'applied') ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="${t('card.reopen')}">${ICON.reopen}<span>${t('card.reopen')}</span></button>
              <button class="fbk-mini fbk-icon" data-act="archive" data-id="${c.id}" title="${t('card.archive')}" aria-label="${t('card.archive')}">${ICON.archive}</button>` : ''}
              ${(!isQuickAccess && c.status === 'archived') ? `<button class="fbk-mini ready" data-act="reopen" data-id="${c.id}" title="${t('card.reopen')}">${ICON.reopen}<span>${t('card.reopen')}</span></button>` : ''}
            </div>
          </div>`;
  },

  // `bugReportEnabled`: only true when the project has page-context capture turned on — the
  // checkbox controls whether the console/network buffer already sitting in memory gets attached
  // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
  popover: (meta: Meta, left: number, top: number, shotEnabled: boolean, actions: PredefinedActionOption[] = [], bugReportEnabled = false) => `
        <div class="fbk-popover" data-fbk-left="${left}" data-fbk-top="${top}">
          <div class="fbk-popover-nav">
            <button type="button" class="fbk-popover-nav-btn" id="fbk-target-up" data-toggle="tooltip" data-placement="top" title="${t('popover.selectParentElement')}" aria-label="${t('popover.selectParentElement')}">${ICON.chevronUp}</button>
            <button type="button" class="fbk-popover-nav-btn" id="fbk-target-down" data-toggle="tooltip" data-placement="top" title="${t('popover.selectFirstChildElement')}" aria-label="${t('popover.selectFirstChildElement')}">${ICON.chevronDown}</button>
            <button type="button" class="fbk-popover-private-toggle" id="fbk-comment-private" data-toggle="tooltip" data-placement="top" title="${t('card.makePrivateOnlyYou')}" aria-label="${t('card.makePrivate')}" aria-pressed="false">${ICON.unlock}</button>
          </div>
          <h3 id="fbk-popover-title">${t('popover.commentOn')} &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="fbk-snippet" id="fbk-popover-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          <div class="fbk-src${meta._sourcePath ? '' : ' fbk-hidden'}" id="fbk-popover-src">&#x26ec; <span id="fbk-popover-src-path">${escapeHtml(meta._sourcePath || '')}</span></div>
          <textarea class="fbk-textarea" id="fbk-comment-text" placeholder="${t('popover.whatShouldChange')}"></textarea>
          ${actions.length ? `<div class="fbk-field-label">${t('popover.predefinedPrompts')}</div>
          <div class="fbk-ms" id="fbk-action-ms">
            <div class="fbk-ms-control" id="fbk-action-ms-control">
              <div class="fbk-ms-chips" id="fbk-action-ms-chips"></div>
              <input type="text" class="fbk-ms-input" id="fbk-action-ms-input" placeholder="${t('popover.searchPrompts')}" autocomplete="off" role="combobox" aria-expanded="false" aria-haspopup="listbox" aria-label="${t('popover.searchPredefinedPrompts')}" />
            </div>
            <div class="fbk-ms-list" id="fbk-action-ms-list" role="listbox" hidden></div>
          </div>` : ''}
          ${(shotEnabled || bugReportEnabled) ? `<div class="fbk-popover-toggles">
            ${shotEnabled ? `<button type="button" class="fbk-mini" id="fbk-comment-shot" aria-pressed="false">&#x1f4f7; ${t('popover.attachScreenshot')}</button>` : ''}
            ${bugReportEnabled ? `<button type="button" class="fbk-mini" id="fbk-comment-bug" aria-pressed="false" title="${t('popover.reportBugTitle')}">&#x1f41e; ${t('popover.reportAsABug')}</button>` : ''}
          </div>` : ''}
          <div class="fbk-reply-row">
            <button class="fbk-btn primary fbk-btn-fill" id="fbk-submit">${t('popover.add')}</button>
            <button class="fbk-mini" id="fbk-cancel">${t('toolbar.cancel')}</button>
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
    const statusLabel = status === 'ready' ? t('pin.ready') : status === 'applied' ? t('pin.applied') : status === 'archived' ? t('pin.archived') : t('pin.open');
    const author = c.authorName || '';
    const bodyText = c.body || c.text || '';
    const replyCount = (c.replies || []).length;
    const selector = (c.element && c.element.selector) || '';
    const label = `${t('pin.commentHash', { n: i + 1 })}${author ? t('pin.byAuthor', { author }) : ''}${bodyText ? `: ${bodyText}` : ''}`;
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
              ${replyCount ? `<span class="fbk-pin-reply-count">${ICON.bubbleSm} ${replyCount === 1 ? t('pin.reply') : t('pin.replies', { n: replyCount })}</span>` : ''}
            </div>` : ''}
          </div>
        </div>`;
  },

  // 2+ pins within 24px of each other (see element.ts's renderPins clustering) render as one
  // expandable "N+" badge instead of stacking indistinguishable pins on top of each other.
  // Clicking it opens pinClusterMenu below, listing each one individually.
  pinCluster: (comments: Comment[], rect: { left: number; top: number }) => `
        <div class="fbk-pin-wrapper" data-ids="${comments.map((c) => c.id).join(',')}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}">
          <button type="button" class="fbk-pin fbk-pin-cluster" aria-label="${t('pin.overlappingComments', { n: comments.length })}" aria-expanded="false">
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
            const statusLabel = status === 'ready' ? t('pin.ready') : status === 'applied' ? t('pin.applied') : status === 'archived' ? t('pin.archived') : t('pin.open');
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
            <h3>${t('notifications.updates')}</h3>
          </div>
          <div class="fbk-notifications-body">
            ${items.length === 0
              ? `<div class="fbk-empty fbk-notifications-empty">${t('notifications.noUpdatesYet')}</div>`
              : items.map((item) => {
                  const isUnread = !item.readAt;
                  let typeLabel = t('notifications.update');
                  let icon: string = ICON.pin;
                  let detail = '';
                  const typeNum = typeof item.type === 'string' ? (item.type === 'CommentApplied' ? 1 : item.type === 'CommentReopened' ? 2 : item.type === 'ReplyAdded' ? 3 : 0) : item.type;
                  if (typeNum === 1) {
                    typeLabel = t('notifications.applied');
                    icon = ICON.check;
                    detail = item.payload?.commitUrl
                      ? `<div class="fbk-notification-item-commit"><a class="fbk-pill" href="${escapeHtml(item.payload.commitUrl)}" target="_blank" rel="noopener noreferrer" onclick="event.stopPropagation()">&#x1f517; ${t('notifications.commit')}</a></div>`
                      : '';
                  } else if (typeNum === 2) {
                    typeLabel = t('notifications.reopened');
                    icon = ICON.reopen;
                  } else if (typeNum === 3) {
                    typeLabel = t('notifications.newReply');
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
