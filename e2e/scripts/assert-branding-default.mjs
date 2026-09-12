#!/usr/bin/env node
// Asserts that branding configuration has been restored to default values.
// Contract: docs/roadmap/testing/R2-00-tests.md (Scenario R2-00-07 ⛓)
// Defaults: Application/Common/BrandingDefaults.cs, Application/Services/Implementation/BrandingService.cs:9-17
import { raw, login } from './lib/api.mjs';
import { SUPER_ADMIN } from './lib/constants.mjs';
import { record } from './lib/report.mjs';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const CRED_PATH = join(STATE_DIR, 'credentials.json');

export async function getSuperAdminCredentials() {
  if (existsSync(CRED_PATH)) {
    try {
      const creds = JSON.parse(readFileSync(CRED_PATH, 'utf8'));
      if (creds.superAdmin) return creds.superAdmin;
    } catch {}
  }
  return SUPER_ADMIN;
}

/**
 * Asserts branding has returned to compiled defaults (R2-00-07 ⛓).
 *
 * @param {{ token?: string }} [opts]
 */
export async function assertBrandingDefault(opts = {}) {
  const start = Date.now();
  let token = opts.token;
  if (!token) {
    const saCreds = await getSuperAdminCredentials();
    const saAuth = await login(saCreds.email, saCreds.password);
    token = saAuth.token;
  }

  // 1. GET /api/branding (public)
  const publicRes = await raw('GET', '/api/branding');
  if (publicRes.status !== 200) {
    throw new Error(`GET /api/branding returned ${publicRes.status}, expected 200`);
  }
  if (publicRes.data?.productName !== 'Pointer') {
    throw new Error(`productName was '${publicRes.data?.productName}', expected 'Pointer'`);
  }
  if (publicRes.data?.tagline !== 'Point at the UI. Ship it with AI.') {
    throw new Error(`tagline was '${publicRes.data?.tagline}', expected 'Point at the UI. Ship it with AI.'`);
  }

  // 2. SA GET /api/admin/branding
  const adminRes = await raw('GET', '/api/admin/branding', { token });
  if (adminRes.status !== 200) {
    throw new Error(`GET /api/admin/branding returned ${adminRes.status}, expected 200`);
  }
  if (adminRes.data?.urls?.app !== 'https://app.pointer.moamen.work') {
    throw new Error(`urls.app was '${adminRes.data?.urls?.app}', expected 'https://app.pointer.moamen.work'`);
  }

  const durationMs = Date.now() - start;
  record({
    id: 'R2-00-07',
    tier: 'nightly',
    layer: 'api',
    role: 'SA',
    result: 'PASS',
    ms: durationMs,
    detail: 'branding restored to defaults',
  });

  return { public: publicRes.data, admin: adminRes.data };
}

// CLI entry point: node assert-branding-default.mjs
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  assertBrandingDefault()
    .then(() => {
      console.log('==> R2-00-07 PASS: Branding default assertions verified');
    })
    .catch((err) => {
      const ms = 0;
      record({
        id: 'R2-00-07',
        tier: 'nightly',
        layer: 'api',
        role: 'SA',
        result: 'FAIL',
        ms,
        detail: err.message,
      });
      console.error('==> R2-00-07 FAIL:', err.message);
      process.exit(1);
    });
}
