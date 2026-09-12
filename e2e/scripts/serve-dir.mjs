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
    const filePath = join(ROOT, path);
    if (!filePath.startsWith(ROOT)) {
      res.writeHead(403);
      res.end('Forbidden');
      return;
    }
    const body = await readFile(filePath);
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
