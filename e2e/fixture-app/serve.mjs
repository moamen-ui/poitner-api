// Zero-dependency static file server for the e2e fixture pages. Usage:
//   node e2e/fixture-app/serve.mjs <site> [port]
//
// The site name is the directory under fixture-app/. It is validated against what is actually on
// disk rather than a hardcoded list: the list had gone stale against fixture-app/privacy/, and the
// failure was a usage message naming three sites while a fourth sat right next to them.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { readdirSync } from 'node:fs';
import { extname, join } from 'node:path';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [, , site, portArg] = process.argv;

const sites = readdirSync(here, { withFileTypes: true })
  .filter((e) => e.isDirectory())
  .map((e) => e.name)
  .sort();

if (!site || !sites.includes(site)) {
  console.error(`Usage: node serve.mjs <${sites.join('|')}> [port]`);
  process.exit(1);
}

const ROOT = join(here, site);
const PORT = Number(portArg) || 4173;

// These static pages hardcode the widget/API origin as http://localhost:8090 (the shared dev
// stack's port). scripts/local-e2e-gate.sh runs an isolated stack on a different port and sets
// E2E_API_URL accordingly; when it differs from the default, rewrite that origin on the fly in
// served .html so the fixture points at whichever server is actually under test. Unset — every CI
// run today — this is a no-op and .html is served byte-for-byte as before.
const FIXTURE_API_URL = process.env.E2E_API_URL || '';
const DEFAULT_API_ORIGIN = 'http://localhost:8090';

const MIME = { '.html': 'text/html', '.css': 'text/css', '.js': 'application/javascript' };

createServer(async (req, res) => {
  const pathname = new URL(req.url, `http://localhost:${PORT}`).pathname;
  const path = pathname === '/' ? '/index.html' : pathname;
  try {
    const filePath = join(ROOT, path);
    if (!filePath.startsWith(ROOT)) throw new Error('path escape');
    let body = await readFile(filePath);
    if (extname(filePath) === '.html' && FIXTURE_API_URL && FIXTURE_API_URL !== DEFAULT_API_ORIGIN) {
      body = Buffer.from(body.toString('utf8').split(DEFAULT_API_ORIGIN).join(FIXTURE_API_URL), 'utf8');
    }
    res.writeHead(200, { 'Content-Type': MIME[extname(filePath)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end('not found');
  }
}).listen(PORT, () => {
  console.log(`e2e fixture (${site}) serving on http://localhost:${PORT}`);
});
