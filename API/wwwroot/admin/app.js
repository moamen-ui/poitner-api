/* Pointer Admin — build-free vanilla JS against the Pointer API.
 * Auth: admin logs in → JWT in localStorage['pointer_admin_token'].
 * Admin access is capability-based (the account's role grants admin / is_admin claim). */
(() => {
  'use strict';

  const TOKEN_KEY = 'pointer_admin_token';
  const USER_KEY = 'pointer_admin_user';

  const $ = (id) => document.getElementById(id);
  const server = window.location.origin;
  let token = localStorage.getItem(TOKEN_KEY) || null;
  let user = null;
  try { user = JSON.parse(localStorage.getItem(USER_KEY) || 'null'); } catch (e) { user = null; }

  let roles = []; // cached role catalog, shared by the user role selects

  // --- helpers -------------------------------------------------------------
  function toast(msg, type = '') {
    const t = $('toast');
    t.textContent = msg;
    t.className = `toast ${type}`;
    t.hidden = false;
    clearTimeout(toast._t);
    toast._t = setTimeout(() => { t.hidden = true; }, 2400);
  }

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  async function api(path, opts = {}) {
    const res = await fetch(`${server}${path}`, {
      ...opts,
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(opts.headers || {}),
      },
    });
    if (res.status === 401) { logout(); throw new Error('unauthorized'); }
    let body = null;
    try { body = await res.json(); } catch (e) { /* no body */ }
    return { ok: res.ok, status: res.status, body };
  }

  // --- views ---------------------------------------------------------------
  function showLogin() { $('app-view').hidden = true; $('login-view').hidden = false; }
  function showApp() {
    $('login-view').hidden = true;
    $('app-view').hidden = false;
    $('whoami').textContent = user ? `${user.displayName || user.email} · ${user.roleName || ''}` : '';
  }
  function logout() {
    token = null; user = null;
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    showLogin();
  }

  // --- login ---------------------------------------------------------------
  $('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const email = $('login-email').value.trim();
    const password = $('login-password').value;
    const errEl = $('login-error');
    const btn = $('login-btn');
    errEl.textContent = '';
    btn.disabled = true; btn.textContent = 'Signing in…';
    try {
      const res = await fetch(`${server}/api/auth/login`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
      });
      const body = await res.json();
      if (!res.ok || !body.isSuccess) { errEl.textContent = body.message || 'Invalid email or password.'; return; }
      if (!body.data.user.isAdmin) { errEl.textContent = 'This account is not an admin.'; return; }
      token = body.data.token;
      user = body.data.user;
      localStorage.setItem(TOKEN_KEY, token);
      localStorage.setItem(USER_KEY, JSON.stringify(user));
      showApp();
      loadAll();
    } catch (err) {
      errEl.textContent = 'Network error. Please try again.';
    } finally {
      btn.disabled = false; btn.textContent = 'Sign in';
    }
  });

  $('logout-btn').addEventListener('click', logout);

  // --- stats / overview ----------------------------------------------------
  async function loadStats() {
    const { ok, body } = await api('/api/admin/stats');
    if (!ok) { toast('Failed to load stats', 'error'); return; }
    const t = body.data.totals;
    const cards = [
      { l: 'Projects', n: t.projects, cls: '' },
      { l: 'Users', n: t.users, cls: '' },
      { l: 'Comments', n: t.comments, cls: '' },
      { l: 'Open', n: t.open, cls: 'open' },
      { l: 'Pending', n: t.pending, cls: 'pending' },
      { l: 'Completed', n: t.completed, cls: 'completed' },
    ];
    $('stat-cards').innerHTML = cards.map((c) =>
      `<div class="card ${c.cls}"><div class="n">${c.n}</div><div class="l">${c.l}</div></div>`).join('');

    const rows = body.data.projects || [];
    $('stats-projects-body').innerHTML = rows.map((p) => `
      <tr>
        <td><code>${esc(p.key)}</code></td>
        <td>${p.comments}</td>
        <td>${p.open}</td>
        <td>${p.pending}</td>
        <td>${p.completed}</td>
        <td><span class="status ${p.isActive ? 'active' : 'inactive'}">${p.isActive ? 'Active' : 'Disabled'}</span></td>
      </tr>`).join('') ||
      '<tr><td colspan="6" class="muted" style="padding:16px;text-align:center;">No projects yet.</td></tr>';
  }

  $('stats-refresh').addEventListener('click', loadStats);

  // --- roles ---------------------------------------------------------------
  function roleOptions(selectedId) {
    return roles
      .filter((r) => r.isActive || r.id === selectedId)
      .map((r) => `<option value="${r.id}" ${r.id === selectedId ? 'selected' : ''}>${esc(r.name)}</option>`)
      .join('');
  }

  async function loadRoles() {
    const { ok, body } = await api('/api/admin/roles');
    if (!ok) { toast('Failed to load roles', 'error'); return; }
    roles = body.data || [];
    $('roles-count').textContent = roles.length;
    $('roles-body').innerHTML = roles.map((r) => `
      <tr data-id="${r.id}">
        <td>${esc(r.name)} ${r.isSystem ? '<span class="tag" style="background:#e2e8f0;color:#475569;">system</span>' : ''}</td>
        <td>
          <input type="checkbox" data-act="grants" ${r.grantsAdmin ? 'checked' : ''} ${r.isSystem ? 'disabled' : ''} />
        </td>
        <td><span class="status ${r.isActive ? 'active' : 'inactive'}">${r.isActive ? 'Active' : 'Disabled'}</span></td>
        <td class="cell-actions">
          ${r.isSystem ? '' : `
            <button class="btn mini" data-act="rename">Rename</button>
            <button class="btn mini ${r.isActive ? 'danger' : ''}" data-act="toggle">${r.isActive ? 'Disable' : 'Enable'}</button>`}
        </td>
      </tr>`).join('');

    $('roles-body').querySelectorAll('[data-act="grants"]').forEach((cb) =>
      cb.addEventListener('change', () => patchRole(cb.closest('tr').dataset.id, { grantsAdmin: cb.checked }, 'Role updated')));
    $('roles-body').querySelectorAll('[data-act="toggle"]').forEach((btn) =>
      btn.addEventListener('click', () => {
        const tr = btn.closest('tr');
        const isActive = tr.querySelector('.status').classList.contains('active');
        patchRole(tr.dataset.id, { isActive: !isActive }, isActive ? 'Role disabled' : 'Role enabled');
      }));
    $('roles-body').querySelectorAll('[data-act="rename"]').forEach((btn) =>
      btn.addEventListener('click', () => {
        const tr = btn.closest('tr');
        const current = roles.find((x) => String(x.id) === tr.dataset.id);
        const name = prompt('Rename role', current ? current.name : '');
        if (name && current && name.trim() !== current.name) patchRole(tr.dataset.id, { name: name.trim() }, 'Role renamed');
      }));
  }

  async function patchRole(id, payload, okMsg) {
    const { ok, body } = await api(`/api/admin/roles/${id}`, { method: 'PATCH', body: JSON.stringify(payload) });
    if (!ok) { toast((body && body.message) || 'Update failed', 'error'); }
    else { toast(okMsg, 'success'); }
    await loadRoles();
    loadUsers(); // role names / availability changed — refresh user selects
  }

  $('role-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const errEl = $('role-form-error');
    errEl.textContent = '';
    const payload = { name: $('r-name').value.trim(), grantsAdmin: $('r-admin').checked };
    const { ok, body } = await api('/api/admin/roles', { method: 'POST', body: JSON.stringify(payload) });
    if (!ok) { errEl.textContent = (body && body.message) || 'Could not create role.'; return; }
    e.target.reset();
    toast('Role created', 'success');
    await loadRoles();
    populateCreateUserRoleSelect();
  });

  // --- users ---------------------------------------------------------------
  function populateCreateUserRoleSelect() {
    const sel = $('u-role');
    const nonAdmin = roles.find((r) => r.isActive && !r.grantsAdmin) || roles.find((r) => r.isActive) || roles[0];
    sel.innerHTML = roleOptions(nonAdmin ? nonAdmin.id : undefined);
  }

  async function loadUsers() {
    const { ok, body } = await api('/api/admin/users');
    if (!ok) { toast('Failed to load users', 'error'); return; }
    const users = body.data || [];
    $('users-count').textContent = users.length;
    $('users-body').innerHTML = users.map((u) => `
      <tr data-id="${u.id}">
        <td>${esc(u.email)}</td>
        <td>${esc(u.displayName)}</td>
        <td><select class="role-select" data-act="role">${roleOptions(u.roleId)}</select></td>
        <td><span class="status ${u.isActive ? 'active' : 'inactive'}">${u.isActive ? 'Active' : 'Disabled'}</span></td>
        <td class="cell-actions">
          <button class="btn mini ${u.isActive ? 'danger' : ''}" data-act="toggle">${u.isActive ? 'Disable' : 'Enable'}</button>
        </td>
      </tr>`).join('') ||
      '<tr><td colspan="5" class="muted" style="padding:16px;text-align:center;">No users yet.</td></tr>';

    $('users-body').querySelectorAll('[data-act="role"]').forEach((sel) =>
      sel.addEventListener('change', () => patchUser(sel.closest('tr').dataset.id, { roleId: Number(sel.value) }, 'Role updated')));
    $('users-body').querySelectorAll('[data-act="toggle"]').forEach((btn) =>
      btn.addEventListener('click', () => {
        const tr = btn.closest('tr');
        const isActive = tr.querySelector('.status').classList.contains('active');
        patchUser(tr.dataset.id, { isActive: !isActive }, isActive ? 'User disabled' : 'User enabled');
      }));
  }

  async function patchUser(id, payload, okMsg) {
    const { ok, body } = await api(`/api/admin/users/${id}`, { method: 'PATCH', body: JSON.stringify(payload) });
    if (!ok) { toast((body && body.message) || 'Update failed', 'error'); }
    else { toast(okMsg, 'success'); }
    loadUsers();
  }

  $('user-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const errEl = $('user-form-error');
    errEl.textContent = '';
    const payload = {
      email: $('u-email').value.trim(),
      displayName: $('u-name').value.trim(),
      password: $('u-password').value,
      roleId: Number($('u-role').value),
    };
    const { ok, body } = await api('/api/admin/users', { method: 'POST', body: JSON.stringify(payload) });
    if (!ok) { errEl.textContent = (body && body.message) || 'Could not create user.'; return; }
    e.target.reset();
    populateCreateUserRoleSelect();
    toast('User created', 'success');
    loadUsers();
  });

  // --- projects ------------------------------------------------------------
  async function loadProjects() {
    const { ok, body } = await api('/api/admin/projects');
    if (!ok) { toast('Failed to load projects', 'error'); return; }
    const projects = body.data || [];
    $('projects-count').textContent = projects.length;
    $('projects-body').innerHTML = projects.map((p) => `
      <tr data-id="${p.id}">
        <td><code>${esc(p.key)}</code></td>
        <td>${esc(p.name)}</td>
        <td><span class="status ${p.isActive ? 'active' : 'inactive'}">${p.isActive ? 'Active' : 'Disabled'}</span></td>
        <td class="cell-actions">
          <button class="btn mini ${p.isActive ? 'danger' : ''}" data-act="toggle">${p.isActive ? 'Disable' : 'Enable'}</button>
        </td>
      </tr>`).join('') ||
      '<tr><td colspan="4" class="muted" style="padding:16px;text-align:center;">No projects yet.</td></tr>';

    $('projects-body').querySelectorAll('[data-act="toggle"]').forEach((btn) =>
      btn.addEventListener('click', async () => {
        const tr = btn.closest('tr');
        const isActive = tr.querySelector('.status').classList.contains('active');
        const { ok, body } = await api(`/api/admin/projects/${tr.dataset.id}`, { method: 'PATCH', body: JSON.stringify({ isActive: !isActive }) });
        if (!ok) { toast((body && body.message) || 'Update failed', 'error'); } else { toast(isActive ? 'Project disabled' : 'Project enabled', 'success'); }
        loadProjects();
      }));
  }

  $('project-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const errEl = $('project-form-error');
    errEl.textContent = '';
    const payload = { key: $('p-key').value.trim(), name: $('p-name').value.trim() };
    const { ok, body } = await api('/api/admin/projects', { method: 'POST', body: JSON.stringify(payload) });
    if (!ok) { errEl.textContent = (body && body.message) || 'Could not create project.'; return; }
    e.target.reset();
    toast('Project created', 'success');
    loadProjects();
  });

  async function loadAll() {
    loadStats();
    await loadRoles();          // roles first — user selects depend on the catalog
    populateCreateUserRoleSelect();
    loadUsers();
    loadProjects();
  }

  // --- boot ----------------------------------------------------------------
  if (token && user && user.isAdmin) { showApp(); loadAll(); } else { showLogin(); }
})();
