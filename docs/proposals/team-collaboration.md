# Proposal: Team Collaboration Data Model (Organizations, Teams, Projects, Memberships, Ticket Owners)

**Status:** **Draft / Planning** (2026-05-04)
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.DAL (migrations + repositories), Heimdall.Core (domain models), Heimdall.BLL (services + Mapster mappers), Heimdall.Web (Blazor pages, ticket-page rewiring), tests/pgtap (FK/uniqueness assertions), tests/Heimdall.* (xUnit)
**Decision required:** What is the data model that turns Heimdall from "single bag of tickets" into "tickets owned by projects, owned by teams, owned by organizations, with explicit user memberships and ticket reporters/assignees" — and in what order do we ship the migrations and code so each commit is independently deployable?
**Depends on:** [`security-and-authorization.md`](./security-and-authorization.md) **Phase 1 complete.** This proposal needs a stable `users` table with stable UUID PKs to anchor every FK introduced below; without it, every step here is rework.

> This document is **research and planning only**. **No code, package, configuration, or DI changes are made in this PR.** A separate, follow-up PR (or series of PRs) will implement the design once approved.

---

## 1. Why we're looking at this now

Heimdall today has a flat `tickets` table with synthetic `reporter` / `assignee` *string* columns (seed data only) and no concept of **organization**, **team**, **project**, or **membership**. Every visible feature beyond the seed demo — sharing, assignment, "my tickets", per-team dashboards, cross-team escalation, external collaborators — is a relationship to one of those missing object types. ReBAC in [`openfga.md`](./openfga.md) **cannot model anything useful** until those object types and the membership rows that connect users to them exist in the database.

This proposal is therefore the **wedge between authentication (Proposal 1) and authorization (Proposal 3)**: it ships the object hierarchy and the people-to-objects edges that OpenFGA will then translate into tuples and policies.

User-stated goal order from [`security-and-authorization.md`](./security-and-authorization.md) §1 is unchanged:

1. **Security** — every new FK is `NOT NULL` (or explicitly nullable with a documented reason), every membership row carries an audit timestamp and inviter ID, no rows escape audit.
2. **Scalability** — slugs are `citext` and unique only *within parent*, so two teams in two different orgs can both be called `platform`. PKs are UUIDs, not bigints, so cross-instance migration and import never collide.
3. **Performance** — every FK gets an index; every uniqueness constraint is composite (`organization_id`, `slug`) so the index doubles as a lookup index.

## 2. Current state

| Area                       | State                                                                                          |
| -------------------------- | ---------------------------------------------------------------------------------------------- |
| Organizations / teams      | **None.** No tables, no domain types, no UI.                                                   |
| Projects                   | **None.** Tickets are not grouped.                                                             |
| Memberships                | **None.** No `*_members` tables; no concept of who belongs to what.                            |
| Ticket reporter / assignee | Synthetic `reporter` / `assignee` *varchar* columns populated by seed data; no FK to `users`.  |
| Domain types               | `Ticket` only (in `Heimdall.Core`). No `Organization`, `Team`, `Project`, or `*Member` types.  |
| Repositories               | Dapper `ITicketRepository` only.                                                               |
| Services                   | `ITicketService` (BLL) + Mapster `ITicketMapper` only.                                         |
| Blazor pages               | Ticket list / detail / create / edit only. No org/team/project/membership pages.               |
| pgTAP tests                | `tickets` schema/uniqueness coverage only.                                                     |

The `reporter0..4` / `assignee0..3` seed strings are **the only existing uses** of those columns, which makes the FK migration in step 8 below safe (it is destructive only against synthetic seed data, which is documented and explicit).

## 3. Authorization stance during Phase 2

This proposal **ships zero authorization changes**. Every Blazor page introduced below is gated by **only** the Phase 1 "authenticated-only" check from [`security-and-authorization.md`](./security-and-authorization.md) §9.3 step 9. Every authenticated user can see and edit every org, team, project, and membership row.

This is **intentional and temporary.** Tightening these surfaces is the entire job of [`openfga.md`](./openfga.md), and trying to do it inside this proposal would either (a) re-introduce the RBAC+PBAC layer Proposal 1 just dropped or (b) ship an in-app authorization layer that OpenFGA replaces a phase later. Either is wasted work.

