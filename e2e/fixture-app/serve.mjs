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

const MIME = { '.html': 'text/html', '.css': 'text/css', '.js': 'application/javascript' };

createServer(async (req, res) => {
  const pathname = new URL(req.url, `http://localhost:${PORT}`).pathname;
  const path = pathname === '/' ? '/index.html' : pathname;
  try {
    const filePath = join(ROOT, path);
    if (!filePath.startsWith(ROOT)) throw new Error('path escape');
    const body = await readFile(filePath);
    res.writeHead(200, { 'Content-Type': MIME[extname(filePath)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end('not found');
  }
}).listen(PORT, () => {
  console.log(`e2e fixture (${site}) serving on http://localhost:${PORT}`);
});
