# Proposal: KeyDB as a Redis Replacement for Heimdall

**Status:** **Draft / Findings** (2026-06-03)
**Author:** Redis Expert (Copilot, delegated by Orchestrator)
**Scope:** Heimdall.Web (Redis multiplexer wiring), Heimdall.DAL (`RedisCacheService`, `ConnectionStringTranslator`), Heimdall.BLL (`RedisAccessTokenDenylist`), Heimdall.Core (`CacheKeys`, `ICacheService`), `docker-compose.yml`, `render.yaml`, and the deployment topology of the Render `keyvalue` managed service.
**Decision required:** Should Heimdall migrate its key-value backing store from Redis to **KeyDB** (the multithreaded Redis fork)? This document evaluates the question and records a recommendation; it changes no code, packages, configuration, `docker-compose.yml`, or `render.yaml`.

> This document is **research and planning only**. **No code, package, configuration, `docker-compose.yml`, or `render.yaml` changes are made in this PR.** Version numbers cited below are non-binding reference points only — Renovate Bot owns all version pinning. The `Decision Log` at the bottom records what was decided. For the existing Redis posture and the in-flight optimization work, this proposal references [`redis-optimization.md`](./redis-optimization.md) rather than restating it.

---

## 1. Why we're looking at this

KeyDB is frequently floated as a "free performance upgrade" over Redis: same protocol, same clients, but multithreaded. The question for Heimdall is narrow and worth answering on the record: **does Heimdall's actual Redis workload benefit from KeyDB, and is the swap worth it?** Because Heimdall's key-value surface is tiny and uses only basic string operations, the evaluation turns less on raw throughput and more on **maintenance health, version compatibility, and deployment/operational fit on Render**.

This is a forward-looking due-diligence exercise, not a response to any production problem. The current Redis posture is healthy (see [`redis-optimization.md`](./redis-optimization.md)).

## 2. What Heimdall actually uses Redis for today

Two workloads, one client, one command family. All facts below are verified against the current source.

### 2.1 Client and connection management

- Client library: **StackExchange.Redis**, referenced from `src/Heimdall.DAL/Heimdall.DAL.csproj`.
- A single `IConnectionMultiplexer` is registered as a **singleton** in `src/Heimdall.Web/Program.cs` (~lines 96–112) with `AbortOnConnectFail = false`, `ClientName = "Heimdall.Web"`, `ConnectTimeout = SyncTimeout = AsyncTimeout = 5_000ms`, `ConnectRetry = 3`, `KeepAlive = 60`.
- Connection-string translation lives in `src/Heimdall.DAL/Configuration/ConnectionStringTranslator.cs` (`ToRedisConfiguration`): `redis://` → `host:port[,****** `rediss://` → adds `,ssl=true`. **AUTH** is via password; **TLS** is via the `rediss://` scheme (`ssl=true`). No client certificates, no ACL users.

### 2.2 Workload 1 — read-through cache

- `src/Heimdall.DAL/Caching/RedisCacheService.cs` implements `ICacheService` (`src/Heimdall.Core/Interfaces/ICacheService.cs`) using **only** `StringGetAsync` / `StringSetAsync(ttl)` / `KeyDeleteAsync`, with `System.Text.Json` camelCase serialization.
- Graceful degradation: `RedisException` / `JsonException` / `ObjectDisposedException` are swallowed → cache miss / no-op.
- Exactly one key: `heimdall:tickets:all` (`src/Heimdall.Core/Caching/CacheKeys.cs`), **5-minute TTL**, consumed by `src/Heimdall.BLL/Services/TicketService.cs` (read-through + `RemoveAsync` on writes) and invalidated by `MigrationRunnerExtensions.SeedAsync`.

### 2.3 Workload 2 — JWT access-token denylist

- `src/Heimdall.BLL/Tokens/RedisAccessTokenDenylist.cs`, key prefix `heimdall:token-denylist:<jti>`, uses **only** `StringSetAsync(ttl)` / `StringGetAsync`. TTL = token expiry clamped to a 30-second minimum.
- JwtBearer wiring in `Program.cs` (~line 387) distinguishes a **confirmed miss** from a **transport failure** (`RedisConnectionException` / `RedisTimeoutException` propagate) so the outage policy is explicit.

### 2.4 Command surface and what is *not* used

- **Entire codebase command surface: `GET` / `SET` (with `EX`) / `DEL` on string keys.** Nothing else.
- **Not used:** pub/sub, streams, Lua / functions, RediSearch / Redis Query Engine, any module, transactions / `MULTI`, distributed locks, cluster, sentinel. A repo-wide sweep confirms **no** `KEYS` / `MONITOR` / `FLUSHDB` / `FLUSHALL` and no blocking commands.
- What is **not** Redis-backed at all (data-protection keys → Postgres; antiforgery / session → in-process; rate limiting → in-process) is documented in [`redis-optimization.md`](./redis-optimization.md) §2.3 and not repeated here.

### 2.5 Deployment topology

