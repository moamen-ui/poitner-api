// E2E spec for R1-04: GET /api/meta endpoint.
// Proves that /api/meta is anonymous, returns version, apiVersion, minCliVersion,
// skillVersion (null until R2-03), productName (matching BrandingService), serverTime
// within clock skew tolerance, and emits public ResponseCache headers.
// Contract: docs/roadmap/testing/R1-04-tests.md
// Tier: PR
import { test, expect } from '@playwright/test';
import { raw, getRaw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

test('R1-04-01 — /api/meta anonymous, all fields, ResponseCache', async () => {
  const start = Date.now();

  // 1. GET /api/meta (no Authorization header); capture headers.
  // Must use raw() so HTTP status and structured fields are asserted directly without throwing.
  const metaRes = await raw('GET', '/api/meta');
  expect(metaRes.status, 'GET /api/meta must return 200').toBe(200);
  expect(metaRes.ok, 'Response must be ok').toBe(true);
  expect(metaRes.isSuccess, 'Result envelope isSuccess must be true').toBe(true);

  const data = metaRes.data;
  expect(data, 'meta response must contain data').toBeTruthy();

  // data.version non-empty and matches InformationalVersion regex (dev default 0.0.0-dev)
  expect(typeof data.version, 'data.version must be a string').toBe('string');
  expect(data.version.length, 'data.version must be non-empty').toBeGreaterThan(0);
  expect(
    data.version,
    'data.version must match SemVer informational version or 0.0.0-dev'
  ).toMatch(/^\d+\.\d+\.\d+(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?$/);

  // data.apiVersion integer >= 1
  expect(Number.isInteger(data.apiVersion), 'data.apiVersion must be an integer').toBe(true);
  expect(data.apiVersion, 'data.apiVersion must be >= 1').toBeGreaterThanOrEqual(1);

  // data.minCliVersion matches /^\d+\.\d+\.\d+$/ (dev default 0.1.0)
  expect(typeof data.minCliVersion, 'data.minCliVersion must be a string').toBe('string');
  expect(
    data.minCliVersion,
    'data.minCliVersion must match semver pattern'
  ).toMatch(/^\d+\.\d+\.\d+$/);

  // R2-03: skillVersion is the value stamped into every served skill and script. doctor compares
  // an installed copy's stamp against it, so it must be a real string, and it must MATCH what the
  // server actually stamps — the two resolving differently would make every install read as stale
  // (or none of them).
  expect(data.skillVersion, 'data.skillVersion must be set once R2-03 lands').toBeTruthy();

  const servedSkill = await getRaw('/skill.md');
  const stamped = String(servedSkill.text ?? servedSkill.body).match(/pointer-skill-version:\s*([^\s>]+)/)?.[1];
  expect(stamped, '/skill.md must carry a resolved stamp, not the placeholder').toBeTruthy();
  expect(stamped).not.toBe('<POINTER_SKILL_VERSION>');
  expect(stamped, 'the served stamp and /api/meta.skillVersion must agree').toBe(data.skillVersion);

  // abs(Date.now() - Date.parse(data.serverTime)) < 5 min
  const parsedServerTime = Date.parse(data.serverTime);
  expect(Number.isNaN(parsedServerTime), 'data.serverTime must be a parseable date').toBe(false);
  const diffMs = Math.abs(Date.now() - parsedServerTime);
  expect(diffMs, 'serverTime must be within 5 minutes of client clock').toBeLessThan(5 * 60 * 1000);

  // Header cache-control contains max-age=60 and public
  const cacheControl = metaRes.headers.get('cache-control') || '';
  expect(cacheControl, 'Cache-Control header must contain max-age=60').toContain('max-age=60');
  expect(cacheControl, 'Cache-Control header must contain public').toContain('public');

  // 2. GET /api/branding and compare productName
  const brandingRes = await raw('GET', '/api/branding');
  expect(brandingRes.status, 'GET /api/branding must return 200').toBe(200);
  const brandingProduct = brandingRes.data?.productName || 'Pointer';
  expect(data.productName, 'data.productName must match branding productName').toBe(brandingProduct);

  // 3. Decision: do not assert two rapid calls return identical serverTime —
  // [ResponseCache] only sets response headers (no output caching), bodies legitimately differ.

  const durationMs = Date.now() - start;
  record({
    id: 'R1-04-01',
    tier: 'PR',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `version=${data.version}, apiVersion=${data.apiVersion}, minCliVersion=${data.minCliVersion}, productName=${data.productName}`,
  });
});
