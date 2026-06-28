/**
 * Compiles the generated client packages into publishable libraries:
 *   - React / Vue : `tsc` → dist/ (JS + .d.ts), package.json points at dist
 *   - Angular     : `ng-packagr` → dist/ (Ivy partial lib + types + package.json)
 *
 * Run after generate-clients.mjs. Publish from:
 *   clients/react/   clients/vue/   clients/angular/dist/
 */
import { execSync } from 'node:child_process';
import { writeFileSync, readFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const VERSION = process.env.CLIENTS_VERSION ?? '1.0.0';
const REGISTRY = 'https://npm.pkg.github.com';
const REPO_URL = 'git+https://github.com/moamen-ui/poitner-api.git';

// ── React / Vue: tsc → dist + a dist-pointing package.json ────────
function buildTsClient(fw, { peerDependencies, dependencies }) {
  const dir = resolve(root, 'clients', fw);
  if (!existsSync(dir)) return;
  const tsconfig = {
    compilerOptions: {
      target: 'ES2022',
      module: 'ESNext',
      moduleResolution: 'bundler',
      declaration: true,
      outDir: 'dist',
      rootDir: 'src',
      strict: false,
      skipLibCheck: true,
      esModuleInterop: true,
      lib: ['ES2022', 'DOM'],
    },
    include: ['src'],
  };
  writeFileSync(resolve(dir, 'tsconfig.build.json'), JSON.stringify(tsconfig, null, 2));
  console.log(`\n🔨 tsc ${fw} ...`);
  execSync('npx tsc -p tsconfig.build.json', { cwd: dir, stdio: 'inherit' });

  const pkg = {
    name: `@moamen-ui/pointer-${fw}`,
    version: VERSION,
    description: `Pointer API client for ${fw[0].toUpperCase() + fw.slice(1)}.`,
    type: 'module',
    main: './dist/index.js',
    module: './dist/index.js',
    types: './dist/index.d.ts',
    exports: {
      '.': { types: './dist/index.d.ts', default: './dist/index.js' },
      './*': { types: './dist/*.d.ts', default: './dist/*.js' },
    },
    files: ['dist'],
    sideEffects: false,
    publishConfig: { registry: REGISTRY },
    repository: { type: 'git', url: REPO_URL, directory: `clients/${fw}` },
    peerDependencies,
    ...(dependencies ? { dependencies } : {}),
  };
  writeFileSync(resolve(dir, 'package.json'), JSON.stringify(pkg, null, 2) + '\n');
  writeFileSync(resolve(dir, '.npmrc'), `@moamen-ui:registry=${REGISTRY}\n`);
  console.log(`   ✓ @moamen-ui/pointer-${fw} → clients/${fw}/dist`);
}

// ── Angular: ng-packagr → dist (Ivy partial lib + types) ──────────
function buildAngular() {
  const dir = resolve(root, 'clients', 'angular');
  if (!existsSync(dir)) return;
  writeFileSync(
    resolve(dir, 'ng-package.json'),
    JSON.stringify({ dest: './dist', lib: { entryFile: 'src/index.ts' } }, null, 2),
  );
  console.log('\n🔨 ng-packagr angular ...');
  execSync('npx ng-packagr -p ng-package.json', { cwd: dir, stdio: 'inherit' });

  // ng-packagr writes dist/package.json with proper exports/types; add the registry.
  const distPkgPath = resolve(dir, 'dist', 'package.json');
  const distPkg = JSON.parse(readFileSync(distPkgPath, 'utf-8'));
  distPkg.publishConfig = { registry: REGISTRY };
  distPkg.repository = { type: 'git', url: REPO_URL, directory: 'clients/angular' };
  writeFileSync(distPkgPath, JSON.stringify(distPkg, null, 2) + '\n');
  writeFileSync(resolve(dir, 'dist', '.npmrc'), `@moamen-ui:registry=${REGISTRY}\n`);
  console.log('   ✓ @moamen-ui/pointer-angular → clients/angular/dist');
}

buildTsClient('react', {
  peerDependencies: { react: '>=18.0.0', '@tanstack/react-query': '>=5.0.0' },
  dependencies: { axios: '>=1.6.0' },
});
buildTsClient('vue', {
  peerDependencies: { vue: '>=3.4.0', '@tanstack/vue-query': '>=5.0.0' },
  dependencies: { axios: '>=1.6.0' },
});
buildAngular();

console.log('\n✅ Clients compiled.');
