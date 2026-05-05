# Proposal: OpenFGA vs Permify as Heimdall's ReBAC Authorization Service

**Status:** **Draft / Planning** (2026-05-04)
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.Web, Heimdall.BLL, Heimdall.DAL, deployment (Render), future PRs
**Decision required:** Should Heimdall adopt **OpenFGA** or **Permify** as its ReBAC sidecar, and on what timeline relative to the Phase 1 RBAC+PBAC work in PR #23?

> This document is **research and planning only**. **No code, package, configuration, or DI changes are made in this PR.** A separate, follow-up PR (or series of PRs) will implement the chosen design once approved. This proposal does **not** edit the merged §10 decision log of PR #23; it supersedes the "(conditional)" framing of Phase 4 in its own decision log below.

---

## 1. Why we're looking at this now

PR #23 ([`security-and-authorization.md`](./security-and-authorization.md)) parked ReBAC at "Phase 4 (conditional)" — *if and only if* a sharing requirement materialised. The user has since pointed out the obvious: Heimdall is a **ticketing system whose entire reason for existing is team collaboration and sharing**, so *"it would be silly not to use ReBAC."* Tickets get assigned, watched, commented on, shared with external collaborators, escalated across teams, and rolled up to org admins. Every one of those is a **relationship**, and relationships are exactly what RBAC + PBAC cannot model without either an attribute-explosion problem or a per-row policy mess.

This proposal therefore promotes ReBAC from "conditional" to **planned Phase 4**, and picks the sidecar.

The user-stated goals from PR #23 §1 are unchanged and still in priority order:

1. **Security** — defense-in-depth, sensible defaults, no foot-guns.
2. **Scalability** — must work for many users, many tickets, many teams, and (as a *future* requirement) a horizontally-scaled web tier. Today `render.yaml` provisions a single free-plan web service; multi-instance is a forward constraint, not a present one.
3. **Performance** — authorization checks are on every request and inside hot paths. The chosen model must be cheap to evaluate and cache-friendly.