The membership tables in step 4 carry a `role` column (`owner` / `admin` / `member` / `viewer`) **purely as a label**. It is **not consumed by any authorization check in this phase.** OpenFGA derives tuples from it in Proposal 3.

## 4. Recommendation summary and phased rollout

The 16 steps below are **strictly ordered** by data dependency. Each step's preconditions are all satisfied by earlier steps; any reordering forces either a backfill rollback or an FK on a column that doesn't yet exist.

#### Phase 2.1 — Object hierarchy (migrations only)

1. **Migration: `organizations`.** Columns: `id` UUID PK, `slug` citext **unique**, `name` text, `created_at` timestamptz, `created_by` UUID FK → `users(id)` `ON DELETE RESTRICT`. Index on `slug`.
2. **Migration: `teams`.** Columns: `id` UUID PK, `organization_id` UUID FK → `organizations(id)` `ON DELETE CASCADE`, `slug` citext, `name` text, `created_at`, `created_by` UUID FK → `users(id)`. Composite **unique** on `(organization_id, slug)`. Index on `organization_id`.
3. **Migration: `projects`.** Columns: `id` UUID PK, `team_id` UUID FK → `teams(id)` `ON DELETE CASCADE`, `slug` citext, `name` text, `created_at`, `created_by`. Composite **unique** on `(team_id, slug)`. Index on `team_id`.

#### Phase 2.2 — Membership tables

4. **Migration: membership tables.** Three sibling tables with the same shape:
   - `organization_members` (`user_id` UUID FK → `users(id)` `ON DELETE CASCADE`, `organization_id` UUID FK → `organizations(id)` `ON DELETE CASCADE`, `role` enum {`owner`, `admin`, `member`, `viewer`}, `added_at`, `added_by` UUID FK → `users(id)`). PK `(user_id, organization_id)`.
   - `team_members` (same shape, parent FK is `team_id`). PK `(user_id, team_id)`.
   - `project_members` (same shape, parent FK is `project_id`). PK `(user_id, project_id)`.

   The `role` column is a **label only** in this phase (see §3); OpenFGA derives permissions from it in [`openfga.md`](./openfga.md) step 7.

#### Phase 2.3 — Default-row backfill (so existing seed tickets keep working)

5. **Migration: backfill default org/team/project.** Insert one `heimdall` org, one `default` team in that org, one `default` project in that team — all owned by the bootstrap admin from [`security-and-authorization.md`](./security-and-authorization.md) §9.3 step 8. Idempotent (insert-if-not-exists keyed on slug). This is the only row whose slug is hard-coded; everything else is user-created.

#### Phase 2.4 — Tickets carry their parent project

6. **Migration: add `tickets.project_id` (nullable).** New `project_id` UUID column, FK → `projects(id)` `ON DELETE RESTRICT`, **nullable**. Backfill every existing ticket to point at the default project from step 5.
7. **Migration: alter `tickets.project_id` to NOT NULL.** Separate migration so step 6's backfill is observable mid-deploy and rollback-friendly. Add an index on `project_id`.

#### Phase 2.5 — Tickets reference real users

8. **Migration: add `tickets.reporter_id` and `tickets.assignee_id` (nullable UUID FKs).** Both nullable and FK → `users(id)` `ON DELETE SET NULL`. Backfill from the existing synthetic seed data: the `reporter0..4` and `assignee0..3` strings are mapped to seeded users by lookup, or set to NULL if no user matches. **This step is destructive against synthetic seed data and that is acceptable**, because the only consumers of those strings are the seed migration and the Blazor display code; no production data exists yet. Document the destructive-against-seed-data nature in the migration's XML doc comment so future readers don't think it touches real customer data.
9. **Migration: drop the legacy `tickets.reporter` / `tickets.assignee` varchar columns.** Separate migration so steps 8 and 9 can be deployed in sequence and the gap is observable.

#### Phase 2.6 — Domain, DAL, BLL

10. **Domain models in `Heimdall.Core`:** `Organization`, `Team`, `Project`, `OrganizationMember`, `TeamMember`, `ProjectMember`. Update `Ticket` to carry `ProjectId`, `ReporterId`, `AssigneeId` (replacing the legacy string fields).
11. **DAL repositories in `Heimdall.DAL`:** Dapper-based `IOrganizationRepository`, `ITeamRepository`, `IProjectRepository`, and an `*MemberRepository` per membership table. Wire all of them into `AddDal()` per the repo's existing convention. `ITicketRepository` updated to read/write the three new ID columns.
12. **BLL services:** `IOrganizationService`, `ITeamService`, `IProjectService`, plus member-management services (`IOrganizationMemberService` etc.). Mapster mappers regenerated via `Mapster.Tool` per the repo convention; the resulting `*.g.cs` files committed under `src/Heimdall.BLL/Mappers` (the scoped `.editorconfig` already suppresses nullable warnings for that folder).

