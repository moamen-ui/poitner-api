import { DEFAULT_SERVER, hostnameOf, type BgRequest, type StoredUser } from './shared';

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
  const defaultProject = tabState.remembered?.project || hostname.replace(/[^a-z0-9._-]/gi, '-');
  const env = tabState.remembered?.environment || 'staging';

  root.innerHTML = `
    <div class="bar"><span class="who">${who}</span><a id="signout">Sign out</a></div>
    <div class="dom">${esc(hostname)}</div>
    <label>Project</label>
    <input id="project" value="${esc(defaultProject)}" placeholder="project-key" />
    <label>Environment</label>
    <select id="env">
      <option value="local">local</option>
      <option value="staging">staging</option>
      <option value="production">production</option>
    </select>
    <button class="${tabState.active ? 'danger' : 'primary'}" id="toggle">${tabState.active ? 'Deactivate on this tab' : 'Activate on this tab'}</button>
    <div class="note">Activating reloads this tab once, then injects the Pointer widget (works even on sites with a strict CSP).</div>`;
  (document.getElementById('env') as HTMLSelectElement).value = env;
  (document.getElementById('signout') as HTMLElement).onclick = signOut;
  (document.getElementById('toggle') as HTMLButtonElement).onclick = async () => {
    if (tabState.active) {
      await send({ type: 'deactivate', tabId: tab!.id! });
    } else {
      const project = (document.getElementById('project') as HTMLInputElement).value.trim();
      const environment = (document.getElementById('env') as HTMLSelectElement).value;
      if (!/^[A-Za-z0-9._-]+$/.test(project)) return err('Project key: letters, digits, . _ - only.');
      await send({ type: 'activate', tabId: tab!.id!, hostname, project, environment });
    }
    window.close();
  };
}

async function signOut() { await send({ type: 'logout' }); render(); }

render();
