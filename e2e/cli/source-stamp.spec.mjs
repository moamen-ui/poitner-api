// E2E spec for R3-01: Vite source-stamp plugin, local manifest, deploy awareness — CLI layer.
// Covers:
// - R3-01-00: plugin off -> markup unchanged (AC-3 second half)
// - R3-01-01: source-stamp-prod-build (cli: steps 1-4)
// - R3-01-02 ⛓: stale-hash-warning (AC-6)
// - R3-01-04: deploy-awareness-cli (AC-7)
// - R3-01-05: manifest determinism (two clean clones) (AC-1)
// Contract: docs/roadmap/testing/R3-01-tests.md
import { test, expect } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, readdirSync, writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { spawnCli } from '../scripts/lib/cli.mjs';
import { tempRepo, bareRemote, commit, branch, checkout } from '../scripts/lib/git.mjs';
import { buildFixture, copyFixture } from '../scripts/lib/vite-fixture.mjs';
import { raw, get, patch, post, login, BASE_URL } from '../scripts/lib/api.mjs';
import { credentials as loadCredentials, keys as loadKeys } from '../scripts/lib/state.mjs';
import { record } from '../scripts/lib/report.mjs';

test.describe.configure({ timeout: 180_000 });

const credentials = () => loadCredentials();
const keys = () => loadKeys();
const PROJECT_KEY = 'e2e-r301';

const sha256 = (content) => createHash('sha256').update(content).digest('hex');

// Strip data-component-source="[0-9a-f]{8}" and data-build-sha="[0-9a-f]{7,40}" plus single preceding space
function stripStamps(content) {
  return content
    .replace(/ data-component-source="[0-9a-f]{8}"/g, '')
    .replace(/ data-build-sha="[0-9a-f]{7,40}"/g, '');
}

// Find emitted route/asset chunk in dist/assets that contains class="card"
function findCardChunk(distDir) {
  const assetsDir = join(distDir, 'assets');
  if (!existsSync(assetsDir)) return null;
  for (const file of readdirSync(assetsDir)) {
    if (file.endsWith('.js') || file.endsWith('.html')) {
      const full = join(assetsDir, file);
      const text = readFileSync(full, 'utf8');
      if (text.includes('card')) {
        return text;
      }
    }
  }
  return null;
}

test('R3-01-00 — plugin off → markup unchanged (AC-3 second half)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const repo = tempRepo();
  try {
    copyFixture({ into: repo.dir });
    execFileSync('git', ['add', '-A'], { cwd: repo.dir });
    execFileSync('git', ['commit', '-m', 'fixture baseline'], { cwd: repo.dir });
    const buildSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo.dir, encoding: 'utf8' }).trim();

    // 1. Disabled build
    const { distDir: distOff } = buildFixture({ dir: repo.dir, enabled: false, buildSha });

    // 2. Enabled build (same temp repo content, same buildSha)
    const { distDir: distOn } = buildFixture({ dir: repo.dir, enabled: true, buildSha });

    // 3. Compare index.html
    const indexOff = readFileSync(join(distOff, 'index.html'), 'utf8');
    const indexOn = readFileSync(join(distOn, 'index.html'), 'utf8');
    const indexOnStripped = stripStamps(indexOn);
    expect(indexOnStripped).toBe(indexOff);

    // Compare chunk containing card
    const cardChunkOff = findCardChunk(distOff);
    const cardChunkOn = findCardChunk(distOn);
    if (cardChunkOff && cardChunkOn) {
      expect(stripStamps(cardChunkOn)).toBe(cardChunkOff);
    }

    // 4. Assert distOff has zero data-component-source occurrences and no manifest was written
    expect(indexOff).not.toContain('data-component-source');
    if (cardChunkOff) {
      expect(cardChunkOff).not.toContain('data-component-source');
    }
    expect(existsSync(join(repo.dir, '.pointer', 'manifest.json'))).toBe(false);

    record({
      id: 'R3-01-00',
      tier: 'nightly',
      layer: 'cli',
      role: 'DEV',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'disabled build identical modulo stamps; no manifest on disabled',
    });
  } finally {
    repo.cleanup();
  }
});

