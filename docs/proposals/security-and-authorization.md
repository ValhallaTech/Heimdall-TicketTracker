# Proposal: Authentication, 2FA, and Authorization Strategy for Heimdall

**Status:** **Draft / Planning** (2026-05-02)
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.Web, Heimdall.BLL, Heimdall.DAL, Heimdall.Core (future PRs)
**Decision required:** What authentication, 2FA, authorization, and admin-management approach should Heimdall adopt to satisfy our goals of (1) security, (2) scalability, and (3) performance?
**Depends on:** *nothing.* This proposal is the foundation that the other two ([`team-collaboration.md`](./team-collaboration.md) and [`openfga.md`](./openfga.md)) build on.

> This document is **research and planning only**. **No code, package, configuration, or DI changes are made in this PR.** A separate, follow-up PR (or series of PRs) will implement the chosen design once approved.

---

## 1. Why we're looking at this

Heimdall today has **no authentication and no authorization layer**. Every visitor of the Blazor app is anonymous and can read/write tickets. This is acceptable for the seed/demo phase but is a hard blocker before:

- Multi-user / multi-tenant operation
- Hosting outside a trusted network
- Storing anything that isn't synthetic seed data
- Adding write-sensitive features (assignment, status changes, deletion, audit trails)

The user-stated goals, in priority order, are:

1. **Security** — defense-in-depth, sensible defaults, no foot-guns, well-understood threat model.
2. **Scalability** — must work for many users, many tickets, many tenants/groups, and (as a **future** requirement) a horizontally scaled web tier. The current `render.yaml` provisions a single free-plan web service with no scale-out configuration, so multi-instance hosting is something this proposal must *not preclude* rather than something we ship today.
3. **Performance** — authorization checks are on every request and inside hot paths (ticket lists, dashboards). The chosen model must be cheap to evaluate and cache-friendly.

This proposal evaluates the design space and recommends a path. References used:

- *Authorization Models: IBAC, RBAC, PBAC, ABAC, ReBAC, ACL, DAC, MAC* — <https://medium.com/@iamprovidence/authorization-models-ibac-rbac-pbac-abac-rebac-acl-dac-mac-b274aa5bdf08>
- *Token Authentication* (Radware Cyberpedia) — <https://www.radware.com/cyberpedia/application-security/token-authentication>

## 2. Current state

| Area                  | State                                                                                  |
| --------------------- | -------------------------------------------------------------------------------------- |
| Authentication        | **None.** No `AddAuthentication` / `UseAuthentication` in `Heimdall.Web/Program.cs`.   |
| Authorization         | **None.** No `[Authorize]`, no policies, no `UseAuthorization`.                         |
| User/identity store   | **None.** No `users`, `roles`, `groups`, `permissions` tables; no Identity migration.   |
| Session model         | Blazor Interactive Server circuit; antiforgery middleware is wired (`UseAntiforgery`).  |
| Secrets / keys        | Only DB + Redis URLs. No signing keys, no JWT issuer config.                            |
| Admin UI              | **None.** No admin pages/components.                                                   |
| Audit logging         | Serilog request logging only; no security-event audit log.                              |

This is a **greenfield** decision — there is no legacy auth surface to preserve, which widens our options.

## 3. Authentication

### 3.1 Why Blazor Server changes the conversation

Heimdall is **Blazor Interactive Server**. The browser holds a long-lived SignalR/WebSocket connection (the *circuit*). Two consequences:

1. **Cookie auth is the natural default.** The circuit is established over an HTTP request that carries cookies; the user's `ClaimsPrincipal` is captured at circuit-start and is available via `AuthenticationStateProvider` for the lifetime of the circuit.
2. **Pure JWT bearer auth is awkward.** Bearer tokens live in JS-accessible storage (or `Authorization` headers) and don't naturally flow through a SignalR circuit. JWT is a great fit for **APIs**, not for the interactive Blazor Server UI.

### 3.2 Options considered

