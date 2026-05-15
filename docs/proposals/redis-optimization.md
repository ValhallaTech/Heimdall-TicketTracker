# Proposal: Redis Infrastructure Optimization

**Status:** **Draft / Findings** (2026-05-13)
**Author:** Redis Expert (Copilot, delegated by Orchestrator)
**Scope:** Heimdall.Web (Redis multiplexer wiring), Heimdall.DAL (`RedisCacheService`, `ConnectionStringTranslator`), Heimdall.Core (`CacheKeys`, `ICacheService`), `docker-compose.yml`, `render.yaml`, and the BLL consumers in `TicketService` and `MigrationRunnerExtensions`.
**Decision required:** Which of the staged Redis improvements below should we promote from "findings" to "implement now," and at what phase boundary should the larger architectural items (key-space versioning, hash-tag-aware multi-key invalidation, Redis Streams-backed audit fan-out, Redis Query Engine for ticket search, semantic caching) ship?
**Depends on:** [`security-and-authorization.md`](./security-and-authorization.md) (cache must not become a source of truth for auth state), [`openfga.md`](./openfga.md) §3 step 7 (tuple-write hooks already cooperate with the cache via list-key invalidation), [`phase-5-signing-key-hardening.md`](./phase-5-signing-key-hardening.md) (the JWKS cache is `IMemoryCache`-backed today and is **not** part of this proposal).

> This document is **research and findings only**. The companion PR that publishes it has already landed a small, low-risk set of in-scope changes (timeouts on `ConfigurationOptions`, `LogDebug` hit/miss tracing, `ObjectDisposedException` graceful-degradation guard, and a `redis-cli ping` healthcheck on the dev compose service). **Everything below the "Decision Log" line is planning only**; nothing here implies follow-up work has shipped.

---

## 1. Why we're looking at this

Heimdall's only Redis-resident workload today is a single read-through cache key (`heimdall:tickets:all`) that holds the unfiltered ticket list with a 5-minute TTL. The cache surface is small, but the connection is shared infrastructure that several future workloads will pull through:

- **Phase 5.x signing-key rotation** — JWKS cache is in-process today (`IMemoryCache` + `IJwksCacheInvalidator`); a horizontal-scale-out story will eventually need either Redis pub/sub or a Redis-backed JWKS cache.
- **OpenFGA Phase 3.7 query/iterator caching** — currently sidecar-only; a future "client-side check cache" would also live in Redis.
- **Audit-event fan-out** — `audit_events` is committed atomically with mutations today, but a future `Streams`-backed fan-out (Seq, SIEM) would land here.

Now is the right time to (a) lock down the connection-management posture, (b) document the key-space discipline so we never drift into untyped string concatenation, and (c) name the architectural decisions that future PRs should make rather than re-derive.

## 2. What is in production today

### 2.1 Connection management

- `IConnectionMultiplexer` is registered as a singleton in `src/Heimdall.Web/Program.cs` (~line 96) — correct per StackExchange.Redis guidance.
- After the in-scope companion changes, the multiplexer is configured with: `AbortOnConnectFail = false`, `ClientName = "Heimdall.Web"`, `ConnectTimeout = SyncTimeout = AsyncTimeout = 5_000ms`, `ConnectRetry = 3`, `KeepAlive = 60s`. TLS, AUTH, and `db=` index are negotiated through `ConnectionStringTranslator.ToRedisConfiguration` from the `REDIS_URL` env var (or `ConnectionStrings:Redis`).
- The translator handles `redis://` → `host:port[,password=...]` and `rediss://` → `...,ssl=true`, with URL-decoding of percent-escaped passwords (test `Should_AddSsl_When_RedissScheme`).
- On Render, `REDIS_URL` is supplied by `fromService` against the `keyvalue` resource (`render.yaml` line 23–27); the keyvalue service's `ipAllowList: []` keeps it on Render's private network.
- On `docker-compose.yml`, the dev Redis (`redis:8-alpine`) now exposes a `redis-cli ping` healthcheck. The `web` service still uses `condition: service_started` (not `service_healthy`) — intentional, because `AbortOnConnectFail = false` lets the app boot ahead of Redis without crashing.

### 2.2 Cache surface

- `Heimdall.Core.Caching.CacheKeys` defines exactly one constant: `TicketList = "heimdall:tickets:all"`.
- `Heimdall.DAL.Caching.RedisCacheService` implements `ICacheService` with `GetAsync<T>` / `SetAsync<T>(ttl)` / `RemoveAsync` over `IDatabase.StringGet/Set/KeyDelete`, using `System.Text.Json` with `CamelCase` naming. All calls swallow `RedisException` / `JsonException` / `ObjectDisposedException` and log at warning, so a Redis outage degrades to "always cache miss / no-op write."
- `TicketService` (BLL) uses `GetAsync` / `SetAsync` for read-through with a 5-minute TTL and `RemoveAsync` on every write path.
- `MigrationRunnerExtensions.SeedAsync` invalidates the same key after seeding so a cached pre-seed empty list does not survive bootstrap.

