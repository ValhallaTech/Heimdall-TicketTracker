# Phase 2 — Team Collaboration Infrastructure: Implementation Checklist

**Status:** Phase 2.1 complete on `main` (PR #27 merged); Phase 2.2 complete on `main` (PR #29 merged); Phase 2.3 in this PR; phases 2.4–2.10 in planning.
**Source of truth:** [`docs/proposals/team-collaboration.md`](../proposals/team-collaboration.md) (§4 sequencing, §5 policy matrix, §6 `IPermissionService`, §7 admin panel, §8 enrollment hook).
**Depends on:** Phase 1 ([`phase-1-checklist.md`](./phase-1-checklist.md)) — complete on `main` after PR #26.

> This file is a **living tracking checklist** for the Phase 2 implementation PRs.
> It does **not** restate the design — see the proposal for rationale, schema sketches, role taxonomy, and decision log.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.
> Each phase below maps to **one or more PRs**, each independently committable and shippable per the proposal's "no big-bang merges" stance.

## Phase 2.1 — Object hierarchy (migrations + domain models only)

- [x] **1. `organizations` migration.** UUID PK, citext `slug` unique, `name`, `created_at`, `created_by` UUID FK → `users(id)` `ON DELETE RESTRICT`. pgTAP: PK / FK / unique-slug coverage.
- [x] **2. `teams` migration.** UUID PK, `organization_id` FK CASCADE, citext `slug`, `name`, audit columns. Composite unique `(organization_id, slug)` (the composite's leading column also serves parent-only lookups and the cascade probe — no standalone index needed). pgTAP coverage.
- [x] **3. `projects` migration.** UUID PK, `team_id` FK CASCADE, citext `slug`, `name`, audit columns. Composite unique `(team_id, slug)` (same reasoning as teams — leading column doubles as the parent-id index). pgTAP coverage.
- [x] **4. `Organization`, `Team`, `Project` domain types** in `Heimdall.Core/Models`. No framework references; mirror existing `HeimdallUser` style (XML doc comments on every public member).
- [x] **5. Dapper repositories** `IOrganizationRepository`, `ITeamRepository`, `IProjectRepository` in `Heimdall.DAL`. Wired into `AddDal()`. xUnit + Testcontainers integration tests against `postgres:18-alpine` (matches Phase 1 fixture).

## Phase 2.2 — Memberships (the people-to-objects edges)

- [x] **6. Membership migrations** — `organization_members`, `team_members`, `project_members`. Note the **two role enums** per [`team-collaboration.md`](../proposals/team-collaboration.md) §3.1 / §4 step 4:
  - `organization_members.role` / `project_members.role` → `{ owner, admin, member, viewer }`
  - `team_members.role` → `{ manager, team_lead, member, viewer }`

  Composite PKs `(user_id, parent_id)`; secondary indexes on parent FK; cascade rules per the proposal. pgTAP coverage including the `team_members.role` enum values.
- [x] **7. Member domain types** `OrganizationMember`, `TeamMember`, `ProjectMember` (+ `TeamMemberRole` enum) in `Heimdall.Core/Models`.
- [x] **8. Dapper member repositories** `IOrganizationMemberRepository`, `ITeamMemberRepository`, `IProjectMemberRepository` in `Heimdall.DAL`, wired into `AddDal()`. xUnit + Testcontainers coverage.

## Phase 2.3 — Default-row backfill (so existing seed tickets keep working)

- [ ] **9. Backfill migration** — insert one `heimdall` org, one `default` team, one `default` project, all `created_by = bootstrap_admin.id`. **Also insert `*_members` rows** for the bootstrap admin: `organization_members.role = 'owner'`, `team_members.role = 'manager'`, `project_members.role = 'owner'`. Idempotent (insert-if-not-exists keyed on slug / `(user_id, parent_id)`).

## Phase 2.4 — Tickets carry their parent project **and** parent team

- [ ] **10. Migration: add `tickets.project_id` (nullable).** FK → `projects(id)` `ON DELETE RESTRICT`. Backfill to default project from step 9.
- [ ] **11. Migration: add `tickets.team_id` (nullable).** FK → `teams(id)` `ON DELETE RESTRICT`. Backfill to default team from step 9. Index on `team_id`.
- [ ] **12. Migration: alter `tickets.project_id` and `tickets.team_id` to NOT NULL.** Separate migration so steps 10 / 11's backfill is observable mid-deploy and rollback-friendly.

## Phase 2.5 — Tickets reference real users (replace synthetic seed strings)

- [ ] **13. Migration: add `tickets.reporter_id` and `tickets.assignee_id` (nullable UUID FKs).** `reporter_id` `ON DELETE RESTRICT`; `assignee_id` `ON DELETE SET NULL`. Backfill from existing seed strings; fall back to bootstrap admin for `reporter_id`, NULL for `assignee_id`.
- [ ] **14. Migration: alter `tickets.reporter_id` to NOT NULL.** Preserves the always-has-a-reporter invariant required by [`openfga.md`](../proposals/openfga.md) step 7.
- [ ] **15. Migration: drop legacy `tickets.reporter` / `tickets.assignee` varchar columns.** Separate migration so steps 13–15 can be sequenced and observed.
- [ ] **16. Update `Ticket` domain type, `ITicketRepository`, `TicketDto`, Mapster mapper.** Replace string fields with `ProjectId`, `TeamId`, `ReporterId`, `AssigneeId`. Mapster `*.g.cs` regenerated and committed (per the repo convention).

## Phase 2.6 — `IPermissionService` (the OpenFGA seam)

- [ ] **17. Declare `IPermissionService`** in `Heimdall.BLL/Authorization` per [`team-collaboration.md`](../proposals/team-collaboration.md) §6. Methods cover queue-visibility, route, assign, manage-members. Deny-closed by default.
- [ ] **18. Implement `TeamRoleBackedPermissionService`** that reads `team_members.role` + `users.system_admin` and applies §5's matrix. Registered behind `Authorization:Provider = "TeamRole"` (default). xUnit unit tests covering every cell of the §5.1 / §5.2 / §5.3 matrices, including the `system_admin == true` short-circuit and the deny-closed unknown-role case.
- [ ] **19. Wire `IPermissionService` into the BLL ticket service.** Routing and self-assign call sites consult `IPermissionService` only — **no** direct reads of `team_members.role` from authorisation paths. Repository-level reads of the column (for the admin panel's member list) remain fine.

## Phase 2.7 — Routing and self-assign behaviours + audit

- [ ] **20. `RouteTicketAsync(actorId, ticketId, destinationTeamId)`** in the BLL ticket service. Gated by `IPermissionService.CanRouteTicketAsync`. Writes `audit_events` row `event_type = 'ticket_routed'` in the same DB transaction as the `UPDATE`.
- [ ] **21. `ClaimTicketAsync(actorId, ticketId)`** in the BLL ticket service. Gated by `IPermissionService.CanAssignTicketAsync(actor, ticket, actor)`. Writes `audit_events` row `event_type = 'ticket_assigned'` (`is_self_assign = true`) in the same transaction.
- [ ] **22. `AssignTicketAsync(actorId, ticketId, targetUserId)`** for manager/team_lead/admin overrides. Same gate, same audit row (`is_self_assign = false`).

## Phase 2.8 — UI

- [ ] **23. Team-queue page.** `/teams/{slug}/queue` (or similar). Lists tickets `WHERE team_id = :team_id`. Visibility per §5.1; route / claim / assign actions per §5.2 / §5.3 calling `IPermissionService` from the page-component layer (defence-in-depth alongside service-layer gates).
- [ ] **24. Update existing ticket pages** (`Tickets.razor`, `TicketEdit.razor`) to read/write `project_id`, `team_id`, `reporter_id`, `assignee_id` instead of the dropped string columns.
- [ ] **25. Admin panel skeleton at `/admin`.** Gated by `system_admin == true` only. Sub-pages per [`team-collaboration.md`](../proposals/team-collaboration.md) §7: hierarchy CRUD, membership management, cross-team queue browser, audit-event feed (read-only). Org/team/project write pages from the original step 14 are placed **under `/admin`**, not at top level, so Phase 3 can replace gates wholesale.

## Phase 2.9 — Auto-enrollment future hook (declaration only)

- [ ] **26. Declare `IUserEnrollmentService`** + `EnrollmentRequest` record in `Heimdall.BLL/Enrollment` per [`team-collaboration.md`](../proposals/team-collaboration.md) §8. Bind `INotImplementedEnrollmentService` (throws on call) so misuse fails loudly. **No** caller is wired to it in this phase — this is the futureproofing seam, not a feature.

## Phase 2.10 — Tests, contract handshake, and acceptance

- [ ] **27. pgTAP — schema invariants.** FK integrity, slug uniqueness within parent, cascade rules (`organizations` → `teams` → `projects` → membership), team-role enum values, `tickets.reporter_id IS NOT NULL` invariant, `tickets.team_id IS NOT NULL` invariant.
- [ ] **28. xUnit + Testcontainers — integration tests.** Repository round-trips for every new repo; full §5 policy matrix exercised end-to-end via the BLL `RouteTicketAsync` / `ClaimTicketAsync` / `AssignTicketAsync` paths; `audit_events` rows asserted in the same transaction as ticket writes.
- [ ] **29. xUnit — `Phase2AcceptanceTests`.** Boots the real `Program` via `WebApplicationFactory` + `postgres:18-alpine` Testcontainer, signs in as the bootstrap admin, exercises end-to-end: create org → team → project, add three users with different team roles, post a ticket, route it, claim it, verify audit feed in the admin panel.
- [ ] **30. Document the OpenFGA input contract.** Confirm [`team-collaboration.md`](../proposals/team-collaboration.md) §4 step 17's mapping is implemented as written (in particular: `team_members.role IN ('manager', 'team_lead')` → `team#admin`). [`openfga.md`](../proposals/openfga.md) step 7's tuple-write hook and step 8's backfill job consume this contract directly.

## Phase 2 sign-off

- [ ] All 30 steps merged on `main`.
- [ ] Coverage targets met across every new file (consistent with Phase 1).
- [ ] No regressions on the Phase 1 acceptance test (`Phase1AcceptanceTests` keeps passing as-is).
- [ ] `Authorization:Provider` configuration flag defaults to `"TeamRole"`; the OpenFGA value is reserved (not implemented) for Phase 3.

## Out of scope for Phase 2

- OpenFGA model file, sidecar, SDK integration, tuple writes, policy-based `[Authorize]` → Phase 3 ([`openfga.md`](../proposals/openfga.md)).
- TOTP / WebAuthn / MFA → Phase 4.
- JWT / API tokens → Phase 5.
- Tuple-aware admin write surfaces → Phase 6 ([`openfga.md`](../proposals/openfga.md) step 11).
- Auto-enrollment **implementations** (admin-invite flow, LDAP) → Phase 3+ via `IUserEnrollmentService`.
- Group-role catalogue beyond `system_admin` → not scheduled; revisit when a second group-wide privilege is required.