test('R3-01-01 — source-stamp-prod-build (cli: steps 1–4)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const { distDir, manifest, repo } = buildFixture({ enabled: true });

  try {
    const headSha = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo.dir, encoding: 'utf8' }).trim();

    // 2. Read .pointer/manifest.json
    const manifestPath = join(repo.dir, '.pointer', 'manifest.json');
    expect(existsSync(manifestPath), '.pointer/manifest.json must exist after enabled build').toBe(true);

    const manifestData = JSON.parse(readFileSync(manifestPath, 'utf8'));
    expect(manifestData.version).toBe(1);

    // Assert on named keys, not a count:
    // the set { entries[k].component } contains Card, PlanList, Shell, TrackedCard
    const entries = manifestData.entries ?? {};
    const components = Object.values(entries).map((e) => e.component);

    for (const expectedComponent of ['Card', 'PlanList', 'Shell', 'TrackedCard']) {
      expect(components, `manifest entries must contain ${expectedComponent}`).toContain(expectedComponent);
      const matchingKeys = Object.keys(entries).filter((k) => entries[k].component === expectedComponent);
      expect(matchingKeys.length, `exactly one key must exist for ${expectedComponent}`).toBe(1);
    }

    // Every key matches /^[0-9a-f]{8}$/
    for (const key of Object.keys(entries)) {
      expect(key).toMatch(/^[0-9a-f]{8}$/);
      expect(entries[key].path).toMatch(/^src\/.*\.tsx$/);
      expect(typeof entries[key].component).toBe('string');
    }

    // 3. Gitignore (AC-4):
    // git check-ignore -q .pointer/manifest.json -> exit 0
    let checkIgnoreOk = false;
    try {
      execFileSync('git', ['check-ignore', '-q', '.pointer/manifest.json'], { cwd: repo.dir });
      checkIgnoreOk = true;
    } catch {
      checkIgnoreOk = false;
    }
    expect(checkIgnoreOk, 'git check-ignore must report .pointer/manifest.json as ignored').toBe(true);

    // git status --porcelain -uall has no line ending in .pointer/manifest.json
    const porcelain = execFileSync('git', ['status', '--porcelain', '-uall'], {
      cwd: repo.dir,
      encoding: 'utf8',
    });
    expect(porcelain).not.toMatch(/\.pointer\/manifest\.json$/m);

    // 4. Parse dist/index.html: <html tag has data-build-sha="<repo HEAD sha>"
    const indexHtml = readFileSync(join(distDir, 'index.html'), 'utf8');
    expect(indexHtml).toMatch(new RegExp(`<html[^>]*data-build-sha="${headSha}"`));

    record({
      id: 'R3-01-01',
      tier: 'nightly',
      layer: 'cli',
      role: 'DEV',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'manifest.version=1, components verified, manifest gitignored, html stamped with headSha',
    });
  } finally {
    repo.cleanup();
  }
});

