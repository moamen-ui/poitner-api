// E2E spec for R5-68: /api/v1/* URL alias.
// Proves that /api/v1/{rest} rewrites to /api/{rest} before routing runs — the same status code
// and an identical body come back for a sample of anonymous GET endpoints — and that the rewrite
// does not leak into the Swagger spec (Swagger must keep documenting /api/*, not /api/v1/*).
// Contract: docs/roadmap/execution/R5-68-versioning-policy-and-v1-alias.md
// Tier: PR
import { test, expect } from '@playwright/test';
import { raw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

const samplePaths = ['/api/v1/branding', '/api/v1/plans', '/api/v1/auth/signup-enabled'];

for (const v1Path of samplePaths) {
  test(`R5-68 — /api/v1 alias: ${v1Path}`, async () => {
    const start = Date.now();
    const plainPath = v1Path.replace('/api/v1/', '/api/');

    const v1Res = await raw('GET', v1Path);
    const plainRes = await raw('GET', plainPath);

    expect(v1Res.status, `${v1Path} must return the same status as ${plainPath}`).toBe(plainRes.status);
    expect(
      JSON.stringify(v1Res.body),
      `${v1Path} body must be identical to ${plainPath} body`
    ).toBe(JSON.stringify(plainRes.body));

    const durationMs = Date.now() - start;
    record({
      id: 'R5-68-01',
      tier: 'PR',
      layer: 'api',
      role: '—',
      result: 'PASS',
      ms: durationMs,
      detail: `${v1Path} <-> ${plainPath} identical (status=${v1Res.status})`,
    });
  });
}

test('R5-68 — /api/v1 rewrite does not leak into Swagger', async () => {
  const start = Date.now();

  const res = await raw('GET', '/swagger/v1/swagger.json');
  expect(res.status, 'GET /swagger/v1/swagger.json must return 200').toBe(200);
  expect(
    JSON.stringify(res.body),
    'Swagger spec must document only /api/* paths, never /api/v1/*'
  ).not.toContain('/api/v1/');

  const durationMs = Date.now() - start;
  record({
    id: 'R5-68-02',
    tier: 'PR',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: 'swagger spec contains no /api/v1/ paths',
  });
});
