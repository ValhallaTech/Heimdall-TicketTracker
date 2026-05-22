# Proposal: Migrate from Autofac to DryIoc (DI Container Replacement)

**Status:** **Proposed** (2026-05-22)
**Author:** Orchestrator (Copilot)
**Scope:** `Heimdall.Web` composition root — `src/Heimdall.Web/Program.cs`, `src/Heimdall.Web/DependencyInjection/ApplicationModule.cs`, `src/Heimdall.Web/Heimdall.Web.csproj`, `tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs`, `tests/Heimdall.Web.Tests/Infrastructure/HeimdallWebApplicationFactoryWithOpenFga.cs`.
**Decision required:** Replace the Autofac DI container (`Autofac` + `Autofac.Extensions.DependencyInjection`) with DryIoc (`DryIoc` + `DryIoc.Microsoft.DependencyInjection`) as the new Phase 7, after Phase 6 (Svelte migration) completes.
**Depends on:** [`docs/proposals/autofac-coverage-audit.md`](./autofac-coverage-audit.md) (current Autofac surface confirmed); [`docs/implementation/phase-6-checklist.md`](../implementation/phase-6-checklist.md) (Phase 6 retires Blazor, rewrites `Program.cs`, and reduces `Heimdall.Web` to a pure API host); decision in [`docs/proposals/blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) to land Topology B + JWT bearer as the production auth model.

*This document is research and planning only.*

---

## 1. Why now — sequencing rationale

Six factors motivate sequencing the DI container migration immediately after Phase 6:

1. **Phase 6 retires Blazor and rewrites `Program.cs`.** The Blazor Server UI is removed in [`phase-6-checklist.md`](../implementation/phase-6-checklist.md) step 16, and `Heimdall.Web` is reduced from a Blazor host to a pure ASP.NET Core API host. The DI wiring in `Program.cs` is not "untouched" through Phase 6 — it is already being refactored. The DI container migration can ride the same wave of change rather than being a disruptive standalone rewrite later.

2. **Post-Phase-6 `Heimdall.Web` is a pure API host where allocation profile matters.** With Blazor retired, the host processes JWT bearer-authenticated API requests at potentially high concurrency. DryIoc's benchmark-documented lower allocation overhead becomes relevant in this workload shape — not because Autofac is slow, but because every allocation saved compounds under concurrency.

3. **The Autofac surface is small.** The [`autofac-coverage-audit.md`](./autofac-coverage-audit.md) confirmed the surface: approximately 121 lines of code in `ApplicationModule.cs`, 3 lines in `Program.cs` (lines 561–565), and one shallow unit test (`ApplicationModuleTests.cs`). This is a bounded migration, not a multi-month refactor.

4. **The config-driven `IPermissionService` factory translates directly.** Autofac's `builder.Register(ctx => ...)` factory pattern maps 1:1 to DryIoc's `container.RegisterDelegate<T>(resolver => ...)` — the most complex registration in the codebase has a clean equivalent.

5. **The Phase 5 JWT API will see real concurrency post-Phase-6.** With the SvelteKit frontend issuing API calls for every data load and mutation, the JWT bearer authentication + OpenFGA `Check()` hot path executes at a higher rate than the Blazor Server circuit model. The reduced allocation overhead of DryIoc is measurable at this scale.

6. **Autofac's NativeAOT story is weaker than DryIoc's.** A slim post-Phase-6 API host becomes a future NativeAOT candidate. DryIoc explicitly supports NativeAOT via its compile-time registration verification and source-generator-friendly patterns. Autofac's reflection-heavy model is AOT-hostile. This is a future-proofing consideration, not an immediate requirement.

**Why DryIoc is retained over plain MEL:** The team wants the richer registration surface the codebase will need as it grows beyond its current small surface:

- **Named/keyed services** — the `Authorization:Provider` factory pattern already demonstrates a need for conditional service selection; DryIoc's `serviceKey` parameter supports this natively.
- **`RegisterDelegate`** — the config-driven factory pattern is used today and will be used for future conditional registrations.
- **Conditional registrations** — DryIoc's `RegisterMany`, `RegisterDelegate`, and `Setup.Condition` support patterns that plain MEL's `IServiceCollection` does not express cleanly.

Plain MEL would work for the current 7-registration surface, but the growth trajectory (Phase 7 Admin UI, future enrollment services, potential multi-tenant scoping) makes a richer container worthwhile.

---

## 2. Current Autofac surface

The [`autofac-coverage-audit.md`](./autofac-coverage-audit.md) enumerated every Autofac touchpoint. This section summarises and verifies the findings.

### 2.1 Packages

`src/Heimdall.Web/Heimdall.Web.csproj` references:

- `Autofac` (version managed by Renovate Bot)
- `Autofac.Extensions.DependencyInjection` (version managed by Renovate Bot)

No other project under `src/` or `tests/` directly references Autofac packages.

### 2.2 `ApplicationModule.cs`

`src/Heimdall.Web/DependencyInjection/ApplicationModule.cs` is an `Autofac.Module` subclass (121 lines) that registers 7 services plus the config-driven `IPermissionService` factory:

| Registration | Interface | Lifetime | Lines |
| --- | --- | --- | --- |
| `TicketRepository` | `ITicketRepository` | `InstancePerLifetimeScope` | 27–29 |
| `RedisCacheService` | `ICacheService` | `SingleInstance` | 32 |
| `TicketService` | `ITicketService` | `InstancePerLifetimeScope` | 35 |
| `MembershipAdminService` | `IMembershipAdminService` | `InstancePerLifetimeScope` | 41–44 |
| `TicketMapper` | `ITicketMapper` | `SingleInstance` | 50 |
| `TeamRoleBackedPermissionService` | (self) | `InstancePerLifetimeScope` | 57–60 |
| `OpenFgaPermissionService` | (self) | `InstancePerLifetimeScope` | 61–64 |
| `NotImplementedPermissionService` | (self) | `InstancePerLifetimeScope` | 65–68 |
| `NotImplementedEnrollmentService` | `IUserEnrollmentService` | `InstancePerLifetimeScope` | 107–110 |

The config-driven `IPermissionService` factory (lines 77–96) reads `IConfiguration["Authorization:Provider"]` and returns one of the three permission service implementations:

- `"TeamRole"` (default) → `TeamRoleBackedPermissionService`
- `"OpenFga"` → `OpenFgaPermissionService`
- Any other value → `NotImplementedPermissionService` (fail-loud on misconfiguration)

### 2.3 `Program.cs`

`src/Heimdall.Web/Program.cs` lines 561–565:

```csharp
// --- Autofac --------------------------------------------------------------
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
    containerBuilder.RegisterModule<ApplicationModule>()
);
```

These three lines are the entire composition-root bridge. `AutofacServiceProviderFactory` replaces the default MEL `IServiceProviderFactory<IServiceCollection>` so every `builder.Services.Add*` registration is forwarded into the Autofac `ContainerBuilder` at build time.

### 2.4 `ApplicationModuleTests.cs`

`tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs` (45 lines) exercises the module:

- `Should_CreateInstance_When_DefaultConstructed` — asserts the module can be instantiated.
- `Should_Throw_When_LoadCalledWithNullBuilder` — asserts `ArgumentNullException` on null input.
- `Should_NotThrow_When_LoadCalledWithRealBuilder` — creates a `new Autofac.ContainerBuilder()`, invokes `Load()` via reflection, asserts no exception.

The test deliberately does **not** call `Build()` or resolve any services — per the project convention "no Autofac container spun up in unit tests" documented inline and in `agents/csharp-unit-tests.md`.

### 2.5 `HeimdallWebApplicationFactoryWithOpenFga.cs`

`tests/Heimdall.Web.Tests/Infrastructure/HeimdallWebApplicationFactoryWithOpenFga.cs` boots the full `Program.cs` startup pipeline via `WebApplicationFactory<Program>`. This exercises the real `AutofacServiceProviderFactory` and `ApplicationModule` end-to-end. The test overrides `Authorization:Provider=OpenFga` via `IWebHostBuilder.ConfigureAppConfiguration` so the factory delegate selects `OpenFgaPermissionService`.

### 2.6 Documentation references

The following documentation files mention Autofac and will need updates after the migration ships (flagged in §6, not edited in this PR):

| File | Mention |
| --- | --- |
| `README.md` | `DI: Autofac (with Autofac.Extensions.DependencyInjection)` and `Mapping: Mapster ... (Autofac-wired via ApplicationModule)` |
| `docs/proposals/autofac-coverage-audit.md` | Entire document |
| `docs/proposals/blazor-to-svelte-transition.md` §2 | `DI \| Autofac with ApplicationModule + DalModule.` |
| `docs/proposals/mapster-fec-transition.md` §9 | Historical decision log mention |

**Note:** [`blazor-to-svelte-transition.md`](./blazor-to-svelte-transition.md) §2 references "DalModule" but **no separate `DalModule` exists in the codebase today** — only `ApplicationModule`. This is documentation drift that should be fixed in the cleanup-list section.

---

## 3. Target DryIoc surface

This section describes the DryIoc equivalent for every registration. Per the repository's "no code blocks longer than 4 lines in proposals" convention, the translation is described in prose.

### 3.1 Container bridge

Replace `AutofacServiceProviderFactory` with `DryIocServiceProviderFactory` from `DryIoc.Microsoft.DependencyInjection`. The `builder.Host.ConfigureContainer<ContainerBuilder>(...)` call becomes `builder.Host.ConfigureContainer<IContainer>(container => { ... })` — DryIoc passes its `IContainer` directly into the configure callback, unlike Autofac's `ContainerBuilder` intermediary.

### 3.2 Module → extension method

Autofac's `Module` subclass pattern has no first-class equivalent in DryIoc. The idiomatic replacement is a static extension method on `IContainer`. **The chosen approach is to rename `ApplicationModule.cs` to `ApplicationRegistrations.cs` and expose a `public static IContainer RegisterApplicationServices(this IContainer container)` extension method.** This makes the change unambiguous on grep and on PR review — a file named `ApplicationModule` that no longer extends `Autofac.Module` would be confusing.

### 3.3 Lifetime mapping

| Autofac | DryIoc |
| --- | --- |
| `InstancePerLifetimeScope` | `Reuse.InCurrentScope` |
| `SingleInstance` | `Reuse.Singleton` |
| `InstancePerDependency` (not used today) | `Reuse.Transient` |

### 3.4 Registration shape

Autofac's `builder.RegisterType<X>().As<I>().InstancePerLifetimeScope()` becomes DryIoc's `container.Register<I, X>(Reuse.InCurrentScope)`.

Autofac's `builder.RegisterType<X>().AsSelf().InstancePerLifetimeScope()` becomes DryIoc's `container.Register<X>(Reuse.InCurrentScope)`.

### 3.5 Config-driven `IPermissionService` factory

Autofac's `builder.Register(ctx => ...).As<IPermissionService>().InstancePerLifetimeScope()` factory becomes a DryIoc `RegisterDelegate`:

The delegate receives an `IResolver` (DryIoc's resolution interface), resolves `IConfiguration`, reads `["Authorization:Provider"]`, and returns one of:

- `TeamRoleBackedPermissionService` (resolved via `resolver.Resolve<TeamRoleBackedPermissionService>()`) when the provider is null, empty, or `"TeamRole"` (case-insensitive).
- `OpenFgaPermissionService` when the provider is `"OpenFga"` (case-insensitive).
- `NotImplementedPermissionService` for any other value (fail-loud on misconfiguration, preserving the deny-closed fall-back).

The delegate is registered with `Reuse.InCurrentScope` to match the current Autofac behaviour.

### 3.6 Forwarded MEL registrations

OpenFGA (via `AddHeimdallOpenFga`), Identity, Serilog, Mapster wiring on the MEL side, Data Protection, FluentMigrator, MemoryCache, Redis `StackExchange.Redis` `IConnectionMultiplexer`, rate-limit policies, the JWKS endpoint, the `JwtBearer` scheme, and the bootstrap services **all continue to register on `builder.Services` (the MEL `IServiceCollection`) and are forwarded into DryIoc by `DryIocServiceProviderFactory`** exactly the way `AutofacServiceProviderFactory` forwards them today.

No change to any `AddX(...)` extension method. No change to any callsite outside `ApplicationModule.cs` (→ `ApplicationRegistrations.cs`) and the 3 lines in `Program.cs`.

---

## 4. Package changes

**Remove:**

- `Autofac`
- `Autofac.Extensions.DependencyInjection`

**Add:**

- `DryIoc`
- `DryIoc.Microsoft.DependencyInjection`

**No version pins** — state "latest stable at time of implementation; Renovate Bot owns subsequent bumps."

**Licence:** DryIoc is MIT-licensed. The repository's `LICENSE` file shows MIT. No licence conflict.

---

## 5. Test changes

### 5.1 `ApplicationModuleTests.cs` → `ApplicationRegistrationsTests.cs`

Drop the `new Autofac.ContainerBuilder()` test. Rename the file to `ApplicationRegistrationsTests.cs` to match the renamed source file.

The replacement test exercises the static `RegisterApplicationServices(this IContainer container)` extension method against a fresh in-memory DryIoc `IContainer` and asserts the call does not throw. This preserves the no-container-build-in-unit-tests convention — the test does **not** call `container.Validate()` or resolve any service. The intent is to assert the registration extension method runs without throwing, not to assert the resolution graph (integration tests own that).

Alternatively, the test could exercise the method against a bare `ServiceCollection` and assert the method is callable and registers the expected service descriptors — same posture, different entry point. The implementation PR will choose one approach and document it.

### 5.2 Integration tests (`HeimdallWebApplicationFactoryWithOpenFga.cs`)

No source change required. `WebApplicationFactory<Program>` boots the real host, and the host swaps `AutofacServiceProviderFactory` for `DryIocServiceProviderFactory` transparently. The `Authorization:Provider=OpenFga` override continues to flow through the `RegisterDelegate` factory unchanged — the factory reads `IConfiguration` the same way.

### 5.3 Quality gate update

The `agents/csharp-unit-tests.md` quality gate "no Autofac container spun up in unit tests" becomes "no DI container spun up in unit tests" — the rule is container-agnostic and stays. The implementation PR will update the agent doc if the exact wording references Autofac by name.

---

## 6. Documentation cleanup (flag only — do NOT update in this PR)

The following documentation files mention Autofac and will need a one-line update after the DI migration ships. These are **flagged only** — this proposal does not edit them.

| File | Change required |
| --- | --- |
| `README.md` | Update `DI:` line from "Autofac (with `Autofac.Extensions.DependencyInjection`)" to "DryIoc (with `DryIoc.Microsoft.DependencyInjection`)". Update the Mapping parenthetical from "(Autofac-wired via `ApplicationModule`)" to "(DryIoc-wired via `ApplicationRegistrations`)". |
| `docs/proposals/autofac-coverage-audit.md` | Append a 2026-05-22 decision-log entry noting the audit findings remain accurate at the time of writing but the migration to DryIoc has been proposed. Flip status header to **Superseded** when the migration ships. |
| `docs/proposals/blazor-to-svelte-transition.md` §2 | Update the `DI \| Autofac with ApplicationModule + DalModule.` row. Both the container name ("Autofac" → "DryIoc") and the spurious "DalModule" reference (which does not exist in code today — documentation drift) should be corrected. |
| `docs/proposals/mapster-fec-transition.md` §9 | The historical decision log sentence "Autofac `ApplicationModule` registers `TicketMapper`" is historically accurate and can stay as-is. Flag a footnote for clarity that the module was later renamed and the container replaced. |

**Source file inline comments:** `src/Heimdall.Web/DependencyInjection/ApplicationModule.cs` contains inline comments referencing "Autofac" / "AutofacServiceProviderFactory" / "forwarded into Autofac". These become "forwarded into DryIoc" once the rewrite lands. This is a source file, not a doc — list it here for the implementation PR's grep checklist, not the docs-cleanup list.

---

## 7. Risk table

| Risk | Description | Mitigation | Owner |
| --- | --- | --- | --- |
| (a) MEL bridge compatibility | `DryIoc.Microsoft.DependencyInjection` MUST honour every `IServiceCollection`-registered service descriptor including `IOptions<T>`, `IOptionsMonitor<T>`, `IHttpClientFactory`, hosted services, and Mapster's `TypeAdapterConfig` if it lands on MEL. | An integration smoke test in the migration PR that resolves a representative set of MEL-registered services from the DryIoc-backed `IServiceProvider`. | C# Coding Agent |
| (b) OpenFGA / Identity / Serilog forwarding | These are MEL-side registrations forwarded through the factory. The `OpenFgaPermissionService` resolution via the `RegisterDelegate` factory must continue to receive the same `OpenFgaClient` / `IMemoryCache` / `IOptions<OpenFgaOptions>` instances. | Integration test `HeimdallWebApplicationFactoryWithOpenFga` covers this end-to-end. **Co-owned by the OpenFGA Expert.** | C# Coding Agent, OpenFGA Expert |
| (c) Integration test `WebApplicationFactory` host boot | DryIoc's stricter resolution diagnostics may surface latent registration bugs that Autofac silently tolerated (e.g. open-generic mismatches). | Run the full xUnit + Testcontainers suite from `dotnet test Heimdall.slnx --settings coverlet.runsettings` (the repo's verified test command) before merging. | C# Coding Agent |
| (d) `Authorization:Provider` config-driven factory | Must continue to deny-close on misconfiguration by binding the `NotImplementedPermissionService` for unrecognised values. | Unit test asserting the factory returns `NotImplementedPermissionService` for an invalid provider string. | C# Coding Agent |
| (e) Lifetime scope naming differences | `InstancePerLifetimeScope` → `Reuse.InCurrentScope` is semantically equivalent in ASP.NET Core where the per-request scope is the only nested scope. If any code path later introduces a manual child scope (e.g. a hosted service's bespoke `IServiceScopeFactory.CreateScope()`), DryIoc and Autofac may differ on what "current scope" means. | Flag as a watch-item in the migration PR's acceptance test list. Avoid introducing named scopes in this phase. | C# Coding Agent |
| (f) Redis registration | `ICacheService` → `RedisCacheService` is `Singleton`. `IConnectionMultiplexer` is registered on MEL via `StackExchange.Redis` and forwarded through. Both Autofac and DryIoc must resolve it as a singleton. | Integration test asserting `ICacheService` resolution succeeds and is singleton-scoped. **Co-owned by the Redis Expert.** | C# Coding Agent, Redis Expert |
| (g) Postgres / Dapper registrations | Connection-factory and FluentMigrator runner registrations on MEL must forward through DryIoc identically. | Integration test asserting database connectivity post-migration. **Co-owned by the Database Expert.** | C# Coding Agent, Database Expert |
| (h) `Reuse.ScopedTo<T>` and named scope discipline | DryIoc supports optional scope-name discipline via `Reuse.ScopedTo<T>`. The migration should NOT introduce named scopes. The default `Reuse.InCurrentScope` matches Autofac's `InstancePerLifetimeScope` without operator surprise. | Code review checklist item: "No `Reuse.ScopedTo<T>` or named scope parameters introduced." | C# Coding Agent |

---

## 8. Decision log

| Date | Decision |
| --- | --- |
| 2026-05-22 | Proposal drafted. Recommends migrating from `Autofac` + `Autofac.Extensions.DependencyInjection` to `DryIoc` + `DryIoc.Microsoft.DependencyInjection` as the new Phase 7, sequenced after Phase 6 retires Blazor and reduces `Heimdall.Web` to a pure API host. The existing Phase 7 (Admin UI — never authored as a standalone checklist file) is renumbered to Phase 8. DryIoc retained over plain MEL because the team wants the richer registration surface (`RegisterDelegate`, named/keyed services, conditional registrations) the project will need as it grows. SMEs consulted via the existing source-of-truth proposals ([`autofac-coverage-audit.md`](./autofac-coverage-audit.md), [`openfga.md`](./openfga.md), [`redis-optimization.md`](./redis-optimization.md), [`security-and-authorization.md`](./security-and-authorization.md)); concerns reflected in the risk table. |

---

## 9. References

- **DryIoc** — [https://github.com/dadhi/DryIoc](https://github.com/dadhi/DryIoc)
- **DryIoc.Microsoft.DependencyInjection** — same repo, sub-project: [https://github.com/dadhi/DryIoc/tree/master/src/DryIoc.Microsoft.DependencyInjection](https://github.com/dadhi/DryIoc/tree/master/src/DryIoc.Microsoft.DependencyInjection)
- **Autofac** — [https://github.com/autofac/Autofac](https://github.com/autofac/Autofac)
- **Autofac.Extensions.DependencyInjection** — [https://github.com/autofac/Autofac.Extensions.DependencyInjection](https://github.com/autofac/Autofac.Extensions.DependencyInjection)
- **Autofac coverage audit** — [`docs/proposals/autofac-coverage-audit.md`](./autofac-coverage-audit.md)
- **.NET Generic Host `IServiceProviderFactory<T>`** — [https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-provider-lifetime](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection#service-provider-lifetime)

---

**Next step:** Review this proposal. If approved, author [`docs/implementation/phase-7-checklist.md`](../implementation/phase-7-checklist.md) per the steps enumerated above and renumber the Admin UI phase to Phase 8.
