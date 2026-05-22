# Phase 8 — Admin UI (Tuple-management surface): Implementation Checklist

> Renumbered from the original Phase 7 to Phase 8 to accommodate the newly inserted DI migration phase (the new Phase 7; see [`phase-7-checklist.md`](./phase-7-checklist.md)).

> Note: a standalone `phase-7-checklist.md` for the Admin UI scope was never authored prior to this renumbering. The Phase 7 = Admin UI scope previously lived only in [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §9 Phase 7 — Admin UI and [`docs/proposals/openfga.md`](../proposals/openfga.md) step 11. This Phase 8 checklist is the first dedicated implementation file for that scope, authored at the time of the renumbering (2026-05-22).

**Status:** Planning. No steps started. Blocked on Phase 7 sign-off ([`phase-7-checklist.md`](./phase-7-checklist.md)) — Phase 7 completes the DI container migration; Phase 8 lands the Admin UI write-side against the post-migration composition root.
**Source of truth:** [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §9 Phase 7 — Admin UI (original scope definition), [`docs/proposals/openfga.md`](../proposals/openfga.md) step 11 (MVP capabilities + deferred write-side).
**Upstream:**
- [OpenFGA SDK](https://github.com/openfga/sdk) — tuple-write operations via `ITupleWriter`.
- [SvelteKit](https://github.com/sveltejs/kit) — the frontend framework (Phase 6 landed the SvelteKit scaffold).
- Phase 3.6 step 11 — the read-side admin surface that Phase 8 complements with write-side capabilities.
**Depends on:**
- Phase 7 complete on `main` ([`phase-7-checklist.md`](./phase-7-checklist.md)) — the DI migration must land before Phase 8 begins.
- Phase 6 complete on `main` ([`phase-6-checklist.md`](./phase-6-checklist.md)) — SvelteKit is the frontend; Blazor is retired.
- Phase 3 complete on `main` ([`phase-3-checklist.md`](./phase-3-checklist.md)) — the OpenFGA model, adapter, and tuple-write hooks are in place.

> This file is a **living tracking checklist** for the Phase 8 implementation PRs.
> It does **not** restate the design — see [`security-and-authorization.md`](../proposals/security-and-authorization.md) §9 Phase 7 and [`openfga.md`](../proposals/openfga.md) step 11 for the original scope definition.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.
> Each step maps to one or more PRs, independently committable and shippable.

---

## Phase 8.1 — Admin tuple-write surface skeleton

- [ ] **1. Author the admin tuple-write surface skeleton.** Create a SvelteKit `/admin/tuples` route family that lets a system admin add and remove ad-hoc tuples on `org` / `team` / `project` / `ticket` objects through the existing `ITupleWriter` seam (no direct OpenFGA SDK calls from the UI). The route structure mirrors the Phase 3.6 step 11 read-side admin pages (`/admin/memberships`, `/admin/access`), extended with write capabilities. The UI must:
  - Accept the object type (`organization`, `team`, `project`, `ticket`) and object ID as route parameters or form inputs.
  - Accept the relation to grant/revoke (`view`, `edit`, `comment`, `assign`, `admin`, `member`, `viewer` depending on object type).
  - Accept the subject user ID (resolved via the existing `IUserLookup.SearchByEmailAsync` pattern from Phase 3.6 step 11).
  - Submit via SvelteKit form actions (`+page.server.ts` `actions`) to a new `/api/v1/authz/admin/tuples` endpoint on `Heimdall.Web`.
  - The API endpoint routes through `IMembershipAdminService` (extended) or a new `IAdminTupleService` seam that invokes `ITupleWriter.WriteAsync` / `ITupleWriter.DeleteAsync`.

  Rationale: Phase 3.6 step 11 landed the **read** surface (Memberships, Ticket access); Phase 8 lands the **write** side that was explicitly deferred in [`openfga.md`](../proposals/openfga.md) step 11 ("write-side admin tooling lands in a follow-up after the read-side has bedded in"). The write-side maintains the dual-write contract (DB row → audit row → tuple write) that [`openfga.md`](../proposals/openfga.md) step 7 established — tuples are never written without a corresponding audit trail.

---

## Phase 8.2 — Audit-event coverage

- [ ] **2. Audit-event coverage for the new write surface.** Every ad-hoc tuple grant / revoke emits a `tuple.admin.granted` / `tuple.admin.revoked` audit event written in the **same DB transaction** as the underlying state change (membership row or ad-hoc grant row). The audit-event payloads include:
  - `actor_id` — the system admin performing the action.
  - `object_type` — the FGA object type (`organization`, `team`, `project`, `ticket`).
  - `object_id` — the specific object (e.g. `ticket:42`).
  - `relation` — the relation granted/revoked.
  - `subject_id` — the user receiving/losing the grant.
  - `timestamp` — the DB transaction timestamp.

  The event names follow the Phase 4 / Phase 5 dot-separated naming convention (`mfa.challenge.succeeded`, `token.access.issued`, etc.). Rationale: the read-side admin pages (Phase 3.6 step 11) deliberately do not write tuples; the write side must surface every change to the audit log for forensic review. The **same DB transaction** requirement mirrors the Phase 1 step 7 precedent (`IAuditEventWriter` in the same scope as the domain write).

---

## Phase 8.3 — Policy gate

- [ ] **3. Policy gate on admin routes.** Every admin route in this phase carries the `[Authorize(AuthenticationSchemes = "JwtBearer", Policy = SystemAdmin)]` attribute (API endpoint, using the existing `AuthorizationPolicies.SystemAdmin` constant) and the SvelteKit server-side equivalent (`hooks.server.ts` or `+page.server.ts` calling a server-side API/policy check that enforces `SystemAdmin` before rendering or accepting actions). The write surface is unreachable without:
  - A valid JWT bearer token (Phase 5).
  - The `SystemAdmin` policy satisfied (Phase 3.5 step 9 — resolves to `HeimdallUser.system_admin == true` via `SystemAdminAuthorizationHandler`).
  - If Phase 4 MFA is configured as required for admins, the `amr=mfa` claim must be present (the existing `RequireMfaPolicy` composition).

  Rationale: a tuple-write surface that bypasses MFA or system-admin checks would defeat the entire OpenFGA deny-closed posture. The policy gate is the same as the existing admin pages (`/admin/memberships`, `/admin/access`) — Phase 8 extends the surface, not the security boundary.

---

## Phase 8.4 — Tests

- [ ] **4. Tests for the admin write surface.** Two test layers:
  - **Vitest specs** (authored by the JS/TS Unit Test Engineer per the Phase 6 ownership boundary) over the admin write components in `Heimdall.Frontend`. Cover: form validation (required fields, invalid object type), error-message surfacing on 4xx responses, loading states, success confirmation.
  - **.NET integration tests** via `WebApplicationFactory` over the new `/api/v1/authz/admin/tuples` endpoints. Cover:
    - Deny-closed posture: a non-system-admin caller receives 403.
    - Deny-closed posture: a caller without a valid JWT receives 401.
    - Successful grant: the tuple is written (verified via `IOpenFgaAuthorizationService.CheckAsync` after the write).
    - Successful revoke: the tuple is deleted (verified via `CheckAsync` returning false after the delete).
    - Audit-event write contract: the `tuple.admin.granted` / `tuple.admin.revoked` row exists in `audit_events` with the correct payload.
    - Idempotent behaviour: a duplicate grant does not throw (per the Phase 3.4 step 7 idempotent-write contract on `ITupleWriter`).

  Rationale: parity with the Phase 3.7 step 12 OpenFGA acceptance suite, extended to the write surface. The deny-closed and audit-event tests are the critical safety assertions.

---

## Phase 8.5 — Documentation

- [ ] **5. Runbook: `docs/runbooks/admin-tuples.md`.** Document:
  - **Supported tuple shapes:** Which (object_type, relation) pairs are valid for ad-hoc grants. Reference the FGA model from Phase 3.1 step 1.
  - **Audit-log inspection:** Which `audit_events` columns to inspect on an unintended grant (actor_id, object_type, object_id, relation, subject_id, timestamp). Provide example `SELECT` queries.
  - **Rollback procedure:** How to revoke an unintended grant — use the same admin UI, or direct `DELETE` via the API, or the backfill job from [`openfga.md`](../proposals/openfga.md) step 8 / step 9 in reconciliation mode.
  - **Reconciliation:** If the tuple store drifts from the DB (e.g. a failed tuple write after a successful DB write), reference the existing backfill job as the reconciliation tool.

  Rationale: the read-side already has runbook coverage indirectly via [`openfga-bootstrap.md`](../runbooks/openfga-bootstrap.md); the write-side carries blast-radius implications (an admin can grant or revoke access to any object) that need their own runbook.

---

## Phase 8.6 — Acceptance

- [ ] **6. Acceptance.** All of the following gates must pass before Phase 8 merges:
  - **(a)** `fga model test` continues green — the FGA model itself does **not** change in this phase. The model was confirmed correct in Phase 3.1 step 1 (2026-05-09 openfga-expert review pass).
  - **(b)** End-to-end Playwright test for an admin add-then-remove flow on a `ticket` object:
    1. Log in as a system admin.
    2. Navigate to `/admin/tuples`.
    3. Grant `view` on `ticket:42` to a test user.
    4. Assert the test user can now view ticket 42 (via a second browser context or API call).
    5. Revoke `view` on `ticket:42` from the test user.
    6. Assert the test user can no longer view ticket 42.
  - **(c)** All Vitest specs green (≥80% coverage threshold per Phase 6 scaffold).
  - **(d)** All .NET integration tests green via `dotnet test Heimdall.slnx --settings coverlet.runsettings`.
  - **(e)** Runbook `docs/runbooks/admin-tuples.md` published and linked from `README.md` alongside the existing runbooks.

  Rationale: closes the loop opened by [`openfga.md`](../proposals/openfga.md) step 11 ("write-side admin tooling lands in a follow-up after the read-side has bedded in"). The Playwright E2E test proves the full user flow; the model-test gate confirms no accidental model regression.

---

## Deferred scope

The following are explicitly **not** in Phase 8:

- **Bulk tuple operations** — batch grant/revoke across multiple users or objects. Future enhancement.
- **Tuple expiry** — time-limited grants with automatic revocation. Requires model changes and a TTL mechanism.
- **Non-user subjects** — granting access to service accounts or external principals. Requires model extension.
- **`ticket#watcher` relation** — a new relation for read-only observers distinct from `viewer`. Deferred per [`openfga.md`](../proposals/openfga.md) §4 open question 4.

---

## Cross-references

- [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §9 Phase 7 — Admin UI (original scope source, pre-renumbering).
- [`docs/proposals/openfga.md`](../proposals/openfga.md) step 11 (MVP capability list + deferred write-side).
- [`docs/implementation/phase-3-checklist.md`](./phase-3-checklist.md) step 11 (read-side admin surface, the precedent for the dual-write contract).
- [`docs/implementation/phase-6-checklist.md`](./phase-6-checklist.md) step 14 (the Svelte port of the admin surface; Phase 8 lands its write-side counterparts against SvelteKit).
- [`docs/implementation/phase-7-checklist.md`](./phase-7-checklist.md) (DI migration, completes before Phase 8 begins).
- [`docs/runbooks/openfga-bootstrap.md`](../runbooks/openfga-bootstrap.md) — backfill/reconciliation job reference.
