// @ts-nocheck
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import pointerSource from 'pointer-feedback/vite';

// @vitejs/plugin-react is what a real React project uses, and it is also what carries the Babel
// toolchain the source-stamp plugin parses with. Without it the plugin warns once per file and
// writes an EMPTY manifest — it degrades rather than failing, by design, so a fixture missing it
// produces a build that looks fine and stamps nothing.
export default defineConfig({
  plugins: [
    react(),
    pointerSource({
      enabled: process.env.VITE_POINTER_SOURCE === 'true',
      buildSha: process.env.VITE_BUILD_SHA || true,
    }),
  ],
});

