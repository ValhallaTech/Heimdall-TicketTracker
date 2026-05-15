# Proposal: Migrate the Heimdall frontend from Blazor Server to Svelte 5 / SvelteKit 2

**Status:** **Approved** (2026-05-15)
**Author:** Orchestrator (Copilot)
**Scope:** `Heimdall.Web` (Blazor host today; reduced to an API + static-asset host after migration), a new `Heimdall.Frontend` SvelteKit project, `tests/Heimdall.Web.Tests` (bUnit retirement).
**Decision required:** Should we replace the Blazor Server UI with a Svelte 5 / SvelteKit 2 frontend, and if so under which hosting topology, on what timeline, and against which authentication model?
**Depends on:** [`security-and-authorization.md`](./security-and-authorization.md) — specifically Phase 3 (OpenFGA ReBAC) and Phase 5 (API + tokens) must be merged before the cutover begins.

> This document is **research and planning only**. **No code, package, configuration, or DI changes are made in this PR.** A separate, follow-up PR (or series of PRs) will implement the chosen design once approved.

---

## 1. Why we're looking at this

Three forces motivate the question:

1. **Reactive-framework fit.** Heimdall's screens are interactive, list-heavy, and trend toward real-time updates (ticket queues, audit feeds, admin tuple management). Svelte 5's runes (`$state`, `$derived`, `$effect`) express that workload more directly than Blazor's component lifecycle + `StateHasChanged()` re-renders driven over a SignalR circuit.
2. **Shipping cost.** Blazor Server's runtime cost is dominated by per-circuit state, sticky sessions, and the DOM-diff payload over WebSocket. A SvelteKit SPA/SSR build is a static bundle plus a tiny Node renderer; bundle sizes are typically an order of magnitude smaller than Blazor WebAssembly and an order of magnitude *cheaper* server-side than Blazor Server because state lives in the browser, not on the server.
3. **Tooling and ecosystem.** The Svelte ecosystem (Vite, Vitest, `@testing-library/svelte`, Playwright, `eslint-plugin-svelte`, `prettier-plugin-svelte`) is mature, JS/TS-native, and aligns with the org's existing Yarn 4 + ESLint + Prettier + Jest toolchain already present in `src/Heimdall.Web/`. The official **Svelte MCP server** (https://mcp.svelte.dev/mcp, source at https://github.com/sveltejs/ai-tools) is now wired into the Frontend Expert agent, giving live documentation access for Svelte 5 runes and SvelteKit 2 APIs during code review and authoring.

What is **not** motivating this: dissatisfaction with Blazor's correctness or Microsoft's roadmap. Blazor Server works. The migration is justified by ecosystem fit, runtime cost, and developer ergonomics — not by a defect.

## 2. Current state

The current frontend is **Blazor Server**, hosted by `src/Heimdall.Web` (`Microsoft.NET.Sdk.Web`, `net10.0`, `BlazorDisableThrowNavigationException=true`). Concretely:

| Concern                | Location / mechanism                                                                                  |
| ---------------------- | ----------------------------------------------------------------------------------------------------- |
| Routing                | `src/Heimdall.Web/Components/Routes.razor` + `@page` directives.                                      |
| Layout                 | `src/Heimdall.Web/Components/Layout/*` (`MainLayout.razor`, nav, footer).                             |
| Pages                  | `src/Heimdall.Web/Components/Pages/*` — `Login`, `Register`, `ForgotPassword`, `ForgotPasswordConfirmation`, `Register­Confirmation`, `ResetPassword`, `NewTicket`, `Tickets`, `TicketEdit`, `Teams/*` (`Queue`, `TicketQueueTable`), `Admin/*` (`AdminNav`, `AuditFeed`, `Hierarchy`, `Memberships`, `Queues`, `TicketAccess`, `TicketAccessIndex`), `Account/*` (`MfaChallenge`, `MfaDisable`, `MfaRecovery`, `MfaRecoveryCodes`, `MfaRecoveryCodesRegenerate`, `MfaSetup`), `AccessDenied`, `Error`, `NotFound`, `Splash`. |
| Transport              | SignalR circuit per browser tab; server-rendered HTML on first hit, then diffs over WebSocket.        |
| AuthN                  | ASP.NET Core Identity + cookie auth; Dapper-backed Identity stores; Data Protection keys persisted to PostgreSQL via `AspNetCore.DataProtection.CustomStorage.Dapper.PostgreSQL`. See `src/Heimdall.Web/Program.cs`. |
| Session revalidation   | `RevalidatingServerAuthenticationStateProvider` against `SecurityStamp`, with a short revalidation interval so revocations propagate across long-lived circuits ([`security-and-authorization.md`](./security-and-authorization.md) §3.4). |
| AuthZ                  | Policy-backed `[Authorize]` resolved through OpenFGA `Check()` (Phase 3 of the security proposal). UI guards are server-side, not client-side.                                                                |
| MFA UI                 | Server-rendered TOTP enrolment with `QRCoder` producing the QR PNG inline on the server.              |
| Assets                 | Bootstrap 5 + Font Awesome pipeline-built under `src/Heimdall.Web/wwwroot/` via Yarn 4.14.1 + corepack; ESLint + Prettier + Jest already configured (`eslint.config.cjs`, `.prettierrc.json`, `jest.config.cjs`, `__tests__/`). |
| DI                     | Autofac with `ApplicationModule` + `DalModule`.                                                       |
| Logging                | Serilog with Postgres sink.                                                                           |
| Tests                  | `tests/Heimdall.Web.Tests` uses **bUnit 2.7.2** for component tests under `Components/Pages/**` (`LoginPageTests`, `RegisterTests`, `ForgotPasswordTests`, `ResetPasswordTests`, `TicketsPageTests`, `TicketEditTests`, `Admin/*`, `Teams/*`, `ConfirmationPageTests`, `AccessDeniedTests`, `SimplePageTests`), `Components/Layout/LayoutTests`, and `Components/Authentication/RedirectToLoginTests`. Other test projects (BLL, DAL, Core, pgtap) are unaffected. |

