import { createServer } from 'node:http';

/**
 * Builds a host page that loads the widget PINNED to a specific build, with Subresource Integrity.
 *
 * Generated per test rather than committed, because these scenarios need to vary the pinned version
 * and the integrity value independently — including combinations no correct install would produce
 * (a real build under a stale hash) — and shipping those as fixtures would mean committing pages
 * that are deliberately broken.
 */
export function pinnedPage({
  v,
  integrity,
  server = 'http://localhost:8090',
  project = 'e2e-widget-smoke',
  environment = 'local',
} = {}) {
  const src = v ? `${server}/pointer.js?v=${v}` : `${server}/pointer.js`;
  const integrityAttr = integrity ? ` integrity="${integrity}" crossorigin="anonymous"` : '';

  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <title>Pinned widget fixture</title>
    <script src="${src}"${integrityAttr} defer></script>
  </head>
  <body>
    <h1 id="title">Pinned fixture</h1>
    <button id="cta" type="submit">Checkout</button>
    <pointer-feedback project="${project}" server="${server}" environment="${environment}" source-attr="data-component-source"></pointer-feedback>
  </body>
</html>`;
}

/**
 * Serves `pinnedPage(...)` from a REAL http server, and resolves once it is accepting connections.
 *
 * Deliberately not Playwright's route-fulfilment. A fulfilled document loads, but the pinned
 * `<script crossorigin>` it contains then fails with a bare net::ERR_FAILED — the browser treats
 * the sub-resource fetch differently from one made by a genuinely served page, and the symptom is
 * a waitForResponse that simply never resolves. Since what these scenarios test IS the browser's
 * handling of a cross-origin pinned script, the page has to arrive the way a real one does.
 *
 * Returns `{ url, stop }`; the caller must stop it.
 */
export async function servePinnedPage(port, options = {}) {
  const body = pinnedPage(options);
  const server = createServer((_req, res) => {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    res.end(body);
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, resolve);
  });

  return {
    url: `http://localhost:${port}/`,
    stop: () =>
      new Promise((resolve) => {
        server.close(() => resolve());
        // close() waits for keep-alive sockets, which a browser holds open; without this a test
        // that finished can still sit here for seconds.
        server.closeAllConnections?.();
      }),
  };
}
