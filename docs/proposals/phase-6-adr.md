# Phase 6 Architecture Decision Record — Svelte/SvelteKit Migration Open Decisions

**Status:** **Proposed** (2026-05-22)
**Author:** Orchestrator (Copilot)
**Scope:** Phase 6 (Blazor → Svelte 5 / SvelteKit 2 migration) — open decisions left unresolved by [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md).
**Decision required:** Resolve the six open decisions enumerated in §3–§8 below so Phase 6 implementation ([`phase-6-checklist.md`](../implementation/phase-6-checklist.md)) can proceed without further design work.
**Depends on:** [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) (approved 2026-05-15), [`security-and-authorization.md`](./security-and-authorization.md) §9.3 Phase 6, [`phase-6-checklist.md`](../implementation/phase-6-checklist.md).

*This document is research and planning only.*

---

## 1. Summary and context

This ADR resolves the remaining open architectural decisions for Phase 6 of the security-and-authorization rollout — the migration from Blazor Server to Svelte 5 / SvelteKit 2. The parent proposal [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) (approved 2026-05-15) established the fixed decisions: Svelte 5 with runes, SvelteKit 2, `@sveltejs/adapter-node`, Topology B (logical separation with single-container deployment). The .NET backend (ASP.NET Core), PostgreSQL, Redis, and OpenFGA remain unchanged — this phase touches only the frontend layer and its integration seams. The ordered implementation plan lives in [`phase-6-checklist.md`](../implementation/phase-6-checklist.md).

