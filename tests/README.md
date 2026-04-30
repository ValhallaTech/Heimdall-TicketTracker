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

Run .NET suites with `dotnet test` from the repo root. Run the Jest suite with `npm test` from `src/Heimdall.Web/`. Run the pgTAP suite with `./tests/pgtap/run-tests.sh` (see `tests/pgtap/README.md` for Docker-based local setup).
