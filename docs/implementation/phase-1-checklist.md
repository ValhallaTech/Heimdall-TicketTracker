# Phase 1 — Authenticated Foundation: Implementation Checklist

**Status:** Planning — awaiting approval before execution.
**Source of truth:** [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §9.3 Phase 1 (steps 1–11).
**Sequenced by:** [PR #25](https://github.com/ValhallaTech/Heimdall-TicketTracker/pull/25).
**Depends on:** *nothing.* Phase 1 is the foundation.

> This file is a **living tracking checklist** for the Phase 1 implementation PR.
> It does **not** restate the design — see the proposal for rationale, schema sketches, and decision log.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.

## Steps

- [ ] **1. `users` migration only** — FluentMigrator; UUID PK, citext email, identity columns, lockout fields, `system_admin` bool. **No** RBAC/PBAC tables. pgTAP coverage.
- [ ] **2. `audit_events` migration** — Lands before any auth code so every later step can write audit rows from day one. pgTAP coverage.
- [ ] **3. Dapper-backed Identity stores** in `Heimdall.DAL` — `IUserStore`, `IUserPasswordStore`, `IUserEmailStore`, `IUserSecurityStampStore`, `IUserLockoutStore`. xUnit tests; coverage ≥ 80%.
- [ ] **4. `AddIdentityCore<HeimdallUser>()` + cookie auth + Data Protection** — `Heimdall.Web/Program.cs`, after `AddDal()`. DP keys persisted to PostgreSQL. No JWT yet.
- [ ] **5. `RevalidatingServerAuthenticationStateProvider`** — Bound to `SecurityStamp`; short revalidation interval.
- [ ] **6. `IEmailSender` seam** — Interface + DTO in `Heimdall.Core`; `MailKitEmailSender` + `NoOpEmailSender` in `Heimdall.BLL`. No flow consumes it yet.
- [ ] **7. Login/logout** — Server-rendered POST endpoint + Blazor pages. Cookie issued on success. Audit events emitted.
- [ ] **8. `SystemAdmin` env-var bootstrap** — `HEIMDALL_BOOTSTRAP_ADMIN_EMAIL` + `HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD` create the seed admin if no users exist. Idempotent.
- [ ] **9. "Authenticated-only" gate** — `[Authorize]` on the Blazor router fallback + `RequireAuthorization()` on protected endpoints. No role/policy checks. Placeholder for OpenFGA.
- [ ] **10. Password reset + self-service register pages** — Gated on `IEmailSender` resolving to `MailKitEmailSender` (feature flag at startup).
- [ ] **11. Phase 1 acceptance** — pgTAP for new tables; xUnit for Dapper Identity stores; integration test for unauth-redirects-to-login + auth-succeeds. Coverage ≥ 80%.

## Out of scope for this PR

- Organizations / teams / projects / memberships / ticket reporter+assignee FKs → Phase 2 ([`team-collaboration.md`](../proposals/team-collaboration.md)).
- OpenFGA, ReBAC, policy-based `[Authorize]` → Phase 3 ([`openfga.md`](../proposals/openfga.md)).
- TOTP / WebAuthn / MFA → Phase 4.
- JWT / API tokens → Phase 5.
- Admin tuple-management UI → Phase 6.
- Any RBAC/PBAC catalogues — explicitly **dropped** in PR #25.
