import { escapeHtml } from './dom';
import { ICON } from './icons';
import { getBrandName } from './constants';
import type { AuthorOption, Comment, Meta, PredefinedActionOption } from './types';

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
            <button class="pf-btn pf-link" id="pf-login-skip" style="width:100%; justify-content:center; margin-top:8px;">Skip for now</button>
          </div>
        </div>`,

  // Sign-in body. After a "rejected" login it also renders an inline re-apply
  // block (role select + "Request again"); pass rejected=true to show it.
  loginBody: (rejected: boolean) => `
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

  // `fixedEnvLabel`: when the host fixed the environment at install time (attribute or injected
  // config), pass its display name to render a read-only label instead of the switcher — letting a
  // visitor switch an environment that was already explicitly configured is redundant and risks
  // misfiling a comment into the wrong bucket. Pass null/undefined to render the normal switcher.
  // `projectName`: shown next to the environment indicator so a visitor can immediately tell which
  // project this install is bound to — project keys aren't unique across a workspace, so two
  // different installs can easily look identical without this.
  chrome: (displayName: string, roleLabel: string, fixedEnvLabel?: string | null, projectName = '', shortcutLabel = '') => `
        <div class="pf-toolbar">
          <span class="pf-grip" id="pf-grip" data-toggle="tooltip" data-placement="bottom" title="Drag to move" aria-label="Drag toolbar">${ICON.grip}</span>
          <button class="pf-btn pf-icon-btn pf-reset-pos" id="pf-reset-pos" data-toggle="tooltip" data-placement="bottom" title="Reset toolbar position" aria-label="Reset toolbar position" style="display:none">${ICON.restore}</button>
          <button class="pf-btn primary pf-icon-btn" id="pf-add" data-toggle="tooltip" data-placement="bottom" title="Comment on an element${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ''}" aria-label="Comment on an element${shortcutLabel ? `, shortcut ${escapeHtml(shortcutLabel)}` : ''}">${ICON.inspect}</button>
          <button class="pf-btn" id="pf-toggle" title="Show comments">Comments <span class="pf-badge" id="pf-count">0</span></button>
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
              <div style="display:flex; align-items:center; gap:6px; min-width:0;">
                <span id="pf-project-name" title="${escapeHtml(projectName)}" style="font-size:12px; color:#64748b; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:150px;">${escapeHtml(projectName)}</span>
                ${fixedEnvLabel
                  ? `<span class="pf-env-label" title="Environment — fixed for this install" style="font-size:12px; color:#64748b; text-transform:capitalize;">&middot; ${escapeHtml(fixedEnvLabel)}</span>`
                  : `<select class="pf-input pf-env-select" id="pf-env" title="Environment — comments are scoped per environment" style="width:auto; padding:4px 8px;">
                <option value="local">local</option>
                <option value="staging">staging</option>
                <option value="production">production</option>
              </select>`}
              </div>
              <button class="pf-mini pf-icon" id="pf-refresh" title="Refresh comments" aria-label="Refresh comments">&#8635;</button>
            </div>
          </div>
          <div class="pf-filters" id="pf-filters"></div>
          <div class="pf-sidebar-body" id="pf-list"></div>
        </div>
        <div id="pf-pins"></div>
        <div id="pf-popover-host"></div>
        <div id="pf-menu-host"></div>`,

  // Dropdown under the user icon: shows identity, the per-user "add comment" shortcut
  // (click to rebind, ↺ to reset), and a Sign out action.
  userMenu: (displayName: string, roleLabel: string, shortcutLabel: string) => `
        <div class="pf-menu" id="pf-user-menu" role="menu">
          <div class="pf-menu-id">
            <span>${displayName}</span>
            ${roleLabel ? `<span class="pf-menu-role">${roleLabel}</span>` : ''}
          </div>
          <div style="display:flex; align-items:center; gap:6px; padding:6px 12px; font-size:12px;">
            <span style="flex:1; color:inherit;">Add comment</span>
            <button type="button" id="pf-shortcut-edit" class="pf-mini" title="Click, then press a new key combo">${escapeHtml(shortcutLabel)}</button>
            <button type="button" id="pf-shortcut-reset" class="pf-mini pf-icon-btn" title="Reset to default">&#8635;</button>
          </div>
          <button class="pf-menu-item" id="pf-signout" role="menuitem">${ICON.logout}<span>Sign out</span></button>
        </div>`,

  // Collapsed state: a small floating launcher that re-opens the overlay.
  // `rtl` makes start/end resolve against the host page direction (the shadow
  // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
  launcher: (count: number, position: string, rtl: boolean) => `
        <button class="pf-launcher pf-pos-${position || 'bottom-end'}${rtl ? ' pf-rtl' : ''}" id="pf-launcher" title="Open Pointer feedback" aria-label="Open Pointer feedback">
          ${ICON.pin}
          ${count ? `<span class="pf-launcher-badge">${count > 99 ? '99+' : count}</span>` : ''}
        </button>`,

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

  card: (c: Comment, i: number) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : c.status === 'archived' ? 'archived' : '';
    const statusPill = c.status === 'applied'
      ? '<span class="pf-pill status-applied">&#x2713; completed</span>'
      : c.status === 'pending-apply' ? '<span class="pf-pill status-pending">pending</span>'
      : c.status === 'archived' ? '<span class="pf-pill status-archived">&#x1f4e6; archived</span>' : '';
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
              ${statusPill}
              <div class="pf-actions-end">
                ${c._mine ? `<button class="pf-mini pf-icon${c.isPrivate ? ' private-on' : ''}" data-act="visibility" data-id="${c.id}" data-private="${c.isPrivate ? 'false' : 'true'}" title="${c.isPrivate ? 'Private — click to make public' : 'Make private (only you)'}" aria-label="${c.isPrivate ? 'Make public' : 'Make private'}">${c.isPrivate ? ICON.lock : ICON.unlock}</button>` : ''}
                ${c.status === 'open' ? `<button class="pf-mini danger pf-icon" data-act="delete" data-id="${c.id}" title="Delete" aria-label="Delete">${ICON.trash}</button>` : ''}
              </div>
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
              ${c._mine ? `<div class="pf-actions-end"><button class="pf-mini pf-icon" data-act="edit" data-id="${c.id}" title="Edit" aria-label="Edit">${ICON.pencil}</button></div>` : ''}
            </div>
          </div>`;
  },

  // `bugReportEnabled`: only true when the project has page-context capture turned on — the
  // checkbox controls whether the console/network buffer already sitting in memory gets attached
  // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
  popover: (meta: Meta, left: number, top: number, shotEnabled: boolean, actions: PredefinedActionOption[] = [], bugReportEnabled = false) => `
        <div class="pf-popover" style="left:${left}px; top:${top}px;">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ''}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          ${actions.length ? `<div class="pf-field-label">Predefined prompts</div>
          <div class="pf-actions-pick" id="pf-action-pick" style="margin-bottom:6px; display:flex; flex-direction:column; gap:4px;">
            ${actions.map((a) => `<label class="pf-check"><input type="checkbox" class="pf-action-opt" value="${a.id}" /> ${escapeHtml(a.text)}</label>`).join('')}
          </div>` : ''}
          ${shotEnabled ? `<label class="pf-check"><input type="checkbox" id="pf-comment-shot" /> &#x1f4f7; Attach screenshot</label>` : ''}
          ${bugReportEnabled ? `<label class="pf-check" title="Attaches any console errors/warnings and failed or slow network requests seen on this page"><input type="checkbox" id="pf-comment-bug" /> &#x1f41e; Report as a bug</label>` : ''}
          <label class="pf-check"><input type="checkbox" id="pf-comment-private" /> &#x1f512; Keep private — only me</label>
          <div class="pf-reply-row">
            <button class="pf-btn primary" id="pf-submit" style="flex:1; justify-content:center;">Add</button>
            <button class="pf-mini" id="pf-cancel">Cancel</button>
          </div>
        </div>`,

  pin: (c: Comment, i: number, rect: DOMRect) => {
    const cls = c.status === 'pending-apply' ? 'pending' : c.status === 'applied' ? 'applied' : '';
    return `<div class="pf-pin ${cls}" data-id="${c.id}" style="left:${rect.left}px; top:${rect.top}px;"><span>${i + 1}</span></div>`;
  },
};
