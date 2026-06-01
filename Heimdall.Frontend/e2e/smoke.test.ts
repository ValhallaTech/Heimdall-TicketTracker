/**
 * E2E smoke test for the splash route (`/`).
 *
 * Phase 6.1 step 4 (phase-6-checklist.md): boot the production build via the
 * `vite preview` webServer configured in playwright.config.ts and assert the
 * index route is healthy — it returns HTTP 200 and the rendered DOM contains
 * the Splash component (data-testid="splash" + "Heimdall" heading).
 */
import { expect, test } from '@playwright/test';

test('home page returns 200 and renders Splash', async ({ page }) => {
  const response = await page.goto('/');

  expect(response, 'navigation should yield a response').not.toBeNull();
  expect(response?.status()).toBe(200);

  await expect(page.getByTestId('splash')).toBeVisible();
  await expect(page.getByRole('heading', { level: 1, name: 'Heimdall' })).toBeVisible();
});
