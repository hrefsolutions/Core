# Href.Core

Core REST API for Href Solutions.

**Stack:** .NET 10 Web API (Controllers) · NHibernate (`Mapping.ByCode`) · PostgreSQL 16 (Npgsql) · Serilog · xUnit. Deployed to Railway with the root `Dockerfile`.

## Run locally

Requirements: .NET 10 SDK and Docker.

1. Start PostgreSQL and Mailpit:

   ```bash
   docker compose up -d
   ```

2. Run the API (Development profile, `http://localhost:8080`):

   ```bash
   dotnet run --project src/Href.Core.Api
   ```

3. Check it:

   ```bash
   curl http://localhost:8080/health
   ```

Mailpit UI: <http://localhost:8025> (SMTP on `localhost:1025`).

## Build and test

```bash
dotnet build Href.Core.sln
```

```bash
dotnet test Href.Core.sln
```

## Run the container

```bash
docker build -t href-core .
```

```bash
docker run --rm -p 8080:8080 -e ConnectionStrings__DefaultConnection="Host=host.docker.internal;Port=5432;Database=href_core;Username=href;Password=href" href-core
```

## Configuration

See `.env.example` and `SYSTEM_MAP.md`. On Railway set `ConnectionStrings__DefaultConnection` (Npgsql format, for example `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}`) and use `/health` as the health check path. Railway sets `PORT`.

## Contributing

Branching, commits, PRs and releases: see `CLAUDE.md`. Changes: `CHANGELOG.md`.
