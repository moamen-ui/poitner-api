/**
 * Downloads the OpenAPI/Swagger spec from the running API server
 * and generates BOTH Angular and React API client packages via Orval.
 *
 * Prerequisites:
 *   - The .NET API must be running (default: http://localhost:8090)
 *
 * Usage:
 *   npm run generate-clients
 *
 * Output:
 *   clients/angular/src/  — @pointer/api-angular (httpResource + HttpClient)
 *   clients/react/src/    — @pointer/api-react   (TanStack Query hooks)
 */

import { writeFileSync, readdirSync, readFileSync, cpSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, '..');
const SPEC_URL =
  process.env.POINTER_SWAGGER_URL ??
  'http://localhost:8090/swagger/v1/swagger.json';
const SPEC_PATH = resolve(root, 'openapi.json');

// ── 1. Download spec ──────────────────────────────────────────────
console.log(`\n📡 Fetching OpenAPI spec from ${SPEC_URL} ...`);
try {
  const response = await fetch(SPEC_URL);
  if (!response.ok) {
    throw new Error(`HTTP ${response.status} ${response.statusText}`);
  }
  const spec = await response.text();
  writeFileSync(SPEC_PATH, spec, 'utf-8');
  console.log(`   Saved openapi.json (${(spec.length / 1024).toFixed(1)} KB)`);
} catch (err) {
  console.error(`\n❌ Failed to download spec from ${SPEC_URL}`);
  console.error('   Make sure the API is running (e.g. `just up` or `dotnet run --project API`).');
  console.error(`   ${err.message}\n`);
  process.exit(1);
}

// ── 2. Run Orval (generates both Angular + React clients) ─────────
console.log('\n🔨 Generating client packages...\n');
try {
  execSync('npx orval --config ./orval.config.ts', {
    cwd: root,
    stdio: 'inherit',
  });
} catch {
  process.exit(1);
}

// ── 3. Create barrel index.ts for each client ────────────────────

// Angular: export only unique symbols (classes, functions) — skip
// duplicate helper types and utility functions
const createAngularBarrel = (dir) => {
  const srcPath = resolve(root, dir, 'src');
  const entries = readdirSync(srcPath, { withFileTypes: true });
  const seen = new Set();
  const exports = [];
  const skip = new Set(['toResourceState', 'filterParams']);

  for (const entry of entries) {
    if (!entry.isDirectory() || entry.name === 'model') continue;
    const subEntries = readdirSync(resolve(srcPath, entry.name));
    const serviceFile = subEntries.find((f) => f.endsWith('.service.ts'));
    if (!serviceFile) continue;

    const content = readFileSync(resolve(srcPath, entry.name, serviceFile), 'utf-8');
    const module = `./${entry.name}/${serviceFile.replace('.ts', '')}`;

    const names = [
      ...content.matchAll(/export class (\w+)/g),
      ...content.matchAll(/export function (\w+)/g),
    ].map((m) => m[1]);

    for (const name of names) {
      if (skip.has(name) || seen.has(name)) continue;
      seen.add(name);
      exports.push(`export { ${name} } from '${module}';`);
    }
  }

  const hasModelDir = entries.some((e) => e.isDirectory() && e.name === 'model');
  if (hasModelDir) exports.push(`export * from './model';`);

  if (exports.length > 0) {
    const content = `// AUTO-GENERATED BARREL — created by generate-clients.mjs\n${exports.join('\n')}\n`;
    writeFileSync(resolve(srcPath, 'index.ts'), content);
    console.log(`   ✓ Created ${dir}/src/index.ts (${exports.length} exports)`);
  }
};

// React: check for both .service.ts and .ts patterns
const createAxiosClientBarrel = (dir) => {
  const srcPath = resolve(root, dir, 'src');
  try {
    const entries = readdirSync(srcPath, { withFileTypes: true });
    let exports = [];
    for (const entry of entries) {
      if (entry.isDirectory()) {
        const subEntries = readdirSync(resolve(srcPath, entry.name));
        const tsFile = subEntries.find((f) => f.endsWith('.ts') && f !== 'mutator.ts');
        if (tsFile) {
          exports.push(`export * from './${entry.name}/${tsFile.replace('.ts', '')}';`);
        }
      }
    }
    const hasModelDir = entries.some((e) => e.isDirectory() && e.name === 'model');
    if (hasModelDir) exports.push(`export * from './model';`);
    if (exports.length > 0) {
      const content = `// AUTO-GENERATED BARREL — created by generate-clients.mjs\n${exports.join('\n')}\n`;
      writeFileSync(resolve(srcPath, 'index.ts'), content);
      console.log(`   ✓ Created ${dir}/src/index.ts (${exports.length} exports)`);
    }
  } catch {
    // Dir might not exist yet
  }
};

console.log('\n📦 Creating barrel exports...');
createAngularBarrel('clients/angular');
createAxiosClientBarrel('clients/react');
createAxiosClientBarrel('clients/vue');

console.log('\n✅ Done! Generated packages:');
console.log('   @pointer/api-angular → clients/angular/');
console.log('   @pointer/api-react   → clients/react/');
console.log('   @pointer/api-vue     → clients/vue/');

// ── 4. Sync Angular client to linked dashboard repo (if exists) ───
const dashboardPath = resolve(root, '..', 'pointer-dashboard');
const dashboardTarget = resolve(dashboardPath, 'src', 'app', 'core', 'api', 'generated');
if (existsSync(resolve(dashboardPath, 'package.json'))) {
  console.log('\n syncing to pointer-dashboard...');
  cpSync(resolve(root, 'clients', 'angular', 'src'), dashboardTarget, { recursive: true, force: true });
  console.log(`   ✓ Copied to ${dashboardTarget.replace(dashboardPath, 'pointer-dashboard')}/`);
  console.log('   (Dashboard uses @api/* alias to import from this directory)');
} else {
  console.log('\n   (pointer-dashboard not found at sibling path — skipping sync)');
}
console.log('');