| Environment | Today | Citation |
|-------------|-------|----------|
| Production (Render) | Managed **`keyvalue`** service, free plan, `ipAllowList: []` (private network only). | `render.yaml` ~lines 23–27, 51–55 |
| Development | `redis:8-alpine` container with a `redis-cli ping` healthcheck, **no** published port, **no** AUTH (loopback dev only). | `docker-compose.yml` ~lines 33–41 |

The two security features Heimdall depends on in production are therefore **AUTH (password)** and **TLS (`rediss`)**. Nothing else.

## 3. What KeyDB is

KeyDB is a **multithreaded fork of Redis** originally built by EQ Alpha Technology and later **acquired by Snap Inc.** Its value proposition is vertical scaling on multi-core hardware while remaining a drop-in for Redis.

| Dimension | KeyDB | Notes |
|-----------|-------|-------|
| Protocol | RESP2 / RESP3 compatible | Speaks the same wire protocol as Redis. |
| Client compatibility | Drop-in | StackExchange.Redis (and node-redis / ioredis) work unchanged. |
| Headline feature | Multithreading | Multiple cores per instance vs. Redis's classic single-threaded core. |
| Other features | Active-active multi-master replication; FLASH (SSD-tier) storage | None of these map to a Heimdall need (see §6). |
| Redis compatibility line | Tracks **~Redis 6.2** | Does **not** implement Redis 7/8 features or RDB formats. |

### 3.1 Maintenance health — the dominant finding

KeyDB is, for practical purposes, **abandoned**:

- Last stable release **v6.3.4 (Oct 30, 2023)**; **no** new releases or security fixes since late 2023.
- Lead maintainer **John Sully left Snap in January 2025** and publicly **recommended Valkey** as the path forward.
- Corroborated by upstream GitHub issues #895 ("Goodbye"), #923, and #533.

> Version numbers here are reference points for the maintenance-health assessment, not pins.

## 4. Drop-in assessment: client level vs. operations level

This distinction is the crux of the evaluation. **KeyDB is a clean drop-in at one level and decidedly not at the other.**

| Level | Drop-in? | Detail |
|-------|----------|--------|
| **Client / application code** | ✅ Yes — zero change | RESP-compatible. `StackExchange.Redis`, the `IConnectionMultiplexer` wiring in `Program.cs`, `ConnectionStringTranslator`, `RedisCacheService`, and `RedisAccessTokenDenylist` would all work **unchanged**. The `redis://` / `rediss://` scheme translation, AUTH, and TLS negotiation behave identically. |
| **Operations / deployment** | ❌ No — non-trivial | **Render does not offer managed KeyDB.** Render's "Key Value" managed service is Redis/Valkey-based. Adopting KeyDB means **giving up the managed `keyvalue` resource** and **self-hosting** a KeyDB container/service (e.g. a Render private service / `pserv` from a KeyDB image), with all the operational ownership that implies. |

So the migration is *technically* trivial in the codebase and *operationally* a real project. The blocker is **not** code compatibility.

## 5. Feature parity for Heimdall's usage

Heimdall uses `GET` / `SET(EX)` / `DEL` on strings, plus AUTH and TLS. Every one of those is supported on KeyDB's 6.2 line.

| Capability Heimdall uses | Supported by KeyDB? | Notes |
|--------------------------|---------------------|-------|
| String `GET` / `SET` / `SET EX` / `DEL` | ✅ Yes | Core RESP commands; identical semantics. |
| TTL on `SET` (cache 5-min; denylist clamped ≥30s) | ✅ Yes | Standard expiry. |
| AUTH (password) | ✅ Yes | Present on the 6.2 line. |
| TLS (`rediss` / `ssl=true`) | ✅ Yes | Present on the 6.2 line. |
| Graceful-degradation exception types (`RedisException`, `RedisConnectionException`, `RedisTimeoutException`) | ✅ Yes | These are **client-side** (StackExchange.Redis) types; unaffected by server choice. |

**Feature parity for Heimdall's actual workload is effectively complete.** There is no command or security feature we use that KeyDB lacks.

### 5.1 But the parity is a tie at our scale — KeyDB's extras buy us nothing

| KeyDB differentiator | Relevance to Heimdall | Verdict |
|----------------------|-----------------------|---------|
| Multithreading | Our workload is one 5-minute-TTL cache key plus short-lived denylist keys. Throughput is nowhere near a single Redis core's ceiling. | No benefit |
| Active-active multi-master replication | We run a single-node free-plan instance; no multi-region write topology. | No benefit |
| FLASH (SSD-tier) storage | Working set is a handful of small string values; everything fits in RAM trivially. | No benefit |

The entire reason to choose KeyDB over Redis is performance/scale headroom Heimdall does not need.

## 6. Security parity and risk

| Security aspect | Assessment |
|-----------------|------------|
| AUTH (password) | ✅ Supported — the feature Heimdall uses. |
| TLS | ✅ Supported — the feature Heimdall uses. |
| Redis 7+ ACL refinements | ⚠️ Lags. Heimdall does **not** use ACL users today, so no *current* gap, but it forecloses the cleanest future hardening path. |
| **Ongoing CVE / security patching** | ❌ **None.** With no releases since late 2023 and the maintainer gone, KeyDB receives **no security patches**. Any future Redis-protocol CVE that also affects KeyDB would go unpatched upstream. |

