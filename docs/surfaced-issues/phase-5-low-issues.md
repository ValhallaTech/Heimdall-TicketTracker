# Phase 5 — Surfaced Low-severity Issues

**Status:** Tracking. Issues collected from SME reviews on PR #61 (Phase 5.5 + 5.6) that were judged Low or informational and deliberately deferred. Each entry records the source PR, the originating reviewer agent, the affected files, and a suggested remediation. Pick up in a follow-up PR (or in the Phase 5.7 runbook where indicated).

## How to use this document

- One section per finding, severity-tagged `[LOW]` or `[NOTE]` / `[INFO]`.
- Each finding cites the **PR it was surfaced in** so reviewers can trace the original review thread.
- Findings marked `[RUNBOOK]` are operational and should be absorbed by `docs/runbooks/jwt-signing-key-rotation.md` (Phase 5.7 step 22) rather than fixed in code.

---

## [LOW] L1 — `RedisAccessTokenDenylist` emits no hit/miss tracing

- **Surfaced in:** PR #61 (Phase 5.5 + 5.6)
- **Reviewer:** Redis Expert
- **File:** `src/Heimdall.BLL/Tokens/RedisAccessTokenDenylist.cs`
- **Description:** `RedisCacheService` (per PR #52) logs `Debug` "GET hit" / "GET miss" on every cache access. `RedisAccessTokenDenylist.IsDeniedAsync` is silent end-to-end on a miss. The deny-hit is logged at `Information` by `JwtBearerDenylistEvents.OnTokenValidatedAsync`, but parity with the cache-service pattern would let operators enable a single `Debug` namespace to see both surfaces.
- **Suggested remediation:** Add `_logger.LogDebug` for hit and miss inside `IsDeniedAsync`. Keep the hit log on the bearer-events side at `Information` (auditable signal).
- **Priority:** Low — hook fires on every authenticated request, so `Debug` is the correct level and most operators won't enable it.

---

## [LOW] L2 — No metrics for deny-hit, deny-miss, outage

- **Surfaced in:** PR #61
- **Reviewer:** Redis Expert
- **Files:** `src/Heimdall.BLL/Tokens/RedisAccessTokenDenylist.cs`, `src/Heimdall.DAL/Caching/RedisCacheService.cs`
- **Description:** Neither `RedisCacheService` nor `RedisAccessTokenDenylist` currently emits `System.Diagnostics.Metrics.Meter` counters. The denylist outage rate and hit rate are exactly the kind of signal SOC dashboards want.
- **Suggested remediation:** Add a shared `Meter("Heimdall.Tokens")` with three counters (`deny_hit`, `deny_miss`, `deny_outage`) and a matching `cache_hit` / `cache_miss` for the cache service. Wire whatever ingest the rest of Heimdall uses (Prometheus, OTLP, etc.) — first need to confirm if there is one yet.
- **Priority:** Low — not a regression; emits a follow-up alongside any future observability work.

---

## [LOW / RUNBOOK] L3 — Denylist key/value confidentiality

- **Surfaced in:** PR #61
- **Reviewer:** Redis Expert + Security Reviewer (concurring)
- **File:** `src/Heimdall.BLL/Tokens/RedisAccessTokenDenylist.cs`
- **Description:** Keys are `heimdall:token-denylist:<jti>` (random GUID — non-sensitive). Values are the literal revoke reason (`logout` / `admin_revoke` / `family_replay`). An attacker with Redis read access could enumerate revoked `jti`s and infer logout timing patterns. Not a client-side vulnerability; defense is network isolation + AUTH/TLS on the shared Redis instance.
- **Suggested remediation:** Capture the requirement in the Phase 5.7 step 22 runbook (`docs/runbooks/jwt-signing-key-rotation.md`) — Redis ACL + AUTH + TLS posture for the deployed cluster.
- **Priority:** Runbook only; no code change.

---

## [LOW] L4 — `HandleLogoutAsync` swallows denylist write failures

- **Surfaced in:** PR #61
- **Reviewer:** Redis Expert
- **File:** `src/Heimdall.Web/Endpoints/ApiAuthEndpoints.cs` (around the `DenyAsync` catch block in `HandleLogoutAsync`)
- **Description:** A block comment in the handler describes the denylist write as *"load-bearing for the logout's primary contract"*, but the code catches non-cancellation exceptions from `DenyAsync` and continues to the family-revoke path. This is defensible (returning 500 on Redis outage during logout is poor UX, and the family-revoke still durably kills the session for the refresh-flow purposes), but the doc comment overstates the durability guarantee.
- **Suggested remediation:** Soften the comment — call it "best-effort, with the durable invariant carried by the family-revoke."
- **Priority:** Low — documentation polish.

---

## [LOW] L5 — Logout endpoint observability gap for missing `jti`

- **Surfaced in:** PR #61
- **Reviewer:** Security Reviewer
- **File:** `src/Heimdall.Web/Endpoints/ApiAuthEndpoints.cs` (`HandleLogoutAsync`)
- **Description:** When the bearer token lacks a `jti` claim, no denylist write and no `token.access.revoked` audit row is produced, but the endpoint still returns 204 and the refresh-family revoke still runs. A misconfigured client posting a non-Heimdall-issued bearer (or a future scheme without `jti`) would silently succeed.
- **Suggested remediation:** Emit a structured warning log + an audit row of type `token.access.revoke_skipped` (or similar) so SOC can see "logout that did not deny anything."
- **Priority:** Low — Heimdall-issued tokens always carry `jti` (asserted by `JwtTokenIssuer`), so the practical exposure is limited to mis-configured clients.

---

## [LOW] L6 — `RequireAuthorization(...).RequireAuthorization(...)` chain on `POST /assign` is fragile

- **Surfaced in:** PR #61
- **Reviewer:** Security Reviewer
- **File:** `src/Heimdall.Web/Endpoints/ApiTicketsEndpoints.cs` (assign endpoint registration)
- **Description:** In ASP.NET Core 7+ (the repo targets net10.0), `RouteHandlerBuilder.RequireAuthorization` appends each `AuthorizeAttribute` to endpoint metadata, and `AuthorizationMiddleware` combines all policies via `AuthorizationPolicy.CombineAsync` — so both `RequireMfa` AND `CanAssignTicket` are enforced. **Behavior verified correct.** However, the call shape is brittle to future contributors who might assume a single attribute wins, and the OR-vs-AND semantics aren't obvious from the call site.
- **Suggested remediation:** Define a composed policy `AuthorizationPolicies.CanAssignTicketWithMfa` (combining `RequireMfa` + `CanAssignTicket`) and apply a single `RequireAuthorization("CanAssignTicketWithMfa")` on the assign endpoint. Single point of truth, regression-safe.
- **Priority:** Low — refactor for clarity, not a correctness bug.

---

## [NOTE] N1 — OpenFGA permission naming convention (`view` vs `can_view`)

- **Surfaced in:** PR #61 (flagged High by OpenFGA Expert but doc-level only — wiring is internally consistent)
- **Reviewer:** OpenFGA Expert
- **Files:** `authz/model.fga`, `src/Heimdall.Web/Authorization/AuthorizationConfiguration.cs`, `src/Heimdall.Web/Endpoints/ApiTicketsEndpoints.cs`
- **Description:** Proposal/spec text (`docs/implementation/phase-5-checklist.md` step 14 and `docs/proposals/openfga.md` §3) refers to `can_view`, `can_edit`, etc., but `authz/model.fga` declares them as bare `view`, `edit`, `assign`, `comment`, `delete`. The endpoint and the policy registration correctly pass the bare names — runtime call against the model is consistent. The OpenFGA-skill guidance prefers `can_*` prefixes on computed permissions to keep them syntactically distinct from direct relations (`viewer`, `member`, `admin`).
- **Suggested remediation:** Rename in `authz/model.fga`: `view → can_view`, `edit → can_edit`, `assign → can_assign`, `comment → can_comment`, `delete → can_delete`. Update `AuthorizationConfiguration.cs`, all call sites, and `authz/store.fga.yaml` test assertions in lock-step. Crosses Phase 5.6 scope — defer.
- **Priority:** Note — doc/hygiene; no behavior change.

---

## [NOTE] N2 — List endpoint defensively swallows `ListObjectsAsync` exceptions

- **Surfaced in:** PR #61
- **Reviewer:** OpenFGA Expert
- **File:** `src/Heimdall.Web/Endpoints/ApiTicketsEndpoints.cs` (list handler around the `ListObjectsAsync` try/catch)
- **Description:** The handler catches non-cancellation exceptions from `ListObjectsAsync`, logs a warning, and returns an empty page. The adapter (per Phase 3.5 step 10) is supposed to deny-closed-swallow already, so this catch is defense-in-depth. It returns empty (deny-closed) rather than 200-with-content, so it does not soften deny-closed.
- **Suggested remediation:** Keep as-is; the defensive layer is acceptable. Optional: add a unit test confirming an exception from the adapter results in an empty page rather than a 500.
- **Priority:** Note — verified safe.

---

## [INFO] I1 — `Swashbuckle.AspNetCore.SwaggerUI` dependency vs spec wording

- **Surfaced in:** PR #61
- **Reviewer:** Security Reviewer
- **File:** `src/Heimdall.Web/Heimdall.Web.csproj`
- **Description:** The Phase 5.6 checklist step 16 explicitly says "no third-party Swashbuckle dependency"; the implementation pulled in `Swashbuckle.AspNetCore.SwaggerUI` (UI assets only — not the doc generator, which remains `Microsoft.AspNetCore.OpenApi`). The split is reasonable because built-in `Microsoft.AspNetCore.OpenApi` has no UI of its own.
- **GitHub Advisory DB:** No known vulnerabilities for either package.
- **Suggested remediation:** Either (a) accept the deviation and amend the checklist wording to "no third-party Swashbuckle **doc-generator** dependency", or (b) replace the UI with `ReDoc`-style static assets or the eventual Microsoft-shipped UI sidecar. Orchestrator decision.
- **Priority:** Informational — orchestrator/owner decision.

---

## [LOW / RUNBOOK] R1 — Redis `maxmemory-policy` must be `noeviction` for the denylist

- **Surfaced in:** PR #61 (flagged High by Redis Expert as **operational**, not a code issue)
- **Reviewer:** Redis Expert
- **File:** `docs/runbooks/jwt-signing-key-rotation.md` (Phase 5.7 step 22 — not yet authored)
- **Description:** The denylist's correctness depends on entries surviving until their TTL. Under `allkeys-lru`, `allkeys-lfu`, `allkeys-random`, `volatile-lru`, `volatile-lfu`, `volatile-ttl`, or `volatile-random`, a memory-pressure event can evict a denylist key before `exp`, silently re-admitting a revoked token. Render's managed Key-Value default is `allkeys-lru` on the free tier and `noeviction` on paid tiers — must verify production plan.
- **Suggested remediation:** In the Phase 5.7 step 22 runbook, mandate `maxmemory-policy noeviction` on the shared Redis instance, document the failure mode under any eviction-allowing policy, and either pin the policy in `render.yaml` or assert it via a startup health check.
- **Priority:** Runbook only — code change not required if operator commitment is documented.

---

## Footnotes

- All findings here are intentionally deferred — they were Low/Note/Info severity and did not block PR #61 from being marked draft-ready.
- Medium findings from the same review pass are tracked in PR #61 directly (system_admin parity, cancellation bridging, `ObjectDisposedException` handling, `HandleAssignAsync` 404 discrimination, `HandleGetByIdAsync` policy-allow / DB-miss drift, defense-in-depth OpenAPI env gating).
- High and Critical findings were fixed in-line in PR #61's review commits.

---

## [LOW / TEST-INFRA] T1 — Autofac `ConfigureTestContainer<ContainerBuilder>` is silently ignored under minimal hosting

- **Surfaced in:** PR #61 (during Phase 5.6 integration test authoring)
- **Reviewer:** C# Unit Test Engineer
- **Files:** `tests/Heimdall.Web.Tests/Acceptance/HeimdallWebApplicationFactory.cs` (and any test helper relying on `ConfigureTestContainer<ContainerBuilder>`)
- **Description:** `ConfigureTestContainer<ContainerBuilder>` is implemented via `IStartupConfigureContainerFilter`, which only runs under the legacy `Startup` model. Under minimal-hosting `WebApplicationBuilder` (which `Heimdall.Web` uses), this seam is silently ignored — **no Autofac-registered service can be substituted in integration tests**. Affected services include `IAccessTokenDenylist`, `ITicketService`, `ITicketRepository`, `RedisCacheService`, `ITicketMapper`, and any other Autofac-only registration. Tests must instead rely on real implementations + observable DB state (the workaround used in `ApiAuthEndpointsLogoutTests`) or stop using Autofac for services that need test substitution. Eleven `ApiTicketsEndpointsTests` cases are currently `[Fact(Skip="...")]` because of this gap.
- **Suggested remediation:** Either (a) move the affected services from Autofac to the built-in DI container (so `ConfigureTestServices` overrides them) — cleanest path; (b) introduce a custom `IHostBuilder` extension that re-wires the Autofac container override under minimal hosting; or (c) document the constraint and write tests around it. Option (a) is preferred for new code; existing Autofac-only services can move opportunistically.
- **Priority:** Low — test-infrastructure gap. Production behavior is unaffected; only the integration-test substitution pattern is constrained.

---

## [LOW] T2 — One pre-existing `Heimdall.Web.Tests` test fails (unrelated to PR #61 changes)

- **Surfaced in:** PR #61 (final regression suite)
- **Reviewer:** C# Unit Test Engineer
- **Description:** `dotnet test Heimdall.slnx --settings coverlet.runsettings` reports `Web.Tests: 387 passed, 11 skipped, 1 failed`. The single failure is in a test that was NOT authored or modified in PR #61. The test agent did not identify the specific test name before time ran out.
- **Suggested remediation:** Grep CI output for `[FAIL]` or run `dotnet test tests/Heimdall.Web.Tests/Heimdall.Web.Tests.csproj --logger "console;verbosity=normal"` and triage the single failure. File a separate issue if it pre-dates the Phase 5 work.
- **Priority:** Low — does not block PR #61 because the failure is pre-existing.
