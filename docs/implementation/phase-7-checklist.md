# Phase 7 — DI Container Migration (Autofac → DryIoc): Implementation Checklist

**Status:** Planning. No steps started. Blocked on Phase 6 sign-off ([`phase-6-checklist.md`](./phase-6-checklist.md)) — Phase 6 retires Blazor and reduces `Heimdall.Web` to a pure ASP.NET Core API host; the DI migration runs against that slimmer composition root.
**Source of truth:** [`docs/proposals/autofac-to-dryioc-migration.md`](../proposals/autofac-to-dryioc-migration.md) (proposed 2026-05-22).
**Upstream:**
- [DryIoc](https://github.com/dadhi/DryIoc) — the replacement container.
- [DryIoc.Microsoft.DependencyInjection](https://github.com/dadhi/DryIoc/tree/master/src/DryIoc.Microsoft.DependencyInjection) — the MEL `IServiceProviderFactory<IContainer>` bridge.
- [.NET Generic Host `IServiceProviderFactory<T>`](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-provider-lifetime) — the abstraction both Autofac and DryIoc implement.
**Depends on:**
- Phase 6 complete on `main` ([`phase-6-checklist.md`](./phase-6-checklist.md)). Phase 6 retires Blazor and reduces `Heimdall.Web` to a pure API host; the DI migration targets that slimmer composition root.
- Phase 5 unchanged.

> This file is a **living tracking checklist** for the Phase 7 implementation PRs.
> It does **not** restate the design — see [`autofac-to-dryioc-migration.md`](../proposals/autofac-to-dryioc-migration.md) for rationale, current-surface enumeration, target-surface translation, and the risk table.
> Steps are **strictly ordered**: do not start step N+1 until step N is merged-quality and tested.
> Each step maps to one PR, independently committable and shippable per the proposal's stance.

---

## Phase 7.1 — Package swap

- [ ] **1. Remove Autofac packages from `src/Heimdall.Web/Heimdall.Web.csproj`.** Delete the `<PackageReference>` entries for `Autofac` and `Autofac.Extensions.DependencyInjection`. Rationale: these are the only Autofac packages referenced in the solution; no other project under `src/` or `tests/` references Autofac directly (confirmed by [`autofac-coverage-audit.md`](../proposals/autofac-coverage-audit.md) §2). Renovate Bot owns version management; the removal is unconditional and version-agnostic.

- [ ] **2. Add DryIoc packages to `src/Heimdall.Web/Heimdall.Web.csproj`.** Add `<PackageReference>` entries for `DryIoc` and `DryIoc.Microsoft.DependencyInjection`. Rationale: `DryIoc` is the container; `DryIoc.Microsoft.DependencyInjection` provides `DryIocServiceProviderFactory` and the `IServiceCollection` → `IContainer` forwarding bridge that mirrors what `Autofac.Extensions.DependencyInjection` does today. **No version pin** per the repository's Renovate policy — use whatever version Renovate tracks at implementation time.

---

## Phase 7.2 — Composition-root rewrite

- [ ] **3. Replace the `AutofacServiceProviderFactory` wiring in `src/Heimdall.Web/Program.cs`.** The three Autofac lines near line 561 (`builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory()); builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder => containerBuilder.RegisterModule<ApplicationModule>());`) are replaced with the DryIoc equivalent: `builder.Host.UseServiceProviderFactory(new DryIocServiceProviderFactory()); builder.Host.ConfigureContainer<IContainer>(container => container.RegisterApplicationServices());`. Rationale: same composition-root seam, same forwarding semantics — the change replaces only the container plumbing. The call to `RegisterApplicationServices()` invokes the extension method created in step 4.

- [ ] **4. Rename and rewrite `src/Heimdall.Web/DependencyInjection/ApplicationModule.cs` → `ApplicationRegistrations.cs`.** The file is renamed (not just the class) so the change is unambiguous on grep and on PR review. The new file exposes a `public static IContainer RegisterApplicationServices(this IContainer container)` extension method. **Chosen approach (per [`autofac-to-dryioc-migration.md`](../proposals/autofac-to-dryioc-migration.md) §3): rename rather than retain the `ApplicationModule` filename**, because the new shape is a static extension method, not an `Autofac.Module` subclass. Inside the method, register each of the seven services plus the config-driven factory using DryIoc's API:
  - `container.Register<ITicketRepository, TicketRepository>(Reuse.InCurrentScope)` (was `InstancePerLifetimeScope`)
  - `container.Register<ICacheService, RedisCacheService>(Reuse.Singleton)` (was `SingleInstance`)
  - `container.Register<ITicketService, TicketService>(Reuse.InCurrentScope)`
  - `container.Register<IMembershipAdminService, MembershipAdminService>(Reuse.InCurrentScope)`
  - `container.Register<ITicketMapper, TicketMapper>(Reuse.Singleton)`
  - `container.Register<TeamRoleBackedPermissionService>(Reuse.InCurrentScope)` (AsSelf)
  - `container.Register<OpenFgaPermissionService>(Reuse.InCurrentScope)` (AsSelf)
  - `container.Register<NotImplementedPermissionService>(Reuse.InCurrentScope)` (AsSelf)
  - `container.Register<IUserEnrollmentService, NotImplementedEnrollmentService>(Reuse.InCurrentScope)`
  - The config-driven `IPermissionService` selection becomes a `container.RegisterDelegate<IPermissionService>(resolver => { ... }, Reuse.InCurrentScope)` that reads `IConfiguration["Authorization:Provider"]` and forwards to the appropriate permission service — exactly as the Autofac factory does today, preserving the deny-closed fall-back to `NotImplementedPermissionService` for unrecognised values.

  Rationale: file-rename + extension-method shape makes the DI seam idiomatic DryIoc; behaviour is byte-for-byte equivalent for every consumer of every registered service.

---

## Phase 7.3 — Test updates

- [ ] **5. Rename and rewrite `tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs` → `ApplicationRegistrationsTests.cs`.** Remove all `new Autofac.ContainerBuilder()` usage. The replacement test exercises the `RegisterApplicationServices(...)` extension method against a fresh in-memory DryIoc `IContainer` and asserts the call does not throw. The test does **not** call `container.Validate()` or resolve any service — this mirrors the existing convention: do not build or resolve in unit tests; the integration-test layer owns that (per [`autofac-coverage-audit.md`](../proposals/autofac-coverage-audit.md) §4.1 and `agents/csharp-unit-tests.md`). Rationale: preserves the no-container-build-in-unit-tests convention; the only change is the container vendor.

- [ ] **6. Verify integration tests boot.** Confirm `tests/Heimdall.Web.Tests/Infrastructure/HeimdallWebApplicationFactoryWithOpenFga.cs` still boots the host. No source change required — `WebApplicationFactory<Program>` boots through whatever `IServiceProviderFactory` `Program.cs` registers. Run `dotnet test Heimdall.slnx --settings coverlet.runsettings` (the repository's verified CI-equivalent command) and confirm the full suite — including the OpenFGA Testcontainers integration tests — is green. Rationale: integration tests are the real container's safety net per [`autofac-coverage-audit.md`](../proposals/autofac-coverage-audit.md) §4.3; if DryIoc surfaces a latent registration bug (e.g. stricter open-generic matching), it surfaces here.

---

## Phase 7.4 — Documentation updates

- [ ] **7. Update `README.md`.** Change the `DI:` line from "Autofac (with `Autofac.Extensions.DependencyInjection`)" to "DryIoc (with `DryIoc.Microsoft.DependencyInjection`)". Update the Mapping parenthetical from "(Autofac-wired via `ApplicationModule`)" to "(DryIoc-wired via `ApplicationRegistrations`)". Rationale: the README's "Tech stack" section is the discoverable source of truth for new contributors and must remain accurate post-migration.

- [ ] **8. Update `docs/proposals/autofac-coverage-audit.md`.** Append a final 2026-05-22 decision-log entry noting the audit's findings are superseded by [`autofac-to-dryioc-migration.md`](../proposals/autofac-to-dryioc-migration.md); flip the status header from **Findings only — no changes recommended (2026-05-12)** to **Superseded (2026-05-22)** in the same PR. Cross-reference the migration proposal. Rationale: the audit's recommendation ("no code changes — Autofac is correct as-is") was correct at the time, but the post-Phase-6 host shape changes the cost/benefit analysis; the audit doc must point forward so readers aren't misled.

---

## Phase 7.5 — Acceptance

- [ ] **9. Final acceptance criteria.** All of the following gates must pass before the migration PR merges:
  - **(a)** All unit and integration tests green via `dotnet test Heimdall.slnx --settings coverlet.runsettings`.
  - **(b)** The OpenFGA integration tests (Phase 3.7 step 12) green against the real Testcontainers OpenFGA sidecar — confirms the `Authorization:Provider=OpenFga` config-driven factory path resolves correctly through DryIoc.
  - **(c)** `grep -rni autofac src/ tests/` returns zero matches — no `using Autofac;`, no `Autofac.Module`, no `ContainerBuilder`, no `InstancePerLifetimeScope` symbol references.
  - **(d)** `grep -rni autofac src/Heimdall.Web/Heimdall.Web.csproj` returns zero matches — the package references are fully removed.
  - **(e)** The `Authorization:Provider=TeamRole` / `OpenFga` / misconfigured-fallback integration test path remains green — the `WebApplicationFactory` override path covers this directly.
  - **(f)** Render production / preview deploy boots without an `IServiceProviderFactory`-related startup exception.

  Rationale: these gates together prove the migration is behaviour-equivalent — no Autofac surface remaining, and the deny-closed factory behaviour preserved.

---

## Deferred scope

The following are explicitly **not** in Phase 7:

- **Named/keyed services** — DryIoc supports `serviceKey` natively, but no new keyed registrations are introduced in this phase. The capability is available for future phases.
- **`Reuse.ScopedTo<T>`** — DryIoc's advanced scope-naming discipline is not introduced. The default `Reuse.InCurrentScope` matches Autofac's `InstancePerLifetimeScope` semantics in ASP.NET Core.
- **DryIoc source generators** — DryIoc offers compile-time registration verification via source generators. This is a future AOT-readiness step, not a Phase 7 scope item.

---

## Cross-references

- [`docs/proposals/autofac-to-dryioc-migration.md`](../proposals/autofac-to-dryioc-migration.md) — design source of truth.
- [`docs/implementation/phase-6-checklist.md`](./phase-6-checklist.md) — Phase 6 must complete first (post-Blazor `Program.cs` is the rewrite target).
- [`docs/proposals/blazor-to-svelte-transition.md`](../proposals/blazor-to-svelte-transition.md) §11 — sequencing rationale (Phase 6 reduces `Heimdall.Web` to a pure API host).
- [`docs/implementation/phase-8-checklist.md`](./phase-8-checklist.md) — successor phase (Admin UI write-side, renumbered from prior Phase 7).
- [`docs/proposals/autofac-coverage-audit.md`](../proposals/autofac-coverage-audit.md) — the audit that confirmed the Autofac surface before this migration was proposed.
