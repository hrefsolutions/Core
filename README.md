# Href.Core

Core REST API for Href Solutions.

**Stack:** .NET 10 Web API (Controllers) · NHibernate (`Mapping.ByCode`) · PostgreSQL 16 (Npgsql) · Serilog · xUnit. Deployed to Railway with the root `Dockerfile`.

## Run locally

Requirements: .NET 10 SDK (pinned by `global.json`) and Docker.

1. Start PostgreSQL and Mailpit:

   ```bash
   docker compose up -d
   ```

2. Run the API (Development profile, `http://localhost:8080`) from Visual Studio (`http` profile) or:

   ```bash
   dotnet run --project src/Href.Core.Api
   ```

3. Check it:

   ```bash
   curl http://localhost:8080/health
   ```

Mailpit UI: <http://localhost:8025> (SMTP on `localhost:1025`).

### Email

The API sends email through SMTP (`IEmailSender`, MailKit) and does not start without a valid `Smtp` configuration (see `SYSTEM_MAP.md`).

- **Local:** `appsettings.Development.json` points to Mailpit (`localhost:1025`, no TLS, no credentials). Every email the API sends shows up in <http://localhost:8025>. The `api` container of `docker-compose.yml` also uses the `mailpit` service.
- **Production (Railway):** SMTP requires a Railway Pro plan or higher (Free, Trial and Hobby block it); redeploy after upgrading. `appsettings.json` already has `smtp.gmail.com`, port `465`, `SslOnConnect` and the `href.notifier@gmail.com` sender. Manual steps:
  1. On the `href.notifier@gmail.com` Google account turn on 2-Step Verification and create an App Password (never use the account password).
  2. In Railway set `Smtp__Username=href.notifier@gmail.com` and `Smtp__Password=<App Password>`. The password lives only in Railway: never commit it or put it in `.env.example`.
  3. Alternative for port `587`: `Smtp__Port=587` and `Smtp__SecureSocket=StartTls`.
- Gmail allows about 500 recipients per day.
- **Delivery is asynchronous:** the API only queues notifications in the `notifications` table, and a background worker sends them (every 5 seconds by default, up to 3 attempts). Locally, with the API and Mailpit running, a queued notification shows up in the Mailpit UI (<http://localhost:8025>) within a few seconds. Set `Notifications__Worker__Enabled=false` to stop delivery. Details in `SYSTEM_MAP.md` (`## Background processing`).

### Keep the API running while you code

`dotnet watch` keeps the API up on `http://localhost:8080` and applies every saved change (Hot Reload), so there is no need to press F5 after each edit:

```bash
dotnet watch --project src/Href.Core.Api
```

- Edits that Hot Reload cannot apply (for example in `Program.cs`) restart the app; `Ctrl+R` forces a restart.
- While it runs, Windows locks the API assembly: a build or test run from Visual Studio can fail with "file in use". Stop it with `Ctrl+C` before building or running tests.
- Visual Studio alternative: start with `Ctrl+F5` (without debugging) and enable *Hot Reload on File Save*.

## Build and test

```bash
dotnet build Href.Core.sln
```

Integration tests start a real PostgreSQL 16 container with Testcontainers, so Docker Desktop must be running:

```bash
dotnet test Href.Core.sln
```

To run only unit tests (without Docker):

```bash
dotnet test Href.Core.sln --filter Category!=Integration
```

## Run the container

Optional, only to check the production image before deploying. Day-to-day development does not need it.

The whole stack (PostgreSQL, Mailpit and the API image built locally from the `Dockerfile`), already configured, on `http://localhost:8080`:

```bash
docker compose --profile container up -d --build
```

Stop the API started from Visual Studio first: both use port 8080. When done:

```bash
docker compose --profile container down
```

To run only the image, pass the variables listed in `.env.example`:

```bash
docker run --rm -p 8080:8080 --env-file .env.example href-core
```

## Configuration

`.env.example` lists the variables the container needs (the API itself does not read `.env`; `dotnet run` uses `appsettings.Development.json`). Details in `SYSTEM_MAP.md`. On Railway set `ConnectionStrings__DefaultConnection` (Npgsql format, for example `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}`) and use `/health` as the health check path. Railway sets `PORT`. Step-by-step Railway setup, with every environment variable: [`docs/instructions.md`](docs/instructions.md).

## Contributing

Branching, commits, PRs and releases: see `CLAUDE.md` (it also describes the CI checks). Changes: `CHANGELOG.md`. Protection of `main` (production) and `develop`: [`docs/branch-protection.md`](docs/branch-protection.md).
