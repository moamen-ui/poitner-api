/**
 * Downloads the OpenAPI/Swagger spec from the running API server
 * and generates the React API client package via Orval.
 *
 * Prerequisites:
 *   - The .NET API must be running (default: http://localhost:8090)
 *
 * Usage:
 *   npm run generate-clients
 *
 * Output:
 *   clients/react/src/    — @moamen-ui/pointer-react   (TanStack Query hooks)
 */

import { writeFileSync, readdirSync, existsSync, mkdirSync, copyFileSync } from 'node:fs';
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

// ── 1b. Materialize the axios mutator for the React client ────────
// orval.config points at clients/react/mutator.ts; clients/ is gitignored,
// so copy the tracked template into place before orval runs (orval keeps it via
// its `clean: ['!**/mutator.ts']` rule). Required for fresh checkouts (CI).
const MUTATOR_SRC = resolve(root, 'scripts', 'mutators', 'axios-mutator.ts');
{
  const dir = resolve(root, 'clients', 'react', 'src');
  mkdirSync(dir, { recursive: true });
  copyFileSync(MUTATOR_SRC, resolve(dir, 'mutator.ts'));
}

// ── 2. Run Orval (generates the React client) ─────────────────────
console.log('\n🔨 Generating client package...\n');
try {
  execSync('npx orval --config ./orval.config.ts', {
    cwd: root,
    stdio: 'inherit',
  });
} catch {
  process.exit(1);
}

// ── 3. Create barrel index.ts for the client ──────────────────────

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
createAxiosClientBarrel('clients/react');

// ── 3b. Write a publishable package.json + .npmrc for the client ──
// clients/ is gitignored, so these are (re)generated every run. Scoped to the
// GitHub owner (@moamen-ui) so it publishes to GitHub Packages (npm.pkg.github.com)
// or a local registry (e.g. Verdaccio) if CLIENTS_REGISTRY is set.
const VERSION = process.env.CLIENTS_VERSION ?? '1.0.0';
const REGISTRY = process.env.CLIENTS_REGISTRY ?? 'https://npm.pkg.github.com';
const REPO_URL = 'git+https://github.com/moamen-ui/poitner-api.git';
const CLIENTS = [
  {
    dir: 'clients/react', name: '@moamen-ui/pointer-react',
    desc: 'Pointer API client for React (TanStack Query hooks).',
    peerDependencies: { react: '>=18.0.0', '@tanstack/react-query': '>=5.0.0' },
    dependencies: { axios: '>=1.6.0' },
  },
];
console.log(`\n🏷  Writing package.json + .npmrc (version: ${VERSION}, registry: ${REGISTRY}) ...`);
for (const c of CLIENTS) {
  const dest = resolve(root, c.dir);
  if (!existsSync(dest)) continue;
  // Minimal source manifest. `scripts/build-clients.mjs` compiles the client and
  // finalizes the publishable package.json (main/types/exports → dist).
  const pkg = {
    name: c.name,
    version: VERSION,
    description: c.desc,
    publishConfig: { registry: REGISTRY },
    repository: { type: 'git', url: REPO_URL, directory: c.dir },
    ...(c.peerDependencies ? { peerDependencies: c.peerDependencies } : {}),
    ...(c.dependencies ? { dependencies: c.dependencies } : {}),
  };
  writeFileSync(resolve(dest, 'package.json'), JSON.stringify(pkg, null, 2) + '\n');
  writeFileSync(resolve(dest, '.npmrc'), `@moamen-ui:registry=${REGISTRY}\n`);
  console.log(`   ✓ ${c.name}  (${c.dir}/package.json)`);
}

console.log('\n✅ Done! Generated packages:');
for (const c of CLIENTS) console.log(`   ${c.name} → ${c.dir}/`);
console.log('\nNext: `npm run build-clients` to compile, then publish (or run the publish workflow).');
console.log('Consumers (incl. the dashboard) install the published @moamen-ui/pointer-* package.\n');
