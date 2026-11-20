# Railway deployment instructions

How to configure the Href.Core service on Railway. Railway builds the root `Dockerfile` and passes the service variables to the container as environment variables.

ASP.NET Core reads environment variables as configuration keys: a double underscore (`__`) separates sections, so `Smtp__Password` is the key `Smtp:Password` of `appsettings.json`. A variable set in Railway overrides the value in `appsettings.json`.

> [!WARNING]
> Keep the Railway service **private** (no public domain) until admin authentication ships (#53, v0.4.0). Today `/api/apps`, `/api/users`, `/api/apps/{appKey}/permissions`, `/roles`, `/users` and `/api/notifications/templates` have no authentication: anyone who reaches the API can create or lock users, change any application's roles and rewrite email templates.

## Variables to set

These must exist in Railway before the first deploy.

| Variable | Value | Notes |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | Npgsql connection string to the Railway PostgreSQL service | **Secret.** The API does not start without it. Use the Npgsql key-value format, not the `postgresql://` URL that Railway shows as `DATABASE_URL`. With Railway reference variables (replace `Postgres` with the name of the database service): `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}` |
| `Smtp__Username` | `href.notifier@gmail.com` | Gmail account that sends the emails. Set together with `Smtp__Password`. |
| `Smtp__Password` | Gmail App Password (16 characters) | **Secret.** An App Password of `href.notifier@gmail.com`, never the account password. Store it only in Railway. Without credentials the API starts, but every email fails and a startup warning `SMTP credentials are not configured` is logged. |

Outbound SMTP needs the Railway Pro plan or higher. After an upgrade, redeploy the service.

## Variables not to set

| Variable | Why |
|---|---|
| `PORT` | Railway sets it. The `Dockerfile` entrypoint copies it to `ASPNETCORE_HTTP_PORTS` (default `8080`). |
| `ASPNETCORE_HTTP_PORTS` | Derived from `PORT` by the entrypoint; a Railway value is overwritten. |
| `ASPNETCORE_ENVIRONMENT` | The `Dockerfile` sets `Production`. Do not use `Development` in Railway: it loads `appsettings.Development.json` (Mailpit on `localhost:1025`) and creates the database if it is missing. |

## Optional variables

The defaults come from the code or from `appsettings.json` and work on Railway. Set one only to change its value.

| Variable | Default | Notes |
|---|---|---|
| `Database__MigrateOnStartup` | `true` | DbUp applies the pending SQL migrations (`Database/Migrations/*.sql`) on startup, each in its own transaction and once per database (journal table `schemaversions`). If a migration fails, the API does not start. `false` starts the API without migrating. |
| `Smtp__Host` | `smtp.gmail.com` | Required, not blank. |
| `Smtp__Port` | `465` | 1..65535. Gmail: `465` with `SslOnConnect`, or `587` with `StartTls`. |
| `Smtp__SecureSocket` | `SslOnConnect` | Exact names only: `None`, `StartTls`, `SslOnConnect` (case-sensitive). `None` is only valid without credentials. |
| `Smtp__FromEmail` | `href.notifier@gmail.com` | Sender address. One bare address, no display name. |
| `Smtp__FromName` | `Href` | Sender display name. |
| `Smtp__TimeoutSeconds` | `30` | Timeout per network operation (connect, read, write), 1..300. |
| `Notifications__Worker__Enabled` | `true` | `false` stops email delivery; the API still queues notifications. Only `true` or `false`. |
| `Notifications__Worker__PollIntervalSeconds` | `5` | Wait after a batch that was not full, 1..3600. |
| `Notifications__Worker__BatchSize` | `20` | Notifications claimed per transaction, 1..500. |
| `Notifications__Worker__StuckAfterMinutes` | `10` | Age after which a notification stuck in `Processing` is recovered, 1..1440. Keep `StuckAfterMinutes * 60` at or above `BatchSize * Smtp__TimeoutSeconds`; otherwise a startup warning is logged. |
| `Serilog__MinimumLevel__Default` | `Information` | Log level of the console output (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). `Debug` also logs the recipient of every sent email. |

The API does not start with an invalid `Smtp` or `Notifications:Worker` configuration (values out of range, unknown `SecureSocket`, only one of `Smtp__Username` / `Smtp__Password`, credentials with `None`). The deploy logs show which value failed.

## First deploy checklist

1. Create the PostgreSQL service and the Href.Core service (from this repository, root `Dockerfile`), with no public domain. Set the health check path of the Href.Core service to `/health`.
2. Set `ConnectionStrings__DefaultConnection`, `Smtp__Username` and `Smtp__Password`.
3. Deploy. In the logs, check that:
   - DbUp applied every migration (`0000_baseline.sql` up to the last `NNNN_*.sql`);
   - the warning `SMTP credentials are not configured` does not appear.
4. From inside the Railway private network (or a temporary private connection), call `GET /health`: it returns `200` with `Healthy`.
5. Create an application (`POST /api/apps`), queue a test email with its credentials (`POST /api/notifications/email`) and check that it arrives.

Keep this file in sync with `## Environment variables` in `SYSTEM_MAP.md` when a configuration key is added or changed.
