// A host page under a strict, nonce-based Content-Security-Policy.
//
// This is the deployment that breaks naive third-party widgets: no 'unsafe-inline', so any style
// the widget injects without the page's nonce is silently dropped by the browser and the widget
// renders unstyled — or not at all. The widget is supposed to survive this by loading its CSS as a
// real stylesheet rather than inlining it.
//
// Usage: node e2e/fixture-app/csp-nonce/serve.mjs [port] [server]
import { createServer } from 'node:http';
import { randomBytes } from 'node:crypto';

const PORT = Number(process.argv[2]) || 4176;
const SERVER = process.argv[3] || 'http://localhost:8090';
const PROJECT = process.env.E2E_CSP_PROJECT || 'e2e-widget-smoke';

createServer((req, res) => {
  // A fresh nonce per response, as a real CSP deployment does — a fixed one would let a test pass
  // against a widget that had simply hardcoded it.
  const nonce = randomBytes(16).toString('base64');

  const csp = [
    "default-src 'self'",
    `script-src 'self' 'nonce-${nonce}' ${SERVER}`,
    // No 'unsafe-inline'. The widget's stylesheet must arrive as a linked resource.
    `style-src 'self' 'nonce-${nonce}' ${SERVER}`,
    `connect-src 'self' ${SERVER}`,
    `img-src 'self' data: ${SERVER}`,
    `font-src 'self' data: ${SERVER}`,
  ].join('; ');

  const body = `<!doctype html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <title>CSP nonce fixture</title>
    <script nonce="${nonce}" src="${SERVER}/pointer.js" defer></script>
  </head>
  <body>
    <h1 id="title">Strict CSP</h1>
    <button id="cta" type="submit">Checkout</button>
    <pointer-feedback project="${PROJECT}" server="${SERVER}" environment="local" source-attr="data-component-source"></pointer-feedback>
  </body>
</html>`;

  res.writeHead(200, {
    'Content-Type': 'text/html; charset=utf-8',
    'Content-Security-Policy': csp,
    'Cache-Control': 'no-store',
  });
  res.end(body);
}).listen(PORT, () => {
  console.log(`csp-nonce fixture on http://localhost:${PORT} (widget from ${SERVER})`);
});
