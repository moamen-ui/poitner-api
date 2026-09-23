// Shared helper for reading `docker compose logs` from an e2e spec/script.
//
// Since R5-58 the API logs structured JSON, so an unbounded `docker compose logs api` can exceed
// Node's default 1 MB `maxBuffer` for execFileSync/spawnSync — spawnSync then throws
// `Error: spawnSync docker ENOBUFS` before the caller ever sees a line of output (see
// api/key-store.spec.mjs R1-06-01, which hit exactly this in CI). Every caller that shells out to
// `docker compose logs` should go through here instead of calling execFileSync directly: it both
// bounds the read (`--tail`, default 2000 lines) and raises maxBuffer well past anything a bounded
// read can produce.
import { execFileSync } from 'node:child_process';

const DEFAULT_TAIL = 2000;
const DEFAULT_MAX_BUFFER = 64 * 1024 * 1024; // 64 MB

/**
 * Returns `docker compose logs <service>` output, tail-bounded and with a large maxBuffer so
 * structured JSON logging never trips Node's default 1 MB limit.
 *
 * @param {string} cwd - repo root (docker compose is run with this as cwd).
 * @param {string} [service] - compose service name (default 'api').
 * @param {object} [opts]
 * @param {number|string} [opts.tail] - `--tail` line count. Pass null/0 to omit --tail entirely
 *   (only do this alongside a `since` bound — an unbounded read is the ENOBUFS failure mode).
 * @param {string} [opts.since] - `--since` value (e.g. '10m', an RFC3339 timestamp), combinable
 *   with tail.
 * @param {number} [opts.maxBuffer] - execFileSync maxBuffer override (bytes).
 */
export function getComposeLogs(cwd, service = 'api', opts = {}) {
  const { tail = DEFAULT_TAIL, since, maxBuffer = DEFAULT_MAX_BUFFER } = opts;

  const args = ['compose', 'logs', service];
  if (tail !== null && tail !== undefined) args.push('--tail', String(tail));
  if (since) args.push('--since', since);

  return execFileSync('docker', args, { cwd, encoding: 'utf8', maxBuffer });
}
