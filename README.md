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
cp .env.example .env
```

```bash
docker run --rm -p 8080:8080 --env-file .env href-core
```

## Configuration

`.env.example` lists the variables the container needs (the API itself does not read `.env`; `dotnet run` uses `appsettings.Development.json`). Details in `SYSTEM_MAP.md`. On Railway set `ConnectionStrings__DefaultConnection` (Npgsql format, for example `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}`) and use `/health` as the health check path. Railway sets `PORT`.

## Contributing

Branching, commits, PRs and releases: see `CLAUDE.md`. Changes: `CHANGELOG.md`.
