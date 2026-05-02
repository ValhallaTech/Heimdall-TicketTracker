# Proposal: C# 13 / C# 14 feature-adoption audit

**Status:** **Draft (2026-05-02)** — planning only.
**Author:** Orchestrator (Copilot)
**Scope:** `src/Heimdall.Core`, `src/Heimdall.DAL`, `src/Heimdall.BLL`, `src/Heimdall.Web`
**Decision required:** Which C# 13 and C# 14 language features (if any) should we adopt across the Heimdall codebase, and which should we explicitly choose **not** to adopt?

> This document is research and planning only. **No code, package, or project changes are made in this PR.** A separate, follow-up PR will implement any approved adoptions.

---

## 1. Why we're looking at this

All `src/*` projects target `net10.0` and do **not** set `<LangVersion>` ([`Heimdall.BLL.csproj:17`][bll-csproj], [`Heimdall.Core.csproj:3`][core-csproj], [`Heimdall.DAL.csproj:25`][dal-csproj], [`Heimdall.Web.csproj:19`][web-csproj]). The default C# language version for `net10.0` is **C# 14**, so every C# 13 feature and every stable C# 14 feature is already available to us — we just have to choose to use them. This proposal inventories the language surface introduced in [C# 13][cs13-whats-new] and [C# 14][cs14-whats-new] against what is actually in the repo today, and recommends a small, low-risk adoption set.

## 2. Audit method

For each feature shipped in C# 13 or C# 14, this document asks three questions:

1. **Is it already in use?** (grep / file review)
2. **Is there a concrete, current site in the repo where it would improve readability, safety, or performance?**
3. **Is there an explicit reason to skip it?** (preview status, churn risk, library compatibility, team conventions)

The audit covers the production source under `src/`. Test code (`tests/`), generated code (`*.g.cs`), and Razor markup are out of scope for this round but can be revisited if the recommendations below are accepted.

## 3. C# 13 feature inventory

| #   | Feature                                                                                | In use today?                       | Recommendation                  |
| --- | -------------------------------------------------------------------------------------- | ----------------------------------- | ------------------------------- |
| 3.1 | `params` collections (`Span<T>`, `IEnumerable<T>`, `ReadOnlySpan<T>`, etc.)            | No                                  | **Skip** — no API surface needs it |
| 3.2 | New lock object — `System.Threading.Lock`                                              | No (no `lock` statements in `src/`) | **Skip** — nothing to migrate       |
| 3.3 | `\e` escape sequence                                                                   | No                                  | **Skip** — no ANSI escape literals  |
| 3.4 | Method-group natural type improvements                                                 | Implicit (compiler-driven)          | N/A — no source change needed       |
| 3.5 | Implicit indexer access (`^1`) in object initializers                                  | No                                  | **Skip** — no current site          |
| 3.6 | `ref struct` interface implementation / `allows ref struct` constraint                 | No                                  | **Skip** — no `ref struct` types    |
| 3.7 | `ref` / `unsafe` in iterators and `async` methods                                      | No                                  | **Skip** — not needed               |
| 3.8 | Partial properties and partial indexers                                                | No                                  | **Skip** — no source-generator that needs it |
| 3.9 | `OverloadResolutionPriorityAttribute`                                                  | No                                  | **Conditional adopt** — see §5.1     |

Citations for "not in use" are provided implicitly by the grep-able absence of these keywords in `src/**/*.cs`. Two ctors on [`TicketRepository`][ticket-repo] (lines 56–75) are different arity, so they do **not** require `OverloadResolutionPriorityAttribute` — Autofac already picks the greediest constructor at runtime by design. The attribute would only become relevant if we ever introduce two same-arity overloads.

## 4. C# 14 feature inventory

