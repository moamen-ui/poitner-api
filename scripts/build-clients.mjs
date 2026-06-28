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

// ── Per-package README (usage docs, shown on the GitHub Packages page) ──
const INSTALL = `## Install

Published to **GitHub Packages**. Add an \`.npmrc\` (repo root) so the \`@moamen-ui\` scope
resolves there, then install:

\`\`\`
@moamen-ui:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=\${GITHUB_TOKEN}   # a token with read:packages
\`\`\`
`;

function clientReadme(fw) {
  if (fw === 'react') {
    return `# @moamen-ui/pointer-react

Typed [Pointer API](https://github.com/moamen-ui/poitner-api) client for **React** — TanStack Query
hooks generated from the API's OpenAPI spec. Responses are already unwrapped from the API's
\`Result<T>\` envelope by the built-in axios mutator.

${INSTALL}
\`\`\`bash
npm install @moamen-ui/pointer-react @tanstack/react-query axios
\`\`\`

## Setup

Point the axios instance at your API and add auth, then provide a QueryClient:

\`\`\`ts
import { AXIOS_INSTANCE } from '@moamen-ui/pointer-react';
AXIOS_INSTANCE.defaults.baseURL = 'https://api.pointer.moamen.work';
AXIOS_INSTANCE.interceptors.request.use((c) => {
  c.headers.Authorization = \`Bearer \${localStorage.getItem('pointer_token')}\`;
  return c;
});
\`\`\`
\`\`\`tsx
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
const qc = new QueryClient();
<QueryClientProvider client={qc}>{children}</QueryClientProvider>
\`\`\`

## Use

\`\`\`tsx
import { useGetApiAdminUsers, usePostApiAdminUsers } from '@moamen-ui/pointer-react';

const { data: users, isLoading } = useGetApiAdminUsers();      // GET → useQuery
const createUser = usePostApiAdminUsers();                     // POST → useMutation
createUser.mutate({ data: { email, password, displayName, roleId } });
\`\`\`

Types come from the same package: \`import type { UserResponse } from '@moamen-ui/pointer-react';\`
`;
  }
  if (fw === 'vue') {
    return `# @moamen-ui/pointer-vue

Typed [Pointer API](https://github.com/moamen-ui/poitner-api) client for **Vue 3** — TanStack Vue
Query composables generated from the API's OpenAPI spec. Responses are already unwrapped from the
API's \`Result<T>\` envelope by the built-in axios mutator.

${INSTALL}
\`\`\`bash
npm install @moamen-ui/pointer-vue @tanstack/vue-query axios
\`\`\`

## Setup

\`\`\`ts
import { AXIOS_INSTANCE } from '@moamen-ui/pointer-vue';
AXIOS_INSTANCE.defaults.baseURL = 'https://api.pointer.moamen.work';
AXIOS_INSTANCE.interceptors.request.use((c) => {
  c.headers.Authorization = \`Bearer \${localStorage.getItem('pointer_token')}\`;
  return c;
});
\`\`\`
\`\`\`ts
// main.ts
import { VueQueryPlugin } from '@tanstack/vue-query';
app.use(VueQueryPlugin);
\`\`\`

## Use

\`\`\`vue
<script setup lang="ts">
import { useGetApiAdminUsers, usePostApiAdminUsers } from '@moamen-ui/pointer-vue';

const { data: users, isLoading } = useGetApiAdminUsers();   // GET → useQuery composable
const createUser = usePostApiAdminUsers();                  // POST → useMutation composable
</script>
\`\`\`

Types come from the same package: \`import type { UserResponse } from '@moamen-ui/pointer-vue';\`
`;
  }
  // angular
  return `# @moamen-ui/pointer-angular

Typed [Pointer API](https://github.com/moamen-ui/poitner-api) client for **Angular** — signal-first
\`httpResource\` functions for GETs and injectable services for mutations, generated from the API's
OpenAPI spec. Built with ng-packagr (Ivy partial).

${INSTALL}
\`\`\`bash
npm install @moamen-ui/pointer-angular
\`\`\`

## Setup

The client uses Angular's \`HttpClient\` with **relative** \`/api/...\` URLs. Provide \`HttpClient\` and an
interceptor that prepends your API origin, attaches the token, and unwraps the \`Result<T>\` envelope:

\`\`\`ts
import { HttpInterceptorFn, provideHttpClient, withInterceptors } from '@angular/common/http';
import { map } from 'rxjs';

const API = 'https://api.pointer.moamen.work';
export const pointerInterceptor: HttpInterceptorFn = (req, next) => {
  const token = localStorage.getItem('pointer_token');
  const r = req.clone({
    url: req.url.startsWith('/api') ? API + req.url : req.url,
    setHeaders: token ? { Authorization: \`Bearer \${token}\` } : {},
  });
  return next(r).pipe(map((e: any) => (e.body && 'isSuccess' in e.body ? e.clone({ body: e.body.data }) : e)));
};

// main.ts
bootstrapApplication(App, { providers: [provideHttpClient(withInterceptors([pointerInterceptor]))] });
\`\`\`

## Use

\`\`\`ts
import { UsersService, getApiAdminUsersResource } from '@moamen-ui/pointer-angular';

// GET → signal-first resource (auto-fetches; .value() / .isLoading() / .reload())
usersResource = getApiAdminUsersResource();

// Mutations → injectable service (Observable; envelope unwrapped by the interceptor)
private users = inject(UsersService);
this.users.postApiAdminUsers({ email, password, displayName, roleId }).subscribe();
\`\`\`

Types come from the same package: \`import type { UserResponse } from '@moamen-ui/pointer-angular';\`
`;
}

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
  writeFileSync(resolve(dir, 'README.md'), clientReadme(fw));
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
  writeFileSync(resolve(dir, 'dist', 'README.md'), clientReadme('angular'));
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
