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

import { writeFileSync, readdirSync, readFileSync, cpSync, existsSync, mkdirSync, copyFileSync } from 'node:fs';
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

// ── 1b. Materialize the axios mutator for the React/Vue clients ───
// orval.config points each at clients/<fw>/mutator.ts; clients/ is gitignored,
// so copy the tracked template into place before orval runs (orval keeps it via
// its `clean: ['!**/mutator.ts']` rule). Required for fresh checkouts (CI).
const MUTATOR_SRC = resolve(root, 'scripts', 'mutators', 'axios-mutator.ts');
for (const fw of ['react', 'vue']) {
  const dir = resolve(root, 'clients', fw, 'src');
  mkdirSync(dir, { recursive: true });
  copyFileSync(MUTATOR_SRC, resolve(dir, 'mutator.ts'));
}

// ── 2. Run Orval (generates Angular + React + Vue clients) ────────
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
    // Expose the axios instance so consumers can set baseURL / auth headers.
    if (entries.some((e) => e.isFile() && e.name === 'mutator.ts')) {
      exports.push(`export { AXIOS_INSTANCE } from './mutator';`);
    }
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

// ── 3b. Write a publishable package.json + .npmrc for each client ──
// clients/ is gitignored, so these are (re)generated every run. Scoped to the
// GitHub owner (@moamen-ui) so they publish to GitHub Packages (npm.pkg.github.com).
const VERSION = process.env.CLIENTS_VERSION ?? '1.0.0';
const REPO_URL = 'git+https://github.com/moamen-ui/poitner-api.git';
const CLIENTS = [
  {
    dir: 'clients/angular', name: '@moamen-ui/pointer-angular',
    desc: 'Pointer API client for Angular (httpResource + HttpClient services).',
    peerDependencies: { '@angular/core': '>=19.0.0', '@angular/common': '>=19.0.0', rxjs: '>=7.0.0' },
  },
  {
    dir: 'clients/react', name: '@moamen-ui/pointer-react',
    desc: 'Pointer API client for React (TanStack Query hooks).',
    peerDependencies: { react: '>=18.0.0', '@tanstack/react-query': '>=5.0.0' },
    dependencies: { axios: '>=1.6.0' },
  },
  {
    dir: 'clients/vue', name: '@moamen-ui/pointer-vue',
    desc: 'Pointer API client for Vue 3 (TanStack Vue Query composables).',
    peerDependencies: { vue: '>=3.4.0', '@tanstack/vue-query': '>=5.0.0' },
    dependencies: { axios: '>=1.6.0' },
  },
];
console.log('\n🏷  Writing package.json + .npmrc ...');
for (const c of CLIENTS) {
  const dest = resolve(root, c.dir);
  if (!existsSync(dest)) continue;
  // Minimal source manifest. `scripts/build-clients.mjs` compiles each client and
  // finalizes the publishable package.json (main/types/exports → dist). For Angular,
  // ng-packagr reads this clean manifest and writes dist/package.json itself.
  const pkg = {
    name: c.name,
    version: VERSION,
    description: c.desc,
    publishConfig: { registry: 'https://npm.pkg.github.com' },
    repository: { type: 'git', url: REPO_URL, directory: c.dir },
    ...(c.peerDependencies ? { peerDependencies: c.peerDependencies } : {}),
    ...(c.dependencies ? { dependencies: c.dependencies } : {}),
  };
  writeFileSync(resolve(dest, 'package.json'), JSON.stringify(pkg, null, 2) + '\n');
  writeFileSync(resolve(dest, '.npmrc'), '@moamen-ui:registry=https://npm.pkg.github.com\n');
  console.log(`   ✓ ${c.name}  (${c.dir}/package.json)`);
}

console.log('\n✅ Done! Generated packages:');
for (const c of CLIENTS) console.log(`   ${c.name} → ${c.dir}/`);

// ── 4. Sync Angular client to linked dashboard repo (if exists) ───
const dashboardPath = resolve(root, '..', 'pointer-dashboard');
const dashboardTarget = resolve(dashboardPath, 'src', 'app', 'core', 'api', 'generated');
if (existsSync(resolve(dashboardPath, 'package.json'))) {
  console.log('\n syncing to pointer-dashboard...');
  cpSync(resolve(root, 'clients', 'angular', 'src'), dashboardTarget, { recursive: true, force: true });
  console.log(`   ✓ Copied to ${dashboardTarget.replace(dashboardPath, 'pointer-dashboard')}/`);
  console.log('   (Dashboard imports it as @moamen-ui/pointer-angular — tsconfig path → this directory)');
} else {
  console.log('\n   (pointer-dashboard not found at sibling path — skipping sync)');
}
console.log('');
