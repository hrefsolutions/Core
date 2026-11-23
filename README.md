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

### Token signing keys (JWT)

Access tokens are signed with RS256. The public keys are published at `GET /.well-known/jwks.json` (with `GET /.well-known/openid-configuration`), so satellite apps validate tokens offline with `AddJwtBearer(options => options.Authority = "<Core URL>")`.

- **Local (`dotnet run`):** no key needed. In Development the API generates an ephemeral key on startup and logs the warning `No JWT signing key is configured`; tokens stop validating after each restart.
- **Production:** the API does not start without `Jwt__Issuer`, `Jwt__SigningKey__KeyId` and `Jwt__SigningKey__PrivateKeyPem`. Steps (generation and rotation) in [`docs/instructions.md`](docs/instructions.md#jwt-signing-keys).

Generate a key pair (RSA 3072, PKCS#8) with OpenSSL (Git Bash includes it):

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-private.pem
openssl pkey -in jwt-private.pem -pubout -out jwt-public.pem
```

Admin endpoints (`/api/apps`, `/api/users`, roles, permissions, accesses and email templates) require `Authorization: Bearer <access token>` for the reserved `core` application with the `core.admin` permission. The first admin and the `core` client secret are set with the `Bootstrap__*` variables: see [`docs/instructions.md`](docs/instructions.md#first-admin). To try them locally, start the API with `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` and `Bootstrap__CoreClientSecret`, then log in with `X-App-Key: core`.

Never commit the private key: `.gitignore` ignores `*.pem` and `*.key`, and the key belongs only in Railway.

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

The image runs as `Production`, so it needs a JWT signing key like Railway does. Generate a throwaway local key on one line (PEM accepts the key without line breaks) in the variable `JWT_SIGNING_PRIVATE_KEY_PEM`, which `docker-compose.yml` passes as `Jwt__SigningKey__PrivateKeyPem` (PowerShell 7):

```powershell
$env:JWT_SIGNING_PRIVATE_KEY_PEM = [System.Security.Cryptography.RSA]::Create(3072).ExportPkcs8PrivateKeyPem() -replace "`r?`n", ''
```

Bash alternative: `export JWT_SIGNING_PRIVATE_KEY_PEM="$(openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 | tr -d '\n')"`. Docker Compose also reads it from a `.env` file at the repository root (ignored by git). Without it the API container stops at startup with `Jwt:SigningKey:PrivateKeyPem and Jwt:SigningKey:KeyId are required outside Development`.

The whole stack (PostgreSQL, Mailpit and the API image built locally from the `Dockerfile`), already configured, on `http://localhost:8080`:

```bash
docker compose --profile container up -d --build
```

Stop the API started from Visual Studio first: both use port 8080. When done:

```bash
docker compose --profile container down
```

To run only the image, copy `.env.example` to `.env.local` (ignored by git), paste the one-line private key after `Jwt__SigningKey__PrivateKeyPem=` and pass the file:

```bash
docker run --rm -p 8080:8080 --env-file .env.local href-core
```

## Configuration

`.env.example` lists the variables the container needs (the API itself does not read `.env`; `dotnet run` uses `appsettings.Development.json`). Details in `SYSTEM_MAP.md`. On Railway set `ConnectionStrings__DefaultConnection` (Npgsql format, for example `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}`) and use `/health` as the health check path. Railway sets `PORT`. Step-by-step Railway setup, with every environment variable: [`docs/instructions.md`](docs/instructions.md).

## Contributing

Branching, commits, PRs and releases: see `CLAUDE.md` (it also describes the CI checks). Changes: `CHANGELOG.md`. Protection of `main` (production) and `develop`: [`docs/branch-protection.md`](docs/branch-protection.md).