**Implication:** the UI surface is small and well-bounded (≈25 pages plus layout and auth glue), the auth model is mature, and there are no Blazor-only features in production aside from `RevalidatingServerAuthenticationStateProvider` and server-rendered QR generation. The migration is large in line count but contained in concept.

## 3. Target state

Svelte 5 with runes, on SvelteKit 2, using `@sveltejs/adapter-node`. Two viable hosting topologies were considered; one is recommended.

### 3.1 Topology A — Co-hosted, ASP.NET Core stays the origin

`Heimdall.Web` remains the public origin and continues to terminate auth (cookie-based, exactly as today). The SvelteKit project (`Heimdall.Frontend/`) is built (`vite build` via `adapter-node`) and either:

- **A.1** — served as **static assets** from `Heimdall.Web/wwwroot/app/` for fully-client-rendered routes, with `Heimdall.Web` exposing JSON endpoints for data, **or**
- **A.2** — run as a **Node sidecar** that `Heimdall.Web` reverse-proxies (YARP or `UseProxy`) for SSR-rendered routes, while still owning the cookie.

In both A.1 and A.2 the **browser only ever talks to one origin**, so the existing Identity cookie + anti-forgery + `SecurityStamp` revalidation continue to work unchanged. OpenFGA `Check()` calls happen server-side (either in `Heimdall.Web` minimal-API handlers or in SvelteKit `+page.server.ts` / `hooks.server.ts` that calls back into `Heimdall.Web` over a trusted internal channel).

### 3.2 Topology B — Standalone SvelteKit origin, `Heimdall.Web` reduced to an API

SvelteKit is deployed on its own origin (e.g., `app.heimdall.example`) and `Heimdall.Web` becomes a pure JSON API at a sibling origin (e.g., `api.heimdall.example`). Because the browser now crosses an origin boundary, cookie auth becomes operationally painful (third-party cookie restrictions, SameSite, CORS preflights on every mutating call); the practical answer is to switch the UI to **JWT bearer + refresh-token rotation**, which is exactly what Phase 5 of [`security-and-authorization.md`](./security-and-authorization.md) already plans to ship.

