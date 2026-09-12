// Git helpers for CLI scenario isolation and assertions.
// Contract: docs/roadmap/testing/00-HARNESS.md §6.2
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, rm, cp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const execFileAsync = promisify(execFile);

/**
 * Creates a fresh temporary git repository.
 * If fixtureSource is provided, copies its files into the repository first.
 *
 * @param {string} [fixtureSource] Path to template fixture directory
 * @returns {Promise<{ dir: string, cleanup: () => Promise<void> }>}
 */
export async function tempRepo(fixtureSource) {
  const dir = await mkdtemp(join(tmpdir(), 'pointer-e2e-git-'));
  if (fixtureSource) {
    await cp(fixtureSource, dir, { recursive: true });
  }
  await execFileAsync('git', ['init'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.email', 'e2e@example.com'], { cwd: dir });
  await execFileAsync('git', ['config', 'user.name', 'E2E Runner'], { cwd: dir });

  const cleanup = async () => {
    try {
      await rm(dir, { recursive: true, force: true });
    } catch {}
  };

  return { dir, cleanup };
}

/**
 * Creates a bare git repository in a temporary directory to act as a remote.
 *
 * @returns {Promise<{ dir: string, cleanup: () => Promise<void> }>}
 */
export async function bareRemote() {
  const dir = await mkdtemp(join(tmpdir(), 'pointer-e2e-bare-'));
  await execFileAsync('git', ['init', '--bare'], { cwd: dir });

  const cleanup = async () => {
    try {
      await rm(dir, { recursive: true, force: true });
    } catch {}
  };

  return { dir, cleanup };
}

/**
 * Captures a snapshot of all git refs in a repository.
 *
 * @param {string} cwd Path to git repository
 * @returns {Promise<string>}
 */
export async function refsSnapshot(cwd) {
  const { stdout } = await execFileAsync('git', ['for-each-ref'], { cwd });
  return stdout.trim();
}

/**
 * Asserts that git refs have not changed since beforeSnapshot.
 *
 * @param {string} cwd Path to git repository
 * @param {string} beforeSnapshot
 */
export async function assertRefsUnchanged(cwd, beforeSnapshot) {
  const afterSnapshot = await refsSnapshot(cwd);
  if (afterSnapshot !== beforeSnapshot) {
    throw new Error(`Git refs changed!\nBefore:\n${beforeSnapshot}\nAfter:\n${afterSnapshot}`);
  }
}
