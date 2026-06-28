import { escapeHtml } from './dom';
import { ICON } from './icons';
import { STATUS_LABEL } from './constants';
import type { AuthorOption, Comment, Meta } from './types';

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
            <h2>Pointer</h2>
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

  chrome: (displayName: string, roleLabel: string) => `
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
  launcher: (count: number, position: string, rtl: boolean) => `
        <button class="pf-launcher pf-pos-${position || 'bottom-end'}${rtl ? ' pf-rtl' : ''}" id="pf-launcher" title="Open Pointer feedback" aria-label="Open Pointer feedback">
          ${ICON.pin}
          ${count ? `<span class="pf-launcher-badge">${count > 99 ? '99+' : count}</span>` : ''}
        </button>`,

  empty: (msg: string) => `<div class="pf-empty">${msg}</div>`,

  filterChip: (f: { key: string; label: string }, active: boolean, count: number) =>
    `<button class="pf-chip ${active ? 'active' : ''} chip-${STATUS_LABEL[f.key] || 'all'}" data-filter="${f.key}">
             ${f.label} <span class="pf-chip-n">${count}</span>
           </button>`,

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

  popover: (meta: Meta, left: number, top: number, shotEnabled: boolean) => `
        <div class="pf-popover" style="left:${left}px; top:${top}px;">
          <h3>Comment on &lt;${escapeHtml(meta._tag)}&gt;</h3>
          <div class="pf-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
          ${meta._sourcePath ? `<div class="pf-src">&#x26ec; ${escapeHtml(meta._sourcePath)}</div>` : ''}
          <textarea class="pf-textarea" id="pf-comment-text" placeholder="What should change here?"></textarea>
          ${shotEnabled ? `<label class="pf-check"><input type="checkbox" id="pf-comment-shot" /> &#x1f4f7; Attach screenshot</label>` : ''}
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