| #   | Feature                                                                                          | Status     | In use today? | Recommendation                  |
| --- | ------------------------------------------------------------------------------------------------ | ---------- | ------------- | ------------------------------- |
| 4.1 | `field` contextual keyword in property accessors                                                 | Stable     | No            | **Adopt where it removes a private field** — see §5.2 |
| 4.2 | Extension members (extension blocks / instance-style extensions)                                 | Stable in C# 14 | No       | **Adopt selectively** — see §5.3 |
| 4.3 | `nameof` on unbound generic types (`nameof(List<>)`)                                             | Stable     | No            | **Skip** — no current site          |
| 4.4 | Null-conditional assignment (`x?.Prop = value;`, `x?[i] = value;`)                               | Stable     | No            | **Skip** — pattern not present      |
| 4.5 | `partial` instance constructors and `partial` events                                             | Stable     | No            | **Skip** — no source generator yet  |
| 4.6 | Lambda parameter modifiers without explicit type (`(ref x) => …`)                                | Stable     | No            | **Skip** — no `ref`/`out` lambdas   |
| 4.7 | Implicit `Span<T>` / `ReadOnlySpan<T>` conversions from arrays                                   | Stable     | Implicit (no source change required) | N/A     |
| 4.8 | User-defined compound assignment operators (`+=`, etc.)                                          | Stable     | No            | **Skip** — no operator-heavy types  |
| 4.9 | File-based programs (`dotnet run app.cs`)                                                        | Stable (tooling) | No      | **Skip** — multi-project SLN format |

## 5. Concrete adoption candidates

This is the short list of *real* sites where a C# 13 / C# 14 feature would measurably improve the code. Each is opt-in; rejecting any one of them is fine.

### 5.1 `OverloadResolutionPriorityAttribute` on the `TicketRepository` ctors — **defer**

[`TicketRepository`][ticket-repo] today has two constructors of different arity (`IOptions<DataOptions>` and `IOptions<DataOptions> + IDapper`). Autofac picks the greediest one at runtime; tests construct the simpler one directly. No same-arity overload set exists, so there is **nothing for `OverloadResolutionPriorityAttribute` to disambiguate**. Defer until / unless we introduce two same-arity overloads.

### 5.2 `field` keyword in `PagedQuery` — **strong adopt candidate**

Today, [`PagedQuery`][paged-query] (lines 36–104) is a hand-written class with explicit private fields hidden behind read-only properties, plus a constructor that performs clamping/validation, plus a separate `Sanitized()` method that re-runs the constructor. With the C# 14 `field` keyword, the same invariants can be expressed directly in the property declarations:

- Eliminates the `_page` / `_pageSize` etc. duplication of state and contract.
- Lets `Sanitized()` collapse to either a `with`-style copy on a `record` or a single-line `new(this.Page, this.PageSize, …)` factory.
- Makes the validation rules visible *next to the property they enforce*, instead of buried in a multi-arg constructor.

Trade-off: `field` is new, so anyone unfamiliar with C# 14 will need a one-line comment / link. That is a small, one-time onboarding cost.

This is the single highest-value change in this proposal. Recommended as a follow-up PR.

### 5.3 Extension members for the `IServiceProvider` migration / seeding helpers — **adopt selectively**

[`MigrationRunnerExtensions`][mig-extensions] (lines 60–183) exposes two extension methods on `IServiceProvider` (`RunHeimdallMigrationsAsync`, `SeedIfRequestedAsync`). C# 14 extension members let us group these inside an `extension(IServiceProvider serviceProvider)` block, which:

- Removes the `this IServiceProvider serviceProvider` parameter repetition on every signature.
- Lets the receiver argument validation (`ArgumentNullException.ThrowIfNull(serviceProvider)`) live once on the block instead of being re-stated in every method body.
- Keeps the same `MigrationRunnerExtensions` static-class shell, so callers' `serviceProvider.RunHeimdallMigrationsAsync(...)` call sites are unchanged.

Same logic applies to [`ServiceCollectionExtensions.AddDal`][svc-extensions]. Recommended as a follow-up PR, *only* once we confirm that the source generator / analyzer ecosystem we depend on (Mapster.Tool, Autofac, Dapper.Extensions) does not regress on extension-member call sites.

### 5.4 Collection expressions — **already adopted**

