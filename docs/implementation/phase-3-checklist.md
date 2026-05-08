# Phase 3 — OpenFGA / ReBAC Cutover: Implementation Checklist

**Status:** Planning. No steps started. Phase 2 complete on this branch ([PR #33](https://github.com/ValhallaTech/Heimdall-TicketTracker/pull/33), steps 27–30); merge before opening any Phase 3 PR.
**Source of truth:** [`docs/proposals/openfga.md`](../proposals/openfga.md) (§3 sequencing, §4 open questions and decision log).
**Upstream:** [`openfga/openfga`](https://github.com/openfga/openfga) — server, model storage, gRPC + HTTP API.
**Input contract:** [`docs/proposals/openfga-input-contract.md`](../proposals/openfga-input-contract.md) — the row-by-row mapping from production columns to OpenFGA tuple shapes. Steps 7 and 8 below consume this contract directly.
**Depends on:** Phase 2 complete ([`phase-2-checklist.md`](./phase-2-checklist.md)).

> This file is a **living tracking checklist** for the Phase 3 implementation PRs.
> It does **not** restate the design — see the proposal for rationale, model sketches, and decision log.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.

## Phase 3.1 — Author and prove the model

- [x] **1. Author `/authz/model.fga`.** Use [DSL `model schema 1.1`](https://openfga.dev/docs/configuration-language) to declare the five object types (`user`, `organization`, `team`, `project`, `ticket`), direct relations like `define admin: [user]`, and computed permissions composed via `or` / `and` / `but not` / `from` (e.g. `define can_view: viewer or editor or admin from parent`). Keep at least one `[user]`-typed relation per queryable object so [`ListObjects`](https://openfga.dev/api/service#/Relationship%20Queries/ListObjects) / [`ListUsers`](https://openfga.dev/api/service#/Relationship%20Queries/ListUsers) work for queue filtering and the admin "who has access" view (a `[user:*]` wildcard alone is not sufficient). See [`openfga.md`](../proposals/openfga.md) §3 step 1.
- [x] **2. Model tests `/authz/store.fga.yaml`.** OpenFGA's [test format](https://openfga.dev/docs/modeling/testing) is `*.fga.yaml` (`model_file` + `tuples` + `tests` with `check` / `list_objects` / `list_users` assertions); validate with `fga model test --tests authz/store.fga.yaml`. Cover org-admin inheritance, team-admin scope, project-viewer read-only, reporter / assignee self-grants, and the deny-closed non-member case. See [`openfga.md`](../proposals/openfga.md) §3 step 2.

## Phase 3.2 — Stand up the sidecar

- [x] **3. Render blueprint update.** Add OpenFGA as a private (no public ingress) Render service running [`docker.io/openfga/openfga`](https://hub.docker.com/r/openfga/openfga) backed by a separate `heimdall_authz` logical DB. Server env: `OPENFGA_DATASTORE_ENGINE=postgres`, `OPENFGA_DATASTORE_URI`, `OPENFGA_HTTP_ADDR`, `OPENFGA_GRPC_ADDR`, `OPENFGA_AUTHN_METHOD=preshared`, `OPENFGA_AUTHN_PRESHARED_KEYS` (see [running in production](https://openfga.dev/docs/getting-started/running-in-production)). Run `openfga migrate --datastore-engine postgres --datastore-uri …` against `heimdall_authz` as a one-shot pre-deploy step before `openfga run`. **Client-side** env injected into Heimdall.Web stays as `OPENFGA_API_URL` / `OPENFGA_STORE_ID` / `OPENFGA_AUTHORIZATION_MODEL_ID` plus the preshared API token. See [`openfga.md`](../proposals/openfga.md) §3 step 3.
- [x] **4. One-time bootstrap job.** Idempotent script that calls `fga store create` (or `POST /stores`) then `fga model write --file authz/model.fga` (or `WriteAuthorizationModel`); captures the returned **immutable** `authorization_model_id` into a Render secret. Every model edit produces a new ID — the runbook must re-run step 4 and update `OPENFGA_AUTHORIZATION_MODEL_ID` on each model change (see [CLI](https://github.com/openfga/cli)). See [`openfga.md`](../proposals/openfga.md) §3 step 4.

## Phase 3.3 — Wire the SDK and the policy adapter

- [x] **5. [`OpenFga.Sdk`](https://www.nuget.org/packages/OpenFga.Sdk) integration in `Heimdall.Web`.** Typed `OpenFgaOptions` bound from config; DI-register `OpenFgaClient` (from `OpenFga.Sdk.Client`) with a `ClientConfiguration` whose `ApiUrl` / `StoreId` / `AuthorizationModelId` are pinned from env (the SDK does **not** auto-discover the model ID — the caller pins it for stable behaviour across model revisions). Add a startup health probe that fails fast on unreachable sidecar (no silent allow). See [.NET SDK](https://github.com/openfga/dotnet-sdk) and [`openfga.md`](../proposals/openfga.md) §3 step 5.
- [x] **6. `IAuthorizationService` adapter in `Heimdall.BLL`.** Wraps `OpenFgaClient.Check` (single tuple → `Allowed`) and `BatchCheck` (multiple tuples in one round trip — preferred for hot paths) with a short-TTL request-or-seconds-scoped cache (explicitly **not** circuit-scoped) and OpenTelemetry instrumentation on every call. Expose the [`consistency`](https://openfga.dev/docs/interacting/consistency) parameter (`MINIMIZE_LATENCY` default vs `HIGHER_CONSISTENCY` for read-after-write paths) and a [`ListObjects`](https://openfga.dev/api/service#/Relationship%20Queries/ListObjects) wrapper for the queue page so we paginate "tickets this user can view" instead of fan-out checking every row. See [`openfga.md`](../proposals/openfga.md) §3 step 6.

## Phase 3.4 — Get tuples into the store

- [x] **7. Tuple-write hooks.** Hook BLL services to call the [`Write`](https://openfga.dev/api/service#/Relationship%20Tuples/Write) endpoint (atomic `writes` + `deletes` arrays per request) on org/team/project create (parent + creator-as-admin), member add/remove, ticket create (parent + reporter + optional assignee), and **assignee change as a single `Write` containing one delete + one write** (not two API calls — `Write` is atomic per request, splitting it breaks the invariant). Reserve [contextual tuples](https://openfga.dev/docs/modeling/token-claims-contextual-tuples) for ad-hoc / time-bound grants that should not persist. Reads from [`openfga-input-contract.md`](../proposals/openfga-input-contract.md). See [`openfga.md`](../proposals/openfga.md) §3 step 7.
- [x] **8. Idempotent backfill / reconciliation job.** Enumerate every row in the Phase 2 hierarchy + membership + ticket tables and write the equivalent tuples; safe to re-run; logs to `audit_events`. Page writes against the server's [`OPENFGA_MAX_TUPLES_PER_WRITE`](https://openfga.dev/docs/getting-started/setup-openfga/configuration#OPENFGA_MAX_TUPLES_PER_WRITE) cap (default `100`) — chunk to ≤ 100 tuples per `Write` request. Reads from [`openfga-input-contract.md`](../proposals/openfga-input-contract.md). See [`openfga.md`](../proposals/openfga.md) §3 step 8.

## Phase 3.5 — Cutover

- [x] **9. Replace Phase 1 "authenticated-only" gates with policy-based `[Authorize]`.** Introduce named policies (`CanViewProject`, `CanEditTicket`, `CanAssignTicket`, `CanManageMembers`, …) that resolve to `Check` via the step-6 adapter (never call `OpenFgaClient` directly from page or service code — go through the adapter so policies are unit-testable). Applied to every Blazor page and BLL entry point. See [`openfga.md`](../proposals/openfga.md) §3 step 9.
- [x] **10. Deny-closed on sidecar outage + DB-only break-glass.** `Check` failures return false for every caller; break-glass requires `HEIMDALL_AUTHZ_BREAK_GLASS=1` **and** `HeimdallUser.system_admin == true` (read directly from PostgreSQL — no sidecar dependency); every use writes an `audit_events` row. See [`openfga.md`](../proposals/openfga.md) §3 step 10.

## Phase 3.6 — Admin surface returns

- [x] **11. Admin UI — tuple-management surface.** Read-side first: list/add/remove org/team/project members through step-7 hooks, plus a "who has access to this ticket and why" view backed by [`ListUsers`](https://openfga.dev/api/service#/Relationship%20Queries/ListUsers) (object → users for a relation) for the user list and [`Expand`](https://openfga.dev/api/service#/Relationship%20Queries/Expand) (returns the userset tree) for the inheritance walk; write-side ad-hoc tuple grants land in a follow-up. See [`openfga.md`](../proposals/openfga.md) §3 step 11.

## Phase 3.7 — Verify and decommission

- [ ] **12. Integration tests.** End-to-end happy-path and negative-path tests against a real `docker.io/openfga/openfga` container in CI via Testcontainers; `fga model test` covers the model layer (step 2), and these xUnit tests cover the adapter + tuple-write-hook layer. Existing UI integration tests updated to seed both DB rows and the equivalent tuples through a shared test helper. See [`openfga.md`](../proposals/openfga.md) §3 step 12.
- [ ] **13. Performance verification.** Measure p95 page-load impact of `Check` / `BatchCheck` / `ListObjects` on the ticket-list hot path. Levers: (a) the step-6 in-process cache TTL, (b) the [`consistency`](https://openfga.dev/docs/interacting/consistency) parameter per call site, and (c) the **server-side check cache** (`OPENFGA_CHECK_QUERY_CACHE_ENABLED`, `OPENFGA_CHECK_QUERY_CACHE_TTL`, `OPENFGA_CHECK_ITERATOR_CACHE_ENABLED`, `OPENFGA_CHECK_ITERATOR_CACHE_TTL`). Document the chosen TTLs in the proposal's decision log. See [`openfga.md`](../proposals/openfga.md) §3 step 13.
- [ ] **14. Decommission.** Confirm — by lint rule or test assertion — that no `RequireAuthorization()`-only endpoints remain, no unnamed `[Authorize]` remains, and the Phase 1 "authenticated-only" fallback in `Heimdall.Web/Program.cs` is fully removed. See [`openfga.md`](../proposals/openfga.md) §3 step 14.

## Phase 3 sign-off

- [ ] All 14 steps merged on `main`.
- [ ] `Authorization:Provider` flips from `"TeamRole"` to `"OpenFga"` in production configuration.
- [ ] Phase 1 "authenticated-only" fallback in `Heimdall.Web/Program.cs` is fully removed (proposal step 14).
- [ ] No `RequireAuthorization()`-only endpoints remain (proposal step 14 — enforced by lint rule or test assertion).
- [ ] Phase 1 + Phase 2 acceptance suites still green; new OpenFGA acceptance test added.
- [ ] Coverage targets met across every new file.

## References

- OpenFGA Production Best practices - [`openfga.dev/docs/best-practices`](https://openfga.dev/docs/best-practices/running-in-production) - Cluster recommendations, database recommendations, concurrency limits, maximum results.
- OpenFGA upstream repo — [`openfga/openfga`](https://github.com/openfga/openfga) — server, model storage, gRPC + HTTP API.
- OpenFGA documentation site — [`openfga.dev/docs`](https://openfga.dev/docs) — concepts, modeling, query APIs, deployment.
- Modeling language (DSL) reference — [`openfga.dev/docs/configuration-language`](https://openfga.dev/docs/configuration-language) — `model schema 1.1`, type/relation syntax, computed-relation operators.
- Modeling tests (`*.fga.yaml`) — [`openfga.dev/docs/modeling/testing`](https://openfga.dev/docs/modeling/testing) and [store file format](https://openfga.dev/docs/modeling/store-file-format) — driven by `fga model test`.
- Running in production — [`openfga.dev/docs/getting-started/running-in-production`](https://openfga.dev/docs/getting-started/running-in-production) — preshared-key auth, TLS, observability, cache flags.
- HTTP / gRPC API reference — [`openfga.dev/api/service`](https://openfga.dev/api/service) — `Check`, `BatchCheck`, `Write`, `ListObjects`, `ListUsers`, `Expand`, plus the `consistency` parameter.
- .NET SDK — [`openfga/dotnet-sdk`](https://github.com/openfga/dotnet-sdk) (NuGet [`OpenFga.Sdk`](https://www.nuget.org/packages/OpenFga.Sdk)) — `OpenFgaClient`, `ClientConfiguration`.
- CLI — [`openfga/cli`](https://github.com/openfga/cli) — `fga store create`, `fga model write`, `fga model test`.

## Out of scope for Phase 3

- TOTP / WebAuthn / MFA → Phase 4.
- JWT / API tokens → Phase 5.
- Tuple-aware admin **write** surfaces and the `ticket#watcher` future relation → Phase 6 (per [`openfga.md`](../proposals/openfga.md) §4 open question 4).
- Auto-enrollment **implementations** (admin-invite flow, LDAP) — still deferred. The seam (`IUserEnrollmentService`) exists from Phase 2.9 step 26; concrete bindings land in Phase 3.6 (admin-invite) and beyond (LDAP).
