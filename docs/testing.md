# Testing Guide

Heimdall TicketTracker is exercised by six test projects grouped into three CI jobs that together cover the .NET back end, the Blazor UI, the front-end JavaScript/TypeScript, and the PostgreSQL schema. CI runs every job on each push and pull request and fails the build if any suite drops below the **80% line and branch** coverage gate.

## Overview

| Suite                  | Layer covered                                        | Framework               | Coverage format         |
| ---------------------- | ---------------------------------------------------- | ----------------------- | ----------------------- |
| `Heimdall.Core.Tests`  | Domain primitives, DTOs, pagination                  | xUnit                   | Cobertura (coverlet)    |
| `Heimdall.BLL.Tests`   | Service layer / business rules                       | xUnit + Moq             | Cobertura (coverlet)    |
| `Heimdall.DAL.Tests`   | Dapper repositories, migrations, Redis cache         | xUnit + Testcontainers  | Cobertura (coverlet)    |
| `Heimdall.Web.Tests`   | Blazor components                                    | xUnit + bUnit           | Cobertura (coverlet)    |
| `Heimdall.Web` (Jest)  | Front-end TypeScript / JavaScript                    | Jest (Yarn 4)           | lcov + text-summary     |
| `tests/pgtap`          | PostgreSQL schema, functions, triggers, RLS          | pgTAP + `pg_prove`      | TAP                     |

Coverage reports land under:

- **.NET**: `TestResults/**/coverage.cobertura.xml` (one per test project)
- **Jest**: `src/Heimdall.Web/coverage/` (`lcov.info`, `lcov-report/index.html`)
- **CI**: uploaded as workflow artifacts (`dotnet-coverage`, `jest-coverage`)

## Project layout

```text
tests/
├── Directory.Build.props          # shared test-project settings
├── README.md                      # suite index
├── Heimdall.Core.Tests/           # xUnit
├── Heimdall.BLL.Tests/            # xUnit + Moq
├── Heimdall.DAL.Tests/            # xUnit + Testcontainers.PostgreSql
├── Heimdall.Web.Tests/            # xUnit + bUnit
└── pgtap/
    ├── 00_sanity.sql
    ├── docker-compose.pgtap.yml   # builds postgres:18.3 + pgtap, host port 55432
    └── run-tests.sh

src/Heimdall.Web/
├── __tests__/                     # Jest test sources
├── scripts/util.ts                # trivial coverage target
├── jest.config.cjs
├── eslint.config.cjs              # ESLint flat config
├── .prettierrc.json
└── tsconfig.json
```

## Running tests locally

### Prerequisites

- **.NET 10 SDK** (the solution targets net10.0)
- **Node.js 24** with **Corepack** enabled (`corepack enable`) — Yarn 4 is activated automatically from `package.json`
- **Docker** (required by `Testcontainers.PostgreSql` in `Heimdall.DAL.Tests` and by the pgTAP Compose stack)

### .NET (xUnit + bUnit)

From the repository root:

```bash
dotnet test Heimdall.slnx --settings coverlet.runsettings
```

The `coverlet.runsettings` file emits Cobertura, excludes `[xunit*]*`, `[*.Tests]*`, and `[Heimdall.*]*Migrations*`, and is the canonical way to run the .NET suites locally so the output matches CI.

To target a single project:

```bash
dotnet test tests/Heimdall.BLL.Tests --settings ../../coverlet.runsettings
```

### Jest (Heimdall.Web)

From `src/Heimdall.Web/`:

```bash
corepack enable          # one-time, enables Yarn 4
yarn install
yarn test                # run tests
yarn test:coverage       # run tests with coverage (matches CI)
yarn lint                # ESLint
yarn format:check        # Prettier
```

The Jest config enforces a **≥80% global** threshold for statements, branches, functions, and lines.

### pgTAP

Bring up the disposable Postgres container, then run the suite. The Compose stack uses host port **55432** to avoid colliding with a local Postgres on 5432.

```bash
docker compose -f tests/pgtap/docker-compose.pgtap.yml up -d

export PGHOST=localhost
export PGPORT=55432
export PGUSER=postgres
export PGPASSWORD=postgres
export PGDATABASE=heimdall_test

bash tests/pgtap/run-tests.sh

docker compose -f tests/pgtap/docker-compose.pgtap.yml down
```

See [`tests/pgtap/README.md`](../tests/pgtap/README.md) for the canonical reference, including how `pg_prove` is invoked and how to extend the suite.

