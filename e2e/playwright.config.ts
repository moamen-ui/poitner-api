import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',
  timeout: 30_000,
  fullyParallel: false, // widget comment creation must stay serial for deterministic createdAt ordering
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: process.env.E2E_FIXTURE_URL || 'http://localhost:4173',
    trace: 'retain-on-failure',
  },
});