test('R3-01-02 ⛓ — stale-hash-warning', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const { repo } = buildFixture({ enabled: true });

  try {
    // Read CardHash from manifest
    const manifestPath = join(repo.dir, '.pointer', 'manifest.json');
    const manifestData = JSON.parse(readFileSync(manifestPath, 'utf8'));
    const entries = manifestData.entries ?? {};
    const cardEntry = Object.entries(entries).find(([, v]) => v.component === 'Card');
    const cardHash = cardEntry ? cardEntry[0] : null;
    expect(cardHash, 'CardHash must be present in manifest').not.toBeNull();

    // 2. Rename Card -> ProductCard
    execFileSync('git', ['mv', 'src/components/Card.tsx', 'src/components/ProductCard.tsx'], { cwd: repo.dir });
    const productCardCode = readFileSync(join(repo.dir, 'src/components/ProductCard.tsx'), 'utf8')
      .replace(/function Card/g, 'function ProductCard');
    writeFileSync(join(repo.dir, 'src/components/ProductCard.tsx'), productCardCode, 'utf8');

    // Update references in App.tsx, Shell.tsx, TrackedCard.tsx
    for (const file of ['src/App.tsx', 'src/components/Shell.tsx', 'src/components/TrackedCard.tsx']) {
      const full = join(repo.dir, file);
      if (existsSync(full)) {
        const updated = readFileSync(full, 'utf8')
          .replace(/from '\.\/Card'/g, "from './ProductCard'")
          .replace(/from '\.\/components\/Card'/g, "from './components/ProductCard'")
          .replace(/<Card/g, '<ProductCard');
        writeFileSync(full, updated, 'utf8');
      }
    }
    execFileSync('git', ['commit', '-am', 'rename: Card to ProductCard'], { cwd: repo.dir });

    // 3. spawnCli: pointer map --from-source
    const mapRes = await spawnCli({ cwd: repo.dir, args: ['map', '--from-source'] });
    expect(mapRes.code).toBe(0);

    // .pointer/manifest.prev.json exists and still has CardHash
    const prevManifestPath = join(repo.dir, '.pointer', 'manifest.prev.json');
    expect(existsSync(prevManifestPath), 'manifest.prev.json must exist').toBe(true);
    const prevData = JSON.parse(readFileSync(prevManifestPath, 'utf8'));
    const prevEntries = prevData.entries ?? prevData;
    expect(prevEntries[cardHash], 'manifest.prev.json must still contain CardHash').toBeTruthy();

    // new manifest lacks CardHash and has ProductCard
    const newManifestData = JSON.parse(readFileSync(manifestPath, 'utf8'));
    const newEntries = newManifestData.entries ?? newManifestData;
    expect(newEntries[cardHash], 'new manifest must not contain CardHash').toBeUndefined();
    const productCardExists = Object.values(newEntries).some((e) => e.component === 'ProductCard');
    expect(productCardExists, 'new manifest must contain ProductCard').toBe(true);

    // Configure pointer config in repo for API operations
    mkdirSync(join(repo.dir, '.pointer'), { recursive: true });
    writeFileSync(
      join(repo.dir, '.pointer', 'config.json'),
      JSON.stringify({ server: BASE_URL, project: PROJECT_KEY }, null, 2),
    );
    writeFileSync(join(repo.dir, '.pointer', 'credentials.env'), `POINTER_API_KEY=${keys().wsAdmin.apiKey}\n`);

    // Create a comment with sourcePath = cardHash
    const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);
    const commentRes = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
      token: wa.token,
      body: {
        body: 'Comment for stale hash test',
        element: { selector: '.card', sourcePath: cardHash },
      },
    });
    expect(commentRes.status).toBe(200);
    const commentId = commentRes.data?.id;

    // 4. pointer get <id> --json -> .resolvedSource deep-equals { kind: 'stale', hash: CardHash, hint: 'search for "Card"' }
    const getJsonRes = await spawnCli({
      cwd: repo.dir,
      args: ['get', String(commentId), '--json'],
    });
    expect(getJsonRes.code).toBe(0);
    expect(getJsonRes.json?.resolvedSource).toEqual({
      kind: 'stale',
      hash: cardHash,
      hint: 'search for "Card"',
    });

    // 5. Human mode: pointer get <id> -> stdout contains warning and hint
    const getHumanRes = await spawnCli({
      cwd: repo.dir,
      args: ['get', String(commentId)],
    });
    expect(getHumanRes.stdout).toContain(`⚠ comment #${commentId}: source hash ${cardHash} is not in the current manifest`);
    expect(getHumanRes.stdout).toContain('search for "Card"');

    // 6. pointer apply --plan -> exit 0, stdout contains the same warning
    const planRes = await spawnCli({
      cwd: repo.dir,
      args: ['apply', '--plan'],
    });
    expect(planRes.code).toBe(0);
    expect(planRes.stdout).toContain(`source hash ${cardHash} is not in the current manifest`);

    // 7. AC-6 fallback half: stdout must also contain search-by-component-name instruction naming Card
    expect(planRes.stdout).toMatch(/search .*"Card"/i);

    record({
      id: 'R3-01-02',
      tier: 'nightly',
      layer: 'cli',
      role: 'DEV',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'map --from-source rotates manifest.prev.json, get warns stale, apply --plan falls back to component search',
    });
  } finally {
    repo.cleanup();
  }
});