### 2.3 What is **not** Redis-backed today

- ASP.NET Core data protection keys (in-process / file-system).
- Antiforgery / session state (in-process).
- Distributed locks (none in code).
- Idempotency keys (none in code).
- Rate limiting (in-process token bucket).
- OpenFGA check cache (sidecar-side, not client-side).
- JWKS cache (`IMemoryCache`).

## 3. Findings (categorized)

| # | Severity | Area | Finding | Citation |
|---|----------|------|---------|----------|
| F-1 | **Medium** | Connection | `Password` segment is appended verbatim by `ToRedisConfiguration`. SE.Redis configuration parsing splits on `,` and `=`; a generated password containing either character would silently corrupt the configuration. Render's auto-generated passwords use a URL-safe alphabet today, so there is no live impact, but a manually rotated password could trip this. | `src/Heimdall.DAL/Configuration/ConnectionStringTranslator.cs` lines 96–144 |
| F-2 | **Low** | Connection | `db=` index is not honoured. A `redis://host/2` URL silently drops `/2` because the translator never reads `uri.AbsolutePath`. Render and our dev compose both use db 0, so no live impact, but documenting this as a known gap prevents the next person from blindly tacking on `db=`. | `src/Heimdall.DAL/Configuration/ConnectionStringTranslator.cs` lines 121–144 |
| F-3 | **Low** | Connection | The translator does not understand multi-host URLs or `redis-cluster://` style config. Single-node only. Acceptable while we are on Render Key-Value (single-node). | Same file |
| F-4 | **Low** | Cache surface | `CacheKeys` has no version segment. Any breaking change to the `CachedList` schema would deserialize old payloads against the new shape until the TTL elapses. Adding a version (e.g. `heimdall:v1:tickets:all`) up front would be cheap but is a one-shot invalidation event right now. | `src/Heimdall.Core/Caching/CacheKeys.cs` |
| F-5 | **Low** | Cache surface | TTL constants live next to consumers (`TicketService.ListCacheTtl`). With one key this is fine; with three or more, drift between consumer and invalidator is likely. | `src/Heimdall.BLL/Services/TicketService.cs` line 27 |
| F-6 | **Low** | Cache surface | `RedisCacheService` does not honour `CancellationToken` in the body of the call (it only checks before dispatch — added in the companion PR). `IDatabase.*Async` methods do not accept a `CancellationToken`; wrapping with `Task.WaitAsync(ct)` would propagate cancellations end-to-end at the cost of an extra await. Worth doing only if a real caller wants to cancel cache reads. | `src/Heimdall.DAL/Caching/RedisCacheService.cs` |
| F-7 | **Info** | Tests | No Testcontainers-backed integration test exercises `RedisCacheService` against a real Redis. The unit tests mock `IDatabase`, which is enough for contract coverage but does not catch SE.Redis version-pin regressions, RESP3 surprises, or serializer round-trip drift on real `RedisValue` boundaries. | `tests/Heimdall.DAL.Tests/Caching/RedisCacheServiceTests.cs` |
| F-8 | **Info** | Observability | We log hit/miss at `Debug` (added in the companion PR) and warnings on failure, but emit no metrics (`System.Diagnostics.Metrics` counters). Without counters, hit ratio is invisible to Render dashboards. | `src/Heimdall.DAL/Caching/RedisCacheService.cs` |
| F-9 | **Info** | Security | No AUTH on dev compose Redis. Acceptable for a loopback-bound dev container; would be a finding if the port were published. The compose file does not publish `6379:6379`, so the surface is limited to the compose network. | `docker-compose.yml` |
| F-10 | **Info** | Anti-patterns sweep | No `KEYS`, `MONITOR`, `FLUSHDB`, `FLUSHALL`, or unbounded-set patterns in the code. No blocking commands (`BLPOP`, `BRPOP`, `XREAD BLOCK`). No use of `When.NotExists` for lock semantics — but no locks are claimed either. Clean. | repo-wide grep |

Nothing rates Critical or High.

## 4. Recommendations

### 4.1 Land in a follow-up PR (small, low-risk; not in this PR)

1. **Harden `ToRedisConfiguration` against passwords containing `,` or `=`.** Rather than escaping (SE.Redis has no escape syntax for the configuration string), split the password out of the configuration string and assign it via `ConfigurationOptions.Password` after `ConfigurationOptions.Parse`. This requires changing the translator's return type from `string` → `ConfigurationOptions` (or returning a `(string Config, string? Password)` tuple), which is why it is **not** in the companion PR.
2. **Honour `db=` in `redis://host/<n>`** by reading `uri.AbsolutePath`, parsing `<n>` as an integer, and assigning `ConfigurationOptions.DefaultDatabase`. Add unit tests against `redis://h/1` and `rediss://h/0`.
3. **Centralize TTLs alongside keys.** Move `TicketService.ListCacheTtl` next to `CacheKeys.TicketList` as `CacheKeys.TicketListTtl = TimeSpan.FromMinutes(5)`. Trivial, but the right shape before key #2 lands.
4. **Add `System.Diagnostics.Metrics` counters** to `RedisCacheService`: `heimdall.cache.hits`, `heimdall.cache.misses`, `heimdall.cache.failures` tagged by `key_prefix`. Prefix is enough; the full key is high-cardinality and not metric-safe.