## Writing new tests

### xUnit (Core, BLL, DAL)

Use the **Arrange / Act / Assert** pattern with [FluentAssertions](https://fluentassertions.com/) and Moq:

```csharp
public class TicketServiceTests
{
    [Fact]
    public void GetById_WhenTicketExists_ReturnsTicket()
    {
        // Arrange
        var repo = new Mock<ITicketRepository>();
        repo.Setup(r => r.GetById(1)).Returns(new Ticket { Id = 1 });
        var sut = new TicketService(repo.Object);

        // Act
        var result = sut.GetById(1);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(1);
    }
}
```

`Heimdall.DAL.Tests` uses `Testcontainers.PostgreSql` to spin up a real Postgres instance per test class; Docker must be running locally.

### bUnit (Web)

bUnit's modern API uses **`BunitContext`** (the older `TestContext` type is obsolete and should not be used in new tests):

```csharp
public class CounterTests : BunitContext
{
    [Fact]
    public void Counter_IncrementsOnClick()
    {
        var cut = Render<Counter>();
        cut.Find("button").Click();
        cut.Find("p").TextContent.Should().Contain("1");
    }
}
```

### Jest (TypeScript)

Tests live under `src/Heimdall.Web/__tests__/` and follow the AAA pattern. Use `jest.Mocked<T>` for typed mocks:

```ts
import { add } from "../scripts/util";

describe("add", () => {
    it("returns the sum of two numbers", () => {
        // Arrange
        const a = 2;
        const b = 3;

        // Act
        const result = add(a, b);

        // Assert
        expect(result).toBe(5);
    });
});
```

Run `yarn lint:fix` and `yarn format` before committing.

### pgTAP

Each test file is a SQL script wrapped in a transaction so it leaves no residue in the database:

```sql
BEGIN;
SELECT plan(1);

SELECT has_table('public', 'tickets', 'tickets table exists');

SELECT * FROM finish();
ROLLBACK;
```

Add new files alongside `00_sanity.sql`; `run-tests.sh` discovers them by glob.

## Coverage reporting

- **Local .NET**: open the latest `TestResults/<guid>/coverage.cobertura.xml`, or generate an HTML view with [`reportgenerator`](https://github.com/danielpalme/ReportGenerator).
- **Local Jest**: open `src/Heimdall.Web/coverage/lcov-report/index.html` after `yarn test:coverage`.
- **CI**: the [`irongut/CodeCoverageSummary`](https://github.com/irongut/CodeCoverageSummary) action enforces `80 80` (line/branch) on the .NET suite and posts a markdown summary; the Jest job enforces the same threshold via Jest's `coverageThreshold`. Coverage XML/lcov is uploaded as workflow artifacts on every run.

## CI integration

The workflow at [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) defines four jobs:

| Job       | Purpose                                                      |
| --------- | ------------------------------------------------------------ |
| `dotnet`  | Restores, builds, and runs all xUnit + bUnit projects with coverlet; enforces the 80/80 gate. |
| `jest`    | Installs Yarn 4 via Corepack, runs `yarn test:coverage`, enforces the Jest threshold. |
| `pgtap`   | Starts the pgTAP Compose stack and runs `tests/pgtap/run-tests.sh`. |
| `summary` | Aggregates the other three; required check for merge.         |

## Troubleshooting

- **`yarn: command not found`** — run `corepack enable` once. Yarn 4 is activated by `packageManager` in `package.json`; do not install Yarn globally.
- **bUnit deprecation warnings** — make sure new tests inherit from **`BunitContext`**, not the obsolete `TestContext`.
- **`pg_prove: command not found`** — install via [TAP::Parser::SourceHandler::pgTAP](https://pgtap.org/documentation.html), e.g. `cpan TAP::Parser::SourceHandler::pgTAP`. The CI job and the Compose stack both ship it preinstalled.
- **Testcontainers errors in `Heimdall.DAL.Tests`** — Docker must be running and the current user must be in the `docker` group (or use Docker Desktop). On Linux, verify with `docker info`.
- **Port 55432 already in use** — another local Postgres is bound to that port; either stop it or override `PGPORT` and the Compose `ports` mapping.
- **Coverage gate failing locally but not in CI (or vice versa)** — make sure you ran with `--settings coverlet.runsettings` for .NET, or `yarn test:coverage` (not `yarn test`) for Jest.
