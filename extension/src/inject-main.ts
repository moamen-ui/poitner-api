// Runs in the PAGE's MAIN world (serialized via chrome.scripting.executeScript
// `func`). MUST be fully self-contained — it cannot reference module scope or
// `chrome.*`. It installs the Pointer config + a proxy transport, then loads the
// live widget from the server. All API traffic is posted to the isolated content
// bridge (which relays to the background), so the real JWT never enters the page.
export function injectMain(cfg: {
  server: string;
  project: string;
  environment: string;
  user: unknown;
}): void {
  const w = window as unknown as Record<string, unknown>;
  if (w.__pointerExtMounted) return;
  w.__pointerExtMounted = true;

  const PROXY_TOKEN = '__pointer_via_proxy__';
  const pending: Record<number, (r: Response) => void> = {};
  let counter = 0;

  window.addEventListener('message', (e: MessageEvent) => {
    const d = e.data;
    if (!d || d.source !== 'pointer-ext-res') return;
    const resolve = pending[d.id];
    if (!resolve) return;
    delete pending[d.id];
    resolve(new Response(d.body, {
      status: d.status || 0,
      headers: d.contentType ? { 'Content-Type': d.contentType } : {},
    }));
  });

  w.__POINTER_FETCH__ = (url: string, opts?: RequestInit): Promise<Response> => {
    opts = opts || {};
    return new Promise((resolve, reject) => {
      const id = ++counter;
      pending[id] = resolve;
      const body = (opts as RequestInit).body;
      if (typeof FormData !== 'undefined' && body instanceof FormData) {
        const file = body.get('file') as File | null;
        const project = (body.get('project') as string) || cfg.project;
        const reader = new FileReader();
        reader.onload = () => {
          const base64 = String(reader.result).split(',')[1] || '';
          window.postMessage({
            source: 'pointer-ext', kind: 'upload', id, url, base64,
            filename: (file && file.name) || 'screenshot',
            contentType: (file && file.type) || 'application/octet-stream',
            project,
          }, '*');
        };
        reader.onerror = () => { delete pending[id]; reject(new Error('read failed')); };
        reader.readAsDataURL(file as Blob);
      } else {
        const headers = (opts!.headers as Record<string, string>) || {};
        const auth = !!(headers.Authorization || headers.authorization);
        window.postMessage({
          source: 'pointer-ext', kind: 'fetch', id, url,
          method: opts!.method || 'GET', headers,
          body: typeof body === 'string' ? body : null, auth,
        }, '*');
      }
    });
  };

  w.__POINTER_CONFIG__ = {
    server: cfg.server,
    project: cfg.project,
    environment: cfg.environment,
    token: PROXY_TOKEN,
    user: cfg.user,
    proxy: true,
  };

  const s = document.createElement('script');
  s.src = cfg.server.replace(/\/$/, '') + '/pointer.js';
  s.defer = true;
  document.head.appendChild(s);
  document.body.appendChild(document.createElement('pointer-feedback'));
}
