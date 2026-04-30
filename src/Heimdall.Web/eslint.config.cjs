/**
 * ESLint flat config for heimdall-ticket-tracker-web.
 *
 * Uses the new flat config format required by ESLint v9+. Integrates with
 * Prettier per https://prettier.io/docs/integrating-with-linters.html by
 * applying `eslint-config-prettier` last to disable conflicting stylistic
 * rules. Source-code formatting is owned by Prettier.
 */
const js = require('@eslint/js');
const tseslint = require('typescript-eslint');
const jestPlugin = require('eslint-plugin-jest');
const prettierConfig = require('eslint-config-prettier');
const globals = require('globals');

module.exports = [
  {
    ignores: ['node_modules/**', 'wwwroot/**', 'coverage/**', '.yarn/**'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx,js,mjs,cjs}'],
    languageOptions: {
      ecmaVersion: 2022,
      sourceType: 'module',
      globals: {
        ...globals.node,
        ...globals.es2022,
      },
    },
  },
  {
    files: ['**/*.cjs'],
    languageOptions: {
      sourceType: 'commonjs',
    },
    rules: {
      '@typescript-eslint/no-require-imports': 'off',
    },
  },
  {
    files: ['**/*.razor.js'],
    languageOptions: {
      globals: {
        ...globals.browser,
        Blazor: 'readonly',
      },
    },
    rules: {
      '@typescript-eslint/no-unused-vars': ['error', { caughtErrors: 'none' }],
    },
  },
  {
    files: ['**/__tests__/**/*.{ts,tsx,js}', '**/*.test.{ts,tsx,js}'],
    plugins: { jest: jestPlugin },
    languageOptions: {
      globals: { ...globals.jest },
    },
    rules: {
      ...jestPlugin.configs.recommended.rules,
    },
  },
  prettierConfig,
];