The repo already uses C# 12 collection expressions (`[.. rows]`) in [`TicketRepository`][ticket-repo] (lines 90 and 167) and `[]` literal in [`PagedResult.Items`][paged-result] (line 14). No further work needed; flagged here so future reviewers don't propose them as "new".

### 5.5 Primary constructors — **deliberate non-adoption**

C# 12 primary constructors are available, but they do **not** participate in `ArgumentNullException.ThrowIfNull` patterns and they do not surface XML doc comments per parameter cleanly. The repo's house style ([`TicketService`][ticket-service] lines 37–52, [`RedisCacheService`][redis-cache] lines 37–43, [`TicketRepository`][ticket-repo] lines 56–75) is to keep an explicit constructor with `ArgumentNullException.ThrowIfNull` per parameter and an XML doc comment per parameter. Recommendation: **continue** the explicit-ctor convention; do **not** adopt primary constructors in `src/`.

## 6. What is *not* recommended (and why)

- **Convert `Ticket` / `TicketDto` to records.** They are mutable domain / form models, with `[Required]` data annotations on `TicketDto` and EF/Dapper-friendly settable properties on `Ticket`. Records would force value-based equality semantics that don't match how the BLL uses these types (e.g. `_mapper.Map(dto)` round-tripping through repository writes).
- **Replace `Newtonsoft.Json` in [`RedisCacheService`][redis-cache] with `System.Text.Json`.** Out of scope for a *language*-feature audit. The Newtonsoft choice is justified inline (lines 18–23) by interoperability with Midgard. Touching it is a separate proposal.
- **Adopt anything in C# 14 marked as "preview" only.** As of .NET 10 GA, the features listed in §4 are stable; if a feature reverts to preview in a future release, this proposal must be revisited before adoption.

## 7. Summary

| Track | Action                                                                                  |
| ----- | --------------------------------------------------------------------------------------- |
| **Adopt** | C# 14 `field` keyword in `PagedQuery` (§5.2)                                        |
| **Adopt** | C# 14 extension members in `MigrationRunnerExtensions` and `ServiceCollectionExtensions` (§5.3) |
| **Defer** | `OverloadResolutionPriorityAttribute` (§5.1) — revisit if a same-arity overload appears |
| **Skip**  | All other C# 13 / 14 features inventoried in §3 and §4                                  |
| **Confirm** | Continue the explicit-ctor + `ArgumentNullException.ThrowIfNull` house style (§5.5) |

Each "Adopt" item is small, isolated, and can ship as its own PR with its own tests. None require package upgrades, target-framework changes, or `<LangVersion>` overrides — they are all unlocked by the existing `net10.0` target.

## 8. Decision log

| Date       | Decision                                                                                  |
| ---------- | ----------------------------------------------------------------------------------------- |
| 2026-05-02 | Proposal drafted. Awaiting review.                                                        |

[cs13-whats-new]: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-13
[cs14-whats-new]: https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14
[bll-csproj]: ../../src/Heimdall.BLL/Heimdall.BLL.csproj
[core-csproj]: ../../src/Heimdall.Core/Heimdall.Core.csproj
[dal-csproj]: ../../src/Heimdall.DAL/Heimdall.DAL.csproj
[web-csproj]: ../../src/Heimdall.Web/Heimdall.Web.csproj
[ticket-repo]: ../../src/Heimdall.DAL/Repositories/TicketRepository.cs
[paged-query]: ../../src/Heimdall.Core/Models/Pagination/PagedQuery.cs
[paged-result]: ../../src/Heimdall.Core/Models/Pagination/PagedResult.cs
[ticket-service]: ../../src/Heimdall.BLL/Services/TicketService.cs
[redis-cache]: ../../src/Heimdall.DAL/Caching/RedisCacheService.cs
[mig-extensions]: ../../src/Heimdall.DAL/Migrations/MigrationRunnerExtensions.cs
[svc-extensions]: ../../src/Heimdall.DAL/Extensions/ServiceCollectionExtensions.cs
