import { svelteTesting } from '@testing-library/svelte/vite';
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vitest/config';

/**
 * Vitest configuration for the Heimdall SvelteKit frontend.
 *
 * Component tests run in a JSDOM environment via @testing-library/svelte.
 * The org-mandated >= 80% coverage threshold is enforced through
 * `coverage.thresholds` so a coverage shortfall fails the CI step (rather
 * than merely producing a report). See phase-6-checklist.md Phase 6.1 step 3.
 *
 * NOTE: Authorship of every real Vitest spec is owned by the
 * JavaScript/TypeScript Unit Test Engineer agent. The Frontend Expert only
 * scaffolds stub specs. See README.md "Test ownership boundary".
 */
export default defineConfig({
  plugins: [sveltekit(), svelteTesting()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./vitest-setup.ts'],
    include: ['src/**/*.{test,spec}.{js,ts}'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html', 'lcov'],
      include: ['src/**/*.{svelte,ts}'],
      exclude: ['src/**/*.{test,spec}.{js,ts}', 'src/app.d.ts', 'src/lib/index.ts'],
      thresholds: {
        statements: 80,
        branches: 80,
        functions: 80,
        lines: 80,
      },
    },
  },
});
