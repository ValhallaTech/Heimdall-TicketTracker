# Proposal: OpenFGA Implementation Plan for Heimdall (ReBAC adoption)

**Status:** **Draft / Planning** (2026-05-04)
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.Web (DI, policies, health probes), Heimdall.BLL (`IAuthorizationService` adapter, tuple-write hooks, backfill job), `/authz` (model file + tests), Render blueprint (private OpenFGA service + `heimdall_authz` DB), tests/Heimdall.* (integration tests against a real OpenFGA container)
**Decision required:** What is the strictly-ordered implementation sequence to replace the Phase 1 "authenticated-only" gate from [`security-and-authorization.md`](./security-and-authorization.md) with policy-based `[Authorize]` resolved through OpenFGA `Check()` calls, given that OpenFGA was already selected over Permify in [`openfga-vs-permify.md`](./openfga-vs-permify.md)?
**Depends on:** [`team-collaboration.md`](./team-collaboration.md) **complete.** Without organizations / teams / projects / memberships / ticket reporter+assignee FKs, every step below either has nothing to authorize or has nowhere to read tuples from.

> This document is **research and planning only**. **No code, package, configuration, or DI changes are made in this PR.** A separate, follow-up PR (or series of PRs) will implement the design once approved. This proposal is **the implementation plan**; it does **not** edit [`openfga-vs-permify.md`](./openfga-vs-permify.md), which holds the comparison and selection rationale.

---

## 1. Why this is a separate proposal

[`openfga-vs-permify.md`](./openfga-vs-permify.md) answered "OpenFGA or Permify?" and "should we even do ReBAC?" It picked OpenFGA and parked the timeline as "Phase 4 of [`security-and-authorization.md`](./security-and-authorization.md)". Two things have since changed:

1. The proposal-doc set was reorganized so that **the data model OpenFGA needs** lives in its own document ([`team-collaboration.md`](./team-collaboration.md)) instead of being assumed.
2. The user asked for **per-proposal step sequencing**. The selection doc in [`openfga-vs-permify.md`](./openfga-vs-permify.md) is the wrong place for an implementation step list — it's a comparison doc.

This proposal therefore narrows [`openfga-vs-permify.md`](./openfga-vs-permify.md) to a concrete, ordered implementation plan. It references the comparison doc for the *why* and never re-litigates it.

## 2. Current state

| Area                     | State after Proposals 1 + 2                                                                                                                            |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| Authentication           | Cookie auth + Identity (Proposal 1 Phase 1 step 4).                                                                                                    |
| Authorization            | **"Authenticated-only" gate** on every Blazor page and protected endpoint (Proposal 1 Phase 1 step 9). No role checks, no policy checks.               |
| Object hierarchy         | `organizations` → `teams` → `projects` → `tickets` with `parent` FKs (Proposal 2 steps 1–7).                                                           |
| Memberships              | `organization_members`, `team_members`, `project_members` with `role` enum **as label only** (Proposal 2 step 4).                                      |
| Ticket subjects          | `tickets.reporter_id`, `tickets.assignee_id` UUID FKs to `users` (Proposal 2 steps 8–9).                                                               |
| OpenFGA / sidecar / SDK  | **None.** No service, no SDK reference, no model file, no tuple writes, no policy adapter.                                                             |
| Admin UI                 | **None.** Deferred to this proposal per [`security-and-authorization.md`](./security-and-authorization.md) §8 banner.                                  |

## 3. Recommendation summary and phased rollout

The 14 steps below are **strictly ordered** by dependency. Each step's preconditions are all satisfied by earlier steps. The cutover step (step 9) is the only step that's *not* strictly additive, and it can be deployed one resource type at a time.

#### Phase 3.1 — Author and prove the model