References used (in addition to those in PR #23):

- *Zanzibar: Google's Consistent, Global Authorization System* — <https://research.google/pubs/zanzibar-googles-consistent-global-authorization-system/>
- OpenFGA documentation — <https://openfga.dev/docs>
- Permify documentation — <https://docs.permify.co/>

## 2. What "ReBAC" actually buys us for a ticketing system

The following are concrete Heimdall scenarios that **RBAC + PBAC cannot express cleanly** without dragging the application database or the policy code into shapes neither was designed for. ReBAC handles each as a few tuples plus a derived permission.

| Scenario                                                                                                             | Why RBAC+PBAC struggles                                                                                                              | ReBAC shape                                                       |
| -------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------- |
| "Share **this** ticket with **this** external collaborator, read-only."                                              | One-off per-resource grant. Either a new role per ticket (explosion) or an ACL table evaluated by hand-rolled policies.              | One tuple: `ticket:42#viewer@user:alice`.                         |
| "Everyone in **Team A** who is also **assigned** to **Project X** can edit."                                         | Cross-product of two memberships per row. Possible with PBAC but the policy has to do its own joins and cache-bust on either side.   | Intersection: `editor = parent_team.member & parent_project.member`. |
| "Watchers receive notifications but cannot edit."                                                                    | RBAC needs a `Watcher` role *per ticket*, which is meaningless — the relation is to the resource, not to a role catalogue.           | `ticket:42#watcher@user:bob`; `can_view = watcher \| ...`.        |
| "Org admin inherits everything; team lead inherits within their team only; project member inherits within project."  | Multi-level inheritance chain. PBAC can express it, but every check walks the chain in app code on every call.                       | `parent_org`, `parent_team`, `parent_project` relations + userset rewrites. |
| **Group-of-groups / nested teams** (Team A is a sub-team of Org Foo).                                                | Recursive group membership. Possible in SQL with `WITH RECURSIVE`, but every authz check now runs a recursive query.                  | `team#member` includes `parent_org#member` via tuple-to-userset.  |
| **Per-resource ACL** without schema explosion (50k tickets × 5 share types).                                          | A `ticket_share(ticket_id, user_id, perm)` table is fine until you also need group shares, link shares, role shares — then it forks. | Tuples are uniform `(object, relation, subject)`; storage is one shape. |

Phrased differently: every one of the above is naturally expressed as **"who is related to what, and how"**, which is the Zanzibar primitive. RBAC stays useful for **coarse role gates** ("is this user a `SystemAdmin` at all?"), and PBAC stays useful for **attribute logic** ("is the ticket in a closed state?"). ReBAC is the layer that handles **sharing semantics**, and that's the part of Heimdall that currently has no good answer.

## 3. Heimdall relationship model sketch

The same conceptual schema in both DSLs. Object types and relations:

- `organization` — relations: `member`, `admin`
- `team` — relations: `parent_org`, `member`, `lead`
- `project` — relations: `parent_team`, `member`, `viewer`
- `ticket` — relations: `parent_project`, `creator`, `assignee`, `watcher`, `commenter`, `editor`, `viewer`
- Derived permissions on `ticket`: `can_view`, `can_comment`, `can_edit`, `can_assign`, `can_delete`

### 3.1 OpenFGA DSL sketch

```fga
model
  schema 1.1

type user

type organization
  relations
    define member: [user]
    define admin: [user]

type team
  relations
    define parent_org: [organization]
    define member: [user] or member from parent_org
    define lead: [user]

type project
  relations
    define parent_team: [team]
    define member: [user] or member from parent_team
    define viewer: [user] or member

type ticket
  relations
    define parent_project: [project]
    define creator: [user]
    define assignee: [user]
    define watcher: [user]
    define commenter: [user]
    define editor: [user]
    define viewer: [user]

    define can_view:
      viewer or watcher or commenter or editor or assignee or creator
      or member from parent_project
      or lead from parent_project as parent_team
      or admin from parent_project as parent_team as parent_org
    define can_comment: commenter or editor or assignee or can_view
    define can_edit: editor or assignee or creator
      or lead from parent_project as parent_team
      or admin from parent_project as parent_team as parent_org
    define can_assign: can_edit
    define can_delete: creator
      or admin from parent_project as parent_team as parent_org
```

(The `as parent_team as parent_org` walk is shorthand for the chained tuple-to-userset rewrite; final wording will follow the OpenFGA DSL grammar in force at adoption time.)

### 3.2 Permify schema sketch

```perm
entity user {}

entity organization {
  relation member  @user
  relation admin   @user
}

entity team {
  relation parent_org @organization
  relation member     @user
  relation lead       @user

  permission org_member = parent_org.member
  permission effective_member = member or org_member
}

entity project {
  relation parent_team @team
  relation member      @user
  relation viewer      @user

  permission effective_member = member or parent_team.effective_member
  permission effective_viewer = viewer or effective_member
}

entity ticket {
  relation parent_project @project
  relation creator        @user
  relation assignee       @user
  relation watcher        @user
  relation commenter      @user
  relation editor         @user
  relation viewer         @user

  permission can_view =
      viewer or watcher or commenter or editor or assignee or creator
      or parent_project.effective_member
      or parent_project.parent_team.lead
      or parent_project.parent_team.parent_org.admin
  permission can_comment = commenter or editor or assignee or can_view
  permission can_edit    = editor or assignee or creator
                           or parent_project.parent_team.lead
                           or parent_project.parent_team.parent_org.admin
  permission can_assign  = can_edit
  permission can_delete  = creator or parent_project.parent_team.parent_org.admin
}
```

Both schemas express the same intent. The ergonomic difference: Permify lets you name intermediate permissions (`effective_member`) and reuse them; OpenFGA expresses the same idea via userset rewrites in the relation definitions. For our use the difference is taste, not capability.

## 4. Side-by-side comparison: OpenFGA vs Permify

| Dimension                         | OpenFGA                                                                        | Permify                                                                          |
| --------------------------------- | ------------------------------------------------------------------------------ | -------------------------------------------------------------------------------- |
| Origin / governance               | **CNCF Sandbox project** (donated by Auth0 / Okta).                            | Independent OSS, maintained by Permify Inc.                                      |
| License                           | Apache 2.0.                                                                    | Apache 2.0.                                                                      |
| Modeling DSL                      | OpenFGA DSL (`model.fga`); JSON form available; userset rewrites.              | Permify schema language (`schema.perm`); named permissions; rule blocks for ABAC. |
| Hosting model                     | Standalone server, gRPC + HTTP/JSON, run as sidecar/service.                   | Standalone server, gRPC + HTTP/JSON, run as sidecar/service.                     |
| Storage backends                  | In-memory, **PostgreSQL**, MySQL, SQLite (newer).                              | In-memory, **PostgreSQL**, MySQL.                                                |
| .NET SDK status                   | **Official `OpenFga.Sdk` NuGet package** maintained by the OpenFGA team.        | Community / vendor-published .NET SDK; verify maturity and maintainership on adoption. |
| Consistency model                 | Zanzibar-style **consistency tokens** ("zookies") returned from writes; supports `MINIMIZE_LATENCY` and `HIGHER_CONSISTENCY` query modes. | Snapshot tokens with read-after-write semantics; configurable consistency per request. |
| Contextual tuples / ad-hoc facts  | First-class `contextual_tuples` on `Check` — caller can pass tuples that aren't persisted. | Supported via attributes / context blocks; less ergonomic than OpenFGA's pattern. |
| Caching / check ergonomics        | `Check`, `BatchCheck`, `ListObjects`, `ListUsers`; cacheable per zookie.        | `Check`, `LookupEntity`, `LookupSubject`, `SubjectPermission`; expand API for debugging. |
| Bulk operations                   | `BatchCheck` (multiple tuples in one round-trip), `ListObjects` for "what can user X see".  | `LookupEntity` / `LookupSubject` cover the same shapes.                          |
| Audit / decision logging          | Server emits structured logs; integrators typically tee `Check` decisions to their own audit store. | Built-in decision logs / data changes log; OpenTelemetry traces include decisions. |
| Multi-tenant story                | **`stores`** are first-class — one OpenFGA server hosts many isolated stores; trivial to scope per tenant. | Multi-tenancy via **tenants**, also first-class on the API.                       |
| Observability                     | OpenTelemetry traces and metrics; Prometheus endpoint; structured logs.        | OpenTelemetry traces and metrics; Prometheus endpoint; structured logs.          |
| Operational footprint on Render   | Single Go binary; modest memory (low-hundreds MB at our scale); a Postgres logical DB.       | Single Go binary; comparable footprint; a Postgres logical DB.                   |
| Maintainer / community velocity   | High; CNCF Sandbox cadence; broad enterprise contributor base; active Slack/CNCF channels. | Active vendor-led development; smaller but engaged community.                    |
| Production references             | Public adopters include Auth0/Okta FGA (managed offering built on it), Docker, Twilio, Italo, Wolt, ÷ others (verify on adoption). | Public adopters include several SaaS vendors (verify on adoption); fewer hyperscale references than OpenFGA. |
| Migration burden if we ever swap  | Tuples and schema are conceptually portable; both are Zanzibar-shaped. The work is rewriting the DSL file and re-importing tuples — non-trivial but bounded **iff** application code talks to a thin `IAuthorizationService` adapter rather than vendor types directly. | Same shape, same caveat.                                                          |

(All "current at time of writing" — version pinning is **explicitly out of scope** for this document. Renovate Bot manages versions. Verify maturity, latest release cadence, and the .NET SDK story on adoption.)

**The decision-relevant differences for *us specifically*:**

1. **CNCF Sandbox governance** materially derisks long-term project survival for a small team that cannot afford to vendor-own its authz layer. OpenFGA wins on this axis cleanly.
2. **Official, vendor-maintained .NET SDK** vs community SDK is a real ergonomics gap when the codebase is .NET 10 with one engineer in the loop. OpenFGA wins.
3. **Postgres support and operational footprint are a wash.** Both run a single Go binary against a Postgres logical DB. Either fits within Render's small-instance budget once we move off the free plan.
4. **Permify's named-permission ergonomics are slightly nicer** for hand-edited schemas, and its decision-log endpoint is built-in rather than DIY. Real, but smaller than 1–2 above.
5. **Production references** are deeper for OpenFGA, including hyperscale adopters running essentially the same shape we're proposing.

## 5. Decision matrix against our goals

Same ★ rubric as PR #23 §7.3.

| Option                                   | Security                                | Scalability                                  | Performance                                      | Operational cost                          |
| ---------------------------------------- | --------------------------------------- | -------------------------------------------- | ------------------------------------------------ | ----------------------------------------- |
| **Built-in only (RBAC + PBAC, status quo from PR #23)** | ★★★★ (proven primitives, in-process, no new attack surface) | ★★★ (sharing-shaped checks force schema/policy hacks at scale) | ★★★★★ (in-process, no hop)                       | ★★★★★ (nothing new to run)                |
| **OpenFGA sidecar**                      | ★★★★ (CNCF-governed, audited, purpose-built) | ★★★★★ (Zanzibar lineage; designed for hyperscale) | ★★★ (network hop, mitigated by short-TTL cache + `BatchCheck` + contextual tuples) | ★★ (extra service + Postgres logical DB + secrets + monitoring) |
| **Permify sidecar**                      | ★★★★ (Apache 2.0, decision logs first-class) | ★★★★ (same lineage; smaller production proof) | ★★★ (network hop, mitigated similarly)            | ★★ (same shape as OpenFGA)                |

Built-in stays best on cost and raw latency; either sidecar is materially better on *expressivity for sharing*, which is the actual blocker.

## 6. Integration architecture for Heimdall

No code in this proposal — just the shape:

- **New Render service** — the authz sidecar runs as its own Render web service, configured **private-networking only** (`type: pserv` or equivalent), never publicly exposed. Heimdall.Web reaches it over Render's internal network.
- **Backed by PostgreSQL** — a separate logical DB (or, at minimum, a separate schema) for authz tuples. The authz service owns this store; **the application database remains Dapper-first**, unchanged. Authz tuples are *not* shoehorned into app tables.
- **`IAuthorizationService` adapter** in `Heimdall.BLL` (or a new `Heimdall.Authorization` project) wrapping the SDK's `Check`, `BatchCheck`, and `ListObjects` (or Permify equivalents). All call sites depend on the adapter, never the vendor SDK directly. This is what makes §4's "migration burden" row real rather than aspirational.
- **Short-TTL in-process cache** for hot checks, consistent with PR #23 §9.2's "short-TTL caching to keep revocation latency bounded". Suggested ceiling: a few seconds, keyed by `(subject, relation, object, consistency_token)`. Long enough to absorb a list view's burst of checks; short enough that revocation is observable within the same UX cycle.
- **Tuple-write strategy** — domain events emitted by `Heimdall.BLL` on the operations that change relationships (ticket created → `creator`; ticket assigned → `assignee`; team membership changed → `team#member`; ticket shared → `viewer` / `editor`) translate to authz writes. A periodic **reconciliation job** re-derives expected tuples from app state and corrects drift. Drift is expected; the system must tolerate it without hand-editing tuples.
- **Failure mode: closed (deny) on authz outage.** If the sidecar is unreachable, `Check` returns deny. A clearly-flagged **break-glass path** for `SystemAdmin` (signed by a separate process, audited loudly) prevents total lockout if the sidecar is down during recovery. This is a deliberate inversion of the convenience-first default and is part of what makes "security first" credible.
- **Observability** — emit `authz.check` spans with attributes `(subject, relation, object, allowed, latency_ms, cache_hit)`; counter `authz.check.total{allowed}`; histogram `authz.check.duration`. Trace context is propagated to the sidecar so end-to-end traces span the hop.
- **Local dev** — `docker-compose.yml` gains the sidecar + a tuple-seeding step keyed off the existing seed data, so `docker compose up` produces a fully-functional authorized environment.

## 7. Risks and mitigations

| Risk                                                                                  | Mitigation                                                                                                                                                                                            |
| ------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Network-hop latency on every check                                                    | Short-TTL cache + `BatchCheck`/`LookupEntity` for list views + contextual tuples for ad-hoc facts; budget p99 against a real workload, not a synthetic one.                                          |
| Tuple drift between app DB and authz store                                            | Event-driven writes from `Heimdall.BLL` + scheduled reconciliation job that diff-and-corrects. Tuples are derivable from app state by construction; the authz store is a denormalised projection, not a system of record. |
| Cold-start / deploy ordering (web up, authz not yet)                                  | Render service dependency / health-check gate on Heimdall.Web; readiness probe on the sidecar; deny-closed during the gap.                                                                          |
| Vendor / project longevity                                                            | Thin `IAuthorizationService` adapter so swapping the sidecar is bounded work (see §4 *Migration burden* row). Bias toward CNCF-governed project (§4 row 1) reduces the probability we'll need to swap at all. |
| Backup / restore semantics                                                            | Authz Postgres logical DB included in the same backup window as the app DB; restore runbook documents the **order** (authz before web) and acknowledges that cross-store referential integrity is **informal** — drift after a partial restore is corrected by the reconciliation job. |
| Local dev story                                                                        | Sidecar in `docker-compose.yml`; tuple seeder runs after migrations; CI uses the same compose file. No "works on my machine" gap between dev and deployed.                                          |
| Schema-evolution / DSL versioning                                                     | DSL file (`docs/authz/model.fga` or `schema.perm`) lives in-repo and is versioned; OpenFGA's `authorization_model_id` (or Permify's schema versioning) is recorded with each `Check`; migrations are PR-reviewed like any other contract. |

## 8. Recommendation

**Adopt OpenFGA.**

Justification, on this repo's specific axes:

- **CNCF Sandbox governance** is the single biggest risk-reducer for a one-engineer-in-the-loop, .NET-10 codebase that cannot afford to vendor-own its authz layer. (§4 row 1.)
- **Official `OpenFga.Sdk` .NET package** maintained by the OpenFGA team beats a community .NET SDK on day-one ergonomics and on five-year survivability. (§4 row 6.)
- **Production references** are deeper and include patterns close to ours (per-resource sharing on top of org/team hierarchies). (§4 row 14.)
- **PostgreSQL backend on Render** is supported and operationally indistinguishable from Permify's. (§4 rows 5 & 12.)
- **Contextual tuples** give us a clean way to authorize against transient facts (e.g. "this request is acting on behalf of role X") without persisting them, which is materially nicer than Permify's attribute/context blocks for our use. (§4 row 8.)

It is a **close call on developer ergonomics** — Permify's named-permission DSL and its built-in decision-log endpoint are genuinely nicer in isolation — but governance and SDK maturity dominate for us.

**Reversal trigger:** *if* the official OpenFGA .NET SDK becomes unmaintained, *or* CNCF removes OpenFGA from its project list, *or* a concrete feature we need (e.g. richer ABAC rules, decision-log retention) ships in Permify and not OpenFGA, revisit.

## 9. Phasing relative to PR #23

> **Note (2026-05-05):** PR #23's phasing was substantially restructured after this proposal was first drafted. The phase descriptions below have been updated to reflect the current sequence in [`security-and-authorization.md`](./security-and-authorization.md) §9.3 and the implementation plan in [`openfga.md`](./openfga.md). The selection rationale in §§1–8 is unchanged. **For the authoritative, ordered implementation steps, see [`openfga.md`](./openfga.md) §3** — that document supersedes the rough sub-step list previously printed here.

The current proposal-set sequence is:

- **Phase 1 — Authenticated foundation** ([`security-and-authorization.md`](./security-and-authorization.md) §9.3 Phase 1). Identity + cookie auth, Dapper user store, MailKit/MimeKit email seam, **"authenticated-only" placeholder gate** (no RBAC, no PBAC, no roles/permissions/groups tables — those were dropped from Phase 1 because OpenFGA replaces them end-to-end and shipping them would mean migrating them away).
- **Phase 2 — Team collaboration data model** ([`team-collaboration.md`](./team-collaboration.md)). Organizations, teams, projects, membership tables, ticket reporter/assignee FKs. Strictly data-and-domain; ships zero ReBAC changes; the temporary `system_admin` write-side gate from `team-collaboration.md` §3 covers Phase-2-only privilege-escalation risk on write surfaces.
- **Phase 3 — OpenFGA ReBAC** ([`openfga.md`](./openfga.md)). Replaces the Phase-1 placeholder with policy-based `[Authorize]` resolved through OpenFGA `Check()`. Tuples are backfilled directly from the Phase-2 `*_members` and `tickets` rows (per the mapping in [`team-collaboration.md`](./team-collaboration.md) step 17), **not** from any RBAC role/group state — there is no such state under the current sequencing.
- **Phase 4 — MFA**, **Phase 5 — API + tokens**, **Phase 6 — Admin UI** all follow OpenFGA so each goes through one policy mechanism.

The OpenFGA selection in §8 of this proposal is unaffected by the resequencing; only the *order* and *upstream data sources* of the implementation steps changed.

## 10. Open questions and decision log

### Open questions

1. **Multi-tenant scoping.** Does `organization` map 1:1 to the OpenFGA *store* (one store per tenant), or do we use a single store and rely on the `organization` object type for scoping? Stores give hard isolation; single-store gives easier cross-org admin tooling.
2. **Decision-log retention.** How long do we keep authz decision logs? (Suggest: align with PR #23 §10's `audit_events` retention — 365 days hot + cold archive — and decide whether to *also* tee `Check` decisions into our own `audit_events`.)
3. **GDPR / user-data export & delete.** Do authz tuples participate in subject-access export and right-to-erasure? Tuples reference user IDs; on delete we must either tombstone or rewrite. Specify before any tuples touch real users.
4. **Authz Postgres colocation.** Same Postgres instance (separate logical DB) as the app, or a separate instance? Same-instance is cheaper and simpler on Render; separate instance gives blast-radius isolation. Probably same-instance until we have a reason otherwise.
5. **Tuple-write transport.** Domain events → tuple writes via (a) explicit calls in `Heimdall.BLL`, (b) outbox pattern with a worker, or (c) Postgres CDC → tuples. (a) is simplest; (b) is most robust; (c) is most decoupled but adds infra. Probably (a) → (b) as scale grows; (c) deferred unless justified.
6. **Schema-evolution / migration story.** How do we ship a DSL change that requires a tuple migration (e.g. introducing `parent_team` after the fact)? Need a documented pattern: write new model, migrate tuples under both, flip checks, retire old model.
7. **Break-glass mechanics.** Exactly *who* signs the break-glass path in §6, *how* is it audited, and *how* is the credential rotated? Do not leave this to "we'll figure it out".

### Decision log

| Date       | Decision                                                                                                                                                  |
| ---------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 2026-05-04 | Proposal drafted; supersedes the "(conditional)" framing of ReBAC in PR #23 §9.3 Phase 4. Recommends **OpenFGA** as the sidecar, deployed in Phase 4 per §9. Awaiting review. |
| 2026-05-05 | §9 phasing rewritten to match the resequenced proposal set: RBAC+PBAC tables were dropped from Phase 1 of [`security-and-authorization.md`](./security-and-authorization.md); OpenFGA now backfills tuples from the Phase-2 `*_members` and `tickets` rows in [`team-collaboration.md`](./team-collaboration.md), not from RBAC role/group state. Authoritative implementation steps moved to [`openfga.md`](./openfga.md) §3; this section now points there. The §8 OpenFGA-vs-Permify selection rationale is unchanged. |

---

**Next step:** Review and resolve the open questions in §10, then sequence the Phase 4 implementation PRs per §9 once Phases 1–3 from PR #23 have landed.