#### Phase 2.7 — UI

13. **Blazor pages:** org/team/project list + detail + create + edit; member list + invite + remove. **All gated by only the Phase 1 "authenticated-only" check** — every authenticated user can see and edit everything. This is intentional and temporary; OpenFGA tightens it in step 9 of [`openfga.md`](./openfga.md).
14. **Update existing ticket pages and services** to read/write `project_id`, `reporter_id`, `assignee_id` instead of the dropped string columns. Update Mapster mappers to the new shape and regenerate.

#### Phase 2.8 — Tests and contract for Proposal 3

15. **Tests.** pgTAP tests for FK integrity, slug uniqueness within parent, cascade rules (`organizations` → `teams` → `projects` → membership). xUnit tests for every new repository and service. Update existing ticket integration tests to seed orgs/teams/projects/users so the new FKs resolve.
16. **Document the relationship shape** that OpenFGA will consume in Proposal 3:
    - `team#parent_org` ← `teams.organization_id`
    - `project#parent_team` ← `projects.team_id`
    - `ticket#parent_project` ← `tickets.project_id`
    - `organization_members.role = admin` → `organization#admin@user:X`; `= member|viewer` → `organization#member@user:X`
    - `team_members.role = admin` → `team#admin@user:X`; `= member|viewer` → `team#member@user:X`
    - `project_members.role = admin` → `project#admin@user:X`; `= member` → `project#member@user:X`; `= viewer` → `project#viewer@user:X`
    - `tickets.reporter_id` → `ticket#reporter@user:X`
    - `tickets.assignee_id` → `ticket#assignee@user:X`

   This mapping is the **input contract** for [`openfga.md`](./openfga.md) — its model file (step 1) and its idempotent backfill job (step 8) read directly from the tables defined here.

Each step is independently committable; each phase is independently shippable.

## 5. Open questions and decision log

### Open questions

1. **Slug normalization.** Do we lowercase + trim on insert (citext handles case-insensitivity but not trailing whitespace), or accept the raw string and rely on a CHECK constraint? Suggest: trim + lower in the BLL, citext for storage.
2. **Multi-org per user.** Confirmed: one user can belong to many organizations. The schema in step 4 already supports this (PK is `(user_id, organization_id)`); confirm the BLL/UI assume the same.
3. **Cascade vs restrict on `created_by`.** If a user is deleted, do their created orgs/teams/projects cascade-delete (data loss) or restrict (cannot delete user until their objects are reassigned)? Proposal: `RESTRICT` for `created_by`, with a separate "transfer ownership" admin flow handled in Proposal 3's admin UI.
4. **Default-org slug clashing with a user-created org.** The seeded `heimdall` org slug is reserved. Either CHECK-constraint it out of user input or document it. Suggest: document and let an admin rename the seed org if they really want the slug.
5. **Synthetic-seed cleanup.** The legacy `reporter0..4` / `assignee0..3` strings are mapped to seeded users in step 8. Should the seed migration that originally populated them also be revised, or do we leave history in the migrations folder and only fix the destination? Suggest: leave history; FluentMigrator history is append-only.
6. **External collaborators.** Future requirement (sharing a ticket with a user who is *not* a member of any team). Not in scope for this proposal — handled via a `ticket#viewer` tuple in [`openfga.md`](./openfga.md) without needing a new column here.

### Decision log

| Date       | Decision                                                                                                                                                              |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2026-05-04 | Proposal drafted as the wedge between [`security-and-authorization.md`](./security-and-authorization.md) Phase 1 and [`openfga.md`](./openfga.md). 16-step strictly-ordered implementation sequence. Membership `role` column is a label only in this phase; OpenFGA derives permissions from it next. Awaiting review. |

---

**Next step:** Review and resolve the open questions in §5, then sequence the Phase 2 implementation PRs per §4 once Proposal 1 Phase 1 has landed.
