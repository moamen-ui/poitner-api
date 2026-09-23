#!/usr/bin/env node
import { existsSync, writeFileSync, unlinkSync, mkdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { BASE_URL } from './lib/api.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const e2eRoot = resolve(here, '..');
const repoRoot = resolve(e2eRoot, '..');
const STATE_DIR = join(e2eRoot, 'state');
const OVERRIDE_COMPOSE_PATH = join(STATE_DIR, 'compose.restart-override.yml');

export async function waitForApi(timeoutMs = 120_000, intervalMs = 1_000) {
  const start = Date.now();
  const swaggerUrl = `${BASE_URL}/swagger/v1/swagger.json`;
  while (Date.now() - start < timeoutMs) {
    try {
      const res = await fetch(swaggerUrl);
      if (res.ok) {
        return;
      }
    } catch {
      // API not up yet
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(`API failed to become ready at ${swaggerUrl} within ${timeoutMs}ms`);
}

// scripts/local-e2e-gate.sh sets these to run against an isolated compose project (alternate
// ports, so the force-recreated `api` container keeps publishing the GATE's port rather than
// reverting to the base file's 8090) instead of the shared dev stack. Unset — every CI run
// today — both default to exactly the single `-f docker-compose.yaml`, no `-p`, this always used.
function composeArgs() {
  const projectArgs = process.env.E2E_COMPOSE_PROJECT ? ['-p', process.env.E2E_COMPOSE_PROJECT] : [];
  const files = process.env.E2E_COMPOSE_FILES
    ? process.env.E2E_COMPOSE_FILES.split(':').filter(Boolean)
    : ['docker-compose.yaml'];
  return [...projectArgs, ...files.flatMap((f) => ['-f', f])];
}

export async function restartApi({ env = {}, timeoutMs = 120_000 } = {}) {
  if (!existsSync(STATE_DIR)) mkdirSync(STATE_DIR, { recursive: true });

  const envEntries = Object.entries(env).filter(([_, v]) => v !== undefined && v !== null);
  const baseArgs = composeArgs();

  if (envEntries.length > 0) {
    const lines = [
      'services:',
      '  api:',
      '    environment:',
      ...envEntries.map(([k, v]) => `      - "${k}=${v}"`),
      '',
    ];
    writeFileSync(OVERRIDE_COMPOSE_PATH, lines.join('\n'), 'utf8');

    execFileSync(
      'docker',
      ['compose', ...baseArgs, '-f', OVERRIDE_COMPOSE_PATH, 'up', '-d', '--force-recreate', 'api'],
      { cwd: repoRoot, stdio: 'inherit' }
    );
  } else {
    if (existsSync(OVERRIDE_COMPOSE_PATH)) {
      unlinkSync(OVERRIDE_COMPOSE_PATH);
    }

    execFileSync(
      'docker',
      ['compose', ...baseArgs, 'up', '-d', '--force-recreate', 'api'],
      { cwd: repoRoot, stdio: 'inherit' }
    );
  }

  await waitForApi(timeoutMs);
}

// CLI support: node restart-api.mjs [--env KEY=VALUE] ...
const isMain = process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (isMain) {
  const env = {};
  const args = process.argv.slice(2);
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--env' && args[i + 1]) {
      const eqIdx = args[i + 1].indexOf('=');
      if (eqIdx !== -1) {
        const k = args[i + 1].slice(0, eqIdx);
        const v = args[i + 1].slice(eqIdx + 1);
        env[k] = v;
      }
      i++;
    } else if (args[i].startsWith('--env=')) {
      const val = args[i].slice(6);
      const eqIdx = val.indexOf('=');
      if (eqIdx !== -1) {
        const k = val.slice(0, eqIdx);
        const v = val.slice(eqIdx + 1);
        env[k] = v;
      }
    }
  }

  restartApi({ env })
    .then(() => {
      console.log('==> API restarted and ready on /swagger/v1/swagger.json');
    })
    .catch((err) => {
      console.error(err);
      process.exit(1);
    });
}