| Option                                              | Fit for Blazor Server | Fit for future API | Verdict                                        |
| --------------------------------------------------- | --------------------- | ------------------ | ---------------------------------------------- |
| A. Cookie auth only                                 | Excellent             | Poor               | Good for UI; bad if/when we add a public API. |
| B. JWT bearer only                                  | Awkward               | Excellent          | Wrong default for an interactive server app.  |
| C. **Hybrid: cookie for UI, JWT for API**           | Excellent             | Excellent          | **Recommended.** Each transport gets its native scheme. |
| D. OIDC against an external IdP (Auth0, Entra, etc.) | Excellent             | Excellent          | Best-in-class security; worth keeping as a Phase-2 toggle (see §9). |

### 3.3 Identity store

Two realistic options:

1. **ASP.NET Core Identity (with a Dapper-backed store).** We get password hashing (PBKDF2 / Argon2-via-extension), lockout, email confirmation, 2FA primitives, and `UserManager`/`SignInManager` for free. The default EF Core store conflicts with our **Dapper-first** convention, so we would either (a) write a Dapper-backed implementation of the relevant `IUserStore<TUser>` / `IRoleStore<TRole>` family of interfaces, or (b) accept EF Core *only* for the Identity tables (a documented exception). Note that to support the feature set proposed elsewhere in this document — email confirmation, password reset, lockout, security stamps, TOTP, recovery codes — option 1a is **not** a "thin" wrapper: it requires implementing `IUserPasswordStore`, `IUserEmailStore`, `IUserLockoutStore`, `IUserSecurityStampStore`, `IUserTwoFactorStore`, `IUserAuthenticatorKeyStore`, `IUserTwoFactorRecoveryCodeStore`, `IUserClaimStore`, `IUserRoleStore`, and `IUserLoginStore`, plus all the corresponding persistence fields. This is a substantial subsystem and should be scoped/sequenced accordingly (see §9.3).
2. **Roll our own users table + password hashing.** Maximally aligned with our Dapper-first style, but we then own password hashing, lockout, token providers, and 2FA. **Not recommended** — this is a known foot-gun.

**Recommendation:** **ASP.NET Core Identity with a Dapper-backed store** (option 1a). We keep our Dapper-first stance and avoid re-implementing crypto. The Identity schema becomes a FluentMigrator migration like every other table.

### 3.4 Recommendation

- **Cookie authentication** (`AddAuthentication().AddCookie()`) for the Blazor UI, with secure defaults: `HttpOnly`, `Secure`, `SameSite=Lax` (or `Strict` where compatible), sliding expiration off, absolute expiration on, fixed cookie name, data-protection keys persisted (see §3.5).
- **Periodic auth-state revalidation on the circuit.** Cookie expiry alone is not sufficient on Blazor Server: the SignalR circuit caches the `ClaimsPrincipal` captured at circuit start, so disable / lockout / role-revocation / force-logout actions will not take effect until the user reconnects. Wire a `RevalidatingServerAuthenticationStateProvider` (or equivalent) that re-checks the user's `SecurityStamp` on a short interval (suggest 30 s) and tears the circuit down on mismatch. This is the canonical Blazor Server pattern and is required for the admin "force-logout" capability described in §8.1 to actually work.
- **JWT bearer** (`AddJwtBearer`) added later for any public API surface; same user store, different scheme.
- **ASP.NET Core Identity** for the user lifecycle, with a Dapper-backed user store so we keep our DAL convention.
- **Antiforgery** stays on (already wired).

### 3.5 Cross-cutting hardening