The approved deployment topology is **single-container**: one Docker image runs both the SvelteKit `adapter-node` process (external ingress) and `Heimdall.Web` (internal API) side by side. The browser communicates with a single external origin (the SvelteKit endpoint); `Heimdall.Web` is reachable only via internal loopback and is never directly addressed by the browser. This deployment posture collapses the logical two-origin Topology B into one external origin per [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §3.2 and §11.2, which means **CORS between the browser and `Heimdall.Web` does not apply** — the browser never makes a cross-origin request. SameSite cookie semantics on the refresh-token cookie remain first-party (same-origin browser → SvelteKit) so `SameSite=Strict` / `SameSite=Lax` and the `__Host-` cookie prefix posture from Phase 5 works unchanged.

This document resolves six open decisions so Phase 6 implementation can proceed.

---

## 2. Fixed decisions (recap)

The following are locked and **not** re-litigated in this ADR:

| Decision | Resolution | Source |
| --- | --- | --- |
| Framework | Svelte 5 with runes (`$state`, `$derived`, `$props`, `$effect`) | [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §1–§3 |
| Meta-framework | SvelteKit 2 | [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §3 |
| Adapter | `@sveltejs/adapter-node` | [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §3.3 |
| Hosting topology | Topology B — logical decoupling, single-container deployment | [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §3.2, §11.2 |
| Auth model | JWT bearer + refresh-token cookie; Phase 5 stack unchanged | [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §4.2 |
| Backend | ASP.NET Core, PostgreSQL, Redis, OpenFGA — unchanged | Implicit |

---

## 3. Open decision 1 — Frontend / backend separation of concerns

### Options

**Option A — Logical decoupling (SvelteKit owns ingress):** `Heimdall.Frontend` (SvelteKit) owns all browser-facing ingress. `Heimdall.Web` is a pure internal API reachable only via server-to-server calls from SvelteKit's `+page.server.ts` / `hooks.server.ts`. The two processes run in a single container under a supervisor; the browser sees one external origin.

**Option B — SPA hosted by .NET (`adapter-static`):** The SvelteKit build produces static assets (`adapter-static`) which ASP.NET Core serves from `wwwroot`. No separate Node process runs at runtime.

### Analysis

| Factor | Option A (logical decoupling) | Option B (`adapter-static`) |
| --- | --- | --- |
| Render deployment | Single Docker image / single Render service — matches the approved single-container topology from [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §11.2 | Single Docker image, but forces the frontend to be fully static (no SSR) |
| SSR-on-demand | Preserved — `adapter-node` supports server-side rendering and form actions | Eliminated — `adapter-static` pre-renders at build time only |
| `+page.server.ts` use cases | Natural place for the refresh-cookie → access-token exchange and server-side OpenFGA `Check()` calls | Unavailable — all rendering happens in the browser |
| JWT bearer model fit | Excellent — the SvelteKit server handles the refresh cookie and forwards the bearer to the internal API; browser never holds the access token | Poor — the browser would need to hold the access token or the refresh cookie would have to be sent to `Heimdall.Web` directly (cross-origin, CORS, SameSite concerns) |
| Complexity | Requires a supervisor to manage two processes (Node + dotnet) in one container — bounded complexity, well-documented patterns (e.g. `s6-overlay`) | Simpler container (single dotnet process) but loses critical SSR and auth capabilities |

SvelteKit's server-side `fetch` can call any origin cleanly — the internal loopback call from SvelteKit to `Heimdall.Web` is a straightforward `fetch('http://localhost:<port>/api/...')` carrying the bearer token in the `Authorization` header. Same-origin is only required for cookie-based browser fetches, which the approved topology already satisfies (browser → SvelteKit is same-origin).

### Recommendation

**Option A — Logical decoupling (SvelteKit owns ingress), collapsed to a single external origin via the approved single-container deployment.**

Option B is rejected because:
1. It forces `adapter-static`, which eliminates SSR-on-demand and breaks the `+page.server.ts` pattern that is the natural place for the refresh-cookie exchange and server-side permission checks.
2. It complicates the JWT bearer model — the browser would either hold the access token (security regression from [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §4.2's "access token never sent to the browser as JS-readable state" stance) or the refresh cookie would have to flow cross-origin to `Heimdall.Web` (requires CORS configuration, `SameSite=None` with `Secure`, third-party cookie restrictions in Safari/Firefox).

### Rationale

Cross-reference [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §3.3 (`adapter-node` chosen) and §4.2 (server-side `Check()`). The logical decoupling preserves the clean API/UI split that Phase 5 was designed to enable while the single-container deployment eliminates browser-facing CORS and SameSite concerns.

---

## 4. Open decision 2 — UI component library

### Options

**Option A — `shadcn-svelte` + `bits-ui`:** Headless accessibility primitives from `bits-ui` (WAI-ARIA compliant, Svelte 5-native) composed with styled components from `shadcn-svelte`. Tailwind CSS arrives as a transitive design-system dependency.

**Option B — Svelte Material UI (SMUI):** Material Design component library for Svelte.

### Analysis

| Factor | Option A (`shadcn-svelte` + `bits-ui`) | Option B (SMUI) |
| --- | --- | --- |
| Svelte 5 runes compatibility | **Full** — `bits-ui` is Svelte 5-native and maintained by huntabyte (same ecosystem as `shadcn-svelte`) | **Incomplete** — SMUI's Svelte 5 runes story is in progress; the project is maintenance-only with limited active development |
| Accessibility (a11y) | **Excellent** — `bits-ui` renders headless WAI-ARIA primitives; a11y is the foundational concern | **Good** — Material Design includes a11y guidance, but SMUI's implementation is less actively audited |
| Bundle size | **Small** — tree-shakeable, copy-paste-and-own model; no vendored runtime | **Larger** — runtime library with Material Design token system |
| Design-system flexibility | **High** — shadcn-svelte components are copied into the project and owned by the team; styling is fully customisable | **Medium** — tied to Material Design; customisation requires overriding tokens |
| Maintenance activity | **Active** — `bits-ui` and `shadcn-svelte` are actively developed with regular releases | **Maintenance-only** — SMUI has slowed significantly |
| Tailwind CSS fit | **Native** — `shadcn-svelte` is built on Tailwind | **Foreign** — SMUI uses SCSS-based theming |

### Recommendation

**Option A — `shadcn-svelte` + `bits-ui`.**

This matches the choice already locked in [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 2 ("Install `bits-ui` + `shadcn-svelte` + Tailwind CSS") and the [`security-and-authorization.md`](./security-and-authorization.md) decision log entry 2026-05-15.

### Rationale

`bits-ui` is Svelte 5-native and renders headless WAI-ARIA primitives owned by huntabyte (same maintainer ecosystem as `shadcn-svelte`). `shadcn-svelte` composes on top with a copy-paste-and-own model rather than a vendored runtime — the design system becomes part of the project's source tree, fully customisable without upstream coupling. Tailwind CSS arrives as a transitive design-system dependency. SMUI's Svelte 5 runes story is incomplete and the project is maintenance-only; its Material Design binding would be a mismatch for the existing Bootstrap 5-based visual language the Blazor host ships today.

---

## 5. Open decision 3 — Forms and validation

### Options

**Option A — Superforms + Formsnap + FluentValidation (backend only):** Client-side form handling via Superforms and Formsnap; validation rules live only in FluentValidation on the .NET API.

**Option B — Superforms + Formsnap + Zod (client-side only):** Validation rules live only in Zod schemas in `Heimdall.Frontend`.

**Option C (Hybrid) — Superforms + Formsnap + Zod (client) + FluentValidation (server):** Zod schemas in `Heimdall.Frontend` for instant UX validation; FluentValidation on the .NET API as the authoritative enforcement boundary.

### Analysis

| Factor | Option A (FluentValidation only) | Option B (Zod only) | Option C (Hybrid) |
| --- | --- | --- | --- |
| Instant UX feedback | **Poor** — validation requires a round-trip to the API | **Excellent** — Zod validates in the browser instantly | **Excellent** — Zod validates in the browser instantly |
| Trust boundary | **Correct** — the API is the trust boundary; browser validation is never trusted | **Incorrect** — the browser is not a trust boundary; a malicious client bypasses Zod trivially | **Correct** — the API remains the authoritative enforcement boundary |
| Type safety (form → API) | Weak — form shape is not statically linked to API contract | Strong — Zod schema types flow through Superforms and Formsnap | Strong — Zod schema types flow through Superforms and Formsnap |
| Error-surfacing UX | Delayed — errors appear only after the API call returns | Instant — errors appear as the user types | Instant — errors appear as the user types; API errors are additive (e.g. "email already registered") |
| Maintenance burden | Low — rules in one place | Low — rules in one place | **Higher** — rules in two places (Zod and FluentValidation) must stay in sync |
| OpenFGA deny-closed alignment | Matches the deny-closed posture (never trust the client) | Violates the deny-closed posture | Matches the deny-closed posture |

### Recommendation

**Option C (Hybrid) — Zod schemas in `Heimdall.Frontend` for instant UX plus FluentValidation on the .NET API as the authoritative enforcement boundary.**

### Rationale

The API is the trust boundary, never the browser — this is a non-negotiable security posture that matches the OpenFGA deny-closed stance documented in [`openfga.md`](./openfga.md). Server-side rule duplication via FluentValidation is therefore mandatory. The extra maintenance burden of dual rule sets is bounded: both layers describe the same shape from a single TypeScript source of truth (Zod) with FluentValidation generated or hand-mirrored as the canonical API guard.

**Risk:** Zod ↔ FluentValidation rule drift is a tracked risk (see §9 consolidated risk table). A follow-up investigation into generators that emit FluentValidation rules from Zod schemas is flagged but **not** committed to in this ADR.

---

## 6. Open decision 4 — Icons

### Options

**Option A — Iconify via `iconify-tailwind`:** Class-driven, tree-shakeable per-icon system integrated natively with Tailwind CSS.

**Option B — Font Awesome (current):** Webfont-based icon system currently used in the Blazor host.

### Analysis

| Factor | Option A (Iconify + Tailwind) | Option B (Font Awesome) |
| --- | --- | --- |
| Bundle size | **Small** — tree-shakeable per icon; only used icons are bundled | **Large** — webfont payloads include the entire icon set |
| Tree-shaking | **Native** — per-icon, class-driven | **Poor** — webfonts are not tree-shakeable |
| Svelte 5 / Vite compatibility | **Excellent** — Iconify is a modern, Vite-native system | **Adequate** — works but requires the deprecated Yarn-pipeline asset chain |
| Icon coverage | **Vast** — Iconify aggregates 200k+ icons from 100+ icon sets (including Font Awesome) | **Good** — Font Awesome's own set |
| shadcn-svelte composition | **Native** — Tailwind class-driven icons compose directly with shadcn-svelte components | **Foreign** — `<i class="fa-...">` element model doesn't compose cleanly with shadcn-svelte |
| Migration cost | Bounded — requires an icon set inventory and 1:1 mapping | Zero — already in use |

### Recommendation

**Option A — Iconify via `iconify-tailwind`.**

### Rationale

Font Awesome's webfont pipeline and `<i class="fa-...">` element model don't compose with shadcn-svelte components and force the deprecated Yarn-pipeline asset chain that [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 18 already plans to remove. Iconify's class-driven, tree-shakeable per-icon model integrates natively with Tailwind CSS (which is already arriving via shadcn-svelte). Migration cost is bounded to the icon set inventory documented in step 18 — each `fa-*` class maps to an Iconify equivalent (Iconify includes the Font Awesome icon set, so visual parity is achievable).

---

## 7. Open decision 5 — Testing

### Confirmation

The testing stack is **Vitest + `@testing-library/svelte` + Playwright**, as locked in [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) steps 3–4 and [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §5.

### Justification

**Vitest over Jest:** Vitest shares Vite's module resolver and natively understands SvelteKit's `$lib`, `$app/*`, and `.svelte` imports without extra transforms. `vitest run` reads the same Svelte plugin config as `vite build`, ensuring parity between test and build environments. Jest would require additional configuration to resolve SvelteKit's aliased imports and would introduce a second module resolution system.

**Playwright coverage:** The critical-path flows enumerated in [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 19 are: login, MFA challenge, create ticket, edit ticket, admin tuple add/remove.

### Flagged gaps

Two gaps are observed that should be addressed as scope additions:

1. **JWKS refresh path:** The SvelteKit client must tolerate a `kid`-rotation without logging the user out unexpectedly. This is covered by the Phase 5 step 22 runbook, but should also have a Playwright assertion that a `kid`-rotation does not cause an unexpected logout.

2. **Refresh-cookie family-replay path:** Phase 5 step 10 specifies family-replay detection. A Playwright scenario should assert that concurrent tab refresh does not trigger a family revoke — i.e., two tabs refreshing simultaneously both succeed (or one succeeds and the other gracefully re-authenticates) rather than both being logged out.

### Recommendation

**Confirmed** — Vitest + `@testing-library/svelte` + Playwright. Add the two flagged gaps to the Phase 6 checklist as scope additions or record them in the consolidated risk table (§9) if deferral is accepted.

---

## 8. Open decision 6 — Bundler (Parcel vs. Vite)

### Options

**Option A — Vite (current, via SvelteKit):** SvelteKit is built on Vite and ships with `vite-plugin-svelte` and `@sveltejs/kit/vite`.

**Option B — Parcel:** Alternative zero-config bundler.

### Analysis

SvelteKit is **tightly coupled to Vite** via `vite-plugin-svelte` and the `@sveltejs/kit/vite` plugin. The SvelteKit build pipeline, HMR, `hooks.server.ts` integration, and `adapter-node` output all depend on Vite internals. Replacing Vite with Parcel would break:

- `adapter-node` — the adapter expects Vite's build output structure
- `hooks.server.ts` — SvelteKit's server hooks are compiled by Vite
- HMR — SvelteKit's hot module replacement is Vite-native
- The entire `@sveltejs/kit/vite` plugin pipeline

This is not a configurable choice — it is an architectural constraint of SvelteKit.

### Recommendation

**Option A — Vite (no change).**

Vite already provides minification and tree-shaking via Rollup at build time. No second bundler is needed or feasible within a SvelteKit project.

---

## 9. Consolidated risk table

| Risk | Description | Mitigation | Owner |
| --- | --- | --- | --- |
| Zod ↔ FluentValidation duplication | Validation rules in TypeScript (Zod) and C# (FluentValidation) may drift out of sync, leading to inconsistent error messages or silent acceptance of invalid input on one layer. | (a) Treat FluentValidation as the canonical enforcement boundary — it must never be weaker than Zod. (b) Code review checklist item: "If the Zod schema changed, did the FluentValidation validator change?" (c) Flag investigation into Zod → FluentValidation generators as a follow-up (not committed). | Frontend Expert, C# Coding Agent |
| Single-container supervisor / process-restart | A Node or dotnet crash in the single-container deployment requires the supervisor to restart the failed process without taking down the other. Misconfiguration could leave half the container running. | Document the supervisor choice (`s6-overlay` or equivalent) in the step-17 PR. Integration test in CI that kills one process and asserts the supervisor restarts it within 10 s. Cross-reference [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 5 (planning artefact) and step 17 (implementation). | Docker Expert |
| SvelteKit refresh-cookie / JWKS-rotation interaction | If the JWKS rotates while a user is mid-session, the SvelteKit `hooks.server.ts` must re-fetch the JWKS and re-verify the access token with the new `kid`. A bug here could log users out unexpectedly. | Playwright E2E test scenario asserting a `kid`-rotation does not cause an unexpected logout. Link to Phase 5 step 22 runbook for the rotation procedure. | Frontend Expert, Security Reviewer |
| Icon migration coverage gap | The Font Awesome → Iconify migration may miss icons used only in conditional branches or edge-case UI states, causing missing icons in production. | Exhaustive `grep` for `fa-` classes before step 18 ships. Visual diff review by Frontend Expert on critical pages. | Frontend Expert |
| shadcn-svelte copy-paste-and-own maintenance burden | Because shadcn-svelte components are copied into the project and owned by the team, upstream bug fixes and a11y improvements do not flow automatically. | Track upstream `shadcn-svelte` releases; periodically audit the project's copied components against upstream. Document the copy-paste-and-own model in `Heimdall.Frontend/README.md`. | Frontend Expert |
| Bundle-size regression from Tailwind purge misconfiguration | Tailwind CSS's purge mechanism must be configured to scan all `.svelte`, `.ts`, and `.html` files. Misconfiguration could ship the entire Tailwind stylesheet (≈4 MB). | CI job asserting `Heimdall.Frontend/build/` CSS size is below a threshold (e.g. 100 KB). Visual inspection of the Vite build output. | Frontend Expert |
| A11y regression from shadcn-svelte vs. Blazor baseline | Blazor Server's server-rendered HTML baseline may have a11y characteristics (focus management, ARIA attributes) that the shadcn-svelte components do not replicate. | Playwright + axe-core accessibility audit on critical pages. Manual screen-reader testing before Phase 6 sign-off. | Frontend Expert |
| CORS and SameSite cookie semantics in single-container deployment | Although the approved single-container deployment eliminates browser-facing CORS concerns, a misconfiguration (e.g. exposing `Heimdall.Web` directly) could reintroduce them. | Integration test asserting `Heimdall.Web` is not reachable from outside the container. Document the internal loopback posture in the step-20 runbook. Link to [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §11.2. | Docker Expert, Security Reviewer |

---

## 10. Decision log

| Date | Decision |
| --- | --- |
| 2026-05-22 | ADR drafted. Six recommendations resolved: (1) **Option A logical decoupling** — SvelteKit owns ingress, collapsed to a single external origin via the approved single-container deployment; (2) **shadcn-svelte + bits-ui** — matches [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 2 and [`security-and-authorization.md`](./security-and-authorization.md) decision log entry 2026-05-15; (3) **Hybrid Zod (UX) + FluentValidation (API trust boundary)** — Zod ↔ FluentValidation duplication is a tracked risk with mitigation; (4) **Iconify + Tailwind** — Font Awesome webfont pipeline deprecated; (5) **Vitest + Testing Library Svelte + Playwright** confirmed, with two flagged gaps (JWKS rotation, refresh-cookie family-replay) to add as scope additions or track in the risk table; (6) **Vite (no Parcel)** — SvelteKit is tightly coupled to Vite; no alternative is feasible. Parent decision record: [`security-and-authorization.md`](./security-and-authorization.md) decision log entry 2026-05-15 (Phase 6 approved). |

---

## 11. References

- **Svelte 5** — [https://github.com/sveltejs/svelte](https://github.com/sveltejs/svelte)
- **SvelteKit** — [https://github.com/sveltejs/kit](https://github.com/sveltejs/kit)
- **`@sveltejs/adapter-node`** — [https://github.com/sveltejs/kit/tree/main/packages/adapter-node](https://github.com/sveltejs/kit/tree/main/packages/adapter-node)
- **`bits-ui`** — [https://github.com/huntabyte/bits-ui](https://github.com/huntabyte/bits-ui)
- **`shadcn-svelte`** — [https://github.com/huntabyte/shadcn-svelte](https://github.com/huntabyte/shadcn-svelte)
- **Tailwind CSS** — [https://github.com/tailwindlabs/tailwindcss](https://github.com/tailwindlabs/tailwindcss)
- **Iconify** — [https://github.com/iconify/iconify](https://github.com/iconify/iconify)
- **Superforms** — [https://github.com/ciscoheat/sveltekit-superforms](https://github.com/ciscoheat/sveltekit-superforms)
- **Formsnap** — [https://github.com/huntabyte/formsnap](https://github.com/huntabyte/formsnap)
- **Zod** — [https://github.com/colinhacks/zod](https://github.com/colinhacks/zod)
- **FluentValidation** — [https://github.com/FluentValidation/FluentValidation](https://github.com/FluentValidation/FluentValidation)
- **Vitest** — [https://github.com/vitest-dev/vitest](https://github.com/vitest-dev/vitest)
- **`@testing-library/svelte`** — [https://github.com/testing-library/svelte-testing-library](https://github.com/testing-library/svelte-testing-library)
- **Playwright** — [https://github.com/microsoft/playwright](https://github.com/microsoft/playwright)
- **Svelte Material UI (SMUI)** — [https://github.com/hperrin/svelte-material-ui](https://github.com/hperrin/svelte-material-ui)
- **Font Awesome** — [https://github.com/FortAwesome/Font-Awesome](https://github.com/FortAwesome/Font-Awesome)
- **Parcel** — [https://github.com/parcel-bundler/parcel](https://github.com/parcel-bundler/parcel)
- **Vite** — [https://github.com/vitejs/vite](https://github.com/vitejs/vite)

---

**Next step:** Apply the six resolutions from this ADR to the [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) implementation steps. No changes to the checklist file are made in this PR — this ADR is design-only.
