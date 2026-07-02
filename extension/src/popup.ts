import { DEFAULT_SERVER, hostnameOf, type BgRequest, type StoredUser, type ExtProject } from './shared';

const root = document.getElementById('root')!;
const errEl = document.getElementById('err')!;

function send<T = any>(msg: BgRequest): Promise<T> {
  return new Promise((resolve) => chrome.runtime.sendMessage(msg, resolve));
}
async function currentTab(): Promise<chrome.tabs.Tab | undefined> {
  const [t] = await chrome.tabs.query({ active: true, currentWindow: true });
  return t;
}
function esc(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]!));
}
function err(msg: string) { errEl.textContent = msg; }

async function render() {
  err('');
  const state = await send<{ server: string; user: StoredUser | null; hasToken: boolean }>({ type: 'getState' });
  if (!state.hasToken) return renderAuth(state.server);
  return renderMain(state.user);
}

function renderAuth(server: string) {
  root.innerHTML = `
    <label>Pointer server</label>
    <input id="server" value="${esc(server || DEFAULT_SERVER)}" />
    <label>Email</label>
    <input id="email" type="email" autocomplete="username" />
    <label>Password</label>
    <input id="password" type="password" autocomplete="current-password" />
    <button class="primary" id="signin">Sign in</button>
    <div class="note">Log in once — the extension keeps you signed in across every site.</div>`;
  const doLogin = async () => {
    err('');
    const server2 = (document.getElementById('server') as HTMLInputElement).value.trim();
    const email = (document.getElementById('email') as HTMLInputElement).value.trim();
    const password = (document.getElementById('password') as HTMLInputElement).value;
    if (!email || !password) return err('Enter email and password.');
    const r = await send<{ ok: boolean; status?: string; message?: string }>({ type: 'login', email, password, server: server2 });
    if (r.ok) return render();
    err(r.status === 'pending' ? 'Account pending approval.' : r.status === 'rejected' ? 'Account rejected.' : (r.message || 'Login failed.'));
  };
  (document.getElementById('signin') as HTMLButtonElement).onclick = doLogin;
  (document.getElementById('password') as HTMLInputElement).onkeydown = (e) => { if (e.key === 'Enter') doLogin(); };
}

async function renderMain(user: StoredUser | null) {
  const tab = await currentTab();
  const url = tab?.url || '';
  const hostname = hostnameOf(url);
  const injectable = /^https?:/.test(url);

  const who = user ? esc(user.displayName || user.email || 'Signed in') : 'Signed in';
  if (!injectable) {
    root.innerHTML = `
      <div class="bar"><span class="who">${who}</span><a id="signout">Sign out</a></div>
      <div class="note" style="margin-top:12px;">Open a normal web page (http/https) to activate Pointer here.</div>`;
    (document.getElementById('signout') as HTMLElement).onclick = signOut;
    return;
  }

  const tabState = await send<{ active: boolean; remembered: { project: string; environment: string } | null }>({ type: 'getTabState', tabId: tab!.id!, hostname });
  const env = tabState.remembered?.environment || 'staging';
  const origin = (() => { try { return new URL(url).origin; } catch { return ''; } })();
  const isAdmin = !!user?.isAdmin;

  // Projects come from the signed-in user's workspace (dashboard-managed) — no free-typing keys.
  const listed = await send<{ ok: boolean; projects: ExtProject[]; error?: string }>({ type: 'listProjects' });
  const projects: ExtProject[] = listed.ok ? listed.projects : [];

  const draw = () => {
    const remembered = tabState.remembered?.project;
    const hasProjects = projects.length > 0;
    const opts = projects
      .map((p) => `<option value="${esc(p.key)}"${p.key === remembered ? ' selected' : ''}>${esc(p.name)} (${esc(p.key)})</option>`)
      .join('');

    root.innerHTML = `
      <div class="bar"><span class="who">${who}</span><a id="signout">Sign out</a></div>
      <div class="dom">${esc(hostname)}</div>
      ${hasProjects
        ? `<label>Project</label><select id="project">${opts}</select>`
        : `<div class="note" style="margin-top:8px;">${isAdmin ? 'No projects yet — add one below.' : 'No projects available. Ask your workspace admin to create one.'}</div>`}
      ${isAdmin ? `
        <a id="add-toggle" style="display:inline-block;margin:8px 0;cursor:pointer;">+ Add project</a>
        <div id="add-form" style="display:none;">
          <input id="new-key" placeholder="project-key" />
          <input id="new-name" placeholder="Display name (optional)" />
          <button class="primary" id="create">Create project</button>
        </div>` : ''}
      <label>Environment</label>
      <select id="env">
        <option value="local">local</option>
        <option value="staging">staging</option>
        <option value="production">production</option>
      </select>
      <button class="${tabState.active ? 'danger' : 'primary'}" id="toggle"${(!hasProjects && !tabState.active) ? ' disabled' : ''}>${tabState.active ? 'Deactivate on this tab' : 'Activate on this tab'}</button>
      <div class="note">Activating reloads this tab once, then injects the Pointer widget (works even on sites with a strict CSP).</div>`;

    (document.getElementById('env') as HTMLSelectElement).value = env;
    (document.getElementById('signout') as HTMLElement).onclick = signOut;

    if (isAdmin) {
      const addForm = document.getElementById('add-form') as HTMLElement | null;
      const addToggle = document.getElementById('add-toggle') as HTMLElement | null;
      if (addToggle && addForm) addToggle.onclick = () => { addForm.style.display = addForm.style.display === 'none' ? 'block' : 'none'; };
      const createBtn = document.getElementById('create') as HTMLButtonElement | null;
      if (createBtn) createBtn.onclick = async () => {
        err('');
        const key = (document.getElementById('new-key') as HTMLInputElement).value.trim();
        const name = (document.getElementById('new-name') as HTMLInputElement).value.trim() || key;
        if (!/^[A-Za-z0-9._-]+$/.test(key)) return err('Project key: letters, digits, . _ - only.');
        createBtn.disabled = true;
        const res = await send<{ ok: boolean; project?: ExtProject; error?: string }>({ type: 'createProject', key, name });
        if (!res.ok) { createBtn.disabled = false; return err(res.error || 'Could not create project.'); }
        if (!projects.some((p) => p.key === key)) projects.push({ key, name, isActive: true });
        tabState.remembered = { project: key, environment: env }; // preselect the newly-created project
        draw();
      };
    }

    (document.getElementById('toggle') as HTMLButtonElement).onclick = async () => {
      err('');
      if (tabState.active) {
        await send({ type: 'deactivate', tabId: tab!.id! });
        return window.close();
      }
      const project = (document.getElementById('project') as HTMLSelectElement | null)?.value.trim() || '';
      const environment = (document.getElementById('env') as HTMLSelectElement).value;
      if (!project) return err('Pick a project first.');
      const res = await send<{ ok: boolean; error?: string }>({ type: 'activate', tabId: tab!.id!, hostname, origin, project, environment });
      if (!res.ok) return err(res.error || 'Could not activate.'); // e.g. extension disabled on this plan
      window.close();
    };
  };

  if (!listed.ok && listed.error) err(listed.error);
  draw();
}

async function signOut() { await send({ type: 'logout' }); render(); }

render();
