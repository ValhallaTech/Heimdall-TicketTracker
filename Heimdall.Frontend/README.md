# Heimdall.Frontend

SvelteKit 2 + Svelte 5 (runes) frontend for Heimdall TicketTracker. This is the
Phase 6 Blazor → Svelte migration target. See
[`../docs/proposals/blazor-to-svelte-transition.md`](../docs/proposals/blazor-to-svelte-transition.md)
for topology, page inventory, and rationale, and
[`../docs/implementation/phase-6-checklist.md`](../docs/implementation/phase-6-checklist.md)
for the implementation checklist.

## Stack

- **SvelteKit 2** with **Svelte 5 runes** (`$state` / `$derived` / `$props` / `$effect`) — legacy syntax is not used.
- **`@sveltejs/adapter-node`** — standalone Node server for SSR + form actions (proposal §3.3, Topology B).
- **TypeScript strict mode** — no `any`; prefer `unknown` + narrowing.
- **bits-ui + shadcn-svelte + Tailwind CSS v4** — design system (see below).
- **Vitest + `@testing-library/svelte`** — component tests (JSDOM).
- **Playwright** — end-to-end / smoke tests.
- **ESLint (flat) + Prettier** — `eslint-plugin-svelte` / `prettier-plugin-svelte`, sharing the lint/format posture of `../src/Heimdall.Web`.

## Package manager / workspace

Yarn 4 via Corepack. **Independent Yarn workspace**: this project owns its own
`yarn.lock` and is _not_ part of a shared root workspace with `src/Heimdall.Web`
(which uses its own lockfile). This keeps the .NET host's JS tooling and the
SvelteKit toolchain decoupled. `nodeLinker: node-modules` is set in `.yarnrc.yml`.

```sh
corepack enable
yarn install
```

## Scripts

| Script               | Purpose                                                 |
| -------------------- | ------------------------------------------------------- |
| `yarn dev`           | Vite dev server                                         |
| `yarn build`         | Production build (`adapter-node` → `build/`)            |
| `yarn preview`       | Preview the production build                            |
| `yarn lint`          | ESLint (flat config)                                    |
| `yarn format`        | Prettier write                                          |
| `yarn format:check`  | Prettier check (CI)                                     |
| `yarn check`         | `svelte-check` (type + a11y diagnostics)                |
| `yarn test`          | Vitest run                                              |
| `yarn test:coverage` | Vitest run with v8 coverage (≥ 80% thresholds enforced) |
| `yarn test:e2e`      | Playwright e2e                                          |

## Design system

> **2026-05-15 — Design-system stance.** The SvelteKit frontend adopts
> **`bits-ui`** (headless, accessible primitives) + **`shadcn-svelte`** (styled
> components composed over bits-ui) + **Tailwind CSS** (brought in transitively
> by shadcn-svelte). `bits-ui` is the source for accessibility primitives where
> shadcn-svelte does not yet ship a parity component.
>
> **Bootstrap 5 and Font Awesome are deprecated within this SvelteKit project**
> and must not be imported here. They remain in the Razor host
> (`src/Heimdall.Web`) only until the Blazor retirement step, at which point they
> are removed. This records the decision from
> [`../docs/proposals/blazor-to-svelte-transition.md`](../docs/proposals/blazor-to-svelte-transition.md)
> §11.5 (open question 5, resolved 2026-05-15).

Tailwind config is expressed Tailwind-v4-style (CSS-first) in
[`src/app.css`](src/app.css) via `@import 'tailwindcss'` + theme tokens; the
Vite integration is `@tailwindcss/vite` in [`vite.config.ts`](vite.config.ts).
shadcn-svelte settings live in [`components.json`](components.json).

## Test ownership boundary

**Authorship of every Vitest spec (`*.spec.ts` / `*.test.ts`) and every
Playwright e2e test is owned by the JavaScript/TypeScript Unit Test Engineer
agent** (`agents/javascript-unit-tests.md`), per proposal §11 (open question 6,
resolved 2026-05-15) and `phase-6-checklist.md` Phase 6.1 steps 3–4.

The **Frontend Expert** authors production Svelte/SvelteKit code only and may
**only scaffold stub specs** for the test engineer to fill in. The stubs in this
repo are intentionally empty (`it.todo` / `test.fixme`) and are marked with a
`STUB — JS/TS Unit Test Engineer authors the real assertions` comment:

- [`src/routes/page.smoke.spec.ts`](src/routes/page.smoke.spec.ts) — splash render smoke (Vitest).
- [`e2e/smoke.test.ts`](e2e/smoke.test.ts) — `/` returns 200 + renders Splash (Playwright).

Do not add real assertions to those files outside the test engineer's ownership.

## Svelte MCP

Per the proposal, any `.svelte` component or SvelteKit route added here must be
authored after consulting the Svelte MCP server (`list-sections` +
`get-documentation`) rather than from training-data recall — Svelte 5 runes and
SvelteKit 2 APIs only.
