# Runbook — JWT signing-key rotation

**Status:** Active. Phase 5.7 step 22.
**Source of truth:** [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §5 (token strategy — shape, client storage, revocation, key management) and §9.3 Phase 5; [`docs/proposals/phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §§2.1–2.6 (envelope encryption, DB access controls, JWKS safety, DP key cadence).
**Tracked in:** [`docs/implementation/phase-5-checklist.md`](../implementation/phase-5-checklist.md) Phase 5.7 step 22.

This runbook covers operator-side JWT signing-key management for the Phase 5 API surface: first-deploy generation of the initial signing key, the 90-day rotation cadence, the JWKS cache window and how it bounds verifier staleness during rotation, the Redis denylist outage stance, the family-replay incident-response path, the production opt-in for the raw `openapi.json` document, the DB-access posture for `heimdall_signer`, and the Data Protection (DP) key-ring cadence that protects `signing_keys.private_key_protected` at rest. It does **not** cover the cookie / antiforgery key ring beyond its role as the KEK for signing-key ciphertext (see [Step 8](#step-8--data-protection-key-cadence)) — that has its own Phase 1 plumbing.

## Prerequisites

- Phase 5.1 (steps 1–3) merged on `main`: `signing_keys` migration with the `heimdall_app` / `heimdall_signer` role split and the `signing_keys_insert` / `signing_keys_read_private` `SECURITY DEFINER` helpers ([`src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs`](../../src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs)); [`SigningKeyService`](../../src/Heimdall.BLL/Tokens/SigningKeyService.cs) wired into DI; [`/.well-known/jwks.json`](../../src/Heimdall.Web/Endpoints/JwksEndpoints.cs) reachable.
- Phase 5.3–5.5 merged on `main`: `JwtBearer` scheme registered alongside the cookie scheme in [`src/Heimdall.Web/Program.cs`](../../src/Heimdall.Web/Program.cs), `IAccessTokenDenylist` keyspace live on the existing Redis sidecar, and the `TokenOptions` invariant `SigningKeyOverlap >= AccessTokenLifetime` enforced at startup by [`TokenOptionsValidator.Validate`](../../src/Heimdall.BLL/Tokens/TokenOptionsValidator.cs) (registered with `ValidateOnStart()` so a misconfigured floor crashes the process at boot rather than at rotation time).
- Operator shell access to the `heimdall-ticket-tracker-web` Render service (env-var management, restart trigger) and read access to the `heimdall-ticket-tracker-postgres` instance for the `signing_keys` and `audit_events` tables.
- The existing Phase 1 Data Protection key ring is persisted in the `data_protection_keys` Postgres table (`PersistKeysWithDapperInPostgreSQL` in `src/Heimdall.Web/Program.cs`). The same key ring is the KEK that wraps `signing_keys.private_key_protected` under the purpose string `"Heimdall.JwtSigningKeys.v1"` per [`phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §2.1.

## Step 1 — First-deploy generation of the initial signing key

The very first deploy of a Phase 5 build has zero rows in `signing_keys`. Until a row exists, `ISigningKeyService.GetCurrentSigningKeyAsync()` returns `null` (no active signing key on file) and the issuer path — `ISigningKeyService.GetCurrentSigningCredentialsAsync()` — throws `InvalidOperationException("No active signing key.")`, so any caller that needs to mint a token (e.g. `JwtTokenIssuer`, invoked by `POST /api/v1/auth/token`) surfaces a `500`. The intended seam is the `HEIMDALL_JWT_BOOTSTRAP=1` environment variable, mirroring the Phase 1 step 8 `HEIMDALL_BOOTSTRAP_ADMIN_EMAIL` pattern:

1. On the `heimdall-ticket-tracker-web` service, set `HEIMDALL_JWT_BOOTSTRAP=1`.
2. Trigger a redeploy (or restart the running instance). On boot, the bootstrap path reads the env-var **once**, invokes `ISigningKeyService.GenerateAsync(SigningAlgorithm.RS256, TokenOptions.SigningKeyValidity)`, and writes one row into `signing_keys` plus one `token.signing_key.generated` audit row in the same transaction.
3. **Unset `HEIMDALL_JWT_BOOTSTRAP`** (or set it to anything other than `1`) and redeploy a second time. Subsequent boots must not re-trigger generation — every active row in `signing_keys` represents real ciphertext under the current DP key ring, and re-running generation needlessly churns the key ring and the audit stream.
4. Confirm the row landed by hitting `/.well-known/jwks.json` anonymously — the response must contain exactly one `kid` and the JWK must include only `kty`, `use`, `kid`, `alg`, and the algorithm-appropriate public components (`n` + `e` for RSA; `crv` + `x` + `y` for EC). Any of `d` / `p` / `q` / `dp` / `dq` / `qi` appearing in the response indicates a regression in the step-3 field whitelist and is a stop-the-line bug.

> **The env-var must not persist in any log or environment dump.** Per [`phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §2.6, `HEIMDALL_JWT_BOOTSTRAP` is read once at startup and is not echoed by the Serilog console sink, the audit-event payload, or the `/healthz` probe response. Treat it the same way you treat `HEIMDALL_BOOTSTRAP_ADMIN_PASSWORD`: set it, deploy, unset it, deploy again.

> **Follow-up:** as of Phase 5.7 step 22 the `HEIMDALL_JWT_BOOTSTRAP` env-var seam is **not yet wired** in [`src/Heimdall.Web/Program.cs`](../../src/Heimdall.Web/Program.cs). Until that step lands, the first-deploy initial-key generation must be performed manually by an operator with shell access — either by invoking `ISigningKeyService.GenerateAsync` through a one-shot host command, or by writing the equivalent row directly through `signing_keys_insert(...)` with a freshly generated ciphertext. Treat this as a stop-the-line gap to close before the first production cutover.

## Step 2 — 90-day rotation procedure

Proposal §5.4 specifies a 90-day rotation schedule for asymmetric signing keys, with an overlap window covering the maximum access-token TTL so tokens already in flight remain verifiable until they expire.

The procedure:

1. **Pre-flight check.** Read the newest `signing_keys` row and confirm `retired_at IS NULL` and `now() < not_after`. If `not_after - now()` is shorter than `TokenOptions.SigningKeyOverlap` (default 15 minutes — see [`TokenOptionsValidator.Validate`](../../src/Heimdall.BLL/Tokens/TokenOptionsValidator.cs)), the rotation will fail with `InvalidOperationException` raised by [`SigningKeyService.GenerateAsync`](../../src/Heimdall.BLL/Tokens/SigningKeyService.cs). The fail message names both the actual and the required overlap. The preferred recovery is simply to **rotate earlier** — i.e. trigger Step 2 well before `not_after - SigningKeyOverlap` so the pre-flight passes on its own. Operators **cannot** extend the outgoing key's `not_after` under the `heimdall_app` role: per [`M202605130001_CreateSigningKeys`](../../src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs) that role is only granted column-level `UPDATE(retired_at)` on `signing_keys`, and writes to any other column raise `42501 insufficient_privilege`. If `not_after` truly must be pushed out, the change has to be made out-of-band through an operator session with `heimdall_signer` (or equivalent maintenance) privileges — there is no `SECURITY DEFINER` helper for this today and adding one is tracked as a follow-up. Rotating earlier is always the safer path; treat the manual `not_after` extension as an emergency recovery only.
2. **Generate the new key.** Invoke `ISigningKeyService.GenerateAsync(SigningAlgorithm.RS256, TokenOptions.SigningKeyValidity)`. The service:
   - generates a fresh RSA-2048 or P-256 EC key pair in-process,
   - wraps the PKCS#8-encoded private half via the purpose-isolated `IDataProtector` (`"Heimdall.JwtSigningKeys.v1"`),
   - inserts the row through `signing_keys_insert(...)` (running as `heimdall_signer` under SECURITY DEFINER),
   - calls `IJwksCacheInvalidator.Invalidate()` so the in-process JWKS cache (5-minute TTL, see [Step 3](#step-3--jwks-cache-ttl-and-in-process-invalidation)) drops its current entry,
   - returns the new `kid`.
   The `AFTER INSERT` trigger writes one `token.signing_key.generated` row to `audit_events`. There is **no** `token.signing_key.rotated` row at this point — the trigger's `rotated` branch fires only on an `UPDATE` whose changed columns are not the `retired_at` soft-delete path (see `M202605130001_CreateSigningKeys` trigger body); the issuer does not perform such an UPDATE during `GenerateAsync`, so a successful rotation produces exactly `generated` on the new row and (after Step 5 below) `retired` on the outgoing row.
3. **Verify trusted set.** Confirm `/.well-known/jwks.json` returns **both** the outgoing and incoming `kid`s, and that `ISigningKeyService.GetCurrentSigningKeyAsync()` now returns the new `kid` (the issuer always signs with the freshest unretired key; the verifier accepts both during the overlap window).
4. **Wait the overlap window.** Do not retire the outgoing key until at least `SigningKeyOverlap` has elapsed. Tokens minted just before the rotation must remain verifiable until they expire. The `TokenOptionsValidator` startup check guarantees `SigningKeyOverlap >= AccessTokenLifetime`, so a 15-minute overlap is sufficient for the 15-minute access-token TTL — operators do not have to remember the invariant; it is enforced by `ValidateOnStart()`.
5. **Retire the outgoing key.** Invoke `ISigningKeyService.RetireAsync(oldKid)`. This sets `retired_at = now()` (column-level `UPDATE(retired_at)` on `heimdall_app` is the only direct mutation that role is permitted to make), invalidates the JWKS cache again, and the trigger writes `token.signing_key.retired`. The retired key remains in the trusted set until `not_after` passes (sweeper-driven `token.signing_key.expired` event), so any token minted under it during the overlap continues to verify.
6. **Post-rotation audit.** A successful rotation produces, in order, audit rows of types `token.signing_key.generated` (emitted by the trigger on the new row's INSERT) → `token.signing_key.retired` (emitted by the trigger when `retired_at` flips from NULL → non-NULL on the outgoing row). The `token.signing_key.rotated` event type is **not** produced by the standard generate-then-retire flow — the trigger only emits it on an UPDATE that mutates columns other than `retired_at` (e.g. a future re-encryption / metadata-change path), so its absence is expected and is **not** a rotation failure signal. Search `audit_events` for the rotation window and confirm one `generated` row on the incoming `kid` and one `retired` row on the outgoing `kid`.

## Step 3 — JWKS cache TTL and in-process invalidation

The `/.well-known/jwks.json` endpoint ([`src/Heimdall.Web/Endpoints/JwksEndpoints.cs`](../../src/Heimdall.Web/Endpoints/JwksEndpoints.cs)) is the only external surface for verifier-side key discovery and is the single point where the rotation overlap window becomes observable to downstream callers. Two staleness controls compose:

- **Process-internal cache.** The endpoint caches its assembled response in `IMemoryCache` with a 5-minute absolute expiration under a single fixed key — [`MemoryCacheJwksCacheInvalidator.CacheKey`](../../src/Heimdall.Web/Tokens/MemoryCacheJwksCacheInvalidator.cs) — and relies on the `IJwksCacheInvalidator` abstraction to evict that entry on rotation rather than on a derived-from-state cache tag. `SigningKeyService.GenerateAsync` and `SigningKeyService.RetireAsync` both call `IJwksCacheInvalidator.Invalidate()` on success, so the in-process cache observes rotations **immediately** rather than after the TTL expires. This is the seam that keeps rotation visible to in-process callers (e.g. the JwtBearer middleware's `IssuerSigningKeyResolver`, which never hits the HTTP endpoint).
- **HTTP cache header.** Responses set `Cache-Control: public, max-age=300` (see `JwksEndpoints.cs:102`). Five minutes is the deliberate floor — short enough that a verifier outside the process picks up a freshly rotated key without redeploy, long enough to avoid hammering the DB on every verify. Kept at 300 s rather than the proposal-draft 3600 s because the in-process invalidation already handles the staleness window; `Cache-Control: no-store` is explicitly **not** used (the overlap window in [Step 2](#step-2--90-day-rotation-procedure) absorbs the residual five-minute staleness).

The composition means: in-process callers see rotations within milliseconds; out-of-process callers see them within five minutes. The `SigningKeyOverlap` floor (≥ `AccessTokenLifetime`, default 15 min) is the upper bound on how long any token in flight can verify against the outgoing key. The three numbers stack:

```
HTTP cache TTL        (5 min)      ≤
in-process overlap    (15 min)     ≤
access-token TTL      (15 min)     <
key validity          (90 days)
```

If you ever change `TokenOptions.AccessTokenLifetime`, `TokenOptions.SigningKeyOverlap`, or the `CacheTtlSeconds` constant in `JwksEndpoints.cs`, re-verify this ordering. The startup validator catches the overlap / access-token mismatch; the cache TTL is not currently validated against either, so operators own that constraint manually.

## Step 4 — Redis denylist outage behaviour

`IAccessTokenDenylist` (Phase 5.5 step 11) lets the API "log out everywhere now" by writing `heimdall:token-denylist:<jti>` keys into the existing Redis sidecar with a TTL equal to the remaining access-token lifetime. Every `JwtBearer` request goes through `JwtBearerEvents.OnTokenValidated` and calls `IAccessTokenDenylist.IsDeniedAsync(jti)`; on hit, `ctx.Fail("denylisted")` rejects the request.

When Redis is unreachable, the behaviour is **policy-aware** rather than uniform:

- **Admin-policy-gated endpoints** — fail-closed. Same precedent as Phase 3 step 10's deny-closed stance on OpenFGA adapter outages. The admin/non-admin classification is made via the existing Phase 3.3 step 6 adapter call `IAuthorizationService.Check("organization", seedOrgId, "admin", userId)` — there is no second copy of the admin predicate.
- **Non-admin reads** — fail-open. The request proceeds, but `IAuditEventWriter` writes one `token.access.denylist_unavailable` row with the `jti` and the route, so the outage is forensically reconstructible.

The audit-event signal operators look for during a suspected Redis outage:

| Symptom | Audit event-type to grep |
|---|---|
| 401s on admin surfaces despite valid tokens | `token.access.denylist_unavailable` (will appear on the surrounding non-admin traffic in the same window) |
| Logout calls "succeed" but denylist hits never fire | Same — combined with absent `token.access.revoked` rows from the logout endpoint |
| Background spike in unauthenticated audit volume | Same — `token.access.denylist_unavailable` is the canonical "Redis was down and we failed open" marker |

Recovery is the standard Redis-sidecar recovery: restore `heimdall-ticket-tracker-redis` connectivity, confirm `/healthz` passes, and the denylist resumes normal behaviour without further operator action. There is no replay or backfill — denylist entries are self-expiring TTL keys, and any access token that *would* have been denied during the outage will simply expire on its own access-token TTL (≤ 15 min).

## Step 5 — Family-replay incident response

Phase 5.4 step 10 implements OAuth refresh-token rotation with reuse detection: every `POST /api/v1/auth/refresh` rotates the cookie, and an attempt to reuse a refresh token that has already been rotated triggers `RevokeFamilyAsync(family_id, 'family_replay')` and writes one `token.refresh.replayed` audit row with the offending `jti` + `family_id` payload.

Operator detection-and-response loop:

1. **Detect.** Alert on any `token.refresh.replayed` row in `audit_events`. This event-type is **never** emitted by a healthy client — it is the canonical signal that either an attacker is replaying a stolen refresh cookie, or a buggy client is re-submitting the same value twice. Treat the first occurrence as an incident, not noise.
2. **Identify the family.** The audit payload includes `family_id`. Query `refresh_tokens WHERE family_id = '<id>'` to see the full family tree: which member was the legitimately-rotated row, which was the replayed one, when each was issued, and which `user_id` they belong to. The `RevokeFamilyAsync` call has already set `revoked_at` + `revoked_reason = 'family_replay'` on every member.
3. **Identify the user.** Map `user_id` to the user record. Inspect their recent `audit_events` for sibling signals — repeated `mfa.challenge.failed`, an unusual `token.access.issued` from a new IP, or a suspicious `bootstrap.admin.promoted` if the affected account is a sysadmin.
4. **Decide on broader revocation.** Two options:
   - **Scoped:** the family revoke from step 2 has already invalidated every refresh token in that family. If the user has *other* families (multiple devices, multiple browsers), those remain valid. Choose this when the replay looks like a single-device cookie-theft (browser malware, shared workstation) and you trust the other devices.
   - **Full user revoke:** issue `RevokeFamilyAsync` for every family belonging to that `user_id`, and `IAccessTokenDenylist.DenyAsync(jti, exp, 'admin_revoke')` for every currently-outstanding access `jti` belonging to that user (look up active `jti`s by querying the recent `token.access.issued` rows). Choose this when the user account itself looks compromised — e.g. concurrent replays from multiple IPs, or correlation with a credential-stuffing burst on the `(ip|email)` login limiter.
5. **Post-incident.** Force the user to re-authenticate (they will be issued a fresh refresh-token family on the next login). If the account is admin-tier, also confirm their MFA second factor is still genuinely under their control before allowing re-login — the `mfa-enrolment` runbook's break-glass scenario applies if it is not.

The `revoked_reason = 'family_replay'` value on `refresh_tokens` (constrained by the step-4 `CHECK`) is the durable forensic anchor. Search for it during quarterly access reviews even when there is no live alert — a single false positive is fine; a clustered pattern is a process gap worth a follow-up ticket.

## Step 6 — `Api:Documentation:Enabled` toggle

Phase 5.6 step 16 ships an OpenAPI document at `/api/v1/openapi.json` and a Swagger UI at `/api/v1/docs`. The two surfaces have different production stances:

- **Swagger UI (`/api/v1/docs`).** Registered only when `app.Environment.IsDevelopment()` evaluates true ([`src/Heimdall.Web/Program.cs`](../../src/Heimdall.Web/Program.cs) around line 1010). There is no production toggle — the interactive surface is dev-only by design.
- **Raw `openapi.json` document.** Gated on the configuration key `Api:Documentation:Enabled` (default `false`), evaluated at `Program.cs:986`. In production deploys, set the env-var `Api__Documentation__Enabled=true` (note the `__` for nested-key binding) on the `heimdall-ticket-tracker-web` service and redeploy if and only if you have a concrete reason to expose the document — for example, generating a client SDK from a deployed environment.

The split deliberately keeps the interactive HTML/JS surface unavailable in production (Swagger UI is the documented attack-surface concern, not the JSON itself) while letting operators opt in to the structured document for tooling. Leaving `Api:Documentation:Enabled=true` set after the tooling job completes is harmless from a security standpoint but is noisy: revert it to `false` and redeploy when the opt-in window is no longer needed, so the production posture stays minimal.

## Step 7 — DB-access pattern for `heimdall_signer`

Phase 5.1 step 1 chose the **`SECURITY DEFINER` function pattern** over the alternative of routing `SigningKeyService` through a second connection string. The implementation is in [`src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs`](../../src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs); the production posture is:

- Two Postgres roles, both `NOLOGIN`: `heimdall_app` (the role under which the application connection pool actually executes queries) and `heimdall_signer` (the table owner of `signing_keys` and the `SECURITY DEFINER` identity for sensitive operations).
- Two `SECURITY DEFINER` helpers owned by `heimdall_signer` with `search_path = pg_catalog, public`:
  - `signing_keys_insert(...)` — the only path through which a new row can be inserted. The function body executes with `heimdall_signer`'s privileges, so it can write the `private_key_protected` column even though `heimdall_app` (the calling role) does **not** hold `INSERT` on the table.
  - `signing_keys_read_private(text)` — the only path through which the ciphertext can be `SELECT`ed. Same SECURITY DEFINER mechanism.
- **Column-level GRANTs** on `heimdall_app` are the real confidentiality control. `heimdall_app` holds:
  - `SELECT` on every column **except** `private_key_protected`;
  - `UPDATE` on `retired_at` only (the soft-delete retirement path used by `RetireAsync`);
  - neither direct `INSERT` nor `DELETE`.
- Row-level security is **enabled but not forced** on `signing_keys` (`pg_class.relrowsecurity = true`, `relforcerowsecurity = false`). The three permissive policies — `signing_key_signer_full FOR SELECT TO heimdall_signer`, `signing_key_app_read FOR SELECT TO heimdall_app`, `signing_key_app_update FOR UPDATE TO heimdall_app` — mirror the GRANT posture. RLS is defence-in-depth; the column-level GRANTs are the primary control. The `SECURITY DEFINER` write path works because RLS is not forced (the function body running as `heimdall_signer` bypasses the per-DML policy check on table-owner writes).
- `INSERT ON audit_events` is granted to **both** roles so the `AFTER INSERT OR UPDATE OR DELETE` trigger on `signing_keys` can write the `token.signing_key.{generated,rotated,retired,revoked,expired}` audit rows regardless of which code path triggered the change.

The pgTAP coverage in `tests/pgtap/21_signing_keys.sql` (Phase 5.7 step 17) pins this posture: table owner = `heimdall_signer`, RLS enabled but not forced, absent `private_key_protected` column privilege for `heimdall_app`, absent direct `INSERT` / `DELETE` for `heimdall_app`, presence of `UPDATE(retired_at)`, absence of `UPDATE(not_after)`, the audit trigger existence, and the `audit_events` INSERT grants for both roles.

The alternative — two connection strings, one per role — was rejected because Render's managed Postgres makes per-role connection-pool management operationally awkward and because the `SECURITY DEFINER` pattern keeps the application code path identical (`SigningKeyService` continues to use the single shared `heimdall_app` connection; the privilege transition happens transparently inside the helper function body).

## Step 8 — Data Protection key cadence

Per [`phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §2.1, the `private_key_protected` column stores PKCS#8-encoded private key bytes wrapped by ASP.NET Core Data Protection under the purpose string `"Heimdall.JwtSigningKeys.v1"`. The DP key ring is therefore the **KEK** for every active signing key. Its lifetime must be considered alongside the 90-day signing-key rotation cadence.

### Configured DP key lifetime

The intended posture per `phase-5-signing-key-hardening.md` §2.6 is a **60-day** DP key lifetime — strictly shorter than the 90-day signing-key rotation — so that a compromise of the DP key ring limits the window of `signing_keys.private_key_protected` ciphertext exposure to at most one DP generation.

The actual configuration in [`src/Heimdall.Web/Program.cs`](../../src/Heimdall.Web/Program.cs) (lines 642–648) is:

```csharp
builder
    .Services.AddDataProtection()
    .PersistKeysWithDapperInPostgreSQL(
        postgresConnectionString,
        config => config.InitializeTable = false
    )
    .SetApplicationName("Heimdall");
```

There is **no `SetDefaultKeyLifetime(...)` call**, so the DP key ring operates on the framework default of **90 days**. This means the DP key lifetime currently **equals** the signing-key rotation cadence rather than being strictly shorter — the hardening goal in §2.6 ("a compromised DP key ring limits the window of signing-key ciphertext exposure") is **not yet met** by the deployed configuration.

> **Follow-up:** Phase 5.7 step 22 should land a `.SetDefaultKeyLifetime(TimeSpan.FromDays(60))` call in `Program.cs` (or an equivalent runtime-configurable value bound from configuration so operators can tune it without a redeploy). Until that lands, document the actual posture (90-day DP lifetime, matching the signing-key cadence) in every operator handover so no one assumes the §2.6 invariant holds.

### Relationship to the 90-day JWT signing-key rotation

When the §2.6 60-day lifetime is in place, the cadence stacks as:

```
DP key lifetime           (60 days)   <
JWT signing-key validity  (90 days)
```

Two implications:

1. **Defence in depth.** A leaked DP key ring lets an attacker decrypt only the signing-key ciphertext encrypted under that specific DP generation. Older signing-key rows wrapped under an earlier DP generation are not affected; rows wrapped under a later DP generation do not yet exist. The blast radius is bounded by one DP lifetime — half the worst case of "the same DP key ring protected every active signing key for its entire validity".
2. **Maintenance obligation.** Every signing-key row encrypted under a DP key that subsequently expires becomes **undecryptable**. `IDataProtector.Unprotect` throws `CryptographicException` on an expired / revoked DP key, and per [`phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §2.1 the service does not swallow that exception — signing fails closed. If no operator action is taken, the affected signing key is effectively retired the moment its wrapping DP key expires, regardless of `signing_keys.not_after`.

### Consequence: ciphertext under an expired DP key cannot be decrypted

Concretely: a `signing_keys` row inserted on day 1 under DP-key-A is wrapped under DP-key-A's bytes. If DP-key-A expires on day 60 and DP-key-B becomes the active wrapping key, the row inserted on day 1 still contains the ciphertext from day 1 — it does **not** auto-re-wrap. The next attempt to call `SigningKeyService.GetCurrentSigningKeyAsync()` for that row will call `_keyProtector.Unprotect(ciphertext)`, which raises `CryptographicException`, which surfaces as a 500 from the issuer endpoint and a `token.signing_key.*` absence in the audit stream.

Operators must therefore **re-encrypt all active signing-key rows before the DP key that wraps them expires**.

### Re-encryption procedure

There is currently no admin endpoint or CLI command that performs this re-encryption automatically.

> **Follow-up:** add a one-shot operator entry point — either an admin API endpoint (gated by `RequireMfaPolicy` + system-admin) or a host-mode CLI subcommand — that:
> 1. Selects every row in `signing_keys` where `retired_at IS NULL AND now() < not_after`.
> 2. For each row: calls `signing_keys_read_private(kid)` to fetch the ciphertext, `Unprotect`s it under the **current** DP key ring (so any row still encrypted under an older-but-not-yet-expired DP key gets re-wrapped under the active key), and writes the re-wrapped ciphertext back via a new `SECURITY DEFINER` helper (`signing_keys_rewrap(kid, new_ciphertext)`).
> 3. Emits one `token.signing_key.rewrapped` audit row per row processed.
> Until that lands, the manual procedure below is the only option.

Manual re-encryption procedure (current state, to be replaced once the above follow-up lands):

1. **Identify the rows.** From a Postgres shell connected as a role that can call `signing_keys_read_private(text)`:
   ```sql
   SELECT kid FROM signing_keys
    WHERE retired_at IS NULL
      AND now() < not_after;
   ```
2. **For each `kid`**, in a host shell with access to the application's `IDataProtectionProvider`:
   - Call `signing_keys_read_private('<kid>')` to fetch the current ciphertext.
   - Resolve the purpose-isolated protector: `dp.CreateProtector("Heimdall.JwtSigningKeys.v1")`.
   - `Unprotect` the ciphertext to recover the PKCS#8 plaintext bytes. **Hold these bytes in memory only for the duration of the round-trip; call `CryptographicOperations.ZeroMemory` on the buffer immediately after the `Protect` step succeeds.**
   - `Protect` the same plaintext bytes back into a fresh ciphertext (which will be wrapped under the **current** DP key generation, not the original one).
   - Write the new ciphertext back. Because there is no `signing_keys_rewrap(...)` helper today, this requires either a direct `UPDATE` issued as `heimdall_signer` in a maintenance shell **or** a one-off in-process script that constructs the row through the existing `SECURITY DEFINER` insert + delete-of-old pattern. Either approach should be performed under a maintenance-window banner with the API surface gated.
3. **Verify.** Hit `/.well-known/jwks.json` and confirm every `kid` still appears (the public halves are not affected by the re-wrap; this is purely a confirmation that the rows are still readable). Then mint a test access token through `POST /api/v1/auth/token` against a non-production environment to confirm signing still works.
4. **Audit.** Manually write one `signing_key.rewrapped` audit row per affected `kid` through whatever change-management trail you use, with the maintenance ticket number in the payload — until the follow-up endpoint above lands, this is not captured by `IAuditEventWriter`.

The manual procedure is friction-heavy by design. If you find yourself running it more than once before the follow-up CLI / endpoint ships, escalate the follow-up to a stop-the-line item.

## Common operator scenarios

| Scenario | First place to look | Recovery |
|---|---|---|
| `POST /api/v1/auth/token` returns 500 immediately after first deploy | `signing_keys` is empty | Run [Step 1](#step-1--first-deploy-generation-of-the-initial-signing-key). |
| Rotation throws `InvalidOperationException` mentioning overlap | Outgoing key's `not_after` is too close to `now()` | Trigger rotation earlier (re-run [Step 2](#step-2--90-day-rotation-procedure) immediately so the new key absorbs the overlap window). Extending `not_after` requires `heimdall_signer`-equivalent privileges — `heimdall_app` only holds `UPDATE(retired_at)` — and should be treated as an emergency-only out-of-band recovery; see [Step 2](#step-2--90-day-rotation-procedure) step 1. |
| `/.well-known/jwks.json` returns stale set after rotation | `IJwksCacheInvalidator` did not fire (process restarted mid-rotation; rotation didn't complete) | Restart the web service — the cache is in-memory, so a fresh process re-populates from the DB. Confirm `kid` set matches `SELECT kid FROM signing_keys WHERE retired_at IS NULL AND now() < not_after`. |
| 401s on admin endpoints with no `denylisted` reason in logs | Likely Redis outage; look for `token.access.denylist_unavailable` in `audit_events` | See [Step 4](#step-4--redis-denylist-outage-behaviour); restore Redis connectivity. |
| `token.refresh.replayed` audit row appears | Refresh-token reuse — could be theft or buggy client | Run [Step 5](#step-5--family-replay-incident-response). |
| `Api:Documentation:Enabled=true` left on after tooling job | Larger-than-necessary attack surface (low-severity) | Set `Api__Documentation__Enabled=false`, redeploy. |
| `CryptographicException` from `SigningKeyService` on `Unprotect` | DP key that wraps the row has expired or been revoked | Run the re-encryption procedure in [Step 8](#step-8--data-protection-key-cadence); follow up by setting the DP lifetime to 60 days so this stays inside one wrapping generation. |

## Decisions recorded

| Date | Decision |
|---|---|
| 2026-05-13 | DB-access pattern for `heimdall_signer`: `SECURITY DEFINER` function helpers (`signing_keys_insert`, `signing_keys_read_private`) on a single shared `heimdall_app` connection. Rejected the two-connection-string alternative as operationally awkward on Render's managed Postgres. Source: [`phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §2.2; pinned by [`M202605130001_CreateSigningKeys.cs`](../../src/Heimdall.DAL/Migrations/M202605130001_CreateSigningKeys.cs). |
| 2026-05-13 | JWKS HTTP cache TTL set to 300 s (5 min), not the proposal-draft 3600 s, because the in-process `IJwksCacheInvalidator` already handles the staleness window. Source: `JwksEndpoints.cs:102`; rationale captured in [Step 3](#step-3--jwks-cache-ttl-and-in-process-invalidation). |
| Open  | DP key lifetime currently defaults to 90 days (no `SetDefaultKeyLifetime` call in `Program.cs`). Target is 60 days per `phase-5-signing-key-hardening.md` §2.6. Tracked as a follow-up in [Step 8](#step-8--data-protection-key-cadence). |
| Open  | `HEIMDALL_JWT_BOOTSTRAP=1` env-var seam not yet wired in `Program.cs`. Tracked as a follow-up in [Step 1](#step-1--first-deploy-generation-of-the-initial-signing-key). |
| Open  | Re-encryption admin endpoint / CLI for rotating `private_key_protected` under a fresh DP key generation does not exist. Tracked as a follow-up in [Step 8](#step-8--data-protection-key-cadence). |

## References

- [`docs/proposals/security-and-authorization.md`](../proposals/security-and-authorization.md) §5 (token strategy — shape, client storage, revocation, key management) and §9.3 Phase 5 — design rationale.
- [`docs/proposals/phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) §§2.1–2.6 — envelope encryption, DB access controls, JWKS safety constraints, audit-event constants, rotation overlap enforcement, and DP key cadence.
- [`docs/implementation/phase-5-checklist.md`](../implementation/phase-5-checklist.md) — Phase 5 step-by-step tracking checklist (steps 1, 2, 3, 10, 12, 16, 22 are directly material to this runbook).
- [`docs/runbooks/openfga-bootstrap.md`](./openfga-bootstrap.md) — companion runbook for the OpenFGA sidecar that the API endpoints' `[Authorize]` policies talk to.
- [`docs/runbooks/mfa-enrolment.md`](./mfa-enrolment.md) — companion runbook for the MFA policy gate that composes with the API JWT bearer scheme on admin-only surfaces.
- ASP.NET Core Data Protection — key-ring management and `SetDefaultKeyLifetime`: [`learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings`](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/default-settings).
- ASP.NET Core JWT bearer authentication — scheme registration, `JwtBearerEvents.OnTokenValidated`, `IssuerSigningKeyResolver`: [`learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication`](https://learn.microsoft.com/aspnet/core/security/authentication/configure-jwt-bearer-authentication).
- [RFC 7517 — JSON Web Key (JWK / JWKS)](https://datatracker.ietf.org/doc/html/rfc7517) — JWKS response shape.
- [RFC 7518 §3 — JSON Web Algorithms (RS256 / ES256)](https://datatracker.ietf.org/doc/html/rfc7518#section-3) — permitted signing algorithms.
- [RFC 8725 — JSON Web Token Best Current Practices](https://datatracker.ietf.org/doc/html/rfc8725) — rejection of `alg=none` / `HS*` for asymmetric verifiers.
- [OAuth 2.0 refresh-token rotation + reuse detection (Auth0)](https://auth0.com/docs/secure/tokens/refresh-tokens/refresh-token-rotation) — family-replay mechanism referenced by [Step 5](#step-5--family-replay-incident-response).
