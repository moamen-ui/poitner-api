/**
 * Compiles the generated React client package into a publishable library:
 *   `tsc` → dist/ (JS + .d.ts), package.json points at dist
 *
 * Run after generate-clients.mjs. Publish from: clients/react/
 */
import { execSync } from 'node:child_process';
import { writeFileSync, existsSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const VERSION = process.env.CLIENTS_VERSION ?? '1.0.0';
const REGISTRY = process.env.CLIENTS_REGISTRY ?? 'https://npm.pkg.github.com';
const REPO_URL = 'git+https://github.com/moamen-ui/poitner-api.git';

console.log(`\n📦 Building client (version: ${VERSION}, registry: ${REGISTRY}) ...`);

// ── Per-package README (usage docs, shown on the package registry page) ──
const isGithub = REGISTRY.includes('npm.pkg.github.com');
const authLine = isGithub
  ? '\n//npm.pkg.github.com/:_authToken=${GITHUB_TOKEN}   # a token with read:packages'
  : '';
const INSTALL = `## Install

Published to the registry this build targets (\`${REGISTRY}\`). Add an \`.npmrc\` (repo root) so the \`@moamen-ui\` scope
resolves there, then install:

\`\`\`
@moamen-ui:registry=${REGISTRY}${authLine}
\`\`\`
`;

function clientReadme() {
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

// ── React: tsc → dist + a dist-pointing package.json ──────────────
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
  writeFileSync(resolve(dir, 'README.md'), clientReadme());
  console.log(`   ✓ @moamen-ui/pointer-${fw} → clients/${fw}/dist`);
}

buildTsClient('react', {
  peerDependencies: { react: '>=18.0.0', '@tanstack/react-query': '>=5.0.0' },
  dependencies: { axios: '>=1.6.0' },
});

console.log('\n✅ Client compiled.');