- **Data Protection keys** persisted to PostgreSQL or a mounted volume so cookies survive container restarts and horizontal scale-out (otherwise users are silently logged out on every redeploy). This is the single most-missed step in containerized Blazor deployments.
- **Sticky sessions / circuit affinity for horizontal scale.** Persisting Data Protection keys lets *cookies* round-trip between nodes, but a Blazor Server **circuit** is server-affine: the SignalR connection holds in-memory component state on a specific node. When we eventually scale out, the load balancer must pin a given client to its origin node (Render-supported session affinity, or a Redis-backed SignalR backplane combined with affinity at the LB), otherwise a reconnect after a scale event lands on a different node and silently loses interactive state. This is a deployment-side requirement, but it's called out here because it constrains the auth design (e.g. avoid putting purely-in-memory auth state on the circuit if we ever go affinity-less).
- **HSTS** is already on for non-Development.
- **Forwarded headers** (`UseForwardedHeaders`) configured for Render's reverse proxy so `Request.IsHttps` is correct.
- **CSP** header (Blazor Server has documented CSP requirements — `script-src` must allow the `_framework` script and any dynamic interop).
- **Brute-force protection.** Identity lockout covers the persistence side. For request-level throttling, **`AddRateLimiter` on a `/login` route is not sufficient on its own** when the login UI is an interactive Blazor component, because the actual credential check then happens over the SignalR circuit (a single WebSocket message), not a fresh HTTP POST. The implementation PR must therefore choose one of:
  1. Implement login as a **dedicated server-rendered POST endpoint** (Razor Page / minimal-API handler) outside the interactive circuit, and apply `AddRateLimiter` to that endpoint. This is the recommended pattern.
  2. *Or* throttle inside the `SignInManager` wrapper itself (per-IP + per-username token bucket), so the limit applies regardless of transport.
  Either way, the limit must key on **both** client IP *and* submitted username to mitigate credential stuffing without enabling username-based lockout DoS.

## 4. Two-factor / multi-factor authentication

Identity ships with TOTP support out of the box. Recommended menu:

| Factor                          | Library                                              | Recommendation                                      |
| ------------------------------- | ---------------------------------------------------- | --------------------------------------------------- |
| TOTP authenticator app          | `Microsoft.AspNetCore.Identity` built-in             | **Default 2FA** for all admin users.               |
| Recovery codes (one-time)       | Identity built-in                                    | **Required** when 2FA is enabled.                   |
| WebAuthn / passkeys             | `Fido2NetLib` (community) or hosted IdP              | **Phase 2** — strongest factor; great UX on modern browsers. |
| SMS OTP                         | Twilio / etc.                                        | **Avoid** — SIM-swap attacks, deliverability cost.  |
| Email "magic link"              | Identity token providers                             | OK for password reset; **not** as a primary 2FA factor. |

**Policy:** 2FA **required for any user with an admin role**; **optional but encouraged** for regular users (configurable per-tenant later). Enforce via an authorization policy (`RequireMfaPolicy`) plus a redirect filter that pushes admins without 2FA to the enrollment page on next login.

## 5. Token strategy (for the API surface)

Aligning with the Radware article's framing of token-based auth:

### 5.1 Token shape

- **Access token:** short-lived JWT (e.g. ~15 minutes), signed with **asymmetric** keys (RS256 or ES256). Asymmetric signing means resource servers can verify without holding the signing key — important if we later split services.
- **Refresh token:** opaque, server-issued, **stored hashed** (not plaintext) in PostgreSQL with `(user_id, token_hash, family_id, issued_at, expires_at, revoked_at)`. Long-lived (e.g. ~14 days, sliding) but **rotated on every use** and bound to a `family_id` so we can detect replay (Radware §"Token Theft and Replay").
- **ID token:** only if/when we move to OIDC.

### 5.2 Storage on the client

- **Web SPA / browser callers:** never `localStorage`. Prefer **HttpOnly cookies** for refresh tokens; access tokens kept in memory only.
- **Native / CLI callers (future):** OS secret store (Keychain / Credential Manager / libsecret).

### 5.3 Revocation

JWTs are stateless, so we need an explicit revocation strategy:

- **Refresh-token revocation table** (already implied above) — primary mechanism.
- **Short access-token TTL** so revocation latency is bounded (≤ access-token TTL).
- **Optional access-token denylist in Redis** for "log out everywhere now" semantics, keyed by `jti`, with TTL = remaining access-token lifetime.

### 5.4 Key management

- **JWT signing keys are *not* stored in ASP.NET Core Data Protection.** Data Protection's key ring is a symmetric, server-internal payload-protection mechanism — it is not a publishable asymmetric JWK source, and reusing it for JWT signing would either force a second key system later or make external verification impossible. Keep the two key rings separate:
  - **Data Protection** keys → cookies / antiforgery / TempData only (§3.5).
  - **JWT signing keys** → asymmetric (RSA / EC) key pairs stored in a dedicated `signing_keys` table (or, preferred when available, a managed KMS / vault) with rotation metadata: `(kid, alg, public_jwk, private_key_protected, created_at, not_before, not_after, retired_at)`.
