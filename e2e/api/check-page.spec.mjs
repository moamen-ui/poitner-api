// E2E spec for R1-02-06: /check page renders productName and sanitizes input.
// Proves that the API-served /check page renders branding and safely sanitizes query parameters.
// Contract: docs/roadmap/testing/R1-02-tests.md
import { test, expect } from '@playwright/test';
import { getRaw } from '../scripts/lib/api.mjs';
import { record } from '../scripts/lib/report.mjs';

test('R1-02-06 — /check page renders productName', async () => {
  const start = Date.now();

  // 1. Anonymous request to /check with valid project and environment.
  const res1 = await getRaw('/check?project=e2e-alpha&environment=local');
  expect(res1.status).toBe(200);
  const contentType = res1.headers?.get ? res1.headers.get('content-type') : '';
  expect(contentType).toContain('text/html');

  const body1 = typeof res1.body === 'string' ? res1.body : JSON.stringify(res1.body);
  expect(body1).toContain('<title>Pointer check</title>');
  expect(body1).toContain(
    'If you can see the Pointer button in the corner, the widget is served correctly. Sign in to test a comment.',
  );
  expect(body1).toContain('/embed.js?project=e2e-alpha&environment=local');

  // 2. Cross-site scripting attempt in query string must be sanitized by Safe().
  const res2 = await getRaw('/check?project=%3Cscript%3Ealert(1)%3C/script%3E');
  expect(res2.status).toBe(200);
  const body2 = typeof res2.body === 'string' ? res2.body : JSON.stringify(res2.body);
  expect(body2).not.toContain('<script>alert(1)');

  const durationMs = Date.now() - start;
  record({
    id: 'R1-02-06',
    tier: 'PR',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: 'check page verified with productName and XSS sanitization',
  });
});
