# Audit: Is Autofac really used everywhere in Heimdall?

**Status:** **Findings only — no changes recommended (2026-05-12)**
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.Web (composition root), Heimdall.Web Blazor components, Heimdall.BLL.Tests / Heimdall.Web.Tests / Heimdall.DAL.Tests
**Decision required:** Should we expand Autofac into areas that currently appear to bypass it (notably tests and Blazor), and/or adopt any Autofac contrib packages (e.g. `Autofac.Extras.Moq`)?

> This document is research and findings only. **No code, package, or DI changes are made in this PR.** If any of the recommendations below are accepted, they will land in separate, focused follow-up PRs.

---

## 1. Why we're looking at this

A maintainer observed that Autofac is the documented DI container for the project but suspected it was not being used in some areas — specifically tests and Blazor — and asked whether that should change, and whether any of the contrib packages on `https://github.com/autofac` (for example `Autofac.Extras.Moq`) should be adopted.

This audit answers three questions:

1. Is Autofac actually used throughout the production runtime, or are parts of the host bypassing it?
2. Why aren't tests using Autofac, and is that a gap or a deliberate choice?
3. Are any of the Autofac contrib / `Autofac.Extras.*` packages a fit for this codebase?

## 2. How Autofac is wired today

The composition root is `src/Heimdall.Web/Program.cs`. The key lines:

```csharp
// src/Heimdall.Web/Program.cs:427-430
builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
    containerBuilder.RegisterModule<ApplicationModule>()
);
```

This installs `Autofac.Extensions.DependencyInjection.AutofacServiceProviderFactory` on the generic host, which **replaces** the default `IServiceProviderFactory<IServiceCollection>`. From that point on:

- Every `builder.Services.Add*` registration (MEL / `IServiceCollection`) is forwarded into the Autofac `ContainerBuilder` at build time.
- The `IServiceProvider` resolved by the host at runtime is the Autofac-backed provider.
- Any consumer that resolves through `IServiceProvider` — including framework infrastructure like ASP.NET Core, Blazor, Identity, `HttpClientFactory`, hosted services, and middleware — is served by Autofac.

`ApplicationModule` (`src/Heimdall.Web/DependencyInjection/ApplicationModule.cs`) registers the app-specific components on the Autofac `ContainerBuilder` directly:

- Repositories (`TicketRepository`, …) — `InstancePerLifetimeScope`
- `RedisCacheService` — `SingleInstance`
- `TicketService`, `MembershipAdminService` — `InstancePerLifetimeScope`
- `TicketMapper` (Mapster-generated) — `SingleInstance`
- `IPermissionService` factory (config-driven: `TeamRole` / `OpenFga` / not-implemented fallback) — `InstancePerLifetimeScope`
- `IUserEnrollmentService` — bound to a fail-loud not-implemented service until a real implementation lands

OpenFGA, Identity, Serilog, Mapster, MFA, rate-limiting, data-protection, FluentMigrator, and the bootstrap services are all registered on `builder.Services` (MEL) and **forwarded into Autofac by the factory**. The inline comment at the bottom of `ApplicationModule.cs` (`// OpenFGA — Phase 3.3 + 3.4 …`) documents this explicitly.

## 3. Does Blazor bypass Autofac?

**No.** Blazor Server resolves `@inject` and constructor dependencies through `IServiceProvider` on the host. Because `AutofacServiceProviderFactory` has replaced the default factory, that `IServiceProvider` is Autofac-backed.

Concrete examples:

| File | Injections (all served by Autofac at runtime) |
| --- | --- |
| `Components/Pages/Tickets.razor:11-15` | `ITicketService`, `IUserLookup`, `NavigationManager`, `IOpenFgaAuthorizationService`, `IOptions<OpenFgaOptions>` |
| `Components/Pages/Login.razor`, `Register.razor`, `ResetPassword.razor`, `ForgotPassword.razor` | Identity + email services |
| `Components/Pages/Admin/*.razor` | Admin services, including `MembershipAdminService` |

The "different runtime" intuition is understandable — Blazor Server feels separate from MVC/Endpoints — but at the DI layer there is only one container, and it is Autofac.