### 4.2 Lock down the key-space contract before a second key lands

Once a second cache key is on the roadmap, freeze the schema as:

```
heimdall:v<schemaVersion>:<entity>[:<scope>]:<id-or-collection>
```

- Bump `<schemaVersion>` only on a breaking shape change of the cached payload.
- `<scope>` is a tenant or team identifier when the key is per-tenant; absent for app-global caches.
- For multi-key invalidation that must commit atomically (e.g. invalidating both `tickets:byTeam:{T}` and `tickets:byProject:{P}` on a re-route), wrap the keys in a hash tag — `heimdall:v1:tickets:{team-T}:by-project:P` — so SE.Redis routes them to the same slot if we ever migrate to Redis Cluster. We are on a single-node Render Key-Value today, so the tag is forward-compatibility insurance.

This document is the right place to record that contract; the implementation will be `CacheKeys.cs` helper methods like `TicketsByTeam(Guid teamId)` returning the formatted key.

### 4.3 Decide explicitly before adopting any of these

| Item | Decision needed | Owner |
|------|-----------------|-------|
| Move JWKS cache from `IMemoryCache` to Redis | Required if we ever scale `Heimdall.Web` horizontally on Render. Today's single-instance free plan does not need it. | Orchestrator + Security Reviewer |
| Move data-protection keys to Redis | Required at horizontal scale-out (else cookies / antiforgery tokens stop validating across instances). | Orchestrator + Security Reviewer |
| Distributed lock for the OpenFGA backfill job | Currently safe because the job is idempotent and runs as a single hosted service. A scaled-out web tier would need RedLock.net or a single-node SET NX EX token. | OpenFGA Expert |
| Redis Streams for audit-event fan-out | Today `audit_events` is the system of record (Postgres). Streams would be a *secondary* consumer surface for downstream sinks. Adds operational surface (consumer-group lag monitoring). | DB Expert + Orchestrator |
| Redis Query Engine (RediSearch) for ticket search | Postgres `pg_trgm` / `tsvector` is the documented path in `team-collaboration.md`. Only revisit if Postgres FTS becomes a measured bottleneck. | DB Expert |
| RedisVL / LangCache (semantic caching) | No LLM call sites in the codebase today. Out of scope until one lands. | — |
| Redis Cluster | Out of scope on Render Key-Value (free plan). Forward-compatibility hints (hash tags around invalidation groups) are the only thing worth carrying now. | Orchestrator |

### 4.4 Explicitly **not** recommending

- Switching from `StackExchange.Redis` to `IDistributedCache`. The current `ICacheService` abstraction is typed and serializer-controlled; `IDistributedCache` would force us back into `byte[]` and lose the integration tests' ability to assert payload shape.
- Pinning a specific SE.Redis version. Renovate Bot manages this.
- Adding `Polly` retry around cache reads. The cache layer already swallows on failure — retries would just delay graceful degradation.

## 5. Boundaries

This document does **not** propose any change under `authz/`, `*.fga`, `*.fga.yaml`, `src/Heimdall.DAL/Migrations/`, or any database schema. The only proposed-but-not-implemented behaviour change in §4.1 item 1 is to `ConnectionStringTranslator`, which is configuration-translation code — not schema or migration code.

If §4.3 "Move JWKS cache to Redis" is accepted, that work is bounded to `Heimdall.BLL.Tokens` and would be authored by the C# Coding Expert with security review; this Redis Expert would only review the connection / TTL / key-naming parts.

---

## Decision Log

| Date | Decision | Rationale | Author |
|------|----------|-----------|--------|
| 2026-05-13 | Land §4 companion changes only (timeouts, hit/miss debug logging, `ObjectDisposedException` guard, dev compose healthcheck). | All four are local, reversible, and have unit-test coverage. None changes the public `ICacheService` surface. | Redis Expert (Copilot) |
| 2026-05-13 | Defer §4.1 items 1–4 to a follow-up PR. | Each touches a public API (translator return type, `CacheKeys` shape, `RedisCacheService` constructor for an `IMeter`) and deserves its own focused review. | Redis Expert (Copilot) |
| 2026-05-13 | Defer §4.3 items pending an explicit horizontal-scale-out decision in `security-and-authorization.md`. | Until we run more than one `Heimdall.Web` instance, in-process caches and locks are correct. Premature Redis adoption is operational debt. | Redis Expert (Copilot) |
