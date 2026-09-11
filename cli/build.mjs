import * as esbuild from 'esbuild';
import { env } from 'node:process';

const defaultServer = env.POINTER_DEFAULT_SERVER || 'https://api.pointer.moamen.work';

await esbuild.build({
  entryPoints: ['src/cli.ts'],
  bundle: true,
  outfile: 'dist/cli.js',
  platform: 'node',
  format: 'esm',
  banner: {
    js: '#!/usr/bin/env node'
  },
  define: {
    'DEFAULT_SERVER': JSON.stringify(defaultServer)
  }
});
