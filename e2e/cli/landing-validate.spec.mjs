// R3-05-04 — the landing site's markup and links are checked offline, on every PR.
//
// The check itself lives in e2e/scripts/validate-landing.mjs so it can also be run by hand; this
// spec is what puts it on the PR tier and gives it a scenario id run-e2e.sh can dispatch with -g.
//
// Nothing here touches the API or the database, so it needs no seed and no server.
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');

test('R3-05-04 — html-validate + link resolution', () => {
  let stdout;
  try {
    stdout = execFileSync('node', [join('e2e', 'scripts', 'validate-landing.mjs')], {
      cwd: repoRoot,
      encoding: 'utf8',
      stdio: 'pipe',
    });
  } catch (err) {
    // The resolver names every unresolved link on stderr. Surfacing that verbatim is the whole
    // point — a bare non-zero exit would send the reader back to run it themselves.
    throw new Error(`validate-landing.mjs failed:\n${err.stdout || ''}\n${err.stderr || ''}`);
  }

  // The contract's expected stdout shape. Asserting the counts are present AND non-zero is what
  // stops a resolver that silently found no pages — or collected no links — from reading as a pass.
  const m = stdout.match(/pages=(\d+) links=(\d+) internal-ok=(\d+) external-listed=(\d+)/);
  expect(m, `resolver stdout did not carry the summary line:\n${stdout}`).not.toBeNull();

  const [, pages, links, internalOk] = m.map(Number);
  expect(pages, 'no landing pages were scanned').toBeGreaterThan(0);
  expect(links, 'no hrefs were collected').toBeGreaterThan(0);
  expect(internalOk, 'no internal links were resolved').toBeGreaterThan(0);
});
