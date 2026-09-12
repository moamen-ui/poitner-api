// @ts-nocheck
import { defineConfig } from 'vite';
import pointerSource from 'pointer-feedback/vite';

export default defineConfig({
  plugins: [
    pointerSource({
      enabled: process.env.VITE_POINTER_SOURCE === 'true',
      buildSha: process.env.VITE_BUILD_SHA || true,
    }),
  ],
});

