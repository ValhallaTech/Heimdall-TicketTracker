import { defineConfig } from '@playwright/test';

/**
 * Playwright E2E configuration for Heimdall.Frontend.
 *
 * Per the Playwright SvelteKit guide, tests run against the production build
 * served by `vite preview` by default. In CI we build first, then preview.
 * The `webServer` block lets Playwright boot the server and wait for it.
 *
 * See phase-6-checklist.md Phase 6.1 step 4. Real smoke assertions are authored
 * by the JS/TS Unit Test Engineer (agents/javascript-unit-tests.md).
 */
const PORT = 4173;

export default defineConfig({
  testDir: 'e2e',
  testMatch: /(.+\.)?(test|spec)\.[jt]s/,
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['html'], ['list']] : 'list',
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: 'on-first-retry',
  },
  webServer: {
    command: `yarn build && yarn preview --port ${PORT}`,
    port: PORT,
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});
