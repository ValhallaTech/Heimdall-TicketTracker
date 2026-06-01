/**
 * ESLint flat config for Heimdall.Frontend (SvelteKit 2 + Svelte 5).
 *
 * This replicates the shared JS/TS lint posture from
 * `../src/Heimdall.Web/eslint.config.cjs` so the lint decision is shared rather
 * than re-decided: @eslint/js recommended -> typescript-eslint recommended ->
 * eslint-config-prettier applied LAST (Prettier owns source formatting per
 * https://prettier.io/docs/integrating-with-linters.html).
 *
 * On top of that shared posture it wires eslint-plugin-svelte's recommended
 * flat config, adds the `*.svelte` globs, and points the Svelte parser at the
 * typescript-eslint parser so `<script lang="ts">` blocks are understood.
 *
 * See blazor-to-svelte-transition.md §5 (Tooling) and
 * phase-6-checklist.md Phase 6.1 step 1.
 */
import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import tseslint from 'typescript-eslint';

import svelteConfig from './svelte.config.js';

export default tseslint.config(
  {
    ignores: [
      'node_modules/**',
      '.svelte-kit/**',
      'build/**',
      'coverage/**',
      'playwright-report/**',
      'test-results/**',
      '.yarn/**',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  ...svelte.configs.recommended,
  {
    languageOptions: {
      globals: {
        ...globals.browser,
        ...globals.node,
      },
    },
  },
  {
    files: ['**/*.svelte', '**/*.svelte.ts', '**/*.svelte.js'],
    languageOptions: {
      parserOptions: {
        extraFileExtensions: ['.svelte'],
        parser: tseslint.parser,
        svelteConfig,
      },
    },
  },
  // eslint-config-prettier MUST stay last so it can disable conflicting
  // stylistic rules from every preceding config.
  ...svelte.configs.prettier,
  prettier,
);
