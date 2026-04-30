# Heimdall TicketTracker

![CI](https://github.com/ValhallaTech/Heimdall-TicketTracker/actions/workflows/ci.yml/badge.svg)

A small, modern ticket-tracking web application built on **.NET 10**, **Blazor (Interactive Server)**, **PostgreSQL**, and **Redis**. It is the watchful guardian of your work items.

## Stack

- **Runtime:** .NET 10 (Blazor Server / Razor Components)
- **DI:** Autofac (with `Autofac.Extensions.DependencyInjection`)
- **Mapping:** AutoMapper (with the Autofac integration package)
- **Data access:** Dapper + `Dapper.Extensions.PostgreSQL`
- **Migrations:** FluentMigrator
- **Database:** PostgreSQL
- **Cache:** Redis (StackExchange.Redis + Newtonsoft.Json)
- **Logging:** Serilog (console sink)
- **Frontend:** Bootstrap 5.3 + Font Awesome 7, vendored via [Yarn](https://yarnpkg.com) (`build-assets.mjs`)
- **Containerisation:** Multi-stage `Dockerfile`, `docker-compose.yml`, and `render.yaml` blueprint

## Repository layout

```
Heimdall-TicketTracker/
├── .config/                       # dotnet local tools (csharpier)
├── .github/                       # issue templates
├── src/
│   ├── Heimdall.Core/             # domain models, DTOs, interfaces, pagination
│   ├── Heimdall.DAL/              # Dapper repositories, FluentMigrator, Redis cache
│   ├── Heimdall.BLL/              # services + AutoMapper profile
│   └── Heimdall.Web/              # Blazor app (Bootstrap via Yarn)
├── Heimdall.slnx                  # solution
├── Dockerfile                     # multi-stage (assets -> sdk -> aspnet)
├── docker-compose.yml             # local dev stack (web + postgres + redis)
├── render.yaml                    # Render blueprint
├── renovate.json                  # Renovate Bot configuration
└── README.md
```

## Configuration

The web app reads two connection settings from environment variables (preferred) or
`ConnectionStrings:*` in `appsettings.json`:

| Variable        | Description                                                                  |
| --------------- | ---------------------------------------------------------------------------- |
| `DATABASE_URL`  | PostgreSQL connection. Supports the `postgres://user:pass@host:port/db` URL form (Render style); also accepts a raw Npgsql connection string. |
| `REDIS_URL`     | Redis connection. Supports `redis://`/`rediss://` URLs and raw `host:port[,options]` strings. |
| `PORT`          | Optional listening port (defaults to `8080`). Set automatically by Render.    |
| `SEED_DATABASE` | Set to `false` to skip startup seeding. Default seeds when the table is empty. |
| `SEED_COUNT`    | Number of synthetic tickets to seed. Default `50`.                            |

## Run locally with Docker Compose

```bash
docker compose up --build
```

Then browse to <http://localhost:8080>.

## Run the Web project from source

Prerequisites:

- .NET 10 SDK
- Node.js 24+ with [Corepack](https://nodejs.org/api/corepack.html) enabled (`corepack enable`)
- A running PostgreSQL and Redis (e.g. `docker compose up postgres redis`)

```bash
# Set connection strings (or export them for your shell)
export DATABASE_URL="postgres://heimdall:heimdall@localhost:5432/heimdall"
export REDIS_URL="redis://localhost:6379"

# Build front-end assets once (the .csproj also runs this on publish)
cd src/Heimdall.Web
yarn install
yarn build

# Run the app
cd ../..
dotnet run --project src/Heimdall.Web
```

The Web project auto-runs FluentMigrator migrations on startup and (by default) seeds 50 sample tickets when the `tickets` table is empty.

## Testing

Heimdall has four parallel test suites, all gated at **≥80% line and branch coverage** in CI. See [`docs/testing.md`](docs/testing.md) for the deep-dive guide and [`tests/README.md`](tests/README.md) for the suite index.

| Suite                | Tooling                          | Quick start |
| -------------------- | -------------------------------- | ----------- |
| .NET (xUnit + bUnit) | xUnit, Moq, FluentAssertions, bUnit, Testcontainers (DAL) | `dotnet test Heimdall.slnx --settings coverlet.runsettings` |
| Frontend (Jest)      | Jest, ESLint, Prettier (Yarn 4)  | `cd src/Heimdall.Web && yarn install && yarn test:coverage` |
| Database (pgTAP)     | pgTAP + `pg_prove` in Docker     | `docker compose -f tests/pgtap/docker-compose.pgtap.yml up -d` then `bash tests/pgtap/run-tests.sh` |

The pgTAP runner expects `PGHOST`, `PGPORT` (default `55432` for the Compose stack), `PGUSER`, `PGPASSWORD`, and `PGDATABASE` to be set. See [`tests/pgtap/README.md`](tests/pgtap/README.md) for full details.

## Deployment

The project includes a Render Blueprint (`render.yaml`) that provisions:

1. A **Docker** web service (Heimdall.Web)
2. A **managed PostgreSQL** instance
3. A **Redis (key-value)** instance

Connect the repo as a Blueprint in the Render dashboard and Render will read `render.yaml` to wire everything up.

## Dependency updates

[Renovate Bot](https://docs.renovatebot.com) is configured via `renovate.json` to keep NuGet, npm, GitHub Actions, and Docker base images up to date weekly. Renovate is the **single source of truth** for dependency versions — do not pin or recommend specific versions in code review.

## Contributing

PRs are welcome — please read [`CONTRIBUTING.md`](CONTRIBUTING.md) before opening one. File bugs and feature requests via [GitHub Issues](https://github.com/ValhallaTech/Heimdall-TicketTracker/issues).

## License

[BSD-3-Clause](LICENSE).
