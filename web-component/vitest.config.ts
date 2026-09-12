import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    environment: 'jsdom',
    // Both locations: R3-04 put its suites under test/, R3-03 co-locates its own beside the source.
    include: ['test/**/*.test.ts', 'src/**/*.test.ts'],
  },
});
