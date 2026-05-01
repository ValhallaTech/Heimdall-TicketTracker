# Proposal: Transition from AutoMapper to Mapster (with optional FastExpressionCompiler)

**Status:** **Implemented (2026-05-01)** — see decision log §9.
**Author:** Orchestrator (Copilot)
**Scope:** Heimdall.BLL, Heimdall.Web, Heimdall.BLL.Tests
**Decision required:** Should we migrate object-to-object mapping from AutoMapper to Mapster, and should we adopt FastExpressionCompiler (FEC) alongside?

> This document is research and planning only. **No code, package, or DI changes are made in this PR.** A separate, follow-up PR will implement the migration if approved.

---

## 1. Why we're looking at this

Two forces motivate the question:

1. **AutoMapper licensing trajectory.** The author of AutoMapper (Jimmy Bogard) has publicly announced that AutoMapper is moving toward a commercial / dual-license model in future major versions. We currently use AutoMapper `16.1.1` (still MIT) but should plan a migration path before we are pinned to a license we don't want, or stuck on an old MIT version that stops receiving fixes.
2. **Performance and AOT-readiness.** Mapster generates strongly-typed mapping code (either at runtime via expression trees or at build time via a Roslyn source generator). Combined with FastExpressionCompiler, runtime mapping cost trends toward hand-written code — and the source-generator path is fully AOT-friendly, which AutoMapper is not.

## 2. Current AutoMapper surface in this repo

The mapping surface is **deliberately tiny** today. A full inventory:

| Concern              | Location                                                                           | Notes                                                                                                                                     |
| -------------------- | ---------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| Profile              | `src/Heimdall.BLL/Mapping/TicketProfile.cs`                                        | Two maps: `Ticket ↔ TicketDto`. `DateCreated` and `DateUpdated` are explicitly **ignored** on `TicketDto → Ticket` to avoid clobbering DB timestamps. |
| Consumer             | `src/Heimdall.BLL/Services/TicketService.cs`                                       | Injects `IMapper`; calls `Map<Ticket>`, `Map<TicketDto>`, and `Map<IReadOnlyList<TicketDto>>` (collection mapping).                       |
| DI registration      | `src/Heimdall.Web/DependencyInjection/ApplicationModule.cs`                        | Uses `AutoMapper.Contrib.Autofac.DependencyInjection` (`builder.RegisterAutoMapper(typeof(TicketProfile).Assembly)`).                     |
| Tests                | `tests/Heimdall.BLL.Tests/Mapping/TicketProfileTests.cs`                           | Builds a `MapperConfiguration` directly, calls `AssertConfigurationIsValid`, asserts both directions including the ignored-fields contract. |
| Packages             | `Heimdall.BLL.csproj`, `Heimdall.Web.csproj`                                       | `AutoMapper 16.1.1`, `AutoMapper.Contrib.Autofac.DependencyInjection 11.0.0`.                                                             |

**Implication:** the migration is small and contained. No flattening conventions, no value resolvers, no `ProjectTo`, no profile inheritance, no `IMappingAction`, no custom type converters — just two `CreateMap` calls and two ignored members.

## 3. Tooling overview

### 3.1 Mapster (`MapsterMapper/Mapster`)

- **License:** MIT.
- **Two flavors:**
  - **Runtime mode:** `Mapster` + `Mapster.DependencyInjection`. Configuration via `TypeAdapterConfig` and fluent `.NewConfig<TSource, TDest>()`. Generates IL via expression trees, cached per type-pair.
  - **Source-generator mode:** `Mapster` + `Mapster.Tool` + `[Mapper]` / `[AdaptTo]` attributes. Emits plain C# mapping methods at compile time — fully AOT-compatible, debuggable, and zero startup cost.
- **API ergonomics:**
  - Usage looks like `dto.Adapt<Ticket>()` or `mapper.Map<TicketDto>(entity)` (when using `IMapper` from `Mapster.DependencyInjection`).
  - Equivalent of AutoMapper's `ForMember(..., opt => opt.Ignore())` is `.Ignore(dest => dest.DateCreated)`.
  - Equivalent of `AssertConfigurationIsValid()` is `config.Compile()` and/or `config.Apply(...).EnableDebugging()` plus per-pair `Should().NotThrow()` tests.
