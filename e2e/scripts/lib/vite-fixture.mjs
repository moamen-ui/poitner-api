// Copies the committed vite-react fixture into a scratch repo.
//
// The fixture is a real Vite + React + Tailwind project shape — index.html, package.json,
// vite.config.ts, tailwind.config.ts, src/styles/globals.css — but it is never installed or
// built here. Stack and design-token detection reads files; it does not need node_modules, and
// an `npm ci` per scenario would put minutes on a suite that otherwise runs in seconds.
import { cpSync, existsSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/** e2e/fixture-app/vite-react */
export const VITE_FIXTURE_DIR = resolve(here, '..', '..', 'fixture-app', 'vite-react');

/**
 * Copies the fixture into `into` (a directory that already exists — typically tempRepo()'s).
 *
 * Returns the list of top-level entries it placed, so a caller can assert the fixture arrived
 * rather than discovering an empty directory three assertions later.
 */
export function copyFixture({ into }) {
  if (!existsSync(VITE_FIXTURE_DIR)) {
    throw new Error(`vite-react fixture is missing at ${VITE_FIXTURE_DIR}`);
  }
  if (!into) throw new Error('copyFixture({ into }) requires a target directory');

  cpSync(VITE_FIXTURE_DIR, into, { recursive: true });

  const placed = readdirSync(into).filter((e) => e !== '.git');
  if (placed.length === 0) {
    throw new Error(`copyFixture placed nothing into ${into}`);
  }
  return placed;
}

/** The fixture's own path for a given relative file, for tests that read the source of truth. */
export function fixtureFile(rel) {
  return join(VITE_FIXTURE_DIR, rel);
}