1. **Author `/authz/model.fga`.** Types: `user`, `organization`, `team`, `project`, `ticket`. Relations:
   - `organization`: `admin`, `member`.
   - `team`: `parent_org`, `admin`, `member`.
   - `project`: `parent_team`, `admin`, `member`, `viewer`.
   - `ticket`: `parent_project`, `reporter`, `assignee`.

   Computed permissions on each object derived via `from` traversal (e.g. `ticket.view = reporter | assignee | parent_project.viewer | parent_project.member | parent_project.admin | parent_project.parent_team.admin | parent_project.parent_team.parent_org.admin`). Computed perms required: `view`, `edit`, `delete`, `comment`, `assign`, `manage_members`. Deny-closed by default — anything not listed is forbidden.
2. **Model unit tests** at `/authz/model.tests.yaml`, run via `fga model test`. Test matrix:
   - org-admin inherits `edit` on every descendant ticket;
   - team-admin inherits `edit` only within their team;
   - project-viewer has `view` and `comment` only, no `edit`;
   - reporter has `view`/`edit` on their own ticket regardless of project membership;
   - assignee has `view`/`edit`/`assign` on their own ticket;
   - non-member of any ancestor is denied (deny-closed proof).

#### Phase 3.2 — Stand up the sidecar

3. **Render blueprint update.** Add OpenFGA as a **private service** (no public ingress; reachable only from the web service over Render's private network). Provision a separate Postgres logical DB (`heimdall_authz`) on the existing instance. Inject `OPENFGA_API_URL`, `OPENFGA_STORE_ID`, `OPENFGA_AUTHORIZATION_MODEL_ID` into the web service via environment variables. Document the secret-rotation story in the same PR.
4. **One-time bootstrap job/script.** Creates the FGA store, writes the model from step 1, captures the resulting model ID into a Render secret. Document re-running on every model change — OpenFGA model IDs are **immutable**; a new model emits a new ID, and the env var must be updated to point at it. The job is idempotent against an existing store (it writes a new model version but does not duplicate the store).

#### Phase 3.3 — Wire the SDK and the policy adapter

5. **`OpenFga.Sdk` integration in `Heimdall.Web`.** Typed `OpenFgaOptions` bound from configuration, DI registration of the typed client, and a startup health probe that fails fast if the sidecar is unreachable (no silent fallback to "allow" — that is exactly the foot-gun ReBAC is supposed to prevent).
6. **`IAuthorizationService` adapter in `Heimdall.BLL`** wrapping `Check()` and `BatchCheck()`.
   - **Cache** is short-TTL, **request-scoped or seconds-scoped** — explicitly **not circuit-scoped**, per [`security-and-authorization.md`](./security-and-authorization.md) §9.2. Circuits live for hours; a circuit-scoped cache would leave revoked privileges active until reconnect.
   - `BatchCheck()` is required for list-page hot paths (ticket list, project list) so a single page render does one round-trip, not one per row.
   - OpenTelemetry instrumentation on every adapter call (latency histogram, allow/deny counter, error counter).

#### Phase 3.4 — Get tuples into the store

7. **Tuple-write hooks.** Hook the BLL services from [`team-collaboration.md`](./team-collaboration.md) step 13 to emit tuple writes on:
   - `organization` / `team` / `project` create → `parent_*` tuples **plus an immediate `*_members` insert with `role = 'owner'` for the creator** (which the same hook then writes as an `admin` tuple per the mapping in [`team-collaboration.md`](./team-collaboration.md) step 17). Without this, a newly-created org/team/project would have no admin tuple at all and its creator would lose access to it the moment the cutover (step 9) lands.
   - member add/remove on any of the three membership tables → `admin` / `member` / `viewer` tuples per the mapping in [`team-collaboration.md`](./team-collaboration.md) step 17;
   - ticket create → `parent_project` tuple, plus `reporter` (always — the column is NOT NULL after [`team-collaboration.md`](./team-collaboration.md) step 9) and (if set) `assignee`;
   - assignee change → tuple replace (delete-old + write-new in one call).

   **Pick one consistency story and document it.** Two options:
   - **(a)** direct write inside the same service-call boundary as the DB write — simplest, but a sidecar outage between the two writes leaves DB and tuple store divergent;
   - **(b)** outbox pattern — the service writes a row to an `authz_outbox` table in the same DB transaction as the domain write, and a worker drains it into OpenFGA. Most robust; harder to introduce.

   Recommendation: ship **(a)** in the first cutover, with the [step-8 backfill job](#phase-3-5-cutover) doubling as a reconciliation tool; promote to **(b)** if drift becomes observable.
8. **Idempotent backfill job.** Enumerate every row in `organizations`, `teams`, `projects`, `*_members`, and `tickets` and write the equivalent tuples. Idempotent — safe to re-run after partial failures, on cutover, and as a periodic reconciliation against drift introduced by step 7's option (a). Logs a summary to `audit_events` (the table from [`security-and-authorization.md`](./security-and-authorization.md) §9.3 step 2).

<a id="phase-3-5-cutover"></a>

#### Phase 3.5 — Cutover

9. **Replace the Phase 1 "authenticated-only" gates with policy-based `[Authorize]`.** Introduce policies that map 1:1 onto the model's computed permissions: `CanViewProject`, `CanEditProject`, `CanViewTicket`, `CanEditTicket`, `CanCommentTicket`, `CanAssignTicket`, `CanManageMembers`, etc. Each policy resolves to a `Check()` call via the adapter from step 6. Apply to **every** Blazor page and BLL service entry point that touched the Phase 1 gate. Once coverage is verified (step 14), **remove the Phase 1 "authenticated-only" fallback entirely** so there's no parallel policy stack.
10. **Deny-closed on sidecar outage.** `Check()` failures (timeout, network, 5xx) return **false** for every caller. The break-glass path is therefore **deliberately not gated by `Check()`** (that would make it unavailable in exactly the failure mode it exists for); it is gated by a **DB-only** check against `HeimdallUser.system_admin == true` (the column from [`security-and-authorization.md`](./security-and-authorization.md) §9.3 step 1). Break-glass:
    - requires an explicit env-var enable (`HEIMDALL_AUTHZ_BREAK_GLASS=1`),
    - additionally requires `system_admin == true` on the requesting user (read directly from PostgreSQL, no sidecar dependency),
    - logs an `audit_events` row per use including actor, target, and reason header,
    - does *not* survive process restart unless the env var remains set (deliberate operator-effort barrier).

    The `system_admin` boolean is therefore the **only** authority Heimdall consults during a sidecar outage, and remains the safety-net that lets a documented operator unstick a deployment when OpenFGA is unreachable.

#### Phase 3.6 — Admin surface returns

11. **Admin UI** — fulfils the admin surface deferred in [`security-and-authorization.md`](./security-and-authorization.md) §8. **Tuple-management surface, not a roles/groups surface.** MVP capabilities:
    - list / add / remove org / team / project members (writes flow through step 7's hooks, not direct tuple writes);
    - "who has access to this ticket and why" — given a `ticket:42` resource, walk the model and show the inheritance path that grants each subject their permission;
    - read-only first; write-side admin tooling (e.g. ad-hoc tuple grants like `ticket:42#viewer@user:alice`) lands in a follow-up after the read-side has bedded in.

#### Phase 3.7 — Verify and decommission

12. **Integration tests.** End-to-end happy-path and negative-path tests against a **real OpenFGA container** in CI (the sidecar is already containerized for Render, so the same image runs in CI). pgTAP tests are unaffected. Existing UI integration tests updated to seed both DB rows *and* the equivalent tuples; a test helper consolidates the two so test authors don't drift.
13. **Performance verification.** Measure p95 page-load impact of the `Check`/`BatchCheck` hot path on the ticket list page (the list page is the worst case — N tickets × 1 view check). Tune the in-process cache TTL (step 6) based on the numbers. Document the decision in the decision log below.
14. **Decommission.** Confirm — by lint rule or by test assertion — that:
    - **no** `RequireAuthorization()`-only endpoints remain (every protected endpoint has a named policy),
    - **no** `[Authorize]` without a policy name remains,
    - the Phase 1 "authenticated-only" fallback in `Heimdall.Web/Program.cs` is fully removed.

   This is the step that finishes the migration. Until it lands, Phase 1 and Phase 3 gates coexist and one of the two could be silently authoritative for a given route — exactly the foot-gun a deny-closed system must not allow.

Each step is independently committable behind a feature flag (steps 9 and 14 are the only two that change request-handling behaviour for end users; the rest are additive).

## 4. Open questions and decision log

### Open questions

1. **Multi-tenant scoping.** Inherited from [`openfga-vs-permify.md`](./openfga-vs-permify.md) §10 Q1. Does `organization` map 1:1 to the OpenFGA *store* (one store per tenant), or do we use a single store with `organization` as an object type for scoping? Single-store is recommended for MVP; revisit if/when a customer requires hard isolation.
2. **Tuple lifecycle on user delete.** Inherited from [`openfga-vs-permify.md`](./openfga-vs-permify.md) §10 Q3. On user delete, do tuples tombstone or rewrite? Tuples reference user IDs; a deleted user must not retain effective grants. Suggest: cascade-delete tuples in step 7's hook, the same way `*_members.user_id` cascades from `users.id`.
3. **Outbox vs direct write.** Step 7 above. Confirm we ship **(a)** direct write first, with step 8's backfill job as the reconciliation safety-net, and only promote to **(b)** outbox if drift is observed in production.
4. **Model evolution.** When we add a new relation (e.g. a future `ticket#watcher`), we need to: (a) write a new model version, (b) update `OPENFGA_AUTHORIZATION_MODEL_ID`, (c) backfill any tuples that should exist under the new relation. Document this as a runbook *before* the first model change.
5. **Cache TTL.** Step 6 leaves the TTL deliberately unspecified pending the step-13 measurement. Initial proposal: **2 seconds** request-scoped — long enough to absorb a list page's repeat checks, short enough that revocations propagate within one user interaction.
6. **OpenTelemetry exporter.** Step 6 instruments the adapter. Confirm the exporter target (Serilog → OTLP, or direct OTLP) — irrelevant to the authz design, relevant to the implementation PR.
7. **Break-glass auditing.** Step 10. Exactly *who* signs the break-glass path, *how* is the credential rotated, and is there a time-boxed expiry on `HEIMDALL_AUTHZ_BREAK_GLASS=1`? Inherited and unresolved from [`openfga-vs-permify.md`](./openfga-vs-permify.md) §10 Q7.

### Decision log

| Date       | Decision                                                                                                                                                                                                                                                                                                                                            |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2026-05-04 | Proposal drafted as the implementation companion to [`openfga-vs-permify.md`](./openfga-vs-permify.md) (which holds the selection rationale and is **not** edited here). 14-step strictly-ordered implementation sequence. Cutover step (9) and decommission step (14) bookend the only non-additive changes; all other steps are additive behind a feature flag. Awaiting review. |
| 2026-05-05 | Revised per review feedback. (a) Step 7 create-hook now also inserts an `*_members` row with `role = 'owner'` for the creator so a newly-created org/team/project produces an admin tuple — without it, creators lost access to their own objects at cutover. (b) Step 10 break-glass path no longer gated by `Check(organization:...#admin)` — that would be unavailable in exactly the sidecar-outage scenario it exists for. Replaced with a DB-only `HeimdallUser.system_admin == true` check that does not depend on OpenFGA. Updated step references to match `team-collaboration.md`'s renumbered steps (12 → 13, 16 → 17, reporter NOT NULL is now step 9). |

---

**Next step:** Review and resolve the open questions in §4, then sequence the implementation PRs per §3 once [`team-collaboration.md`](./team-collaboration.md) has landed.