- **Maturity:** Active (regular releases through 2024–2025), used widely in .NET ecosystems.

### 3.2 FastExpressionCompiler (`dadhi/FastExpressionCompiler`)

- **License:** MIT.
- **What it does:** Provides `expression.CompileFast()` as a drop-in replacement for `Expression.Compile()`. Per the project's own benchmarks, **compilation is 10–40× faster** than the BCL compiler, **delegate invocation is comparable or marginally faster**, and **allocations during compilation are roughly 3× lower**.
- **How Mapster uses it:** Mapster has first-class support for FEC. When FEC is on the same load context, Mapster's runtime mode will use it for compiling the cached mapping delegates. This primarily reduces **first-call / startup cost** for each new type-pair, not steady-state throughput.
- **Caveats:**
  - FEC has historically had occasional regressions on exotic expression shapes (nullables-in-closures, ref-locals, certain `switch` lowering). In practice, mapping expressions are simple enough that this rarely bites, but we should have a fallback path (`useInterpretation: true` or stock `Compile()`).
  - On AOT (NativeAOT, iOS), runtime expression compilation is not available at all — FEC doesn't help there. This is exactly the case where Mapster's **source-generator** path is the right answer; FEC becomes irrelevant.

## 4. Benefits analysis

### Benefits of moving to Mapster

| Benefit                                                                                                | Magnitude for this repo                                                                                       |
| ------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------- |
| Stays MIT; no future license risk                                                                      | **High** — primary motivator.                                                                                 |
| Source-generator path → AOT-ready, zero startup cost, no expression trees in production                | **Medium** — we don't ship NativeAOT today, but it removes a future blocker and trims trim-warnings.          |
| Runtime mode is faster than AutoMapper in published benchmarks (typically 2–4× on simple POCO maps)    | **Low in absolute terms** — we map ~tens of objects per request at most; not a current hot path.              |
| Smaller dependency surface (no Autofac contrib package needed if we use stock `Mapster.DependencyInjection` adapted into Autofac) | **Low** — saves one package.                                                                                  |
| `IRegister` profile pattern is a near-1:1 swap for AutoMapper `Profile` (mapping authoring stays familiar) | **Medium** — keeps the diff small and reviewable.                                                             |

### Benefits of adding FastExpressionCompiler

| Benefit                                                                                            | Magnitude for this repo                                                                                       |
| -------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| 10–40× faster expression compilation → faster cold start of the mapping subsystem                 | **Low** — we have **two** maps; the cold-start delta is sub-millisecond either way.                           |
| Lower allocations during compilation                                                               | **Low** — same reason; absolute allocation savings are tiny at our scale.                                     |
| Often a couple ns faster per invocation                                                            | **Negligible** at our request volume.                                                                         |

### Honest assessment

For a repository with **two mappings** and **no measured mapping hotspot**, the case for FEC is weak. The case for Mapster rests almost entirely on **license** and **AOT-readiness**, with performance as a nice-to-have rather than a justification.

## 5. Recommendation

> **Adopt Mapster (source-generator mode preferred) on a deferred timeline tied to license/AOT triggers.**
> **Do not adopt FastExpressionCompiler.**

Rationale:

1. **Mapster — yes, but not urgent.** AutoMapper `16.1.1` is still MIT and works. We should migrate **before** upgrading to any AutoMapper version that changes the license, or as part of a "go AOT" initiative — whichever comes first. The migration is small enough (~2 maps, ~3 files, ~1 test class) that it can be a single afternoon's work.
2. **Source generator over runtime mode.** Given how few maps we have, the source-generator path is strictly better: AOT-clean, no startup cost, no DI gymnastics, and the generated code is auditable in `obj/`.
3. **Skip FEC.** It only matters for runtime-mode mappers compiling many delegates at startup. With the source generator we don't compile expressions at all; with runtime mode we compile two delegates. Adding a third dependency for a sub-millisecond startup win that no user will perceive is not justified. We can revisit if the mapping surface grows by an order of magnitude.

## 6. Transition plan (if approved)

This is a **future PR**, not part of this proposal. Listed for completeness so reviewers can see the shape.

### Phase 0 — Decision & spike

- [ ] Approve this proposal (or amend it).
- [ ] Pick **source-generator** vs **runtime** mode. (Recommended: source generator.)

