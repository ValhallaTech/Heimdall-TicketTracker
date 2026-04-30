# Heimdall-TicketTracker Test Suites

This directory and its sibling locations contain every test type used by the project. Each suite has its own runner and conventions; consult the per-suite README for details. CI runs all suites on pull requests targeting `main`.

| Suite      | Location                          | Framework         | Purpose                                               |
| ---------- | --------------------------------- | ----------------- | ----------------------------------------------------- |
| Unit (BLL) | `tests/Heimdall.BLL.Tests/`       | xUnit             | Business-logic layer tests                            |
| Unit (DAL) | `tests/Heimdall.DAL.Tests/`       | xUnit             | Data-access layer tests (Dapper, repositories)        |
| Unit (Core)| `tests/Heimdall.Core.Tests/`      | xUnit             | Domain/core primitives                                |
| Component  | `tests/Heimdall.Web.Tests/`       | xUnit + bUnit     | Blazor component tests for the web project           |
| Frontend   | `src/Heimdall.Web/__tests__/`     | Jest              | JavaScript / TypeScript tests for web assets          |
| Database   | `tests/pgtap/`                    | pgTAP + pg_prove  | Postgres schema, function, trigger, and RLS tests     |

Run .NET suites with `dotnet test Heimdall.slnx --settings coverlet.runsettings` from the repo root. Run the Jest suite with `yarn test:coverage` from `src/Heimdall.Web/` (Yarn 4 via Corepack). Run the pgTAP suite with `bash tests/pgtap/run-tests.sh` (see `tests/pgtap/README.md` for Docker-based local setup).

For the full testing guide — prerequisites, per-suite conventions, code templates, coverage reporting, and CI integration — see [`../docs/testing.md`](../docs/testing.md). Contribution standards are documented in [`../CONTRIBUTING.md`](../CONTRIBUTING.md).
