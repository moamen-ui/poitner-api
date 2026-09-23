// Static file server for generated apps.
// Usage: node e2e/scripts/serve-dir.mjs <directory> [port]
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, resolve } from 'node:path';

const [, , dirArg, portArg] = process.argv;

if (!dirArg) {
  console.error('Usage: node serve-dir.mjs <directory> [port]');
  process.exit(1);
}

const ROOT = resolve(dirArg);
const PORT = Number(portArg) || 4174;

// Some served fixtures hardcode the widget/API origin as http://localhost:8090 (the shared dev
// stack's port) — e.g. fixture-app/privacy/index.html (widget/privacy-snapshot.spec.ts).
// scripts/local-e2e-gate.sh runs an isolated stack on a different port and sets E2E_API_URL
// accordingly; when it differs from the default, rewrite that origin on the fly in served .html so
// the fixture points at whichever server is actually under test. Unset — every CI run today — this
// is a no-op and .html is served byte-for-byte as before.
const FIXTURE_API_URL = process.env.E2E_API_URL || '';
const DEFAULT_API_ORIGIN = 'http://localhost:8090';

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.mjs': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.ico': 'image/x-icon',
};

const server = createServer(async (req, res) => {
  const pathname = new URL(req.url, `http://localhost:${PORT}`).pathname;
  const path = pathname === '/' ? '/index.html' : pathname;
  try {
    let filePath = join(ROOT, path);
    if (!filePath.startsWith(ROOT)) {
      res.writeHead(403);
      res.end('Forbidden');
      return;
    }
    let body;
    try {
      body = await readFile(filePath);
    } catch (err) {
      if (err.code === 'EISDIR') {
        filePath = join(filePath, 'index.html');
        body = await readFile(filePath);
      } else {
        throw err;
      }
    }
    if (extname(filePath) === '.html' && FIXTURE_API_URL && FIXTURE_API_URL !== DEFAULT_API_ORIGIN) {
      body = Buffer.from(body.toString('utf8').split(DEFAULT_API_ORIGIN).join(FIXTURE_API_URL), 'utf8');
    }
    res.writeHead(200, {
      'Content-Type': MIME[extname(filePath)] || 'application/octet-stream',
      'Access-Control-Allow-Origin': '*',
    });
    res.end(body);
  } catch {
    res.writeHead(404, { 'Content-Type': 'text/plain' });
    res.end('Not Found');
  }
});

// Without this handler, a bind failure (e.g. EADDRINUSE because a previous fresh-app scenario's
// server hasn't released this same shared port yet) throws an unhandled 'error' event and kills
// this process silently (spawned with stdio: 'ignore'). The caller's waitForServer() then keeps
// polling the port and happily finds the STALE server still answering — serving the wrong
// scenario's page with no indication anything went wrong. Fail loudly instead.
server.on('error', (err) => {
  console.error(`serve-dir.mjs: failed to listen on port ${PORT}: ${err.message}`);
  process.exit(1);
});

server.listen(PORT, () => {
  console.log(`Serving ${ROOT} on http://localhost:${PORT}`);
});

// Clean shutdown on SIGINT / SIGTERM
process.on('SIGINT', () => {
  server.close(() => process.exit(0));
});
process.on('SIGTERM', () => {
  server.close(() => process.exit(0));
});
