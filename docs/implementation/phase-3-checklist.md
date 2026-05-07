# Phase 3 — OpenFGA / ReBAC Cutover: Implementation Checklist

**Status:** Planning. No steps started. Phase 2 complete on this branch (steps 27–30 in PR #<this PR>); merge before opening any Phase 3 PR.
**Source of truth:** [`docs/proposals/openfga.md`](../proposals/openfga.md) (§3 sequencing, §4 open questions and decision log).
**Input contract:** [`docs/proposals/openfga-input-contract.md`](../proposals/openfga-input-contract.md) — the row-by-row mapping from production columns to OpenFGA tuple shapes. Steps 7 and 8 below consume this contract directly.
**Depends on:** Phase 2 complete ([`phase-2-checklist.md`](./phase-2-checklist.md)).

> This file is a **living tracking checklist** for the Phase 3 implementation PRs.
> It does **not** restate the design — see the proposal for rationale, model sketches, and decision log.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.

## Phase 3.1 — Author and prove the model

- [ ] **1. Author `/authz/model.fga`.** Define the five object types (`user`, `organization`, `team`, `project`, `ticket`), their relations, and the deny-closed computed permissions (`view`, `edit`, `delete`, `comment`, `assign`, `manage_members`) that downstream policies will check. See [`openfga.md`](../proposals/openfga.md) §3 step 1.
- [ ] **2. Model unit tests `/authz/model.tests.yaml`.** Run via `fga model test`; cover org-admin inheritance, team-admin scope, project-viewer read-only, reporter / assignee self-grants, and the deny-closed non-member case. See [`openfga.md`](../proposals/openfga.md) §3 step 2.

## Phase 3.2 — Stand up the sidecar

- [ ] **3. Render blueprint update.** Add OpenFGA as a private (no public ingress) Render service backed by a separate `heimdall_authz` logical DB; inject `OPENFGA_API_URL` / `OPENFGA_STORE_ID` / `OPENFGA_AUTHORIZATION_MODEL_ID` into the web service. See [`openfga.md`](../proposals/openfga.md) §3 step 3.
- [ ] **4. One-time bootstrap job.** Idempotent script that creates the FGA store, writes the model from step 1, and captures the resulting (immutable) model ID into a Render secret; documented as the runbook for every model change. See [`openfga.md`](../proposals/openfga.md) §3 step 4.

## Phase 3.3 — Wire the SDK and the policy adapter

- [ ] **5. `OpenFga.Sdk` integration in `Heimdall.Web`.** Typed `OpenFgaOptions`, DI registration of the typed client, and a startup health probe that fails fast on unreachable sidecar (no silent allow). See [`openfga.md`](../proposals/openfga.md) §3 step 5.
- [ ] **6. `IAuthorizationService` adapter in `Heimdall.BLL`.** Wraps `Check()` / `BatchCheck()` with a short-TTL request-or-seconds-scoped cache (explicitly **not** circuit-scoped) and OpenTelemetry instrumentation on every call. See [`openfga.md`](../proposals/openfga.md) §3 step 6.

## Phase 3.4 — Get tuples into the store

- [ ] **7. Tuple-write hooks.** Hook BLL services to emit tuple writes on org/team/project create (parent + creator-as-admin), member add/remove, ticket create (parent + reporter + optional assignee), and assignee change (delete-old + write-new). Ship consistency option (a) — direct write — first, with step 8 as the reconciliation safety-net. Reads from [`openfga-input-contract.md`](../proposals/openfga-input-contract.md). See [`openfga.md`](../proposals/openfga.md) §3 step 7.
- [ ] **8. Idempotent backfill / reconciliation job.** Enumerate every row in the Phase 2 hierarchy + membership + ticket tables and write the equivalent tuples; safe to re-run; logs to `audit_events`. Reads from [`openfga-input-contract.md`](../proposals/openfga-input-contract.md). See [`openfga.md`](../proposals/openfga.md) §3 step 8.

## Phase 3.5 — Cutover

- [ ] **9. Replace Phase 1 "authenticated-only" gates with policy-based `[Authorize]`.** Introduce named policies (`CanViewProject`, `CanEditTicket`, `CanAssignTicket`, `CanManageMembers`, …) that resolve to `Check()` via the step-6 adapter, applied to every Blazor page and BLL entry point. See [`openfga.md`](../proposals/openfga.md) §3 step 9.
- [ ] **10. Deny-closed on sidecar outage + DB-only break-glass.** `Check()` failures return false for every caller; break-glass requires `HEIMDALL_AUTHZ_BREAK_GLASS=1` **and** `HeimdallUser.system_admin == true` (read directly from PostgreSQL — no sidecar dependency); every use writes an `audit_events` row. See [`openfga.md`](../proposals/openfga.md) §3 step 10.

## Phase 3.6 — Admin surface returns

- [ ] **11. Admin UI — tuple-management surface.** Read-side first: list/add/remove org/team/project members through step-7 hooks, plus a "who has access to this ticket and why" inheritance walk; write-side ad-hoc tuple grants land in a follow-up. See [`openfga.md`](../proposals/openfga.md) §3 step 11.

## Phase 3.7 — Verify and decommission

- [ ] **12. Integration tests.** End-to-end happy-path and negative-path tests against a real OpenFGA container in CI; existing UI integration tests updated to seed both DB rows and the equivalent tuples through a shared test helper. See [`openfga.md`](../proposals/openfga.md) §3 step 12.
- [ ] **13. Performance verification.** Measure p95 page-load impact of `Check` / `BatchCheck` on the ticket-list hot path and tune the step-6 cache TTL accordingly; document the chosen TTL in the proposal's decision log. See [`openfga.md`](../proposals/openfga.md) §3 step 13.
- [ ] **14. Decommission.** Confirm — by lint rule or test assertion — that no `RequireAuthorization()`-only endpoints remain, no unnamed `[Authorize]` remains, and the Phase 1 "authenticated-only" fallback in `Heimdall.Web/Program.cs` is fully removed. See [`openfga.md`](../proposals/openfga.md) §3 step 14.

## Phase 3 sign-off

- [ ] All 14 steps merged on `main`.
- [ ] `Authorization:Provider` flips from `"TeamRole"` to `"OpenFga"` in production configuration.
- [ ] Phase 1 "authenticated-only" fallback in `Heimdall.Web/Program.cs` is fully removed (proposal step 14).
- [ ] No `RequireAuthorization()`-only endpoints remain (proposal step 14 — enforced by lint rule or test assertion).
- [ ] Phase 1 + Phase 2 acceptance suites still green; new OpenFGA acceptance test added.
- [ ] Coverage targets met across every new file.

## Out of scope for Phase 3

- TOTP / WebAuthn / MFA → Phase 4.
- JWT / API tokens → Phase 5.
- Tuple-aware admin **write** surfaces and the `ticket#watcher` future relation → Phase 6 (per [`openfga.md`](../proposals/openfga.md) §4 open question 4).
- Auto-enrollment **implementations** (admin-invite flow, LDAP) — still deferred. The seam (`IUserEnrollmentService`) exists from Phase 2.9 step 26; concrete bindings land in Phase 3.6 (admin-invite) and beyond (LDAP).