test('R3-01-04 — deploy-awareness-cli', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const repo = tempRepo();
  const bareDir = bareRemote(repo.dir);
  const wa = await login(credentials().wsAdmin.email, credentials().wsAdmin.password);

  try {
    // 1. Commits C1 -> C2 -> C3 on main, side branch commit X from C1
    const C1 = commit(repo.dir, 1);
    const C2 = commit(repo.dir, 2);
    const C3 = commit(repo.dir, 3);

    branch(repo.dir, 'side', C1);
    const X = commit(repo.dir, 'X');
    checkout(repo.dir, 'master'); // or main

    // 2. Two Applied comments on e2e-r301 via PATCH:
    // A with commitSha = C1, B with commitSha = X; assert both deployedAt === null
    const resA = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
      token: wa.token,
      body: { body: 'Comment A', element: { selector: '#a' } },
    });
    expect(resA.status).toBe(200);
    const idA = resA.data?.id;

    const resB = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
      token: wa.token,
      body: { body: 'Comment B', element: { selector: '#b' } },
    });
    expect(resB.status).toBe(200);
    const idB = resB.data?.id;

    const patchA = await raw('PATCH', `/api/comments/${idA}`, {
      token: wa.token,
      body: { status: 3, commitSha: C1 },
    });
    expect(patchA.status).toBe(200);
    expect(patchA.data?.deployedAt).toBeNull();

    const patchB = await raw('PATCH', `/api/comments/${idB}`, {
      token: wa.token,
      body: { status: 3, commitSha: X },
    });
    expect(patchB.status).toBe(200);
    expect(patchB.data?.deployedAt).toBeNull();

    // 3. Configure .pointer/config.json in repo -> e2e-r301, WA key in credentials.env
    mkdirSync(join(repo.dir, '.pointer'), { recursive: true });
    writeFileSync(
      join(repo.dir, '.pointer', 'config.json'),
      JSON.stringify({ server: BASE_URL, project: PROJECT_KEY }, null, 2),
    );
    writeFileSync(join(repo.dir, '.pointer', 'credentials.env'), `POINTER_API_KEY=${keys().wsAdmin.apiKey}\n`);

    // 4. pointer status --deployed C3 -> exit 0; stdout matches /^1 comments? marked deployed in ${C3.slice(0,7)}/m
    const statusC3Res = await spawnCli({
      cwd: repo.dir,
      args: ['status', '--deployed', C3],
    });
    expect(statusC3Res.code).toBe(0);
    expect(statusC3Res.stdout).toMatch(new RegExp(`^1 comments? marked deployed in ${C3.slice(0, 7)}`, 'm'));

    // 5. API: A -> deployedAt set, deployedSha === C3; B -> still null
    const getA = await raw('GET', `/api/comments/${idA}`, { token: wa.token });
    expect(getA.status).toBe(200);
    expect(getA.data?.deployedAt).not.toBeNull();
    expect(getA.data?.deployedSha).toBe(C3);
    const aDeployedAt = getA.data?.deployedAt;

    const getB = await raw('GET', `/api/comments/${idB}`, { token: wa.token });
    expect(getB.status).toBe(200);
    expect(getB.data?.deployedAt).toBeNull();

    // 6. Re-run step 4 -> stdout 0 comments marked deployed …; A unchanged
    const statusReRun = await spawnCli({
      cwd: repo.dir,
      args: ['status', '--deployed', C3],
    });
    expect(statusReRun.code).toBe(0);
    expect(statusReRun.stdout).toMatch(/^0 comments? marked deployed/m);

    const getAAfter = await raw('GET', `/api/comments/${idA}`, { token: wa.token });
    expect(getAAfter.data?.deployedAt).toBe(aDeployedAt);

    // 7. pointer status --deployed (default HEAD = C3) -> exit 0, same result
    const statusHead = await spawnCli({
      cwd: repo.dir,
      args: ['status', '--deployed'],
    });
    expect(statusHead.code).toBe(0);
    expect(statusHead.stdout).toMatch(/^0 comments? marked deployed/m);

    // 8. pointer status --deployed ZZZ -> exit 2 (or non-zero), stderr contains 'not a commit'
    const statusBad = await spawnCli({
      cwd: repo.dir,
      args: ['status', '--deployed', 'ZZZ'],
    });
    expect(statusBad.code).not.toBe(0);
    expect(statusBad.stderr).toContain('not a commit');

    // 9. apply path: create a ReadyToApply comment, stage a file, apply --mark <id> --reply ok
    const resApply = await raw('POST', `/api/projects/${PROJECT_KEY}/comments`, {
      token: wa.token,
      body: { body: 'Ready to apply comment', element: { selector: '#c' } },
    });
    expect(resApply.status).toBe(200);
    const idApply = resApply.data?.id;

    const patchReady = await raw('PATCH', `/api/comments/${idApply}`, {
      token: wa.token,
      body: { status: 2 }, // ReadyToApply
    });
    expect(patchReady.status).toBe(200);

    // Stage a file in the repo
    writeFileSync(join(repo.dir, 'applied-file.txt'), 'fixed!\n');
    execFileSync('git', ['add', 'applied-file.txt'], { cwd: repo.dir });

    const applyRes = await spawnCli({
      cwd: repo.dir,
      args: ['apply', '--mark', String(idApply), '--reply', 'ok'],
    });
    expect(applyRes.code).toBe(0);

    const checkApply = await raw('GET', `/api/comments/${idApply}`, { token: wa.token });
    expect(checkApply.status).toBe(200);
    const headAfterApply = execFileSync('git', ['rev-parse', 'HEAD'], { cwd: repo.dir, encoding: 'utf8' }).trim();
    expect(checkApply.data?.commitSha).toBe(headAfterApply);
    expect(checkApply.data?.commitUrl).not.toBeNull();

    record({
      id: 'R3-01-04',
      tier: 'nightly',
      layer: 'cli',
      role: 'WA',
      result: 'PASS',
      ms: Date.now() - start,
      detail: 'status --deployed marks ancestor commits only, idempotency verified, apply stores commitSha',
    });
  } finally {
    repo.cleanup();
  }
});

