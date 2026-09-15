/**
 * Fully-local client loop (R1-10 / NEW-4d):
 * Generates API clients against the local running API, builds them,
 * publishes 0.0.0-local.<unix> prerelease packages to the local Verdaccio registry,
 * and prints paste-ready install commands for the dashboard apps.
 *
 * Usage:
 *   npm run clients:local
 */

import { execSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, '..');

const REGISTRY = (process.env.CLIENTS_REGISTRY ?? 'http://localhost:4873').trim().replace(/\/+$/, '');
const SPEC_URL = process.env.POINTER_SWAGGER_URL ?? 'http://localhost:8090/swagger/v1/swagger.json';
const VERSION = process.env.CLIENTS_VERSION ?? `0.0.0-local.${Math.floor(Date.now() / 1000)}`;

// ── 1. Preflight checks ──────────────────────────────────────────
console.log('🔍 Preflighting local services...');

// 1a. Preflight API
try {
  const apiRes = await fetch(SPEC_URL, { signal: AbortSignal.timeout(5000) });
  if (!apiRes.ok) {
    throw new Error(`HTTP ${apiRes.status} ${apiRes.statusText}`);
  }
  console.log(`   ✓ API reachable (${SPEC_URL})`);
} catch (err) {
  console.error(`\n❌ API is not reachable at ${SPEC_URL}`);
  console.error('   Start the API before running this command:');
  console.error('     just up');
  console.error('   or:');
  console.error('     docker compose up -d\n');
  process.exit(1);
}

// 1b. Preflight Registry
const pingUrl = `${REGISTRY}/-/ping`;
try {
  const regRes = await fetch(pingUrl, { signal: AbortSignal.timeout(5000) });
  if (!regRes.ok) {
    throw new Error(`HTTP ${regRes.status} ${regRes.statusText}`);
  }
  console.log(`   ✓ Registry reachable (${pingUrl})`);
} catch (err) {
  console.error(`\n❌ Local npm registry is not reachable at ${pingUrl}`);
  console.error('   Start the local registry before running this command:');
  console.error('     docker compose up -d verdaccio\n');
  process.exit(1);
}

// ── 2. Generate and compile clients ──────────────────────────────
const childEnv = {
  ...process.env,
  CLIENTS_VERSION: VERSION,
  CLIENTS_REGISTRY: REGISTRY,
  POINTER_SWAGGER_URL: SPEC_URL,
};

try {
  console.log(`\n🔨 Generating clients (version: ${VERSION}, registry: ${REGISTRY}) ...`);
  execSync(`node "${resolve(root, 'scripts', 'generate-clients.mjs')}"`, {
    cwd: root,
    env: childEnv,
    stdio: 'inherit',
  });

  console.log('\n🔨 Compiling clients...');
  execSync(`node "${resolve(root, 'scripts', 'build-clients.mjs')}"`, {
    cwd: root,
    env: childEnv,
    stdio: 'inherit',
  });
} catch (err) {
  console.error('\n❌ Generation or build failed.');
  process.exit(1);
}

// ── 3. Publish to local Verdaccio with isolated scratch .npmrc ───
// Redirect npm_config_userconfig and npm_config_cache to scratch dir (harness §6.1)
// so ~/.npmrc is never written or modified.
const scratchDir = mkdtempSync(join(tmpdir(), 'pointer-publish-local-'));
const scratchNpmrc = join(scratchDir, '.npmrc');
const scratchCache = join(scratchDir, 'cache');
mkdirSync(scratchCache, { recursive: true });

const regUrl = new URL(REGISTRY);
const regHost = regUrl.host;
const regPath = regUrl.pathname.endsWith('/') ? regUrl.pathname : `${regUrl.pathname}/`;
const npmrcContent = [
  `registry=${REGISTRY}/`,
  `//${regHost}${regPath}:_authToken="dummy"`,
  `//${regHost}/:_authToken="dummy"`,
  `@moamen-ui:registry=${REGISTRY}/`,
  '',
].join('\n');
writeFileSync(scratchNpmrc, npmrcContent, 'utf-8');

const publishEnv = {
  ...process.env,
  npm_config_userconfig: scratchNpmrc,
  npm_config_cache: scratchCache,
};

const publishDirs = ['clients/react', 'clients/vue', 'clients/angular/dist'];

try {
  for (const d of publishDirs) {
    const dirPath = resolve(root, d);
    console.log(`\n🚀 Publishing ${d} → ${REGISTRY} ...`);
    // `0.0.0-local.<unix>` is a prerelease; npm ≥ 11.6 refuses to publish one without an explicit
    // dist-tag, and we must not claim `latest` anyway.
    execSync(`npm publish --registry "${REGISTRY}" --tag local`, {
      cwd: dirPath,
      env: publishEnv,
      stdio: 'inherit',
    });
  }
} catch (err) {
  console.error('\n❌ Publishing to local registry failed.');
  process.exit(1);
} finally {
  try {
    rmSync(scratchDir, { recursive: true, force: true });
  } catch {
    // ignore cleanup error
  }
}

// ── 4. Print paste-ready install commands ────────────────────────
console.log(`\n✅ Published @moamen-ui/pointer-{angular,react,vue}@${VERSION} → ${REGISTRY}\n`);
console.log('Use them:');
console.log(`  (cd ../pointer-dashboard/angular && npm i @moamen-ui/pointer-angular@${VERSION} --registry ${REGISTRY} --@moamen-ui:registry=${REGISTRY} --no-save)`);
console.log(`  (cd ../pointer-dashboard/react   && npm i @moamen-ui/pointer-react@${VERSION}   --registry ${REGISTRY} --@moamen-ui:registry=${REGISTRY} --no-save)`);
console.log(`  (cd ../pointer-dashboard/vue     && npm i @moamen-ui/pointer-vue@${VERSION}     --registry ${REGISTRY} --@moamen-ui:registry=${REGISTRY} --no-save)\n`);
console.log('Back to published:  (cd ../pointer-dashboard/<app> && npm ci)   # needs NODE_AUTH_TOKEN\n');
