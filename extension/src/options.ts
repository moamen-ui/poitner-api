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
  await send({ type: 'setServer', server });
  okEl.textContent = 'Saved ✓'; okEl.style.color = '#16a34a';
  setTimeout(() => { okEl.textContent = ''; }, 1500);
};
