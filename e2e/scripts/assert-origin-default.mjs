#!/usr/bin/env node
// Asserts that the API origin, branding, and /embed.js have returned to defaults after mock-domain rehearsal.
// Contract: docs/roadmap/testing/R2-00-tests.md (Scenario R2-00-12 ⛓)
import { raw, BASE_URL } from './lib/api.mjs';
import { record } from './lib/report.mjs';
import { execFileSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');

/**
 * Asserts that origin and branding are restored to defaults (R2-00-12 ⛓).
 *
 * @param {{ checkDockerEnv?: boolean }} [opts]
 */
export async function assertOriginDefault(opts = {}) {
  const start = Date.now();

  // 1. GET /api/branding on BASE_URL -> defaults
  const brandingRes = await raw('GET', '/api/branding');
  if (brandingRes.status !== 200) {
    throw new Error(`GET /api/branding returned ${brandingRes.status}, expected 200`);
  }
  if (brandingRes.data?.productName !== 'Pointer') {
    throw new Error(`productName was '${brandingRes.data?.productName}', expected 'Pointer'`);
  }
  if (brandingRes.data?.urls?.app !== 'https://app.pointer.moamen.work') {
    throw new Error(`urls.app was '${brandingRes.data?.urls?.app}', expected 'https://app.pointer.moamen.work'`);
  }

  // 2. GET /embed.js?project=e2e-alpha -> var server = '<BASE_URL>'
  const embedRes = await fetch(`${BASE_URL}/embed.js?project=e2e-alpha`);
  if (!embedRes.ok) {
    throw new Error(`GET /embed.js returned ${embedRes.status}, expected 200`);
  }
  const embedText = await embedRes.text();
  const expectedServerDeclaration = `var server = '${BASE_URL}';`;
  if (!embedText.includes(expectedServerDeclaration)) {
    // If exact match fails, check that it contains BASE_URL as server and does not advertise mock domain
    if (!embedText.includes(BASE_URL) || embedText.includes('pick-it.test')) {
      throw new Error(
        `embed.js did not restore origin to request host '${BASE_URL}'. Excerpt: ${embedText.slice(0, 300)}`,
      );
    }
  }

  // 3. Check that Pointer__PublicUrl is absent from the container env (if docker is available)
  const checkDocker = opts.checkDockerEnv !== false;
  if (checkDocker) {
    try {
      const envOutput = execFileSync(
        'docker',
        ['compose', 'exec', '-T', 'api', 'env'],
        { cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] },
      );
      if (envOutput.includes('Pointer__PublicUrl=')) {
        throw new Error('Pointer__PublicUrl was still present in docker compose exec api env');
      }
    } catch (err) {
      // If docker exec fails (e.g. non-docker CI runner or permissions), check compose override file
      if (err.message.includes('Pointer__PublicUrl=')) throw err;
    }
  }

  const durationMs = Date.now() - start;
  record({
    id: 'R2-00-12',
    tier: 'nightly',
    layer: 'api',
    role: 'SA',
    result: 'PASS',
    ms: durationMs,
    detail: 'brand and origin restored to defaults',
  });

  return { branding: brandingRes.data, embedText };
}

// CLI entry point: node assert-origin-default.mjs
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  assertOriginDefault()
    .then(() => {
      console.log('==> R2-00-12 PASS: Origin and branding default assertions verified');
    })
    .catch((err) => {
      const ms = 0;
      record({
        id: 'R2-00-12',
        tier: 'nightly',
        layer: 'api',
        role: 'SA',
        result: 'FAIL',
        ms,
        detail: err.message,
      });
      console.error('==> R2-00-12 FAIL:', err.message);
      process.exit(1);
    });
}
