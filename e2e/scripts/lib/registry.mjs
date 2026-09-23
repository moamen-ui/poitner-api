import { execSync, execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync, mkdirSync, readdirSync, mkdtempSync, rmSync, symlinkSync, cpSync } from 'node:fs';
import { join } from 'node:path';
import { homedir } from 'node:os';
import { createHash } from 'node:crypto';
import { dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PORTS } from './constants.mjs';

const here = dirname(fileURLToPath(import.meta.url));

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
  // The auth-token line's host:port must match REGISTRY_URL's own — hardcoding PORTS.registry
  // here silently pointed the auth token at :4873 even when VERDACCIO_URL (and so REGISTRY_URL)
  // had been overridden to a different port, e.g. by scripts/local-e2e-gate.sh's isolated stack.
  const registryHostPort = new URL(REGISTRY_URL).host;
  const npmrcContent = `registry=${REGISTRY_URL}/\n//${registryHostPort}/:_authToken=e2e-local-only\n`;
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

    // Built from a COPY OF THE SOURCE TREE, not from the tarball.
    //
    // The published package ships `files: ["dist", "README.md"]` — four files, no src/, no
    // build.mjs. Extracting the tarball and rebuilding inside it worked only while the package
    // shipped its own sources, and would now fail with a missing build script.
    const pkgDir = join(vDir, 'package');
    mkdirSync(pkgDir, { recursive: true });
    const cliDir = repoCliDir();
    for (const entry of ['src', 'build.mjs', 'package.json', 'tsconfig.json']) {
      const from = join(cliDir, entry);
      if (existsSync(from)) cpSync(from, join(pkgDir, entry), { recursive: true });
    }
    // Symlinked rather than copied: the build needs esbuild and the tree is large.
    const nm = join(pkgDir, 'node_modules');
    if (!existsSync(nm)) {
      try {
        symlinkSync(join(cliDir, 'node_modules'), nm, 'dir');
      } catch {
        /* a copy already in place is fine */
      }
    }

    // Bump version without git tagging
    execFileSync('npm', ['version', version, '--no-git-tag-version'], {
      cwd: pkgDir,
      env,
      stdio: 'pipe',
    });

    // REBUILD. The CLI's version is baked into dist/cli.js by esbuild's `define` at build time,
    // read from package.json — so bumping package.json alone produces a package published as
    // 99.1.0 whose binary still reports 0.1.0 and still fails the server's minimum. That artifact
    // could never come out of a real publish (which builds, then packs).
    try {
      execFileSync('npm', ['run', 'build'], { cwd: pkgDir, env, stdio: 'pipe' });
    } catch (err) {
      throw new Error(
        `could not rebuild the CLI at version ${version} — the published package would report ` +
          `its old version and the scenario would fail for the wrong reason: ${err.message}`,
      );
    } finally {
      rmSync(join(pkgDir, 'node_modules'), { force: true });
    }

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

  // Publish to local verdaccio.
  //
  // Verdaccio keeps its storage in a named docker volume, so it OUTLIVES the run. Publishing the
  // same version again is a 409 ("this package is already present"), which made the whole registry
  // phase pass exactly once — on a fresh volume — and fail on every run after that, for a reason
  // that looks nothing like the scenario it breaks.
  //
  // Unpublish-then-publish rather than tolerating the 409: treating "already there" as success
  // would let a version published by an EARLIER build of the CLI stand in for the one this run
  // just packed, and the scenario would then be asserting against stale bytes while looking green.
  // Replacing guarantees the registry holds what this run built.
  const pkgName = readPackageName(targetTgz, scratch);
  const pkgVersion = version || readPackageVersion(targetTgz, scratch);
  if (pkgName && pkgVersion) {
    try {
      execFileSync(
        'npm',
        ['unpublish', `${pkgName}@${pkgVersion}`, '--force', '--registry', REGISTRY_URL],
        { env, stdio: 'pipe' },
      );
    } catch {
      // Not present yet (the normal first-run case), or the registry refuses — publish will say so.
    }
  }

  execFileSync('npm', ['publish', targetTgz, '--registry', REGISTRY_URL], {
    env,
    stdio: 'pipe',
  });

  return { tgzPath: targetTgz };
}

/** The repo's cli/ directory — the only place a built node_modules exists. */
function repoCliDir() {
  return join(here, '..', '..', '..', 'cli');
}

/** Reads one field out of a packed tarball's package.json without leaving anything behind. */
function readPackedField(tgzPath, scratch, field) {
  try {
    const peek = mkdtempSync(join(scratch, 'peek-'));
    execFileSync('tar', ['-xzf', tgzPath, '-C', peek, 'package/package.json'], { stdio: 'pipe' });
    const pkg = JSON.parse(readFileSync(join(peek, 'package', 'package.json'), 'utf8'));
    rmSync(peek, { recursive: true, force: true });
    return pkg[field];
  } catch {
    return undefined;
  }
}

const readPackageName = (tgz, scratch) => readPackedField(tgz, scratch, 'name');
const readPackageVersion = (tgz, scratch) => readPackedField(tgz, scratch, 'version');
