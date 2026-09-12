#!/usr/bin/env node
// Super-admin branding management helper.
// Writes branding configuration via PUT /api/admin/branding.
// DTO contract: Application/DTOs/Branding/BrandingWriteDto.cs
// Controller: API/Controllers/Admin/BrandingController.cs
// Contract: docs/roadmap/testing/00-HARNESS.md §4, docs/roadmap/testing/R2-00-tests.md
import { raw, login } from './lib/api.mjs';
import { SUPER_ADMIN } from './lib/constants.mjs';
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
 * Sets runtime branding configuration via PUT /api/admin/branding.
 * Uses BrandingWriteDto field names:
 * - productName?: string
 * - tagline?: string
 * - primaryColor?: string
 * - urls?: { app?: string, demo?: string, docs?: string, landing?: string }
 *
 * @param {import('../../Application/DTOs/Branding/BrandingWriteDto').BrandingWriteDto | Record<string, any>} dto
 * @param {{ token?: string }} [opts]
 */
export async function setBranding(dto, opts = {}) {
  let token = opts.token;
  if (!token) {
    const saCreds = await getSuperAdminCredentials();
    const saAuth = await login(saCreds.email, saCreds.password);
    token = saAuth.token;
  }

  const res = await raw('PUT', '/api/admin/branding', {
    token,
    body: dto,
  });

  if (res.status !== 200) {
    throw new Error(`setBranding failed (${res.status}): ${JSON.stringify(res.body)}`);
  }

  return res.data;
}

// CLI entry point: node set-branding.mjs [--name <name>] [--tagline <tagline>] [--app-url <url>]
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const args = process.argv.slice(2);
  let productName = 'Acme Review';
  let tagline = 'Review anything';
  let appUrl = 'https://app.acme.test';

  for (let i = 0; i < args.length; i++) {
    if ((args[i] === '--name' || args[i] === '-n') && args[i + 1]) {
      productName = args[++i];
    } else if ((args[i] === '--tagline' || args[i] === '-t') && args[i + 1]) {
      tagline = args[++i];
    } else if ((args[i] === '--app-url' || args[i] === '-u') && args[i + 1]) {
      appUrl = args[++i];
    } else if (args[i] === '--json' && args[i + 1]) {
      try {
        const parsed = JSON.parse(args[++i]);
        productName = parsed.productName ?? productName;
        tagline = parsed.tagline ?? tagline;
        if (parsed.urls?.app) appUrl = parsed.urls.app;
      } catch (e) {
        console.error('Invalid JSON payload:', e);
        process.exit(1);
      }
    }
  }

  const payload = {
    productName,
    tagline,
    urls: {
      app: appUrl,
    },
  };

  setBranding(payload)
    .then((data) => {
      console.log(`[set-branding] Applied branding: productName="${data?.productName || productName}"`);
    })
    .catch((err) => {
      console.error('[set-branding] Failed:', err);
      process.exit(1);
    });
}
