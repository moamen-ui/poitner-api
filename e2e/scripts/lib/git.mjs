import { mkdtempSync, cpSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { writeFileSync } from 'node:fs';
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
 *
 * Returns `{ dir, cleanup }`. `dir` is the path; `cleanup()` removes it. Callers must use `.dir`
 * — an earlier version returned the bare path, which left every caller that expected a cleanup
 * function failing at teardown and leaking temp directories.
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

  return { dir, cleanup: () => rmSync(dir, { recursive: true, force: true }) };
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

/**
 * The branch a fresh `git init` left us on.
 *
 * Not a constant: git's init.defaultBranch is configurable and changed default in 2.28, so both
 * `main` and `master` are live possibilities on different machines. Asking is cheap; assuming
 * fails with "pathspec did not match", which reads like a missing commit rather than a naming
 * difference.
 */
export function defaultBranch(repoDir) {
  // symbolic-ref, not `rev-parse --abbrev-ref HEAD`. Both name the current branch, but rev-parse
  // needs HEAD to resolve to a COMMIT and so fails on a freshly-init'd repo with "ambiguous
  // argument 'HEAD'" — exactly when a caller wants to record the starting branch before making
  // any commits. symbolic-ref reads the ref itself and answers on an unborn branch.
  return execFileSync('git', ['symbolic-ref', '--short', 'HEAD'], {
    cwd: repoDir,
    encoding: 'utf8',
  }).trim();
}

/**
 * Creates a commit with an arbitrary change in repoDir and returns the resulting commit sha.
 */
export function commit(repoDir, n = 1) {
  const msg = typeof n === 'number' ? `commit-${n}` : String(n);
  const filename = typeof n === 'number' ? `file-${n}.txt` : `file-${Date.now()}-${Math.random().toString(36).slice(2, 6)}.txt`;
  writeFileSync(join(repoDir, filename), `${msg}\n`);
  execFileSync('git', ['add', '-A'], { cwd: repoDir });
  execFileSync('git', ['commit', '-m', msg], { cwd: repoDir });
  return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repoDir, encoding: 'utf8' }).trim();
}

/**
 * Creates a new branch in repoDir and switches to it.
 */
export function branch(repoDir, name, startPoint) {
  const args = ['checkout', '-b', name];
  if (startPoint) args.push(startPoint);
  execFileSync('git', args, { cwd: repoDir });
}

/**
 * Checks out a git ref in repoDir.
 */
export function checkout(repoDir, ref) {
  execFileSync('git', ['checkout', ref], { cwd: repoDir });
}