> **Approved deployment note (2026-05-15):** The approved deployment is **single-container** — one Docker image runs both the SvelteKit `adapter-node` process (external ingress) and `Heimdall.Web` (internal API) side by side. The browser communicates with **a single external origin** (the SvelteKit endpoint); `Heimdall.Web` is reachable only via an internal loopback channel (e.g. `http://localhost:<port>`) and is never directly addressed by the browser. This eliminates browser-facing CORS concerns: CORS headers on `Heimdall.Web` apply only to the internal server-to-server channel between the SvelteKit process and the ASP.NET Core process, not to any browser request. SameSite cookie semantics are also unaffected — the refresh token cookie is set by the single SvelteKit origin. The logical Topology B split (clean API/UI code separation, JWT bearer auth) is preserved; the deployment topology collapses the two origins into one service. See §11.2 for the resolution record and [`docs/implementation/phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 17 for the Dockerfile/render.yaml implementation plan.

### 3.3 Recommendation: **Topology B, but only after Phase 5 ships.**

Reasoning:

1. **Topology B is the design we want long-term.** A clean API/UI split is a stated goal of Phase 5 (API + tokens); it unlocks mobile / CLI / third-party consumers without privileging the web client. Topology A is functionally a halfway house that preserves the current cookie+circuit model but inherits its sticky-session and revalidation complexity.
2. **Topology A is the only safe option *before* Phase 5.** If we cut over to Svelte before JWT + refresh tokens exist, we'd either have to ship Topology A and then re-migrate, or invent a one-off bearer scheme just for the SvelteKit frontend. Both are wasted work.
3. **Therefore the cutover is sequenced behind Phase 5.** This proposal slots in as **Phase 6** of the security-and-authorization rollout, after OpenFGA (Phase 3) and after API + tokens (Phase 5). The pre-existing Phase 6 (Admin UI) shifts to Phase 7.

`adapter-node` is the right SvelteKit adapter for both topologies — it produces a standalone Node server that handles SSR and form actions, and is the only adapter that cleanly supports our deployment targets (Render, container hosts) without locking us to a specific serverless platform. `adapter-static` is rejected because we want server-side data loading for authenticated routes; `adapter-cloudflare` / `adapter-vercel` are rejected to avoid platform lock-in.

## 4. Authentication impact

### 4.1 Under Topology A (rejected, but documented for completeness)

Cookie auth, anti-forgery, and `SecurityStamp` revalidation continue to function unchanged. SvelteKit's `hooks.server.ts` is given the cookie by the browser (same-origin), validates it against `Heimdall.Web` via an internal endpoint or shared Data Protection key, and exposes `event.locals.user` to `+page.server.ts` and `+layout.server.ts`. OpenFGA `Check()` is wrapped in a server-only helper imported from SvelteKit server modules. No new threat surface beyond what Blazor Server already has.

### 4.2 Under Topology B (recommended)

The flow becomes:

1. Browser hits SvelteKit origin. `+layout.server.ts` reads an **`HttpOnly`, `Secure`, `SameSite=Lax`** cookie containing the refresh token (issued by `Heimdall.Web` at the SvelteKit origin via a same-site `/auth/callback` after login — this keeps the refresh token out of JS). The access token is held only in memory in the SvelteKit server process per request and never sent to the browser as JS-readable state.
2. SvelteKit `hooks.server.ts` exchanges the refresh token (or a still-valid access token cached in `event.locals`) for the user identity by calling `Heimdall.Web` over a trusted server-to-server channel. The result, including OpenFGA-derived permissions, is attached to `event.locals` for downstream loaders.
3. Mutations from the browser go to **SvelteKit form actions** (`+page.server.ts` `actions`) which then call `Heimdall.Web`'s API with the access token. The browser never holds a bearer token; the browser only holds the refresh cookie.
4. **`SecurityStamp` revalidation** is preserved: the access-token verification path in `Heimdall.Web` continues to honour `SecurityStamp` (or its token-era equivalent — short access-token TTL + refresh-token rotation is the Phase 5 design). Revocation latency is bounded by the access-token TTL, same as Phase 5 already commits to.
5. **Anti-forgery** is enforced by SvelteKit form actions natively (origin + double-submit token); CSRF from a third-party origin against `Heimdall.Web`'s API is mitigated by strict CORS (only the SvelteKit origin is allow-listed) plus bearer-only authentication on the API.

This design **does not bypass or weaken any decision** in [`security-and-authorization.md`](./security-and-authorization.md). The refresh-cookie pattern is consistent with Phase 5's "rotating, family-bound refresh tokens" decision (§5.3 of that doc); we just put the refresh token in an `HttpOnly` cookie on the SvelteKit origin rather than in JS. OpenFGA `Check()` continues to be the single authorization mechanism, called from server-side SvelteKit loaders/actions and from the API endpoints.

### 4.3 What stops working without Phase 5

If we attempted Topology B without Phase 5:

- No JWT signing keys (no JWKS, no RS256/ES256 keys, no key rotation).
- No `refresh_tokens` table, no family-replay detection, no Redis denylist.
- No `JWT bearer` scheme on `Heimdall.Web`.

This is why the cutover is sequenced **after** Phase 5. Topology B without Phase 5 would require inventing an interim bearer scheme that we'd then throw away — pure waste.

## 5. Tooling

The org's standard Frontend Expert toolchain applies. No version pinning appears below — **Renovate Bot owns all package versions** per the repository's stored policy. Numbers given are **non-binding reference points** observed at proposal time only.

- **Svelte MCP server** — https://mcp.svelte.dev/mcp (source: https://github.com/sveltejs/ai-tools). The Frontend Expert agent (`agents/frontend-expert.md`) is wired to this server and must consult `list-sections` + `get-documentation` before authoring any `.svelte` component or SvelteKit route. This replaces guessing against training data for Svelte 5 runes and SvelteKit 2 APIs.
- **`eslint-plugin-svelte`** — https://github.com/sveltejs/eslint-plugin-svelte. Integrates into the existing flat `eslint.config.cjs` (already in `src/Heimdall.Web/`) by adding the plugin's recommended config to the `extends` chain and adding `*.svelte` to the file globs. No structural change to the existing JS/TS lint pipeline is required.
- **`prettier-plugin-svelte`** — https://github.com/sveltejs/prettier-plugin-svelte. Integrates into the existing `.prettierrc.json` per the official Prettier plugin documentation (`"plugins": ["prettier-plugin-svelte"]` + an `overrides` entry pinning `*.svelte` to the `svelte` parser). Again, no structural change.
- **Vite** — provided transitively by SvelteKit; no separate decision to make.
- **Vitest** — replaces bUnit and complements the existing Jest config. Vitest is preferred over Jest specifically for the SvelteKit subtree because Vitest shares Vite's module resolver and natively understands `$lib`, `$app/*` and `.svelte` imports without extra transforms. Jest stays where it is for non-Svelte JS under `src/Heimdall.Web/__tests__/`.
- **`@testing-library/svelte`** — component-test API used from Vitest. Standard `render` / `screen` / `userEvent` patterns.
- **Playwright** — recommended for end-to-end / smoke tests, unchanged from the org default. No bUnit equivalent today; this is a *net new* layer.
- **Component library:** `bits-ui` for accessibility primitives plus `shadcn-svelte` for styled components built on top of them. `shadcn-svelte` brings Tailwind CSS in as a transitive design-system dependency. Bootstrap 5 / Font Awesome are deprecated within the SvelteKit project once shadcn-svelte parity is reached; the Razor host retains them until the Blazor retirement step.

**Reference baselines (non-binding, Renovate-managed):** Svelte v5.55.7, SvelteKit 2.60.1, Vitest v4.1.6 were the versions cited at proposal-drafting time. They appear here only so reviewers can match docs against what was current when this was written; **the implementation PR must not pin to these numbers** — Renovate will land whatever is current at implementation time, and the proposal explicitly does not gate on a version.

## 6. Testing migration

bUnit is retired in favour of Vitest + `@testing-library/svelte`. The cutover happens **page by page** alongside the Svelte port — a Razor page and its bUnit test are removed in the same PR as the SvelteKit equivalent and its Vitest tests land. No "big bang" test rewrite.

### 6.1 bUnit inventory to retire

Every file under `tests/Heimdall.Web.Tests/Components/**` needs a Svelte equivalent:

- `Components/Pages/LoginPageTests.cs`
- `Components/Pages/RegisterTests.cs`
- `Components/Pages/ForgotPasswordTests.cs`
- `Components/Pages/ResetPasswordTests.cs`
- `Components/Pages/ConfirmationPageTests.cs` (covers both `ForgotPasswordConfirmation` and `RegisterConfirmation`)
- `Components/Pages/AccessDeniedTests.cs`
- `Components/Pages/SimplePageTests.cs` (`Error`, `NotFound`, `Splash`)
- `Components/Pages/TicketsPageTests.cs`
- `Components/Pages/TicketEditTests.cs`
- `Components/Pages/Teams/*` (matching `Queue.razor`, `TicketQueueTable.razor`)
- `Components/Pages/Admin/*` (matching `AdminNav.razor`, `AuditFeed.razor`, `Hierarchy.razor`, `Memberships.razor`, `Queues.razor`, `TicketAccess.razor`, `TicketAccessIndex.razor`)
- `Components/Layout/LayoutTests.cs`
- `Components/Authentication/RedirectToLoginTests.cs`

Each gets a sibling Vitest spec in `Heimdall.Frontend/src/**/*.test.ts` covering the same assertions: render, role-gated visibility, form-validation surfaces, error states, and a11y attributes.

### 6.2 Quality gates (unchanged from org defaults)

- ≥ 80% statement coverage on the SvelteKit project (Vitest `--coverage`).
- `eslint .` clean, including `eslint-plugin-svelte` rules.
- `prettier --check .` clean.
- No `any` in TypeScript; explicit `unknown` + narrowing where needed.
- All async I/O uses `async`/`await`; no floating promises.
- Playwright smoke suite covers the critical-path flows: login, MFA challenge, create ticket, edit ticket, admin tuple add/remove.

## 7. UI parity inventory

| Razor page                                                       | SvelteKit route                                                                       |
| ---------------------------------------------------------------- | ------------------------------------------------------------------------------------- |
| `Components/Pages/Splash.razor`                                  | `routes/+page.svelte`                                                                 |
| `Components/Pages/Login.razor`                                   | `routes/login/+page.svelte` + `+page.server.ts` action                                |
| `Components/Pages/Register.razor`                                | `routes/register/+page.svelte` + `+page.server.ts` action                             |
| `Components/Pages/RegisterConfirmation.razor`                    | `routes/register/confirmation/+page.svelte`                                           |
| `Components/Pages/ForgotPassword.razor`                          | `routes/account/forgot-password/+page.svelte` + action                                |
| `Components/Pages/ForgotPasswordConfirmation.razor`              | `routes/account/forgot-password/confirmation/+page.svelte`                            |
| `Components/Pages/ResetPassword.razor`                           | `routes/account/reset-password/+page.svelte` + action                                 |
| `Components/Pages/Account/MfaSetup.razor`                        | `routes/account/mfa/setup/+page.svelte` + `+page.server.ts` (loads QR via API)        |
| `Components/Pages/Account/MfaChallenge.razor`                    | `routes/account/mfa/challenge/+page.svelte` + action                                  |
| `Components/Pages/Account/MfaDisable.razor`                      | `routes/account/mfa/disable/+page.svelte` + action                                    |
| `Components/Pages/Account/MfaRecovery.razor`                     | `routes/account/mfa/recovery/+page.svelte` + action                                   |
| `Components/Pages/Account/MfaRecoveryCodes.razor`                | `routes/account/mfa/recovery-codes/+page.svelte`                                      |
| `Components/Pages/Account/MfaRecoveryCodesRegenerate.razor`      | `routes/account/mfa/recovery-codes/regenerate/+page.svelte` + action                  |
| `Components/Pages/Tickets.razor`                                 | `routes/tickets/+page.svelte` + `+page.server.ts` load                                |
| `Components/Pages/NewTicket.razor`                               | `routes/tickets/new/+page.svelte` + action                                            |
| `Components/Pages/TicketEdit.razor`                              | `routes/tickets/[id]/+page.svelte` + `+page.server.ts` load + action                  |
| `Components/Pages/Teams/Queue.razor`                             | `routes/teams/[teamId]/queue/+page.svelte`                                            |
| `Components/Pages/Teams/TicketQueueTable.razor`                  | `lib/components/TicketQueueTable.svelte` (shared component, not a route)              |
| `Components/Pages/Admin/AdminNav.razor`                          | `routes/admin/+layout.svelte` (nav lives in the layout)                               |
| `Components/Pages/Admin/AuditFeed.razor`                         | `routes/admin/audit/+page.svelte`                                                     |
| `Components/Pages/Admin/Hierarchy.razor`                         | `routes/admin/hierarchy/+page.svelte`                                                 |
| `Components/Pages/Admin/Memberships.razor`                       | `routes/admin/memberships/+page.svelte` + actions                                     |
| `Components/Pages/Admin/Queues.razor`                            | `routes/admin/queues/+page.svelte`                                                    |
| `Components/Pages/Admin/TicketAccessIndex.razor`                 | `routes/admin/ticket-access/+page.svelte`                                             |
| `Components/Pages/Admin/TicketAccess.razor`                      | `routes/admin/ticket-access/[ticketId]/+page.svelte` + actions                        |
| `Components/Pages/AccessDenied.razor`                            | `routes/access-denied/+page.svelte` (and `+error.svelte` for 403)                     |
| `Components/Pages/Error.razor`                                   | `routes/+error.svelte` (root error boundary)                                          |
| `Components/Pages/NotFound.razor`                                | `routes/+error.svelte` branch on `status === 404`                                     |
| `Components/Layout/MainLayout.razor` + nav/footer                | `routes/+layout.svelte` + `lib/components/{NavBar,Footer}.svelte`                     |

**`Heimdall.Frontend` uses `bits-ui` + `shadcn-svelte` (Tailwind CSS) as its component layer — Bootstrap 5 and Font Awesome are not imported into the SvelteKit project.** The Razor host (`src/Heimdall.Web/`) retains Bootstrap 5 and Font Awesome until the Blazor retirement step (Phase 6.8, step 18), at which point a per-component parity audit confirms shadcn-svelte coverage before Bootstrap is removed. Material Design principles (elevation, motion, typography) are applied within Svelte components per the Frontend Expert standards.

## 8. Risks and mitigations

| Risk                                                                                       | Mitigation                                                                                                                                                                                                                                                                       |
| ------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Auth regression at cutover (cookie → bearer)                                              | Deferred behind Phase 5; SvelteKit goes live against the same JWT/refresh design Phase 5 commits to. The Razor UI stays up during the port so we can switch routes incrementally behind a reverse proxy.                                                                          |
| OpenFGA guard regression                                                                   | The single `Check()` adapter remains in `Heimdall.Web` server-side. SvelteKit `+page.server.ts` and `hooks.server.ts` call it; no policy evaluation moves to the browser. Existing OpenFGA integration tests are unchanged.                                                       |
| SignalR/streaming features lose their transport                                            | Blazor's circuit-driven push is replaced with **SSE** (server-sent events from `Heimdall.Web` for audit feeds, ticket queue updates) or **WebSocket** where bi-directional. SvelteKit handles both natively. Inventory the actual push surfaces before the port begins.            |
| Anti-forgery weakens                                                                       | SvelteKit form actions provide native CSRF protection (origin check + same-site cookies). The Razor anti-forgery middleware on `Heimdall.Web`'s API is preserved.                                                                                                                |
| SEO / first-paint regression                                                               | SvelteKit SSR via `adapter-node` produces server-rendered HTML on first hit, same as Blazor Server. Lighthouse first-paint is expected to improve, not regress, because the WebSocket-driven hydration of Blazor is replaced with a smaller static bundle.                       |
| Bundle hosting / CDN                                                                       | `adapter-node` outputs `build/client/` (static, hash-named, cache-forever) and `build/server/` (the Node entry). The static subtree can be served from a CDN if desired; not required for v1.                                                                                    |
| MFA UI parity (QRCoder is server-side today)                                              | Keep QR generation on the server. The SvelteKit MFA-setup loader calls a `Heimdall.Web` endpoint that returns a base64 PNG (existing `QRCoder` usage) and the Svelte component renders it. No client-side TOTP library is introduced.                                              |
| Bootstrap / Font Awesome pipeline disruption                                               | The existing Yarn 4 + corepack pipeline in `src/Heimdall.Web/` is unchanged for the Razor host throughout the migration. `Heimdall.Frontend` does **not** import Bootstrap; it uses `bits-ui` + `shadcn-svelte` (Tailwind CSS) exclusively. Bootstrap 5 and Font Awesome are removed from `src/Heimdall.Web/` only in Phase 6.8 step 18, after a per-component parity audit confirms shadcn-svelte coverage.                                                                                     |
| Build pipeline shift (SDK type change for `Heimdall.Web`)                                  | Long-term, `Heimdall.Web` may revert from `Microsoft.NET.Sdk.Web` Blazor-host shape to a slimmer API host. That change is **out of scope** for this proposal and happens in the cleanup PR after the Blazor UI is removed.                                                       |
| Contributor onboarding (Razor → Svelte)                                                    | Frontend Expert agent is wired to the Svelte MCP server; `agents/frontend-expert.md` documents the runes-only / SvelteKit-2-only stance. The org docs already require following Svelte 5 conventions. Pair-programming the first few page ports is the cheapest mitigation.        |
| Two frontends running simultaneously during the port                                       | Time-bounded: each phase below ships a coherent slice. The reverse proxy on `Heimdall.Web` routes specific paths to Razor or to SvelteKit until the final cutover, after which Razor is removed.                                                                                  |
| bUnit coverage drops to zero between port and Vitest port-in                               | Mitigation is procedural: a Razor page is removed only in the same PR that lands its Svelte equivalent **and** its Vitest spec. CI keeps a coverage floor.                                                                                                                       |
| Tailwind CSS arrives as a transitive design-system dependency via shadcn-svelte            | Accepted in the design-system decision (§11.5); contained within `Heimdall.Frontend` only. The Razor host keeps Bootstrap until retirement. Tailwind config is committed at the repo root for `Heimdall.Frontend` so it's discoverable. |

## 9. Phased implementation plan

This plan is written so it can be lifted into [`security-and-authorization.md`](./security-and-authorization.md) §9.3 as the new **Phase 6**. Phases inside are strictly ordered.

1. **Scaffold `Heimdall.Frontend/`.** New SvelteKit project at the repo root, using `adapter-node`. Add `eslint-plugin-svelte` + `prettier-plugin-svelte` to a shared config that extends the existing `eslint.config.cjs` and `.prettierrc.json`. No Razor changes.
2. **Dev proxy.** Configure `Heimdall.Web` to reverse-proxy `/app/*` to the SvelteKit dev server (`vite dev`) in `Development` only, so the two frontends run side-by-side locally without breaking cookie auth.
3. **Wire `hooks.server.ts` + a thin auth client.** SvelteKit calls `Heimdall.Web` over a trusted server-to-server channel using Phase 5's bearer tokens, populates `event.locals.user`, and exposes a typed OpenFGA `check(...)` helper to loaders.
4. **Port the unauthenticated public pages.** `Splash`, `AccessDenied`, `Error`, `NotFound`, `Login`, `Register`, `RegisterConfirmation`, `ForgotPassword`, `ForgotPasswordConfirmation`, `ResetPassword`. Each lands with its Vitest spec; the matching Razor file and bUnit test are removed in the same PR.
5. **Port the authenticated ticket flows.** `Tickets`, `NewTicket`, `TicketEdit`, `Teams/Queue` + the `TicketQueueTable` shared component.
6. **Port the MFA flows.** Server endpoint exposes the existing `QRCoder` PNG; SvelteKit renders it. All six MFA pages (`MfaSetup`, `MfaChallenge`, `MfaDisable`, `MfaRecovery`, `MfaRecoveryCodes`, `MfaRecoveryCodesRegenerate`) land together so the flow is never half-Svelte/half-Razor.
7. **Port the admin tuple-management surface.** `AdminNav` (→ `+layout.svelte`), `AuditFeed`, `Hierarchy`, `Memberships`, `Queues`, `TicketAccessIndex`, `TicketAccess`. This is intentionally **last** because it is the most behaviour-rich surface and benefits from lessons learned in steps 4–6. Note that Phase 7 (the existing Admin UI work, renumbered) lands its functional features against whichever frontend is current at that time — if Phase 7 ships *before* this step, the Svelte port absorbs it.
8. **Flip default to SvelteKit.** Reverse proxy now routes all UI traffic to SvelteKit; Razor host stays mounted but unreachable for one release.
9. **Retire Blazor.** Remove `Components/`, `App.razor`, Razor packages, and the Blazor middleware from `Heimdall.Web/Program.cs`. Drop the `tests/Heimdall.Web.Tests` bUnit project (or repurpose it as a Razor-free integration-test project — to be decided at cleanup time). `Heimdall.Web.csproj` may shift from a Blazor-host SDK shape to a slimmer API host.
10. **Acceptance.** Vitest coverage ≥ 80% on `Heimdall.Frontend`; Playwright smoke suite green; OpenFGA integration tests unchanged and green; auth integration tests cover the bearer + refresh-cookie flow end-to-end; visual diff on critical pages reviewed by the Frontend Expert.

The authoritative ordered plan now lives in [`docs/implementation/phase-6-checklist.md`](../implementation/phase-6-checklist.md).

## 10. Viability verdict (recommendation)

> **YES, with conditions.** Migrate the Heimdall frontend from Blazor Server to Svelte 5 / SvelteKit 2 (`adapter-node`, Topology B), **but only after [`security-and-authorization.md`](./security-and-authorization.md) Phases 3 (OpenFGA) and 5 (API + tokens) have been merged.** Insert this work as the new **Phase 6** of the security-and-authorization rollout; renumber the existing Phase 6 (Admin UI) to **Phase 7**.

The recommendation is grounded in:

1. **Ecosystem fit.** Svelte 5 + SvelteKit 2 matches the shape of Heimdall's workload (list-heavy, real-time-leaning, JWT-bearer-friendly) better than Blazor Server's circuit model. Vitest + `@testing-library/svelte` + Playwright is a saner test pyramid than bUnit, which is a high-friction single layer.
2. **Team familiarity.** The repo already standardises on Yarn 4 + ESLint + Prettier + Jest in the Blazor host; the org docs commit to Svelte 5 runes and SvelteKit 2 with Bootstrap 5; the Frontend Expert has live MCP-backed Svelte docs. The ramp is real but bounded.
3. **Security-flow disruption.** Doing the cutover **before** Phase 5 would force inventing a throwaway bearer scheme or shipping Topology A and re-migrating. Doing it **after** Phase 5 lets Topology B land on top of the design Phase 5 already commits to. The disruption is therefore **zero net new auth surface** at cutover — it reuses what Phase 5 ships.
4. **Test-coverage cost.** bUnit retirement is one-for-one against Svelte/Vitest specs, paid page-by-page; no coverage cliff. Playwright adds a *net new* end-to-end layer Blazor never had.
5. **Parallel-shipping feasibility.** The reverse-proxy + per-page-cutover plan in §9 lets Razor and SvelteKit coexist for the duration of the port. There is no flag day.
6. **Cost honesty.** This is a large piece of work — roughly 25 page ports plus the layout, auth glue, MFA, and admin tuple surfaces — and it pauses other UI feature work for its duration. The decision to do it should be made with that cost on the table. The recommendation is "yes" because the steady-state cost of operating two frontends, or of carrying Blazor Server into the API-first era Phase 5 unlocks, is higher than the one-time migration cost.

The case for **no**: if the team's bandwidth is committed to Phases 1–5 plus the existing Phase 6 (Admin UI) and there is no slack for a frontend rewrite, defer this proposal. It does not block anything else in the security-and-authorization roadmap; it builds *on top* of Phase 5. Nothing else is gated on doing this now.

## 11. Open questions and decision log

### Open questions

1. ~~Adapter choice — `adapter-node` is the recommendation; do we want to revisit if Render adds first-party SvelteKit support or if we move off Render entirely?~~ **Resolved 2026-05-15:** stay on `adapter-node` for portability.
2. ~~Deployment topology — single container (`Heimdall.Web` + Node sidecar) or two separate Render services?~~ **Resolved 2026-05-15:** single-container — one Docker image composes the ASP.NET Core API and the SvelteKit `adapter-node` Node server; the existing `Dockerfile` and `render.yaml` are updated in Phase 6, not split into two Render services.
3. ~~SSR vs CSR for ticket lists.~~ **Resolved 2026-05-15:** use SvelteKit's default hybrid rendering — initial-load SSR, hydration, then CSR for subsequent navigation. No per-route overrides at cutover.
4. ~~i18n.~~ **Resolved 2026-05-15:** English-only at cutover. No i18n library adopted in Phase 6.
5. ~~Design-system reuse.~~ **Resolved 2026-05-15:** adopt Svelte-native components — `bits-ui` for accessibility primitives and `shadcn-svelte` for styled components composed over them. Bootstrap 5 / Font Awesome are deprecated in the SvelteKit project and removed in the Blazor retirement step once parity is proven. `shadcn-svelte` brings Tailwind CSS in as a transitive design-system dependency.
6. ~~Frontend test ownership.~~ **Resolved 2026-05-15:** the JavaScript/TypeScript Unit Test Engineer agent owns Vitest spec authorship per `agents/javascript-unit-tests.md`; the Frontend Expert authors production Svelte/SvelteKit code only. Separation-of-concerns retained.

### Decision log

| Date       | Decision                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| ---------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2026-05-15 | Proposal drafted; new Phase 6 (Blazor → Svelte/SvelteKit migration) proposed; existing Phase 6 (Admin UI) renumbered to Phase 7. Recommendation: **YES with conditions** — Topology B (`adapter-node`, standalone SvelteKit origin + `Heimdall.Web` reduced to an API), gated on Phases 3 (OpenFGA) and 5 (API + tokens) being merged first. Svelte MCP server adopted for Frontend Expert; `eslint-plugin-svelte` + `prettier-plugin-svelte` integrate into existing flat configs; bUnit retires in favour of Vitest + `@testing-library/svelte`; Playwright added as a net-new end-to-end layer. No version pinning per Renovate ownership; user-cited baselines (Svelte v5.55.7, SvelteKit 2.60.1, Vitest v4.1.6) recorded as non-binding reference points only. |
| 2026-05-15 | **Proposal approved.** All six §11 open questions resolved (see inline strikethrough): `adapter-node` retained; single-container deployment (one Docker image); SvelteKit default hybrid rendering (SSR initial-load → hydration → CSR navigation); English-only at cutover; `bits-ui` + `shadcn-svelte` adopted with Bootstrap 5 / Font Awesome deprecation scheduled for the Blazor retirement step (Tailwind CSS enters as a transitive design-system dependency via shadcn-svelte); Vitest spec authorship owned by the JavaScript/TypeScript Unit Test Engineer agent. Implementation checklist tracked in [`docs/implementation/phase-6-checklist.md`](../implementation/phase-6-checklist.md). |

---

**Next step:** All §11 open questions are resolved and the proposal is approved. Schedule Phase 6.1 (SvelteKit scaffold) for after Phase 5.7 sign-off; track progress in [`docs/implementation/phase-6-checklist.md`](../implementation/phase-6-checklist.md).
