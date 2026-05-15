# Phase 1 — Authenticated Foundation: Implementation Checklist

**Status:** Implementation complete (steps 1–11 done on `copilot/outline-phase-1-steps`).
**Source of truth:** [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §9.3 Phase 1 (steps 1–11).
**Sequenced by:** [PR #25](https://github.com/ValhallaTech/Heimdall-TicketTracker/pull/25).
**Depends on:** *nothing.* Phase 1 is the foundation.

> This file is a **living tracking checklist** for the Phase 1 implementation PR.
> It does **not** restate the design — see the proposal for rationale, schema sketches, and decision log.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.

## Steps

- [x] **1. `users` migration only** — FluentMigrator; UUID PK, citext email, identity columns, lockout fields, `system_admin` bool. **No** RBAC/PBAC tables. 51-assertion pgTAP coverage.
- [x] **2. `audit_events` migration** — Lands before any auth code so every later step can write audit rows from day one. 36-assertion pgTAP coverage.
- [x] **3. Dapper-backed Identity stores** in `Heimdall.DAL` — `HeimdallUserStore` implements `IUserStore`, `IUserPasswordStore`, `IUserEmailStore`, `IUserSecurityStampStore`, `IUserLockoutStore`. 29 xUnit tests; 100% line coverage.
- [x] **4. `AddIdentityCore<HeimdallUser>()` + cookie auth + Data Protection** — `Heimdall.Web/Program.cs`, after `AddDal()`. DP keys persisted to PostgreSQL via `data_protection_keys` migration. No JWT yet.
- [x] **5. `RevalidatingServerAuthenticationStateProvider`** — Bound to `SecurityStamp`; 5-minute revalidation interval. 100% line coverage.
- [x] **6. `IEmailSender` seam** — Interface + DTO in `Heimdall.Core`; `MailKitEmailSender` + `NoOpEmailSender` in `Heimdall.BLL`; auto-selected at startup based on `Email:Smtp` config.
- [x] **7. Login/logout** — Server-rendered POST endpoint + Blazor SSR `/login` page. `IAuditEventWriter` (Dapper). `(ip|email)` rate limiter. 100% line coverage on `AccountEndpoints`.
- [x] **8. `SystemAdmin` env-var bootstrap** — `HEIMDALL_BOOTSTRAP_ADMIN_EMAIL` + `HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD`. Idempotent (create / promote / no-op). 100% line coverage.
- [x] **9. "Authenticated-only" gate** — Fallback `RequireAuthenticatedUser()` policy; `<AuthorizeRouteView>` + `RedirectToLogin` on the Blazor router; `[AllowAnonymous]` on public pages. Placeholder for OpenFGA.
- [x] **10. Password reset + self-service register pages** — Gated on `EmailFlowGate.IsActive` (and registration additionally gated on `Registration:Enabled` config). User-enumeration mitigated. New `password-reset` rate-limit policy.
- [x] **11. Phase 1 acceptance** — pgTAP migrations covered (steps 1, 2, 4); xUnit on Dapper Identity stores + bootstrap + auth state provider + endpoints + email gate; `Phase1AcceptanceTests` boots the real `Program` via `WebApplicationFactory` + `postgres:18-alpine` Testcontainer to verify the gate, end-to-end login, and audit-event persistence; coverage targets met across new files.

## Phase 1 sign-off

- **Postgres pinning:** `docker-compose.yml` + DAL/Web testcontainer fixtures all on `postgres:18-alpine`. The pgtap stack stays on `postgres:18.3` (Debian) because `postgresql-18-pgtap` is an apt package (no Alpine equivalent).
- **Mailgun SMTP smoke test:** **deferred to deployment time.** The CI/sandbox environment does not have outbound network access to `smtp.mailgun.org:587`, so the live SMTP path cannot be exercised here. After the first deploy with `Email__Smtp__*` env vars set, the operator should manually trigger a password-reset and a registration to confirm email delivery.
- **Phase 1 features ready:** cookie auth (Identity + cookie scheme), Data Protection key persistence, idempotent SystemAdmin bootstrap, Dapper-backed audit logging, `(ip|email)`-keyed rate limiting on login + password-reset, gated password reset / self-service registration, global authenticated-only gate.

## Out of scope for this PR

- Organizations / teams / projects / memberships / ticket reporter+assignee FKs → Phase 2 ([`team-collaboration.md`](../proposals/team-collaboration.md)).
- OpenFGA, ReBAC, policy-based `[Authorize]` → Phase 3 ([`openfga.md`](../proposals/openfga.md)).
- TOTP / WebAuthn / MFA → Phase 4.
- JWT / API tokens → Phase 5.
- Admin tuple-management UI → Phase 7.
- Any RBAC/PBAC catalogues — explicitly **dropped** in PR #25.
