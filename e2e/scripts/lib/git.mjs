import { mkdtempSync, cpSync, mkdirSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..', '..');
const repoRoot = resolve(e2eRoot, '..');
const DEFAULT_VITE_FIXTURE = join(e2eRoot, 'fixtures', 'vite');

/**
 * Creates a fresh temporary git repository with user.email e2e@example.com
 * and optional initial files copied from sourceDir.
 */
export function tempRepo(sourceDir) {
  const dir = mkdtempSync(join(tmpdir(), 'pointer-e2e-repo-'));
  execFileSync('git', ['init'], { cwd: dir });
  execFileSync('git', ['config', 'user.email', 'e2e@example.com'], { cwd: dir });
  execFileSync('git', ['config', 'user.name', 'E2E Tester'], { cwd: dir });
  mkdirSync(join(dir, 'src'), { recursive: true });

  let srcToCopy = sourceDir;
  if (srcToCopy && !existsSync(srcToCopy)) {
    // If sourceDir was specified (e.g. cli/test/fixtures/vite) but does not exist,
    // fallback to e2e/fixtures/vite if applicable
    if (srcToCopy.includes('vite') && existsSync(DEFAULT_VITE_FIXTURE)) {
      srcToCopy = DEFAULT_VITE_FIXTURE;
    }
  }

  if (srcToCopy && existsSync(srcToCopy)) {
    cpSync(srcToCopy, dir, { recursive: true });
  }

  return dir;
}

/**
 * Creates a bare git repository in a temp directory and adds it as a remote in repoDir.
 */
export function bareRemote(repoDir, name = 'origin') {
  const bareDir = mkdtempSync(join(tmpdir(), 'pointer-e2e-bare-'));
  execFileSync('git', ['init', '--bare'], { cwd: bareDir });
  execFileSync('git', ['remote', 'add', name, bareDir], { cwd: repoDir });
  return bareDir;
}

/**
 * Captures a snapshot of all git refs in a bare repository.
 */
export function refsSnapshot(bareDir) {
  return execFileSync('git', ['for-each-ref'], { cwd: bareDir, encoding: 'utf8' }).trim();
}

/**
 * Asserts that refs in bareDir match the captured beforeSnapshot.
 */
export function assertRefsUnchanged(bareDir, beforeSnapshot) {
  const after = refsSnapshot(bareDir);
  if (after !== beforeSnapshot) {
    throw new Error(`Git refs changed in bare remote!\nBefore:\n${beforeSnapshot}\nAfter:\n${after}`);
  }
}