### Phase 1 — Land Mapster behind a parity test

- [ ] Add `Mapster` (and `Mapster.SourceGenerator` if SG path) to `Heimdall.BLL.csproj`. Renovate will own version pinning per repo policy.
- [ ] Replace `TicketProfile : Profile` with a Mapster equivalent:
  - **SG mode:** an interface annotated with `[Mapper]` declaring `TicketDto Map(Ticket)` and `Ticket Map(TicketDto)`, with `[Map(nameof(Ticket.DateCreated), "Ignore")]` style attributes on the reverse map for `DateCreated` / `DateUpdated`.
  - **Runtime mode:** an `IRegister` class calling `config.NewConfig<Ticket, TicketDto>();` and `config.NewConfig<TicketDto, Ticket>().Ignore(d => d.DateCreated).Ignore(d => d.DateUpdated);`.
- [ ] Update `TicketService` to depend on `MapsterMapper.IMapper` (runtime) or the generated mapper interface (SG). Keep the constructor shape so the rest of the service is untouched.
- [ ] Replace `RegisterAutoMapper(...)` in `ApplicationModule` with either:
  - SG: `builder.RegisterType<TicketMapper>().As<ITicketMapper>().SingleInstance();`
  - Runtime: register a `TypeAdapterConfig` singleton + `MapsterMapper.ServiceMapper` (or `Mapper`) as `IMapper`.
- [ ] Port `TicketProfileTests` to the new mapper. Keep all four existing assertions: round-trip equivalence, ignored `DateCreated` / `DateUpdated`, and a configuration-validity check (`config.Compile()` for runtime; the SG simply fails the build if a property has no resolution).
- [ ] Run full test suite. Verify the cache, repository, and pagination paths in `TicketService` are byte-identical in observable behavior.

### Phase 2 — Remove AutoMapper

- [ ] Remove `AutoMapper` and `AutoMapper.Contrib.Autofac.DependencyInjection` from `Heimdall.BLL.csproj` and `Heimdall.Web.csproj`.
- [ ] Remove `using AutoMapper;` from `TicketService`, `ApplicationModule`, and `TicketProfileTests`.
- [ ] Update `README.md` "Tech stack" / mapping mentions.

### Phase 3 — (Optional) Benchmarks

- [ ] Add a `tests/Heimdall.Benchmarks` project (BenchmarkDotNet, NOT run in CI by default).
- [ ] Benchmarks to cover (see §7).
- [ ] Document numbers in this proposal as an appendix; do **not** gate the migration on a performance threshold, since the migration is justified primarily on licensing/AOT.

### Rollback

- The migration is contained to one profile, one DI module, one service constructor, and one test file. Reverting is `git revert <merge-sha>`.

## 7. Benchmark approach (if/when we want numbers)

A small, throwaway BenchmarkDotNet project — kept out of the CI graph — that compares the **same** mapping work under three configurations:

1. **AutoMapper 16.x** (baseline)
2. **Mapster runtime mode** (with and without FEC, two columns)
3. **Mapster source-generator mode**

### Suggested benchmarks

| Benchmark              | What it measures                                                            | Why it matters                                                       |
| ---------------------- | --------------------------------------------------------------------------- | -------------------------------------------------------------------- |
| `MapEntityToDto`       | Single `Ticket → TicketDto` after warmup                                    | Steady-state hot path equivalent to `GetByIdAsync`.                  |
| `MapDtoToEntity`       | Single `TicketDto → Ticket` (with the two-property ignore)                  | Steady-state write path (`CreateAsync` / `UpdateAsync`).             |
| `MapList_100`          | `IReadOnlyList<Ticket>` of 100 → `IReadOnlyList<TicketDto>`                 | Steady-state list path (`GetAllAsync`, `GetPagedAsync`).             |
| `ColdStart_FirstMap`   | Time from container build to first successful map                           | The only place FEC could plausibly help us.                          |
| `Allocations`          | Bytes allocated per op (BenchmarkDotNet `[MemoryDiagnoser]`)                | GC pressure under load.                                              |

### Methodology

