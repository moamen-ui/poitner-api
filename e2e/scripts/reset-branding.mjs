#!/usr/bin/env node
// Restores branding to defaults via PUT /api/admin/branding.
// DTO contract: Application/DTOs/Branding/BrandingWriteDto.cs
// Defaults: Application/Common/BrandingDefaults.cs, Application/Services/Implementation/BrandingService.cs:9-17
// Contract: docs/roadmap/testing/00-HARNESS.md §4, §8, docs/roadmap/testing/R2-00-tests.md
import { raw, login } from './lib/api.mjs';
import { SUPER_ADMIN } from './lib/constants.mjs';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const STATE_DIR = join(here, '..', 'state');
const CRED_PATH = join(STATE_DIR, 'credentials.json');

export const BRANDING_DEFAULTS = {
  productName: 'Pointer',
  tagline: 'Point at the UI. Ship it with AI.',
  primaryColor: '#2563eb',
  urls: {
    app: 'https://app.pointer.moamen.work',
    demo: 'https://demo.pointer.moamen.work',
    docs: 'https://github.com/moamen-ui/poitner-api#readme',
    landing: 'https://pointer.moamen.work',
  },
};

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
 * Resets branding to default values via super-admin PUT /api/admin/branding.
 *
 * @param {{ token?: string }} [opts]
 */
export async function resetBranding(opts = {}) {
  let token = opts.token;
  if (!token) {
    const saCreds = await getSuperAdminCredentials();
    const saAuth = await login(saCreds.email, saCreds.password);
    token = saAuth.token;
  }

  const res = await raw('PUT', '/api/admin/branding', {
    token,
    body: BRANDING_DEFAULTS,
  });

  if (res.status !== 200) {
    throw new Error(`resetBranding failed (${res.status}): ${JSON.stringify(res.body)}`);
  }

  return res.data;
}

// CLI entry point: node reset-branding.mjs
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  resetBranding()
    .then((data) => {
      console.log(`[reset-branding] Restored default branding: productName="${data?.productName || 'Pointer'}"`);
    })
    .catch((err) => {
      console.error('[reset-branding] Failed:', err);
      process.exit(1);
    });
}