- **Rotate** on a schedule (e.g. 90 days) and on suspected compromise. Multiple active keys overlap during rotation so existing tokens remain verifiable until they expire.
- Publish the **public** halves of all currently-trusted keys via a **JWKS endpoint** (`/.well-known/jwks.json`) keyed by `kid`; verifiers pick up new keys without redeploy.
- Private halves are stored encrypted-at-rest and never logged or checked into the repo.

## 6. Authorization model survey

The Medium article catalogues eight models. Here is each, with a Heimdall-specific fit assessment.

| Model     | One-line definition                                                                                 | Fits Heimdall?                                  | Notes |
| --------- | --------------------------------------------------------------------------------------------------- | ----------------------------------------------- | ----- |
| **IBAC**  | Identity-Based Access Control — permissions assigned directly to user identities.                   | ❌ Doesn't scale (per-user grants explode).      | Use only as a degenerate case of ACL. |
| **RBAC**  | Role-Based Access Control — users get roles; roles get permissions.                                 | ✅ Strong baseline.                              | Classic and well-understood. Good for "Admin / Agent / Viewer" coarse roles. |
| **PBAC**  | Policy-Based Access Control — permissions decided by policies (often code or a policy language).    | ✅ Native fit for ASP.NET Core.                  | `IAuthorizationRequirement` + `IAuthorizationHandler` is literally PBAC. |
| **ABAC**  | Attribute-Based Access Control — decisions from attributes of subject/resource/action/environment.  | ◐ Useful for fine-grained ticket rules.         | E.g. "agent can edit tickets where `ticket.assignee_id == user.id`". |
| **ReBAC** | Relationship-Based Access Control — decisions from a graph of relationships (Google Zanzibar).      | ◐ Powerful for sharing/teams/groups.            | Overkill today; valuable if we add cross-team sharing or external collaborators. |
| **ACL**   | Access Control List — per-resource list of (principal, permission) pairs.                           | ◐ Useful for one-off resource sharing.          | Tends to fragment; easier to express as ReBAC tuples. |
| **DAC**   | Discretionary — owner of a resource grants access at their discretion.                              | ◐ Implicit in "ticket creator can always edit". | Realised on top of RBAC/ReBAC, not as the primary model. |
| **MAC**   | Mandatory — central authority assigns labels (clearances) and enforces rules; users cannot delegate. | ❌ Wrong domain.                                 | Government/military classification; not a ticket tracker. |

**Hybrid recommendation:** **RBAC as the coarse layer + PBAC for fine-grained, attribute-aware rules**, with an **escape hatch toward ReBAC** (via an external authorization service — see §7) once we need real sharing semantics. This is the same trajectory taken by GitHub, Linear, Notion, and Figma.

Concretely:

- **Roles (RBAC):** `SystemAdmin`, `TenantAdmin`, `Agent`, `Submitter`, `Viewer`.
- **Permissions (granular):** `tickets.read`, `tickets.create`, `tickets.update`, `tickets.delete`, `tickets.assign`, `users.manage`, `roles.manage`, `audit.read`, …
- **Policies (PBAC):** `CanEditTicket = role.has(tickets.update) AND (ticket.assignee_id == user.id OR role.has(tickets.update.any))`.
- **Groups:** users belong to groups; groups carry roles. Grants are computed as `user.roles ∪ ⋃(group.roles for group in user.groups)`.

## 7. Built-in .NET vs external authorization libraries

### 7.1 Built-in: ASP.NET Core authorization

- **What it is:** `AddAuthorization`, policies, requirements, handlers, `[Authorize(Policy = "...")]`, `IAuthorizationService`.
- **Strengths:** zero new dependencies; first-class Blazor and minimal-API support; well-documented; runs in-process (no network hop).
- **Weaknesses:** policies live in C# — fine for RBAC + simple PBAC, painful for ReBAC graphs or for non-developer policy edits; no built-in policy-decision audit log; no central admin UI.

### 7.2 External candidates