- BenchmarkDotNet `[MemoryDiagnoser]`, `[SimpleJob(RuntimeMoniker.Net80)]`, default warmup/iteration counts.
- Run on a developer machine (not in CI) and paste the results table into this doc as an appendix.
- We are looking for **order-of-magnitude differences**, not single-digit-percent wins. If the numbers are within 2× of each other (which they will be, except for cold start), the decision stays grounded in licensing/AOT, not performance.

### What we are **not** going to do

- We are **not** adding BenchmarkDotNet to the default CI workflow.
- We are **not** treating the benchmark as an acceptance gate. It's evidence, not a contract.
- We are **not** benchmarking AutoMapper's `ProjectTo` — we don't use it.

## 8. Risks and mitigations

| Risk                                                                                                | Mitigation                                                                                                                                              |
| --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Mapster's default conventions silently start populating `DateCreated` / `DateUpdated` from the DTO  | Keep the existing `TicketProfileTests` assertions — they are the contract. The SG path will refuse to compile a member it can't account for; the runtime path will be caught by the existing test. |
| `MapsterMapper.IMapper` and `AutoMapper.IMapper` share a name; accidental cross-using in the diff   | Use fully-qualified `MapsterMapper.IMapper` in `TicketService`'s field type during the swap PR, then re-introduce the using once AutoMapper is gone.    |
| Autofac registration semantics differ (`InstancePerLifetimeScope` vs `SingleInstance`)              | Mapster's mapper is thread-safe and is intended to be a singleton. Register `As<IMapper>().SingleInstance()`. The `TypeAdapterConfig` is also singleton. |
| FEC instability on a future expression shape                                                        | We're recommending **not** adopting FEC, so this risk is avoided entirely.                                                                              |
| Misreading AutoMapper's license trajectory and migrating prematurely                                | Migration is small and reversible; even an "early" migration costs an afternoon and unlocks AOT-readiness, so the downside is bounded.                  |

## 9. Decision log

- **2026-05-01 — Proposal drafted.** Recommends Mapster (source-generator mode) on a deferred timeline; recommends **against** FastExpressionCompiler. Awaiting maintainer review.
- **2026-05-01 — Proposal approved; migration shipped.** Maintainer accepted the recommendation. Implementation followed the source-generator path (`Mapster.Tool`, not a Roslyn `ISourceGenerator` package — no such package exists for Mapster). FastExpressionCompiler was **not** adopted, as recommended. Implementation summary:
  - Replaced `TicketProfile : Profile` with the `[Mapper]` interface `ITicketMapper` plus an `IRegister` (`TicketMappingRegister`) that carries the `DateCreated`/`DateUpdated` ignore semantics.
  - Generated `src/Heimdall.BLL/Mappers/TicketMapper.cs` via `dotnet dotnet-mapster mapper` (`Mapster.Tool` 10.x, registered in `.config/dotnet-tools.json`); the generated file is committed and lightly hand-tuned to throw on null inputs and to use `List<T>` + `foreach` in the list mapper (see the header of `TicketMapper.cs`).
  - Removed `AutoMapper` and `AutoMapper.Contrib.Autofac.DependencyInjection` from both `Heimdall.BLL.csproj` and `Heimdall.Web.csproj`. Added a single `Mapster` (10.x) reference to `Heimdall.BLL.csproj`.
  - Autofac `ApplicationModule` registers `TicketMapper` as `ITicketMapper` (`SingleInstance`); `TicketService` now injects `ITicketMapper` directly (no `MapsterMapper.IMapper` indirection — strongly typed, AOT-clean, no expression compilation at runtime).
  - Tests ported: `TicketProfileTests` → `TicketMapperTests` (exercises the generated mapper directly), and `TicketServiceTests` was updated to construct `new TicketMapper()` instead of an AutoMapper `MapperConfiguration`.
  - README "Tech stack" + a new "Regenerating Mapster mappers" section document the workflow.

## 10. References

- AutoMapper repository — https://github.com/AutoMapper/AutoMapper
- Mapster repository — https://github.com/MapsterMapper/Mapster
- Mapster wiki — https://github.com/MapsterMapper/Mapster/wiki
- Mapster source generator — https://github.com/MapsterMapper/Mapster/wiki/Mapster-code-generator
- FastExpressionCompiler — https://github.com/dadhi/FastExpressionCompiler
- BenchmarkDotNet — https://github.com/dotnet/BenchmarkDotNet
