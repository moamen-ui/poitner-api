// Zero-dependency static file server for the e2e fixture pages. Usage:
//   node e2e/fixture-app/serve.mjs <alpha|smoke|beta> [port]
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join } from 'node:path';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const [, , site, portArg] = process.argv;

if (!['alpha', 'smoke', 'beta'].includes(site)) {
  console.error('Usage: node serve.mjs <alpha|smoke|beta> [port]');
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
