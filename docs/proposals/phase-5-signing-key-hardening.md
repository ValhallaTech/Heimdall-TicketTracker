# Proposal: Signing-Key Hardening for the Phase 5 DB-backed `signing_keys` Solution

**Status:** Draft — 2026-05-13
**Author:** IronSloth
**Scope:** `docs/implementation/phase-5-checklist.md` — steps 1, 2, 3, 17, 19, 22 are directly affected; no other phase checklist is touched.
**Source of truth:** [`docs/proposals/security-and-authorization.md`](./security-and-authorization.md) §5.4 (key management), §9.3 Phase 5.
**Depends on:** Phase 5 planning state (no steps started). All changes proposed here must be incorporated before any Phase 5 PR is opened.

> This document records the outcome of a pre-implementation review of three candidate KMS solutions
> (HashiCorp Vault CE, OpenKMS at [`Gosayram/openkms`](https://github.com/Gosayram/openkms), and the
> DB-backed approach already specified in the proposal) and translates the findings into concrete
> amendments to the Phase 5 checklist.
>
> It does **not** change the architectural decision to use the DB-backed approach — that decision
> stands. It fills in implementation detail that §5.4 deliberately left open ("stored encrypted-at-rest")
> and adds controls that were absent from the original checklist steps.

---

## 1. Background

§5.4 of [`security-and-authorization.md`](./security-and-authorization.md) specifies:

- Asymmetric signing keys (RS256 / ES256), `kid`-indexed, stored in a dedicated `signing_keys` table.
- Private halves "stored encrypted-at-rest and never logged or checked into the repo."
- Rotation on a 90-day schedule and on suspected compromise; overlap window covering the maximum access-token TTL.
- JWKS endpoint publishing only the public halves.

What §5.4 does **not** specify is the encryption mechanism, the database access controls, the in-process key-material lifetime, or the JWKS caching and safety constraints. These are the gaps this proposal closes.

Two third-party KMS solutions were evaluated as alternatives before this hardening path was chosen:

| Candidate | Rejection reason |
|---|---|
| **HashiCorp Vault CE** | Auto-unseal requires Enterprise or cloud KMS; BSL 1.1 license concern; third sidecar on top of OpenFGA + Redis; no native JWKS endpoint. |
| **OpenKMS (`Gosayram/openkms`)** | Explicitly self-described as a skeleton; 0 stars / 0 forks; Apache 2.0 is correct but production-stability horizon unknown; no .NET SDK; no native JWKS endpoint. |

Both share the JWKS gap, meaning the JWKS assembly layer in Phase 5 step 3 would be needed regardless. The DB-backed approach, hardened as described below, reaches an equivalent key-isolation posture without a new sidecar or license risk, and is a better fit at the current scale of the project.

OpenKMS remains a viable **future** replacement candidate behind the `ISigningKeyService` abstraction once it reaches a stable release — no Phase 5 code should close that door.

---

## 2. Hardening changes required

The changes below are grouped by the layer they affect. Each group maps to one or more existing checklist steps that must be amended, or a new step that must be inserted.

### 2.1 Envelope encryption of private key material (amends step 1 and step 2)

**The gap.** The current `signing_keys` migration step (step 1) stores `private_key_encrypted` as a text column with no specification of what "encrypted" means. The current `SigningKeyService` step (step 2) says keys are created via `RSACryptoServiceProvider` / `ECDsa` but is silent on how the private key blob is protected before it is written to the database.

**Required change.** `SigningKeyService` must use **ASP.NET Core Data Protection envelope encryption** for private key material. Specifically:

1. At construction, resolve a **purpose-isolated** `IDataProtector` from the injected `IDataProtectionProvider`:
   ```
   _keyProtector = dp.CreateProtector("Heimdall.SigningKeys.PrivateKey.v1");
   ```
   This is the same Data Protection infrastructure wired in Phase 1 step 4 (`data_protection_keys` table), already used for cookies and antiforgery. No new dependency is introduced.

2. After generating the RSA/EC key pair, export the private key as PKCS#8 DER bytes, call `_keyProtector.Protect(pkcs8Bytes)`, and store the resulting ciphertext in `signing_keys.private_key_encrypted`. The plaintext byte array must be zeroed with `CryptographicOperations.ZeroMemory` before it goes out of scope.

3. On read, call `_keyProtector.Unprotect(ciphertext)`. If the Data Protection key ring has been revoked or the ciphertext has been tampered with, `Unprotect` throws `CryptographicException` — this is the correct fail-closed behaviour (signing fails loudly rather than silently producing a verifiable token with a compromised key).

**Why this is sufficient.** A database dump alone does not recover private keys; an attacker also needs the Data Protection key ring, which lives in a separate `data_protection_keys` table row under a master key tied to the deployment environment. The threat model is bounded to scenarios where both tables are compromised simultaneously, which requires a deeper application-layer breach than a SQL-layer one.

**Step 1 amendment.** The migration description must note that `private_key_encrypted` stores a Data Protection ciphertext blob, not a raw or independently-encrypted value. The column type should be `bytea` rather than `text` to make the binary nature explicit and prevent accidental encoding bugs.

**Step 2 amendment.** The `SigningKeyService` description must specify:
- Constructor injection of `IDataProtectionProvider`; purpose-isolated protector created at construction time.
- `EncryptPrivateKey(byte[] pkcs8) → byte[]` and `DecryptPrivateKey(byte[] ciphertext) → byte[]` private helpers wrapping `Protect` / `Unprotect`.
- `CryptographicOperations.ZeroMemory` called on every plaintext PKCS#8 byte array before it leaves scope.
- The decrypted `RSA` / `ECDsa` object must not be cached in a field; it must be created, used for signing, and disposed within a single method call.

### 2.2 Database-level access controls (amends step 1)

**The gap.** The current step 1 migration creates the `signing_keys` table but specifies no column-level grants, no Row Security Policy, and no audit trigger. Any query that can reach the database and holds the application role can `SELECT private_key_encrypted` incidentally (e.g. a misconfigured `SELECT *` in an unrelated Dapper query).

**Required changes to step 1.**

**a. Separate DB roles.** The migration must create (or document the creation of) two Postgres roles:

- `heimdall_app` — the role used by the application for all normal operations. Must **not** have `SELECT` on `signing_keys.private_key_encrypted`.
- `heimdall_signer` — used exclusively by `SigningKeyService` for key reads. Has `SELECT` on the full `signing_keys` row.

In practice on Render's managed Postgres, a `SECURITY DEFINER` function owned by `heimdall_signer` called from the `heimdall_app` connection is a workable pattern if two connection strings are operationally awkward. The migration must document which pattern is chosen and why.

**b. Row Security Policy.** Enable RLS on `signing_keys` and add a policy restricting `private_key_encrypted` reads to `heimdall_signer`:

```sql
ALTER TABLE signing_keys ENABLE ROW LEVEL SECURITY;

CREATE POLICY signing_key_private_read ON signing_keys
    FOR SELECT
    USING (current_user = 'heimdall_signer');
```

**c. Audit trigger.** Any `INSERT`, `UPDATE`, or `DELETE` on `signing_keys` must write a row to `audit_events` with `event_type` values drawn from the new constants defined in step 8 (see §2.4 below). This catches both scheduled rotation and any out-of-band row manipulation.

### 2.3 JWKS endpoint safety constraints (amends step 3)

**The gap.** The current step 3 description says the endpoint returns keys from `GetTrustedKeysAsync()` but does not specify caching, field whitelisting, or HTTP cache headers.

**Required changes to step 3.**

**a. Field whitelist.** The JWK serialisation must explicitly include only: `kty`, `use`, `kid`, `alg`, and the algorithm-appropriate public components (`n` + `e` for RSA; `crv` + `x` + `y` for EC). The fields `d`, `p`, `q`, `dp`, `dq`, `qi` (RSA private components) and `d` (EC private scalar) must never appear in the output. This must be enforced by a unit test assertion (see §2.5).

**b. Response caching.** The JWKS response must be cached in `IMemoryCache` with an absolute expiration. The TTL must be shorter than the key rotation overlap window but long enough to avoid per-request DB reads. A value of 1 hour is appropriate given a 90-day rotation schedule. On key rotation, the cache entry must be invalidated by `SigningKeyService` via a cache key derived from the current key-ring state.

**c. HTTP cache headers.** The endpoint must set `Cache-Control: public, max-age=3600` so downstream verifiers (if added in a future phase) can cache appropriately.

### 2.4 Signing-key audit event constants (amends step 8)

Step 8 defines audit-event type constants for the token lifecycle. The following constants must be added alongside `token.access.issued` etc.:

| Constant | Trigger |
|---|---|
| `signing_key.generated` | A new signing key pair is created by `SigningKeyService.GenerateAsync`. |
| `signing_key.rotated` | A new key becomes the current signing key (old key enters overlap / grace period). |
| `signing_key.revoked` | A key's `not_after` is set to now (emergency revocation). |
| `signing_key.expired` | A key's `not_after` passes and it is removed from the trusted set. |

These constants are in `Heimdall.Core` alongside the Phase 4 MFA event names. The audit trigger added in §2.2c writes rows using these constants.

### 2.5 Rotation overlap window enforcement (amends step 2)

**The gap.** The current step 2 mentions rotation support but does not enforce the overlap invariant: the window between an outgoing key's `not_after` and an incoming key's `not_before` must be at least as long as the maximum access-token TTL (15 minutes, per proposal §5.1).

**Required change.** `SigningKeyService.GenerateAsync` must reject a rotation request — throwing `InvalidOperationException` with a descriptive message — if the computed overlap between the outgoing key's `not_after` and the new key's `not_before` would be shorter than `TokenOptions.AccessTokenLifetime` (resolved from `IOptions<TokenOptions>`). This must be covered by a unit test.

### 2.6 Data Protection key rotation cadence (amends step 22 — runbook)

**The gap.** Step 22 documents the JWT signing key rotation runbook but does not address the Data Protection key ring, which is now the KEK for signing key material (§2.1).

**Required change.** The runbook (`docs/runbooks/jwt-signing-key-rotation.md`) must include a section covering:

- Data Protection key lifetime. The existing Phase 1 `SetDefaultKeyLifetime` configuration must be set to **60 days** — shorter than the 90-day JWT signing key rotation schedule — so that a compromised DP key ring limits the window of signing-key ciphertext vulnerability. This configuration value must be documented here and cross-referenced from Phase 1.
- The consequence of DP key expiration: existing `signing_keys.private_key_encrypted` blobs encrypted under the expired DP key can no longer be decrypted. The runbook must instruct operators to re-encrypt all active signing key rows (by calling a documented admin endpoint or CLI command) before the DP key expires.
- `HEIMDALL_JWT_BOOTSTRAP=1` bootstrap path: the runbook must confirm this env-var is read once at startup, used to trigger `GenerateAsync`, and does not persist in any log or environment dump.

### 2.7 Test coverage additions (amends steps 17 and 19)

**Step 17 — pgTAP (`tests/pgtap/21_signing_keys.sql`).** The existing plan description must be extended to assert:

- RLS is enabled on `signing_keys` (`pg_class.relrowsecurity = true`).
- The `heimdall_app` role does not have column-level `SELECT` privilege on `private_key_encrypted` (`information_schema.column_privileges`).
- The audit trigger exists on `signing_keys` (`pg_trigger`).
- `private_key_encrypted` is `bytea NOT NULL` (not `text`).

**Step 19 — xUnit (`SigningKeyService` and JWKS tests).** The existing plan description must be extended to assert:

- The serialised JWKS output for both RSA and EC keys contains none of the fields `d`, `p`, `q`, `dp`, `dq`, `qi` (assert by deserialising the JSON and checking property names).
- `DecryptPrivateKey` throws `CryptographicException` when given a tampered ciphertext (flip one byte; assert the exception).
- `GenerateAsync` throws `InvalidOperationException` when the overlap window would be shorter than `AccessTokenLifetime`.
- `GetCurrentSigningCredentials` does not cache the `RSA` / `ECDsa` instance across two calls (assert by verifying the returned `RsaSecurityKey` instances are not reference-equal).

---

## 3. Impact on the Phase 5 checklist — summary of amendments

The table below maps each §2 change to the checklist step it affects and states whether the step description needs to be **amended in place** or a **new step** must be inserted.

| §  | Checklist step | Action | One-line description of change |
|----|---|---|---|
| 2.1 | Step 1 — `signing_keys` migration | **Amend** | Change `private_key_encrypted` type to `bytea`; note it stores a DP ciphertext blob. |
| 2.2a–c | Step 1 — `signing_keys` migration | **Amend** | Add `heimdall_app` / `heimdall_signer` role grants, RLS policy, and audit trigger to the migration. |
| 2.1 | Step 2 — `SigningKeyService` | **Amend** | Add `IDataProtectionProvider` injection, `EncryptPrivateKey` / `DecryptPrivateKey` helpers, `ZeroMemory` requirement, no-caching-of-decrypted-key requirement. |
| 2.5 | Step 2 — `SigningKeyService` | **Amend** | Add overlap window enforcement: `GenerateAsync` rejects rotations that would violate the minimum overlap invariant. |
| 2.3 | Step 3 — JWKS endpoint | **Amend** | Add field whitelist, `IMemoryCache` caching with invalidation on rotation, `Cache-Control` header. |
| 2.4 | Step 8 — audit-event constants | **Amend** | Add `signing_key.generated`, `signing_key.rotated`, `signing_key.revoked`, `signing_key.expired` constants. |
| 2.7 | Step 17 — pgTAP | **Amend** | Add RLS, column privilege, audit trigger, and `bytea` type assertions to the `21_signing_keys.sql` plan. |
| 2.7 | Step 19 — xUnit | **Amend** | Add JWKS field-whitelist, tampered-ciphertext, overlap-window, and no-key-caching unit test assertions. |
| 2.6 | Step 22 — runbook | **Amend** | Add Data Protection key cadence section (60-day DP lifetime, re-encryption procedure, bootstrap env-var note). |

**No new numbered steps are required.** All changes are amendments to existing step descriptions. The total step count remains 22. The sign-off criteria and "Out of scope" sections are unchanged.

---

## 4. No-change decisions

The following were considered and explicitly **not** adopted:

| Item | Decision |
|---|---|
| Two separate Postgres connection strings (`heimdall_app` / `heimdall_signer`) | Deferred. If Render's managed Postgres makes two connection pools operationally awkward, a `SECURITY DEFINER` function pattern is acceptable. The runbook (step 22) must document whichever pattern is chosen. This is an operational, not a code, decision. |
| Independent KEK (raw AES key in env-var) instead of Data Protection | Rejected. Data Protection is already wired and tested from Phase 1. A raw env-var KEK would require implementing AES-GCM wrapping and key derivation that Data Protection already provides, for no gain at this scale. |
| Redis-cached signing credentials (in-memory `SecurityKey` across requests) | Rejected. The decrypted `RSA` / `ECDsa` object must not be held in a long-lived field. Request-scoped decrypt is the required pattern (§2.1). If per-request decrypt becomes a measured performance problem, the correct mitigation is a short-TTL `IMemoryCache` entry holding the `SecurityKey` (not the raw private bytes) — this can be added without re-opening the step. |
| Replacing `ISigningKeyService` with a cloud KMS adapter now | Deferred. The `ISigningKeyService` abstraction boundary already makes this a future drop-in. OpenKMS (`Gosayram/openkms`) is the preferred candidate once it reaches a stable release. No Phase 5 code should close that door (avoid `sealed` on `SigningKeyService`; avoid internal constructors that block subclassing). |

---

## 5. Agent instructions

> The following instructions are for the coding agent tasked with applying this proposal to
> [`docs/implementation/phase-5-checklist.md`](../implementation/phase-5-checklist.md).

1. **Read the current checklist in full** before making any edits. The file is at `docs/implementation/phase-5-checklist.md` on `main`. All 22 steps and all sign-off criteria must be present in the output.

2. **For each amended step**, extend the existing description in place. The amendment must be:
   - Consistent in tone and formatting with the existing checklist style (present-tense imperatives, inline code for identifiers, parenthetical cross-references to other steps and proposal sections).
   - Precise enough that an implementer reading only the checklist — without this proposal — has complete instructions.
   - Terminated with a period and not followed by a blank line before the next checkbox item.

3. **Do not restate the design rationale** from §2 of this proposal inside the checklist. The checklist convention (established in every prior phase) is that it does not restate design — it only states what must be done. Rationale belongs in the proposal documents.

4. **After amending the checklist**, verify that the amended step 1 references both the role grants and the RLS policy and the audit trigger as a single atomic migration; that step 2 references `IDataProtectionProvider` injection and `ZeroMemory` and the overlap invariant; that step 3 references the field whitelist and `IMemoryCache` and `Cache-Control`; that step 8 lists all four new `signing_key.*` constants by name; that step 17 lists all four new pgTAP assertions by name; that step 19 lists all four new xUnit assertions by name; and that step 22 references the 60-day DP key lifetime and the re-encryption procedure.

5. **Cross-references to this proposal.** Add the following entry to the References section of the checklist (before the "Out of scope" section):
   ```
   - Signing-key hardening proposal: [`docs/proposals/phase-5-signing-key-hardening.md`](../proposals/phase-5-signing-key-hardening.md) — envelope encryption, DB access controls, JWKS safety, and DP key cadence.
   ```

6. **Output.** Produce the complete amended `docs/implementation/phase-5-checklist.md`. Do not produce a diff or a partial file. The output will be committed directly to the repository.