There is also no Blazor WebAssembly project in this repo (`Heimdall.Web` is Blazor Server only, per `<BlazorDisableThrowNavigationException>true` in `Heimdall.Web.csproj` and `Program.cs`'s server-side endpoint mapping). If a Blazor WASM client is ever added, that client would run in the browser and could not use Autofac at all — but that is a property of WASM (no .NET host in the browser process), not a choice this project has made.

## 4. Do tests bypass Autofac?

Partially — and **deliberately**.

### 4.1 Unit tests do NOT spin up Autofac

This is the explicit project convention:

- `agents/csharp-unit-tests.md` quality gate: *"No Autofac container spun up in unit tests."*
- `tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs:33` carries an in-line comment that mirrors this: *"Per project convention we do NOT spin up an Autofac container or call Build() in unit tests."*

Unit tests instead use Moq + manual constructor injection. For example, `TicketServiceTests` (`tests/Heimdall.BLL.Tests/Services/TicketServiceTests.cs:21-38`):

```csharp
private readonly Mock<ITicketRepository> _repository = new(MockBehavior.Strict);
private readonly Mock<ICacheService> _cache = new(MockBehavior.Strict);
private readonly Mock<IPermissionService> _permissions = new(MockBehavior.Loose);
// …
private TicketService CreateSut() =>
    new(_repository.Object, _cache.Object, _mapper, _permissions.Object,
        _connectionFactory.Object, _auditWriter.Object, _tupleWriter.Object,
        NullLogger<TicketService>.Instance);
```

### 4.2 Why this is the right call

Building a real Autofac container in unit tests would:

1. **Couple unit tests to module wiring.** Every change in `ApplicationModule` would churn tests that have nothing to do with the change under test.
2. **Test the wrong thing.** Unit tests exist to test individual components; standing up a container exercises Autofac's registration system, not the SUT.
3. **Hide bugs in collaborators.** With a real container, an accidentally-resolvable dependency masks the explicit, intentional collaborator graph that `MockBehavior.Strict` enforces.
4. **Slow the inner loop.** Container construction and reflection-based registration add tens of milliseconds per test class.

The composition root itself is exercised in two ways that *don't* require running the container during unit tests:

- `ApplicationModuleTests` calls `Load()` directly with a fresh `ContainerBuilder` and asserts it doesn't throw. (`tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs:30-44`)
- Integration tests boot the real host via `WebApplicationFactory<Program>`, which constructs the real Autofac container end-to-end.

### 4.3 Integration tests DO exercise Autofac

`tests/Heimdall.Web.Tests/Infrastructure/HeimdallWebApplicationFactoryWithOpenFga.cs` boots the full `Program.cs` startup pipeline against Testcontainers Postgres + OpenFGA. That run goes through `AutofacServiceProviderFactory` + `ApplicationModule` exactly as production does — including the `Authorization:Provider` factory line:

```csharp
// HeimdallWebApplicationFactoryWithOpenFga.cs:133-139
builder.ConfigureAppConfiguration(configBuilder =>
{
    configBuilder.AddInMemoryCollection(new[]
    {
        new KeyValuePair<string, string?>("Authorization:Provider", "OpenFga"),
    });
});
```

The comment on lines 26-29 explicitly references the Autofac module: *"`Authorization:Provider` is injected via `IWebHostBuilder.ConfigureAppConfiguration` so the Autofac module selects `OpenFgaPermissionService` rather than the default TeamRole service."*

So: Autofac **is** tested. It is tested at the integration layer, where it belongs.

## 5. Are any `Autofac.Extras.*` / contrib packages worth adopting?

| Package | Purpose | Fit for Heimdall | Recommendation |
| --- | --- | --- | --- |
| `Autofac.Extensions.DependencyInjection` | Bridge MEL `IServiceCollection` ↔ Autofac container. | **Already in use** (`Heimdall.Web.csproj`). | Keep. |
| `Autofac.Extras.Moq` | AutoMock unresolved dependencies inside an Autofac scope at test time. | **Bad fit.** Conflicts with the repo's `MockBehavior.Strict` convention (see §4.1). AutoMock silently fabricates dependencies, which hides bugs that `Strict` is designed to surface. | **Do not adopt.** |
| `Autofac.Configuration` | XML/JSON-driven container configuration. | We have one config-driven binding (`Authorization:Provider`) handled in code in `ApplicationModule.cs:76-96`. A JSON file would be more indirect, not less. | **Do not adopt.** |
| `Autofac.Extras.DynamicProxy` / Castle interception | AOP / cross-cutting via dynamic proxies. | No AOP requirements. Logging is Serilog at the framework layer; auth is policies + OpenFGA; auditing is explicit (`IAuditEventWriter`). | **Do not adopt.** |
| `Autofac.Extras.AggregateService` | Auto-implement an aggregate-of-services interface. | No aggregate-service pattern in use; constructors are explicit and small. | **Do not adopt.** |
| `Autofac.Extras.AttributeMetadata` | Attribute-driven metadata on registrations. | All registrations are explicit in `ApplicationModule`. | **Do not adopt.** |
| `Autofac.AspNetCore.Multitenant` | Per-tenant container subtrees. | Heimdall is not multi-tenanted at the container level; tenancy is data-scoped (Org / Team) and authorization-scoped (OpenFGA). | **Do not adopt** unless container-level multi-tenancy becomes a requirement. |
| `Autofac.Pooling` | Pooled lifetime scopes for hot allocations. | No measured allocation hot spot points at scope creation; per-request scopes are already cheap. | **Do not adopt** without a measured perf case. |

### 5.1 On the AutoMapper contrib package (historical context)

`AutoMapper.Contrib.Autofac.DependencyInjection` *was* used for AutoMapper registration but was removed as part of the Mapster cutover (`docs/proposals/mapster-fec-transition.md` §9, 2026-05-01). `TicketMapper` is now registered directly in `ApplicationModule.cs:50` as `SingleInstance`. That's the right outcome — one less contrib package, and the mapper is now a source-generated, AOT-clean type rather than a runtime reflection profile.

## 6. Why the perceived gaps are not gaps

| Perceived gap | Reality |
| --- | --- |
| "Blazor doesn't use Autofac." | Blazor resolves through `IServiceProvider`, which is Autofac-backed via `AutofacServiceProviderFactory`. Every `@inject` flows through Autofac. |
| "Tests don't use Autofac." | Unit tests deliberately don't, by documented project convention, because they test components in isolation. Integration tests boot the real host and therefore the real Autofac container. |
| "There must be a reason — different runtime?" | The only runtime where Autofac genuinely cannot run is **Blazor WebAssembly** (no .NET host process in the browser). This repo doesn't ship a WASM client, so this is not relevant today. |

## 7. Recommendations

1. **No code changes.** Autofac coverage is correct as-is: full coverage at the runtime composition root (including Blazor) and at integration tests; deliberate absence at the unit-test layer.
2. **Do not adopt `Autofac.Extras.Moq`** or other contrib packages listed in §5. They either duplicate functionality already present or actively conflict with established test discipline.
3. **Optional, low-priority docs improvement (separate PR if desired):** Add a short "DI / composition root" section to `README.md` or `docs/` that calls out (a) `AutofacServiceProviderFactory` is the host-wide factory and therefore Blazor + MEL services are Autofac-served, and (b) the unit-test convention forbids spinning up the container. This audit document partially fills that gap.

## 8. Risks

This is a docs-only PR. There are no functional risks. The only risk is that future contributors miss the unit-test convention; that risk is mitigated by:

- The in-line comment in `ApplicationModuleTests.cs:33`.
- The orchestrator-level quality gate in `agents/csharp-unit-tests.md`.
- This document, if merged.

## 9. Decision log

- **2026-05-12 — Audit completed.** Findings: Autofac is used end-to-end at runtime (including Blazor via `AutofacServiceProviderFactory`); unit tests deliberately avoid the container per documented convention; integration tests exercise the full container. No contrib packages recommended for adoption. Awaiting maintainer review.

## 10. References

- Autofac — https://github.com/autofac/Autofac
- `Autofac.Extensions.DependencyInjection` — https://github.com/autofac/Autofac.Extensions.DependencyInjection
- Autofac contrib org — https://github.com/autofac
- `AutofacServiceProviderFactory` docs — https://autofac.readthedocs.io/en/latest/integration/aspnetcore.html
- Blazor Server DI — https://learn.microsoft.com/aspnet/core/blazor/fundamentals/dependency-injection
- Repo composition root — `src/Heimdall.Web/Program.cs`, `src/Heimdall.Web/DependencyInjection/ApplicationModule.cs`
- Unit-test convention — `agents/csharp-unit-tests.md`, `tests/Heimdall.Web.Tests/DependencyInjection/ApplicationModuleTests.cs`
- Prior related decision — `docs/proposals/mapster-fec-transition.md` (removed `AutoMapper.Contrib.Autofac.DependencyInjection`)
