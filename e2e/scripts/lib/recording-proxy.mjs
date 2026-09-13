// A reverse proxy that records what the CLI actually sends.
//
// Some guarantees are about the REQUEST, not the response: "the stack POST must not carry the
// design block" cannot be checked by reading the server's reply, because a server that quietly
// ignored an extra field would look identical to one that was never sent it. The only place that
// question can be answered is on the wire.
import { createServer, request as httpRequest } from 'node:http';
import { appendFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import { URL } from 'node:url';

/**
 * Starts the proxy and resolves once it is accepting connections.
 *
 * @param {object} opts
 * @param {number} opts.port       listen port
 * @param {string} opts.target     upstream origin, e.g. http://localhost:8090
 * @param {string} opts.recording  path to a .jsonl file; truncated on start
 * @returns {Promise<{ url: string, stop: () => Promise<void> }>}
 */
export async function startRecordingProxy({ port, target, recording }) {
  mkdirSync(dirname(recording), { recursive: true });
  // Truncated, not appended: a recording left over from a previous run would make an assertion
  // like "exactly one stack POST" fail for a reason that has nothing to do with this run.
  writeFileSync(recording, '', 'utf8');

  const upstream = new URL(target);

  const server = createServer((req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const body = Buffer.concat(chunks).toString('utf8');

      // Recorded BEFORE proxying, so a request that never gets a reply is still on the record —
      // a hang is itself something a test may need to see.
      appendFileSync(
        recording,
        JSON.stringify({ method: req.method, path: req.url, body }) + '\n',
        'utf8',
      );

      const proxied = httpRequest(
        {
          hostname: upstream.hostname,
          port: upstream.port || 80,
          path: req.url,
          method: req.method,
          // Host is rewritten so the upstream sees its own name; everything else is passed through
          // untouched, including auth — the point is to observe, not to alter.
          headers: { ...req.headers, host: upstream.host },
        },
        (upRes) => {
          res.writeHead(upRes.statusCode || 502, upRes.headers);
          upRes.pipe(res);
        },
      );

      proxied.on('error', (err) => {
        res.writeHead(502, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: `proxy could not reach ${target}: ${err.message}` }));
      });

      if (body) proxied.write(body);
      proxied.end();
    });
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, resolve);
  });

  return {
    url: `http://localhost:${port}`,
    stop: () =>
      new Promise((resolve) => {
        server.close(() => resolve());
        server.closeAllConnections?.();
      }),
  };
}

/** Parses a recording file into entries. */
export function readRecording(path) {
  if (!existsSync(path)) return [];
  return readFileSync(path, 'utf8')
    .split('\n')
    .filter(Boolean)
    .map((line) => JSON.parse(line));
}
