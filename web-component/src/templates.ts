import { escapeHtml } from './dom';
import { ICON } from './icons';
import { getBrandName } from './constants';
import type { AuthorOption, Comment, Meta, NotificationItem, PredefinedActionOption } from './types';

// All component markup lives here (pure string builders). Event wiring stays in
// the element / UI modules, which call these then attach listeners to the nodes.
// Values interpolated here are pre-escaped via escapeHtml where needed.
export const TPL = {
  // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
  // shell. showLoginModal() renders the shell once and then swaps #pf-auth-body
  // between loginBody and signupBody. The shell keeps the Skip control so
  // deferred-login dismissal works from either view.
  loginModal: (project: string) => `
        <div class="pf-modal-overlay">
          <div class="pf-modal">
            <h2>${escapeHtml(getBrandName())}</h2>
            <p>Leave feedback on <b>${escapeHtml(project)}</b>.</p>
            <div id="pf-auth-body"></div>
            <button class="pf-btn pf-link pf-btn-block pf-auth-skip" id="pf-login-skip">Skip for now</button>
          </div>
        </div>`,

  // Sign-in body. After a "rejected" login it also renders an inline re-apply
  // block (role select + "Request again"); pass rejected=true to show it.
  loginBody: (rejected: boolean) => `
        <input class="pf-input pf-stack-gap" id="pf-email" type="email" placeholder="Email" />
        <input class="pf-input pf-stack-gap" id="pf-password" type="password" placeholder="Password" />
        <div class="pf-modal-error" id="pf-login-error"></div>
        <button class="pf-btn primary pf-btn-block" id="pf-login-submit">Sign in</button>
        ${rejected ? `
        <div class="pf-reapply" id="pf-reapply">
          <label class="pf-field-label" for="pf-reapply-role">Choose a role to request again</label>
          <select class="pf-input pf-stack-gap" id="pf-reapply-role"></select>
          <button class="pf-btn primary pf-btn-block" id="pf-reapply-submit">Request again</button>
        </div>` : ''}
        <div class="pf-auth-foot">
          No account? <button class="pf-btn pf-link pf-link-inline" id="pf-show-signup">Create account</button>
        </div>`,

  // Sign-up body. The role <select> is populated at runtime from GET /api/roles.
  signupBody: () => `
        <input class="pf-input pf-stack-gap" id="pf-su-name" type="text" placeholder="Name" />
        <input class="pf-input pf-stack-gap" id="pf-su-email" type="email" placeholder="Email" />
        <input class="pf-input pf-stack-gap" id="pf-su-password" type="password" placeholder="Password" />
        <label class="pf-field-label" for="pf-su-role">Role</label>
        <select class="pf-input pf-stack-gap" id="pf-su-role"></select>
        <div class="pf-modal-error" id="pf-signup-error"></div>
        <div class="pf-modal-success" id="pf-signup-success"></div>
        <button class="pf-btn primary pf-btn-block" id="pf-signup-submit">Create account</button>
        <div class="pf-auth-foot">
          Already have an account? <button class="pf-btn pf-link pf-link-inline" id="pf-show-login">Back to sign in</button>
        </div>`,

  // `fixedEnvLabel`: when the host fixed the environment at install time (attribute or injected
  // config), pass its display name to render a read-only label instead of the switcher — letting a
  // visitor switch an environment that was already explicitly configured is redundant and risks
  // misfiling a comment into the wrong bucket. Pass null/undefined to render the normal switcher.
  // `projectName`: shown next to the environment indicator so a visitor can immediately tell which
  // project this install is bound to — project keys aren't unique across a workspace, so two
  // different installs can easily look identical without this.
  chrome: (displayName: string, roleLabel: string, fixedEnvLabel?: string | null, projectName = '', shortcutLabel = '', unreadNotifyCount = 0) => `
        <div class="pf-toolbar">
          <span class="pf-grip" id="pf-grip" data-toggle="tooltip" data-placement="bottom" title="Drag to move" aria-label="Drag toolbar">${ICON.grip}</span>
          <button class="pf-btn pf-reset-pos pf-icon-btn pf-hidden" id="pf-reset-pos" data-toggle="tooltip" data-placement="bottom" title="Reset toolbar position" aria-label="Reset toolbar position">${ICON.restore}</button>
          <button class="pf-btn primary pf-icon-btn" id="pf-add" data-toggle="tooltip" data-placement="bottom" title="Comment on an element${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ''}" aria-label="Comment on an element${shortcutLabel ? `, shortcut ${escapeHtml(shortcutLabel)}` : ''}">${ICON.inspect}</button>
          <button class="pf-btn" id="pf-toggle" title="Show comments">Comments <span class="pf-badge" id="pf-count">0</span></button>
          <button class="pf-btn" id="pf-updates" title="Show notifications">Updates <span class="pf-badge pf-notify-badge${unreadNotifyCount > 0 ? '' : ' pf-hidden'}" id="pf-notify-count">${unreadNotifyCount > 99 ? '99+' : unreadNotifyCount}</span></button>
          ${displayName ? `<button class="pf-btn pf-icon-btn" id="pf-user" data-toggle="tooltip" data-placement="bottom" title="Signed in as ${displayName}${roleLabel ? ' · ' + roleLabel : ''}" aria-label="Signed in as ${displayName}">${ICON.user}</button>` : ''}
          <button class="pf-btn pf-icon-btn" id="pf-hide" data-toggle="tooltip" data-placement="bottom" title="Hide ${escapeHtml(getBrandName())}" aria-label="Hide ${escapeHtml(getBrandName())}">${ICON.eyeOff}</button>
        </div>
        <div class="pf-sidebar" id="pf-sidebar">
          <div class="pf-sidebar-head">
            <div class="pf-sidebar-head-row">
              <h2>Comments</h2>
              <button class="pf-mini pf-icon" id="pf-close" title="Close" aria-label="Close">&#x2715;</button>
            </div>
            <div class="pf-sidebar-head-row">
              <div class="pf-sidebar-meta">
                <span class="pf-project-name pf-caption" id="pf-project-name" title="${escapeHtml(projectName)}">${escapeHtml(projectName)}</span>
                ${fixedEnvLabel
                  ? `<span class="pf-env-label pf-caption" title="Environment — fixed for this install">&middot; ${escapeHtml(fixedEnvLabel)}</span>`
                  : `<select class="pf-input pf-env-select" id="pf-env" title="Environment — comments are scoped per environment">
                <option value="local">local</option>
                <option value="staging">staging</option>
                <option value="production">production</option>
              </select>`}
              </div>
              <button class="pf-mini pf-icon" id="pf-refresh" title="Refresh comments" aria-label="Refresh comments">&#8635;</button>
            </div>
            <div class="pf-commit-style pf-hidden" id="pf-commit-style"></div>
          </div>
          <div class="pf-filters" id="pf-filters"></div>
          <div class="pf-sidebar-body" id="pf-list"></div>
        </div>
        <div id="pf-pins"></div>
        <div id="pf-popover-host"></div>
        <div id="pf-menu-host"></div>`,

  // Commit-style control (see element.ts's fetchCaptureConfig/renderCommitStyleControl) — only
  // ever rendered when the CURRENT caller is authorized to change project settings
  // (CaptureConfigResponse.CanEditSettings, same gate as the PATCH itself). Lets whoever's looking
  // choose whether the AI apply flow bundles applied comments into one commit or commits each one
  // separately — read by skill.md's Step 1 the next time an agent applies.
  commitStyleControl: (commitStyle: number) => `
        <span class="pf-caption">Commit style</span>
        <select class="pf-input pf-commit-style-select" id="pf-commit-style-select" title="How the AI apply flow commits applied comments">
          <option value="1" ${commitStyle === 1 ? 'selected' : ''}>One commit</option>
          <option value="2" ${commitStyle === 2 ? 'selected' : ''}>Separate commits</option>
        </select>`,

  // Dropdown under the user icon: shows identity, the per-user "add comment" shortcut
  // (click to rebind, ↺ to reset), and a Sign out action.
  userMenu: (displayName: string, roleLabel: string, shortcutLabel: string, authOwnedByHost?: boolean) => `
        <div class="pf-menu" id="pf-user-menu" role="menu">
          <div class="pf-menu-id">
            <span>${displayName}</span>
            ${roleLabel ? `<span class="pf-menu-role">${roleLabel}</span>` : ''}
          </div>
          <div class="pf-menu-shortcut">
            <span class="pf-menu-shortcut-label">Add comment</span>
            <button type="button" id="pf-shortcut-edit" class="pf-mini" title="Click, then press a new key combo">${escapeHtml(shortcutLabel)}</button>
            <button type="button" id="pf-shortcut-reset" class="pf-mini pf-icon-btn" title="Reset to default">&#8635;</button>
          </div>
          ${authOwnedByHost
            ? `<div class="pf-menu-note pf-caption">Signed in via the browser extension — sign out from its popup.</div>`
            : `<button class="pf-menu-item" id="pf-signout" role="menuitem">${ICON.logout}<span>Sign out</span></button>`}
        </div>`,

  // Collapsed state: a small floating launcher that re-opens the overlay.
  // `rtl` makes start/end resolve against the host page direction (the shadow
  // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
  launcher: (count: number, position: string, rtl: boolean, unreadNotifyCount = 0) => {
    const hasUnread = unreadNotifyCount > 0;
    const badgeCount = hasUnread ? unreadNotifyCount : count;
    return `
        <button class="pf-launcher pf-pos-${position || 'bottom-end'}${rtl ? ' pf-rtl' : ''}" id="pf-launcher" title="Open ${escapeHtml(getBrandName())} feedback" aria-label="Open ${escapeHtml(getBrandName())} feedback">
          ${ICON.pin}
          ${badgeCount ? `<span class="pf-launcher-badge${hasUnread ? ' pf-notify-badge' : ''}">${badgeCount > 99 ? '99+' : badgeCount}</span>` : ''}
        </button>`;
  },

  empty: (msg: string) => `<div class="pf-empty">${msg}</div>`,

  // Status filter as a dropdown (rather than a row of chip buttons) — keeps the filter bar compact.
  statusFilterSelect: (filters: { key: string; label: string; color?: string }[], active: string, counts: Record<string, number>) =>
    `<select class="pf-status-select" id="pf-status-filter" title="Filter by status">
             ${filters.map((f) => `<option value="${f.key}" ${f.key === active ? 'selected' : ''}>${escapeHtml(f.label)} (${counts[f.key] ?? 0})</option>`).join('')}
           </select>`,

  // "Mine only" toggle — a chip that composes with the status chips above.
  // Rendered only when a user is logged in.
  mineToggle: (active: boolean) =>
    `<button class="pf-chip pf-mine ${active ? 'active' : ''}" id="pf-mine-toggle" title="Show only my comments" aria-pressed="${active ? 'true' : 'false'}">
             &#x1f464; Mine only
           </button>`,

  // User filter — only rendered when the list has comments from >1 author.
  authorFilter: (authors: AuthorOption[], selectedId: string) =>
    `<select class="pf-userfilter" id="pf-author-filter" title="Filter by user">
             <option value="">&#x1f465; All users</option>
             ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? 'selected' : ''}>${escapeHtml(a.name)}</option>`).join('')}
           </select>`,

  card: (c: Comment, i: number, isQuickAccess?: boolean) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : '';
    // "completed" means a developer applied it; "live" means it is actually on the site. Those
    // are different days for the person who left the comment, and telling them apart is the whole
    // point of deploy awareness — the title names the build so they can ask about a specific one.
    const statusPill = c.status === 'applied' && c.deployedAt
      ? `<span class="pf-pill status-applied" title="Deployed in ${escapeHtml((c.deployedSha || '').slice(0, 7))}">&#x2713; live</span>`
      : c.status === 'applied'
      ? '<span class="pf-pill status-applied">&#x2713; completed</span>'
      : c.status === 'pending-apply' ? '<span class="pf-pill status-pending">pending</span>'
      : c.status === 'archived' ? '<span class="pf-pill status-archived">&#x1f4e6; archived</span>' : '';
    const verifiedPill = (c.status === 'applied' && c.verifiedAt)
      ? '<span class="pf-pill verified">&#x2713; Verified</span>'
      : '';
    const verifyGroup = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<span class="pf-verify-group">
          <button class="pf-mini pf-verify-ok" data-act="verify-ok" data-id="${c.id}" title="Looks right">&#x1f44d; Looks right</button>
          <button class="pf-mini pf-verify-reject" data-act="verify-reject" data-id="${c.id}" title="Not fixed">&#x1f44e; Not fixed</button>
        </span>`
      : '';
    const verifyBox = (c.status === 'applied' && !c.verifiedAt && c._canVerify)
      ? `<div class="pf-verify-box pf-hidden" id="pf-verify-box-${c.id}">
          <input class="pf-input pf-verify-note-input" id="pf-verify-note-${c.id}" placeholder="Explain what is still not fixed…" />
          <div class="pf-verify-actions">
            <button class="pf-mini primary" data-act="verify-submit" data-id="${c.id}">Submit</button>
            <button class="pf-mini" data-act="verify-cancel" data-id="${c.id}">Cancel</button>
          </div>
        </div>`
      : '';
    // A "#" href for comments with no tracked commit (applied before this field existed, or by a
    // flow that doesn't record one) — inert rather than a broken/missing link.
    const commitLink = c.status === 'applied'
      ? `<a class="pf-pill" href="${c.commitUrl ? escapeHtml(c.commitUrl) : '#'}" ${c.commitUrl ? 'target="_blank" rel="noopener noreferrer"' : ''} title="${c.commitUrl ? 'View commit' : 'No commit recorded for this comment'}">&#x1f517; commit</a>`
      : '';
    // Advisory only: the server flagged this text as looking like a credential or payload, so a
    // reviewer notices before acting on it. Nothing is blocked and nothing is rewritten — and the
    // flag is absent entirely for AI callers, so this pill is the only place it ever appears.
    const payloadPill = c.hasPayloadFlag
      ? `<span class="pf-pill pf-payload-flag" title="${escapeHtml((c.payloadFlags || []).join(', '))}">&#x26a0; contains a secret/payload?</span>`
      : '';

    const replies = (c.replies || []).map((r) =>
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
              ${payloadPill}
              ${statusPill}
              ${verifiedPill}
              ${verifyGroup}
              ${commitLink}
              <div class="pf-actions-end">
                ${c._mine ? `<button class="pf-mini pf-icon${c.isPrivate ? ' private-on' : ''}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? 'false' : 'true'}" title="${c.isPrivate ? 'Private — click to make public' : 'Make private (only you)'}" aria-label="${c.isPrivate ? 'Make public' : 'Make private'}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ''}
                ${c.status === 'open' ? `<button class="pf-mini danger pf-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ''}
              </div>
            </div>
            <div class="pf-text">${escapeHtml(c.body || c.text || '')}</div>
            ${shot}
            <div class="pf-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ''}${c.editedAt ? ' &middot; <span class="pf-edited">edited</span>' : ''}</div>
            ${verifyBox}
            ${replies ? `<div class="pf-replies">${replies}</div>` : ''}
            <div class="pf-reply-row">
              <input class="pf-input pf-reply-input" placeholder="Reply…" data-id="${c.id}" />
            </div>
            <div class="pf-actions">
              ${isQuickAccess ? '' : (c.status === 'applied' || c.status === 'archived') ? '' : `<button class="pf-mini ${c.status === 'pending-apply' ? 'apply' : 'ready'}" data-act="apply" data-id="${c.id}" title="${c.status === 'pending-apply' ? 'Marked ready — click to unmark' : 'Mark ready to apply'}">
                ${ICON.flag}<span>Ready</span>
              </button>`}
              ${(!isQuickAccess && (c.status === 'open' || c.status === 'pending-apply')) ? `<button class="pf-mini done pf-icon" data-act="complete" data-id="${c.id}" title="Mark completed" aria-label="Mark completed">${ICON.check}</button>` : ''}
              ${(!isQuickAccess && c.status === 'applied') ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>
              <button class="pf-mini pf-icon" data-act="archive" data-id="${c.id}" title="Archive" aria-label="Archive">${ICON.archive}</button>` : ''}
              ${(!isQuickAccess && c.status === 'archived') ? `<button class="pf-mini ready" data-act="reopen" data-id="${c.id}" title="Re-open">${ICON.reopen}<span>Re-open</span></button>` : ''}
              ${c._mine ? `<div class="pf-actions-end"><button class="pf-mini pf-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button></div>` : ''}
            </div>
          </div>`;
  },

  // `bugReportEnabled`: only true when the project has page-context capture turned on — the
  // checkbox controls whether the console/network buffer already sitting in memory gets attached
  // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
  popover: (meta: Meta, left: number, top: number, shotEnabled: boolean, actions: PredefinedActionOption[] = [], bugReportEnabled = false) => `
        <div class="pf-popover" data-pf-left="${left}" data-pf-top="${top}">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ''}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          ${actions.length ? `<div class="pf-field-label">Predefined prompts</div>
          <div class="pf-actions-pick" id="pf-action-pick">
            ${actions.map((a) => `<label class="pf-check"><input type="checkbox" class="pf-action-opt" value="${a.id}" /> ${escapeHtml(a.text)}</label>`).join('')}
          </div>` : ''}
          ${shotEnabled ? `<label class="pf-check"><input type="checkbox" id="pf-comment-shot" /> &#x1f4f7; Attach screenshot</label>` : ''}
          ${bugReportEnabled ? `<label class="pf-check" title="Attaches any console errors/warnings and failed or slow network requests seen on this page"><input type="checkbox" id="pf-comment-bug" /> &#x1f41e; Report as a bug</label>` : ''}
          <label class="pf-check"><input type="checkbox" id="pf-comment-private" /> &#x1f512; Keep private — only me</label>
          <div class="pf-reply-row">
            <button class="pf-btn primary pf-btn-fill" id="pf-submit">Add</button>
            <button class="pf-mini" id="pf-cancel">Cancel</button>
          </div>
        </div>`,

  pin: (c: Comment, i: number, rect: DOMRect) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : '';
    return `<div class="pf-pin ${cls}" data-id="${c.id}" data-pf-left="${rect.left}" data-pf-top="${rect.top}"><span>${i + 1}</span></div>`;
  },

  notificationsMenu: (items: NotificationItem[]) => `
        <div class="pf-notifications-menu" id="pf-notifications-menu" role="menu">
          <div class="pf-notifications-head">
            <h3>Updates</h3>
          </div>
          <div class="pf-notifications-body">
            ${items.length === 0
              ? '<div class="pf-empty pf-notifications-empty">No updates yet</div>'
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
                      ? `<div class="pf-notification-item-commit"><a class="pf-pill" href="${escapeHtml(item.payload.commitUrl)}" target="_blank" rel="noopener noreferrer" onclick="event.stopPropagation()">&#x1f517; Commit</a></div>`
                      : '';
                  } else if (typeNum === 2) {
                    typeLabel = 'Reopened';
                    icon = ICON.reopen;
                  } else if (typeNum === 3) {
                    typeLabel = 'New reply';
                    icon = ICON.inspect;
                    if (item.payload?.replyExcerpt) {
                      detail = `<div class="pf-notification-reply pf-caption">"${escapeHtml(item.payload.replyExcerpt)}"</div>`;
                    }
                  }
                  const timeAgo = item.createdAt ? new Date(item.createdAt).toLocaleDateString() : '';
                  return `
                    <div class="pf-notification-item${isUnread ? ' unread' : ''}" data-id="${item.commentId}" role="menuitem">
                      <div class="pf-notification-item-head">
                        <span class="pf-notification-item-type">${icon} ${escapeHtml(typeLabel)}</span>
                        <span class="pf-notification-item-time">${escapeHtml(timeAgo)}</span>
                      </div>
                      <div class="pf-notification-item-body">
                        ${escapeHtml(item.commentBodyExcerpt || '')}
                      </div>
                      ${detail}
                    </div>`;
                }).join('')}
          </div>
        </div>`,
};
