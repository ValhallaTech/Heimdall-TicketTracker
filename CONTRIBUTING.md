# Contributing to Heimdall TicketTracker

Thanks for your interest in contributing! This document describes how to set up the project locally, the conventions we follow, and what we expect from a pull request.

## Code of Conduct

A repository-level Code of Conduct is **TBD**. In the meantime, please follow the [Contributor Covenant v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/) and the standards set by the [ValhallaTech](https://github.com/ValhallaTech) organization.

## Development setup

You will need:

- **.NET 10 SDK**
- **Node.js 24** with **Corepack** enabled (`corepack enable`) — Yarn 4 is activated automatically by `packageManager` in `package.json`
- **Docker** (required for `docker-compose.yml`, `Testcontainers.PostgreSql`, and the pgTAP suite)

Common workflows:

```bash
# Restore + build the solution
dotnet restore Heimdall.slnx
dotnet build  Heimdall.slnx

# Build front-end assets
cd src/Heimdall.Web && yarn install && yarn build

# Bring up the full stack
docker compose up --build
```

See the [README](README.md) for full configuration and runtime details.

## Branching & commits

- Branch from `main`. Use a short, descriptive branch name (e.g. `feature/ticket-search`, `fix/cache-key-collision`).
- Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/). Common types in this repo:
  - `feat:` user-facing feature
  - `fix:` bug fix
  - `refactor:` internal change with no behavior shift
  - `test:` test-only change
  - `ci:` CI / workflow change
  - `docs:` documentation
  - `chore:` tooling, dependencies, or other non-code maintenance
- Renovate Bot owns dependency bumps — do not pin or hand-bump versions in PRs.

## Testing requirements

Every PR must pass all four test suites and clear the **80% line and branch** coverage gate enforced in CI:

- **xUnit** — `Heimdall.Core.Tests`, `Heimdall.BLL.Tests`, `Heimdall.DAL.Tests`
- **bUnit** — `Heimdall.Web.Tests`
- **Jest** — `src/Heimdall.Web/__tests__/`
- **pgTAP** — `tests/pgtap/`

For per-suite quick-start commands, conventions, and templates, see [`docs/testing.md`](docs/testing.md). The suite index is at [`tests/README.md`](tests/README.md).

Before pushing:

```bash
dotnet test Heimdall.slnx --settings coverlet.runsettings
cd src/Heimdall.Web && yarn test:coverage && yarn lint && yarn format:check
```

## Pull request process

1. Open the PR as a **draft** while you iterate.
2. Link the issue(s) the PR closes (`Closes #123`) in the description.
3. Keep PRs focused — one concern per PR. Squash-merge is preferred; the squash commit subject should follow Conventional Commits.
4. Mark the PR ready for review only when CI is green.
5. At least one approving review is required before merge.

## License

By contributing, you agree that your contributions will be licensed under the [BSD-3-Clause](LICENSE) license that covers this project.
