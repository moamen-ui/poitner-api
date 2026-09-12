// E2E spec for R1-03: Dashboard quick-start and Orval contract guard.
// Proves that the swagger spec at /swagger/v1/swagger.json exposes inner-typed schemas
// (not Result<T> envelope) for all endpoints consumed by the dashboard, ensuring Orval client
// generation remains clean and stable.
// Also implements the runner-level gate for R1-03-02 (nightly dashboard suite).
// Contract: docs/roadmap/testing/R1-03-tests.md
// Tier: PR (R1-03-01), Nightly (R1-03-02)
import { test, expect } from '@playwright/test';
import { execSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BASE_URL, raw } from '../scripts/lib/api.mjs';
import { DASHBOARD_CONTRACT_ENDPOINTS } from '../scripts/lib/constants.mjs';
import { record } from '../scripts/lib/report.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = resolve(here, '..', 'state');

test('R1-03-01 — swagger contract guard (Orval surface)', async () => {
  const start = Date.now();

  // 1. GET http://localhost:8090/swagger/v1/swagger.json (anonymous)
  // Must use raw() so HTTP status and structured fields are asserted directly without throwing.
  const swaggerRes = await raw('GET', '/swagger/v1/swagger.json');
  expect(swaggerRes.status, 'GET /swagger/v1/swagger.json must return 200').toBe(200);
  expect(swaggerRes.ok, 'Response must be ok').toBe(true);

  const spec = swaggerRes.body;
  expect(spec, 'Swagger spec must be a valid JSON object').toBeTruthy();
  expect(typeof spec, 'Swagger spec must be an object').toBe('object');
  expect(spec.paths, 'Swagger spec must define paths').toBeTruthy();
  expect(spec.components?.schemas, 'Swagger spec must define components.schemas').toBeTruthy();

  // 2. For each (method, path) in DASHBOARD_CONTRACT_ENDPOINTS:
  // Assert path/method exists, 200 response with application/json exists, and schema resolves to a component.
  const resolvedComponents = [];
  const errors = [];

  for (const [method, path] of DASHBOARD_CONTRACT_ENDPOINTS) {
    const verb = method.toLowerCase();
    const endpointLabel = `${verb.toUpperCase()} ${path}`;

    const pathItem = spec.paths?.[path];
    if (!pathItem) {
      errors.push(`Missing path in swagger spec: ${path}`);
      continue;
    }

    const operation = pathItem[verb];
    if (!operation) {
      errors.push(`Missing operation in swagger spec: ${endpointLabel}`);
      continue;
    }

    const response200 = operation.responses?.['200'];
    if (!response200) {
      errors.push(`Missing 200 response definition for ${endpointLabel}`);
      continue;
    }

    // A missing content means the action carries no [ProducesResponseType] at all —
    // report that as the failure message, do not let the step throw on undefined.
    const content = response200.content;
    if (!content) {
      errors.push(
        `${endpointLabel} response 200 has no content (action carries no [ProducesResponseType] at all)`
      );
      continue;
    }

    const jsonContent = content['application/json'];
    if (!jsonContent) {
      errors.push(
        `${endpointLabel} response 200 content has no application/json media type`
      );
      continue;
    }

    const schema = jsonContent.schema;
    if (!schema) {
      errors.push(`${endpointLabel} response 200 application/json has no schema`);
      continue;
    }

    // Schema must resolve to a component: schema.$ref ?? schema.items?.$ref matches ^#/components/schemas/
    // The items form is required because GET /api/admin/projects is annotated typeof(List<ProjectResponse>),
    // so its 200 schema is type: array with items.$ref.
    const ref = schema.$ref ?? schema.items?.$ref;
    if (!ref) {
      errors.push(
        `${endpointLabel} schema does not resolve to a component ref (schema.$ref ?? schema.items?.$ref is missing)`
      );
      continue;
    }

    if (!ref.startsWith('#/components/schemas/')) {
      errors.push(
        `${endpointLabel} component ref '${ref}' does not match ^#/components/schemas/`
      );
      continue;
    }

    const componentName = ref.replace(/^#\/components\/schemas\//, '');
    resolvedComponents.push({ endpointLabel, componentName, ref });
  }

  // 3. For each resolved component: components.schemas[<Name>] does not define properties
  // isSuccess / data (not the Result<T> envelope).
  for (const { endpointLabel, componentName } of resolvedComponents) {
    const componentSchema = spec.components?.schemas?.[componentName];
    if (!componentSchema) {
      errors.push(
        `Component schema '${componentName}' not found in components.schemas (referenced by ${endpointLabel})`
      );
      continue;
    }

    const properties = componentSchema.properties || {};
    const hasEnvelopeProperties =
      Object.prototype.hasOwnProperty.call(properties, 'isSuccess') &&
      Object.prototype.hasOwnProperty.call(properties, 'data');

    if (hasEnvelopeProperties) {
      errors.push(
        `Component '${componentName}' referenced by ${endpointLabel} defines Result envelope properties (isSuccess/data)`
      );
    }
  }

  const durationMs = Date.now() - start;

  if (errors.length > 0) {
    record({
      id: 'R1-03-01',
      tier: 'PR',
      layer: 'api',
      role: '—',
      result: 'FAIL',
      ms: durationMs,
      detail: errors.join('; '),
    });
    expect(errors, `Swagger contract guard violations:\n${errors.join('\n')}`).toEqual([]);
  }

  record({
    id: 'R1-03-01',
    tier: 'PR',
    layer: 'api',
    role: '—',
    result: 'PASS',
    ms: durationMs,
    detail: `verified ${DASHBOARD_CONTRACT_ENDPOINTS.length} endpoints, ${resolvedComponents.length} inner component schemas checked without Result envelope`,
  });
});

test('R1-03-02 — quickstart-copies-prefilled-command', async () => {
  // Respect the tier column: nightly scenarios must not run in the PR tier
  test.skip(process.env.TIER === 'pr', 'nightly tier only — skipped during PR tier');

  const start = Date.now();
  const DASHBOARD_DIR = process.env.DASHBOARD_DIR;

  // With DASHBOARD_DIR unset, record SKIP row (never green) per 00-HARNESS §10 and R1-03-tests.md
  if (!DASHBOARD_DIR) {
    record({
      id: 'R1-03-02',
      tier: 'nightly',
      layer: 'dashboard',
      role: 'wsAdmin',
      result: 'SKIP',
      attempts: 1,
      ms: 0,
      detail: 'DASHBOARD_DIR unset (harness §10)',
    });
    test.skip(true, 'DASHBOARD_DIR unset (harness §10)');
    return;
  }

  // When DASHBOARD_DIR is set, run the dashboard Playwright suite against this stack
  try {
    execSync('npm ci && npx playwright test', {
      cwd: DASHBOARD_DIR,
      env: {
        ...process.env,
        POINTER_STACK: process.env.POINTER_STACK || BASE_URL,
        POINTER_E2E_STATE: STATE_DIR,
      },
      stdio: 'pipe',
      encoding: 'utf8',
    });

    const durationMs = Date.now() - start;
    record({
      id: 'R1-03-02',
      tier: 'nightly',
      layer: 'dashboard',
      role: 'wsAdmin',
      result: 'PASS',
      attempts: 1,
      ms: durationMs,
      detail: 'dashboard suite passed against local stack',
    });
  } catch (err) {
    const durationMs = Date.now() - start;
    record({
      id: 'R1-03-02',
      tier: 'nightly',
      layer: 'dashboard',
      role: 'wsAdmin',
      result: 'FAIL',
      attempts: 1,
      ms: durationMs,
      detail: err.message?.slice(0, 200) || 'dashboard suite failed',
    });
    throw err;
  }
});