**The dominant security risk is not a missing feature — it is the absence of a maintained upstream that will ship security fixes.** Moving a production dependency onto an abandoned codebase is the opposite of the defense-in-depth posture the rest of Heimdall targets.

## 7. Migration bandwidth / effort estimate

| Work item | Effort | Notes |
|-----------|--------|-------|
| Application / client code changes | **Zero** | Drop-in at the client level (§4). |
| Replace Render managed `keyvalue` with self-hosted KeyDB | **Non-trivial** | New Render private service from a KeyDB image; persistence volume; AUTH secret + TLS material provisioning; healthcheck; wiring `REDIS_URL` to the self-hosted endpoint. Replaces an operationally-free managed resource. |
| Update dev `docker-compose.yml` image | Low | `redis:8-alpine` → a KeyDB image. (Not changed in this PR.) |
| Ongoing maintenance | **Recurring** | We inherit patching, upgrades, monitoring, and incident response for a key-value store we previously got as a managed service — on top of a base that ships no upstream fixes. |
| Compatibility regression check | Low–Medium | KeyDB tracks Redis 6.2; our dev image is `redis:8-alpine` and Render Key-Value is modern Redis/Valkey. Moving to a 6.2-line server is a **downgrade** in compatibility/RDB format. Our command surface is basic enough to survive it, but it is a real regression direction. |

**Summary: low code effort, non-trivial and ongoing operational/maintenance effort and cost**, in exchange for capabilities we will not use.

## 8. Conclusion and recommendation

**Recommendation: do _not_ migrate Heimdall to KeyDB.**

To be fair to KeyDB: **it is technically a clean drop-in** — the blocker is maintenance and operations, not protocol or client incompatibility. But on Heimdall's specific axes the case against is decisive:

1. **Project abandonment / no security patching** (§3.1, §6). No releases since late 2023, maintainer departed, no CVE pipeline. This alone disqualifies it for a production dependency.
2. **Redis 6.2 compatibility ceiling vs. our `redis:8` usage** (§3, §7). Adopting KeyDB moves us *backward* on the compatibility line and off Redis 7/8 RDB formats.
3. **Zero benefit at our scale** (§5.1). We use only basic string ops on a tiny working set; multithreading, active-active replication, and FLASH solve problems we do not have.
4. **Loss of the Render managed service** (§4, §7). We would trade an operationally-free managed `keyvalue` resource for a self-hosted store we must run and patch ourselves.

**If** multithreaded scaling or active-active replication ever becomes a genuine, measured need, the right move is **not** KeyDB but an **actively-maintained, Redis-compatible successor** — most directly **Valkey** (the Linux Foundation fork, and the path KeyDB's own former maintainer endorsed), which stays Redis-protocol-compatible (so the same client-level drop-in argument holds) while remaining under active development and security maintenance. Render's managed Key-Value offering is itself Redis/Valkey-based, so that direction also preserves the managed-service operational model.

## 9. Boundaries

This document proposes **no** change to any file. It does not touch `src/`, `authz/`, `*.fga`, `docker-compose.yml`, `render.yaml`, or any package manifest. Any future server-swap work would be owned operationally (Render service definition, Docker image) by the **Docker Expert** and the deployment pipeline, with security review of the AUTH/TLS posture by the **Security Reviewer**; this Redis Expert would review only the connection / command-surface compatibility aspects.

---

## Decision Log

| Date | Decision | Rationale | Author |
|------|----------|-----------|--------|
| 2026-06-03 | **Recommend against migrating Heimdall from Redis to KeyDB.** | KeyDB is effectively abandoned (last release v6.3.4, Oct 2023; lead maintainer departed Snap Jan 2025 and endorsed Valkey) → no ongoing security patching; it tracks Redis 6.2 vs. our `redis:8` usage (a compatibility regression); it offers zero benefit at Heimdall's scale since we use only `GET`/`SET(EX)`/`DEL` on a tiny working set; and Render offers no managed KeyDB, so adoption would forfeit the managed `keyvalue` service for self-hosting and ongoing maintenance burden. KeyDB *is* a clean client-level drop-in (StackExchange.Redis unchanged), so the blocker is maintenance/operational, not technical. | Redis Expert (Copilot, delegated by Orchestrator) |
| 2026-06-03 | If multithreaded / active-active scaling ever becomes a real, measured need, evaluate **Valkey** (actively maintained, Redis-compatible, maintainer-endorsed) rather than KeyDB. | Preserves the client-level drop-in argument while keeping an actively-maintained, security-patched upstream and a managed-service path on Render. | Redis Expert (Copilot, delegated by Orchestrator) |

---

**Next step:** Review and accept the recommendation in §8. No implementation follows from this proposal; it closes the KeyDB question on the record.