test('R3-01-05 — manifest determinism (two clean clones)', async () => {
  test.skip(process.env.TIER === 'pr', 'nightly tier only');
  const start = Date.now();

  const repoA = tempRepo();
  const repoB = tempRepo();

  try {
    const { manifest: manifestA } = buildFixture({ dir: repoA.dir, enabled: true });
    const { manifest: manifestB } = buildFixture({ dir: repoB.dir, enabled: true });

    expect(manifestA).not.toBeNull();
    expect(manifestB).not.toBeNull();

    const entriesA = manifestA.entries ?? manifestA;
    const entriesB = manifestB.entries ?? manifestB;

    // Both clean clones produce deep-equal entries
    expect(entriesA).toEqual(entriesB);

    const shaA = sha256(JSON.stringify(entriesA, Object.keys(entriesA).sort()));
    const shaB = sha256(JSON.stringify(entriesB, Object.keys(entriesB).sort()));
    expect(shaA).toBe(shaB);

    record({
      id: 'R3-01-05',
      tier: 'nightly',
      layer: 'cli',
      role: 'DEV',
      result: 'PASS',
      ms: Date.now() - start,
      detail: `deterministic entries sha256=${shaA}`,
    });
  } finally {
    repoA.cleanup();
    repoB.cleanup();
  }
});
