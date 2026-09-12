import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: '.',
  // upgrade-assert.mjs is listed explicitly: it is dual-mode (a Playwright test AND a script the
  // upgrade workflow runs directly with node), so it deliberately does not carry a .spec name.
  testMatch: ['**/*.spec.{ts,mjs}', '**/upgrade-assert.mjs'],
  timeout: 30_000,
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never', outputFolder: 'playwright-report' }]] : 'list',
  use: {
    baseURL: process.env.E2E_FIXTURE_URL || 'http://localhost:4173',
    trace: 'retain-on-failure',
    ...(process.env.E2E_MOCK_TLS ? { ignoreHTTPSErrors: true } : {}),
    ...(process.env.E2E_MOCK_DOMAIN
      ? {
          launchOptions: {
            args: [
              `--host-resolver-rules=MAP ${process.env.E2E_MOCK_DOMAIN} 127.0.0.1, MAP *.${process.env.E2E_MOCK_DOMAIN} 127.0.0.1`,
            ],
          },
        }
      : {}),
  },
});