| Library / service | Model strengths           | Hosting model                     | Notes for Heimdall                                                                         |
| ----------------- | ------------------------- | --------------------------------- | ------------------------------------------------------------------------------------------ |
| **OpenFGA**       | ReBAC (Zanzibar-style)    | Sidecar / external service        | CNCF sandbox, MIT-style license, gRPC + HTTP. Best-in-class for sharing/teams/orgs. Adds a network hop and an extra service to Render. |
| **Casbin**        | RBAC + ABAC + custom      | In-process (`Casbin.NET`)         | Apache 2.0. Policies in `.conf` + `.csv` (or DB adapter). Good if we want non-code policies but keep in-process. |
| **Cerbos**        | ABAC/PBAC                 | Sidecar (gRPC)                    | Apache 2.0. Policies in YAML, decoupled from app deploys. Great audit story; sidecar overhead. |
| **Permify**       | ReBAC + RBAC + ABAC       | Sidecar / external service        | Apache 2.0. Zanzibar-inspired, .NET SDK exists. Similar trade-off to OpenFGA. |
| **Oso (Polar)**   | RBAC + ABAC + ReBAC       | In-process library or Oso Cloud   | Library is open-source; Oso Cloud is commercial. Polar is a learning curve. |

### 7.3 Decision matrix against our goals

| Option                                | Security                              | Scalability                                   | Performance                                  | Operational cost              |
| ------------------------------------- | ------------------------------------- | --------------------------------------------- | -------------------------------------------- | ----------------------------- |
| **Built-in (RBAC + PBAC)**            | ★★★★ (no new attack surface)          | ★★★ (limited by app process / DB lookups)     | ★★★★★ (in-process, no hop)                   | ★★★★★ (nothing new to run)    |
| **Casbin (in-process)**               | ★★★★                                  | ★★★                                           | ★★★★ (cached evaluator)                      | ★★★★ (one library)            |
| **OpenFGA / Permify (sidecar)**       | ★★★★ (purpose-built, audited)         | ★★★★★ (designed for hyperscale ReBAC)         | ★★★ (network hop, mitigated by client cache) | ★★ (extra service, secrets, monitoring) |
| **Cerbos (sidecar)**                  | ★★★★                                  | ★★★★                                          | ★★★ (network hop)                            | ★★                            |
| **Oso Cloud (SaaS)**                  | ★★★ (3rd-party data plane)            | ★★★★                                          | ★★★                                          | ★★ (vendor lock + cost)       |

### 7.4 Recommendation

