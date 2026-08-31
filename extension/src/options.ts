import { DEFAULT_SERVER, type BgRequest } from './shared';

const serverEl = document.getElementById('server') as HTMLInputElement;
const okEl = document.getElementById('ok')!;

function send<T = any>(msg: BgRequest): Promise<T> {
  return new Promise((resolve) => chrome.runtime.sendMessage(msg, resolve));
}

(async () => {
  const state = await send<{ server: string }>({ type: 'getState' });
  serverEl.value = state.server || DEFAULT_SERVER;
})();

(document.getElementById('save') as HTMLButtonElement).onclick = async () => {
  const server = serverEl.value.trim().replace(/\/$/, '');
  if (!/^https?:\/\//.test(server)) { okEl.textContent = 'Enter a valid http(s) URL'; okEl.style.color = '#dc2626'; return; }

  // The manifest only pre-grants host access to the default hosted server (so install doesn't ask
  // for anything broader than that). A self-hosted server needs the same access to bypass CORS on
  // its own /api/admin/* calls — request it here, the one moment the user deliberately points the
  // extension at a different backend. Must run in this direct click handler (a user-gesture
  // requirement of chrome.permissions.request — it can't be proxied through the background).
  if (server.replace(/\/$/, '') !== DEFAULT_SERVER.replace(/\/$/, '')) {
    const granted = await chrome.permissions.request({ origins: [`${server}/*`] });
    if (!granted) {
      okEl.textContent = 'Permission denied — the extension needs access to this server to work.';
      okEl.style.color = '#dc2626';
      return;
    }
  }

  await send({ type: 'setServer', server });
  okEl.textContent = 'Saved ✓'; okEl.style.color = '#16a34a';
  setTimeout(() => { okEl.textContent = ''; }, 1500);
};
