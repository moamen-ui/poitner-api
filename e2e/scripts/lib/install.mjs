// Shared helper for installing the product's real legacy helper into a temp repo
// (defined in docs/roadmap/testing/R2-03-tests.md, reused by R2-06-02).
//
// The contract mirrors install.sh's REAL interface (API/wwwroot/install.sh): the skills directory
// is the POSITIONAL argument to `sh -s --`, never an env var, and there is no POINTER_AI_TOOL.
// install.sh writes .pointer/credentials.env with an EMPTY POINTER_API_KEY= and leaves an
// existing file untouched — so when an apiKey is passed, this helper writes it into
// .pointer/credentials.env itself afterwards.
import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { BASE_URL } from './api.mjs';

/**
 * Runs `curl -fsSL <server>/install.sh | sh -s -- .agents` inside `repo`, then (optionally)
 * writes the given API key into repo/.pointer/credentials.env.
 *
 * Returns { code, stdout, stderr } — spawnSync's outcome, so a scenario can assert exit 0 and
 * grep the "installed skill version" / "updated from" lines the R2-03 scenarios key on.
 */
export function installPointerSh(repo, { apiKey, server = BASE_URL } = {}) {
  const res = spawnSync('sh', ['-c', `curl -fsSL "${server}/install.sh" | sh -s -- .agents`], {
    cwd: repo,
    encoding: 'utf8',
    timeout: 60_000,
  });

  if (apiKey) {
    mkdirSync(join(repo, '.pointer'), { recursive: true });
    writeFileSync(join(repo, '.pointer', 'credentials.env'), `POINTER_API_KEY=${apiKey}\n`, {
      mode: 0o600,
    });
  }

  return { code: res.status ?? -1, stdout: res.stdout ?? '', stderr: res.stderr ?? '' };
}