- **Phase 1 (now):** **Built-in ASP.NET Core authorization** — RBAC + PBAC. Cheapest path to "secure by default", best performance, no new infra.
- **Phase 2 (when sharing/teams arrive):** Re-evaluate **OpenFGA** *or* **Permify** as a ReBAC sidecar. Defer until a concrete sharing requirement justifies the operational cost.
- **Avoid:** rolling a custom policy DSL; SaaS authorization (vendor lock for a problem we don't yet have).

This sequencing keeps us aligned with the user-stated goal order: **security first** (proven primitives), **scalability** (we can adopt ReBAC later without rewriting RBAC), **performance** (in-process is fastest until proven otherwise).

## 8. Admin system

> **Deferred out of Phase 1.** With the introduction of [`team-collaboration.md`](./team-collaboration.md) and [`openfga.md`](./openfga.md), the admin surface is no longer a roles/groups/permissions catalogue — it becomes a **tuple-management surface** that returns *after* OpenFGA lands. Detailed implementation steps live in [`openfga.md`](./openfga.md) (step 11 of its sequence). The text below is preserved as the original capability sketch and informs that later work; it is **not** a Phase 1 deliverable.

A first-class admin surface inside the Blazor app, gated by a `RequireRole("SystemAdmin")` policy and 2FA.

### 8.1 Capabilities (MVP)

- **Users:** list / search / invite / disable / force-logout / reset-MFA.
- **Groups:** create / rename / delete; assign users to groups.
- **Roles:** view the catalogue of system roles (seeded, not user-editable in MVP); assign roles to users *or* groups.
- **Permissions:** read-only catalogue (developer-defined; not editable from the UI in MVP — this prevents accidental privilege escalation).
- **Audit log:** view security events (login, logout, MFA enrol/disable, role grant/revoke, permission denial). Append-only, written to PostgreSQL (and optionally streamed to Serilog/OpenTelemetry).

### 8.2 Schema sketch (to be detailed in the implementation PR)

```
users(id, email_ci UNIQUE, password_hash, mfa_enabled, locked_until, created_at, ...)
roles(id, name UNIQUE, description)
permissions(id, key UNIQUE, description)        -- e.g. "tickets.update"
role_permissions(role_id, permission_id)
user_roles(user_id, role_id)
groups(id, name UNIQUE, description)
group_members(group_id, user_id)
group_roles(group_id, role_id)
refresh_tokens(id, user_id, token_hash, family_id, issued_at, expires_at, revoked_at)
audit_events(id, occurred_at, actor_user_id, event_type, target_type, target_id, payload_jsonb)
```

All FluentMigrator migrations; all access via Dapper repositories (DAL convention preserved).

### 8.3 Hardening for the admin UI

- **Separate authorization policy** for every admin operation, not a single blanket `[Authorize(Roles = "SystemAdmin")]`.
- **Re-authentication prompt** ("sudo mode") for destructive actions (delete user, grant `SystemAdmin`).
- **Audit every mutation** with actor, target, before/after.
- **No silent self-elevation** — the only user who can grant `SystemAdmin` is another `SystemAdmin`. The first admin is bootstrapped via a one-shot env var or a documented migration step (never via the UI).

## 9. Recommendation summary and phased rollout

### 9.1 Recommended design (one-line per layer)

1. **AuthN:** ASP.NET Core Identity + cookie auth (UI), JWT bearer (future API), Dapper-backed Identity store, Data Protection keys persisted.
2. **MFA:** TOTP + recovery codes; required for admins; WebAuthn in Phase 2.
3. **AuthZ:** Phase 1 ships an **"authenticated-only"** gate as a deliberate placeholder. Real authorization is **ReBAC via OpenFGA** ([`openfga.md`](./openfga.md)), introduced after the team-collaboration data model ([`team-collaboration.md`](./team-collaboration.md)). RBAC+PBAC is **dropped** from Phase 1 because OpenFGA replaces it end-to-end; keeping it would mean shipping a roles/permissions/groups schema that gets migrated away.
4. **Tokens:** short-lived RS256/ES256 access tokens; opaque, hashed, rotating, family-bound refresh tokens; JWKS endpoint; Redis denylist for emergency revoke.
5. **Admin:** deferred to Phase 4, post-OpenFGA, as a tuple-management surface (see §8 banner and [`openfga.md`](./openfga.md) step 11).

### 9.2 Scoring against goals

| Goal              | How this design serves it                                                                                              |
| ----------------- | ---------------------------------------------------------------------------------------------------------------------- |
| **Security**      | Identity-managed crypto; 2FA required for admins; rotating refresh tokens with replay detection; deny-by-default policies; comprehensive audit log; no homemade auth. |
| **Scalability**   | Stateless access tokens; Redis-backed denylist; horizontal Blazor scale via persisted Data Protection keys; clear upgrade path to ReBAC (OpenFGA/Permify) without re-architecting RBAC. |
| **Performance**   | In-process authorization (no network hop) for the common case; **short-TTL** role/permission cache (request- or seconds-scoped, *not* circuit-scoped) so revocations propagate within seconds while still avoiding per-request DB hits; refresh-token check is one indexed PG read; access-token verification is local signature verify. Caching for the lifetime of a Blazor circuit is explicitly rejected because circuits can live for hours and would leave revoked privileges active until reconnect — see §3.4 (security-stamp revalidation). |

### 9.3 Phased rollout

The phases below are **strictly ordered** by dependency. Each numbered step inside a phase must complete before the next one starts. The proposal-level dependency chain is:

- **Proposal 1** (this doc) → **Proposal 2** ([`team-collaboration.md`](./team-collaboration.md)) → **Proposal 3** ([`openfga.md`](./openfga.md)) → MFA → API+tokens → Admin UI.

MFA, API+tokens, and Admin UI have been **repositioned to follow OpenFGA** so that policy evaluation and admin-surface tuple management share one mechanism instead of two.

#### Phase 1 — Authenticated foundation

The existing list mixed prerequisites with delivery. The order below is topological: each step's preconditions are all satisfied by earlier steps.

1. **`users` migration only.** FluentMigrator migration creating `users` (`id` UUID, `email` citext unique, `normalized_email`, `password_hash`, `security_stamp`, `concurrency_stamp`, `email_confirmed` bool, lockout fields, `created_at`, `updated_at`, `system_admin` bool). **No** `roles`, `permissions`, `groups`, `role_permissions`, `user_roles`, `group_*` tables — those were RBAC+PBAC, which is dropped.
2. **`audit_events` migration.** Columns: `actor_user_id`, `event_type`, `target`, `ip`, `user_agent`, `payload jsonb`, `occurred_at`. Lands **before** any auth code so every subsequent step (login, bootstrap, MFA, token issuance) can write to it from day one.
3. **Dapper-backed Identity stores** in `Heimdall.DAL`: `IUserStore<HeimdallUser>`, `IUserPasswordStore`, `IUserEmailStore`, `IUserSecurityStampStore`, `IUserLockoutStore`. Unit-tested against the migration from step 1. Honours the repo's Dapper-first DAL convention.
4. **`AddIdentityCore<HeimdallUser>()` + cookie auth + Data Protection key persistence to PostgreSQL** in `Heimdall.Web/Program.cs`, placed immediately after the existing Serilog / `AddDal()` block. No JWT scheme yet (that's Phase 3).
5. **`RevalidatingServerAuthenticationStateProvider`** wired against `SecurityStamp` per §3.4. Configure a short revalidation interval so revocations propagate quickly across long-lived Blazor circuits.
6. **`IEmailSender` abstraction in `Heimdall.Core` + two implementations** (`SmtpEmailSender`, `NoOpEmailSender`) registered by configuration. **No flow depends on this yet** — it's just the seam, deliberately introduced before any user-visible flow needs it.
7. **Server-rendered login/logout POST endpoint** (per §3.5) plus Blazor login/logout pages that post to it. Cookie issued on success. Audit events emitted to the table from step 2.
8. **`SystemAdmin` env-var bootstrap on startup.** If no users exist *and* `HEIMDALL_BOOTSTRAP_ADMIN_EMAIL` + `HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD` are set, create one user with `system_admin = true`. Idempotent — safe to re-run on restart, no-ops once a user exists.
9. **"Authenticated-only" gate applied globally.** `[Authorize]` on the Blazor router fallback + `RequireAuthorization()` on protected endpoints. **No role checks, no policy checks** — just authenticated. This is the **placeholder OpenFGA replaces** in [`openfga.md`](./openfga.md) step 9.
10. **Password reset + self-service register pages**, **gated on `IEmailSender` not being `NoOpEmailSender`** (a feature flag evaluated at startup based on configuration). Keeps Phase 1 independently shippable to environments without SMTP credentials provisioned.
11. **Phase 1 acceptance.** pgTAP tests for the new tables; xUnit tests for the Dapper Identity stores; integration test asserting that an unauthenticated request to a protected page redirects to login and an authenticated one succeeds.

Phase 1 ships **only** the data model and flows above. It does **not** ship organizations, teams, projects, tickets-with-owners, role catalogues, permission catalogues, or any authorization beyond "is this request authenticated?" Those are Proposals 2 and 3.

#### Phase 2 — Team collaboration data model

See [`team-collaboration.md`](./team-collaboration.md). Strictly data-and-domain; ships zero authorization changes; the Phase 1 "authenticated-only" gate still applies. Adding it as a phase here for sequencing — full step list lives in that doc.

#### Phase 3 — OpenFGA ReBAC

See [`openfga.md`](./openfga.md). Replaces the Phase 1 "authenticated-only" gate with policy-based `[Authorize]` resolved through OpenFGA `Check()` calls. Full step list lives in that doc.

#### Phase 4 — MFA

Content unchanged from earlier drafts; **repositioned to follow OpenFGA** so the admin-required check can be expressed as an OpenFGA `Check()` against `organization#admin` rather than against a coarse `system_admin` boolean.

1. TOTP enrolment migration (`user_authenticator_keys`, `user_recovery_codes`).
2. Implement `IUserAuthenticatorKeyStore`, `IUserTwoFactorStore`, `IUserTwoFactorRecoveryCodeStore`.
3. Enrolment + challenge UI; QR-code rendering server-side.
4. Policy: require MFA for users matching `Check(organization:heimdall#admin@user:X)` (since OpenFGA precedes this phase in the new order, the admin predicate is already a tuple lookup, not a column read).
5. WebAuthn deferred to a follow-up (unchanged from earlier drafts).

#### Phase 5 — API + tokens

Content unchanged from earlier drafts.

1. RS256/ES256 signing key generation + JWKS endpoint.
2. `refresh_tokens` migration (hashed token, `family_id`, `parent_id`, `replaced_by`, `revoked_at`).
3. JWT bearer scheme registration alongside cookie auth.
4. Refresh-token rotation with family-replay detection.
5. Redis denylist for emergency revoke.
6. First `/api/v1/...` endpoints, authorized via the **same** OpenFGA `Check()` adapter as the UI (no parallel policy stack).

#### Phase 6 — Admin UI

Returns *after* OpenFGA lands as a **tuple-management surface**, not a roles/groups surface. Detailed steps in [`openfga.md`](./openfga.md) step 11.

Each phase is independently shippable and independently testable.

## 10. Open questions and decision log

### Open questions

1. **Multi-tenant?** Is Heimdall single-tenant (one org per deployment) or multi-tenant (one deployment, many orgs)? Affects schema (`tenant_id` on every row) and group semantics.
2. **External IdP?** Do we want OIDC against Entra ID / Google / GitHub now, or only password+MFA initially? OIDC removes password storage entirely from our threat model.
3. **EF Core exception?** Are we willing to take ASP.NET Core Identity's default EF Core store *for the Identity tables only*, or do we invest in a Dapper-backed `IUserStore` from day one?
4. **Token TTLs?** Default proposal is 15 min access / 14 day refresh — confirm acceptable for the product.
5. **Audit retention?** How long do we retain `audit_events`? (Suggest: 365 days hot + cold archive.)
6. **Compliance posture?** Any contractual obligations (SOC 2, HIPAA, GDPR DPIA) that constrain choices?

### Decision log

| Date       | Decision                                                                 |
| ---------- | ------------------------------------------------------------------------ |
| 2026-05-02 | Proposal drafted; awaiting review.                                      |
| 2026-05-02 | Revised per review feedback: clarified Render scale-out is a *future* requirement (§1); reframed Dapper Identity store as a substantial subsystem rather than "thin" (§3.3); added periodic `SecurityStamp` revalidation on the circuit (§3.4); called out sticky-session/circuit-affinity requirement for horizontal scale (§3.5); replaced naive `/login` rate-limit guidance with a Blazor-Server-aware approach (§3.5); separated JWT signing keys from Data Protection and clarified JWKS source-of-truth (§5.4); replaced per-circuit role/permission caching with short-TTL caching (§9.2); made the email-delivery subsystem and a password-only onboarding fallback explicit Phase 1 prerequisites (§9.3). |
| 2026-05-04 | **Phase 1 retopologised and RBAC+PBAC dropped.** §9.3 rewritten as 11 strictly-ordered steps (`users` migration → `audit_events` → Dapper Identity stores → `AddIdentityCore` + cookie + Data Protection → `RevalidatingServerAuthenticationStateProvider` → `IEmailSender` seam → login POST + Blazor pages → env-var `SystemAdmin` bootstrap → "authenticated-only" gate → email-gated reset/register → tests). RBAC+PBAC and the `roles`/`permissions`/`groups` migrations removed from Phase 1 because OpenFGA ([`openfga.md`](./openfga.md)) replaces them end-to-end. §8 admin surface deferred to post-OpenFGA as a tuple-management surface. §9.1 line 3 (AuthZ) and §9.1 line 5 (Admin) updated accordingly. MFA, API+tokens, and Admin UI repositioned to phases 4–6, after OpenFGA, so policy evaluation goes through one mechanism. Added "Depends on: nothing" line in the header; sibling proposals [`team-collaboration.md`](./team-collaboration.md) and [`openfga.md`](./openfga.md) added in the same review. |

---

**Next step:** Review and resolve the open questions in §10, then open the Phase 1 implementation PR per §9.3.
