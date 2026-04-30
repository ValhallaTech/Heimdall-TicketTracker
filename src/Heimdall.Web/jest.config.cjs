/**
 * Jest configuration for heimdall-ticket-tracker-web.
 *
 * Notes:
 * - CommonJS config (`.cjs`) is used intentionally to avoid ESM-config friction
 *   alongside the existing ESM `build-assets.mjs` asset-copy script.
 * - `build-assets.mjs` is excluded from coverage because it is a build-time
 *   asset orchestration script, not testable application logic in this PR.
 * - `wwwroot/**` is generated/copied output and is also excluded.
 * - The 80% coverage threshold is real and enforced. A trivial pure-function
 *   utility (`scripts/util.ts`) is included and covered by the sanity test so
 *   the gate is meaningful from day one. As real production TypeScript is
 *   added, expand `collectCoverageFrom` accordingly.
 */
module.exports = {
  preset: 'ts-jest',
  testEnvironment: 'node',
  roots: ['<rootDir>/__tests__'],
  passWithNoTests: true,
  collectCoverage: true,
  coverageDirectory: 'coverage',
  coverageReporters: ['text', 'lcov', 'cobertura'],
  collectCoverageFrom: [
    'scripts/**/*.ts',
    'src/**/*.ts',
    '!**/*.config.*',
    '!**/__tests__/**',
    '!**/node_modules/**',
    '!**/coverage/**',
    '!**/wwwroot/**',
    '!build-assets.mjs',
  ],
  coverageThreshold: {
    global: {
      statements: 80,
      branches: 80,
      functions: 80,
      lines: 80,
    },
  },
};
