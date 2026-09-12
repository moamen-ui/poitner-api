import { execSync, execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, mkdirSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { PORTS } from './constants.mjs';

export const REGISTRY_URL = process.env.VERDACCIO_URL || `http://localhost:${PORTS.registry || 4873}`;

/**
 * Captures user registry and ~/.npmrc sha256 hash for level 3 isolation verification.
 */
export function captureNpmConfig() {
  let registry = '';
  try {
    registry = execSync('npm config get registry --location=user', { encoding: 'utf8' }).trim();
  } catch {
    try {
      registry = execSync('npm config get registry', { encoding: 'utf8' }).trim();
    } catch {
      registry = 'unknown';
    }
  }

  const npmrcPath = join(homedir(), '.npmrc');
  let npmrcHash = null;
  if (existsSync(npmrcPath)) {
    const content = readFileSync(npmrcPath);
    npmrcHash = createHash('sha256').update(content).digest('hex');
  }

  return { registry, npmrcHash };
}

/**
 * Asserts that user's npm configuration and ~/.npmrc remain completely untouched.
 */
export function assertNpmConfigUnchanged(baseline) {
  const current = captureNpmConfig();
  if (current.registry !== baseline.registry) {
    throw new Error(
      `npm user registry config changed! Baseline: ${baseline.registry}, Current: ${current.registry}`
    );
  }
  if (current.npmrcHash !== baseline.npmrcHash) {
    throw new Error(
      `~/.npmrc hash changed! Baseline: ${baseline.npmrcHash}, Current: ${current.npmrcHash}`
    );
  }
}

/**
 * Sets up scratch isolation env vars (userconfig and cache) per harness §6.1.
 */
export function createScratchEnv(scratchDir) {
  const npmrcContent = `registry=${REGISTRY_URL}/\n//localhost:${PORTS.registry || 4873}/:_authToken=e2e-local-only\n`;
  const npmrcPath = join(scratchDir, '.npmrc');
  writeFileSync(npmrcPath, npmrcContent, 'utf8');
  const cacheDir = join(scratchDir, 'npm-cache');
  mkdirSync(cacheDir, { recursive: true });

  return {
    npm_config_userconfig: npmrcPath,
    npm_config_cache: cacheDir,
  };
}

/**
 * Builds standard args for npx to guarantee registry routing.
 */
export function npxArgs(extraArgs = []) {
  return ['--registry', REGISTRY_URL, ...extraArgs];
}

/**
 * Publishes a tarball to the local Verdaccio registry with complete scratch isolation.
 * If version is given, unpacks into scratchDir, bumps version with npm version, repacks, and publishes.
 */
export function publishTarball(tgzPath, { version, scratchDir } = {}) {
  const scratch = scratchDir || join(homedir(), '.pointer-scratch');
  mkdirSync(scratch, { recursive: true });
  const env = { ...process.env, ...createScratchEnv(scratch) };

  let targetTgz = tgzPath;

  if (version) {
    const vDir = join(scratch, `v-${version}`);
    mkdirSync(vDir, { recursive: true });
    // Extract tarball: npm pack creates tarballs with a top-level `package/` folder
    execFileSync('tar', ['-xzf', tgzPath, '-C', vDir]);

    const pkgDir = existsSync(join(vDir, 'package')) ? join(vDir, 'package') : vDir;

    // Bump version without git tagging
    execFileSync('npm', ['version', version, '--no-git-tag-version'], {
      cwd: pkgDir,
      env,
      stdio: 'pipe',
    });

    // Re-pack into scratch
    execFileSync('npm', ['pack', '--pack-destination', scratch], {
      cwd: pkgDir,
      env,
      stdio: 'pipe',
    });

    const repackedFiles = readdirSync(scratch).filter(
      (f) => f.endsWith('.tgz') && f.includes(version)
    );
    if (repackedFiles.length === 0) {
      throw new Error(`Repacked tarball for version ${version} not found in ${scratch}`);
    }
    targetTgz = join(scratch, repackedFiles[0]);
  }

  // Publish to local verdaccio
  execFileSync('npm', ['publish', targetTgz, '--registry', REGISTRY_URL], {
    env,
    stdio: 'pipe',
  });

  return { tgzPath: targetTgz };
}
