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

| `Jwt__Issuer` | Public HTTPS base URL of the Core, for example `https://core.example.com` | Used as the token `iss` and as the base of `jwks_uri`. Absolute URL, no query or fragment; a trailing `/` is removed. `http` is rejected except for loopback hosts. |
| `Jwt__SigningKey__KeyId` | Key id, for example `core-2026-11` | Published as `kid`. 1 to 64 characters among `A-Z`, `a-z`, `0-9`, `.`, `_`, `-`. Use a new id on every rotation. |
| `Jwt__SigningKey__PrivateKeyPem` | RSA private key in PEM | **Secret.** See [JWT signing keys](#jwt-signing-keys). PKCS#8 (`BEGIN PRIVATE KEY`) or PKCS#1 (`BEGIN RSA PRIVATE KEY`), at least 2048 bits (3072 recommended), not encrypted. Paste it with its line breaks or on one line. The API does not start without it. |

Outbound SMTP needs the Railway Pro plan or higher. After an upgrade, redeploy the service.

## JWT signing keys

Access tokens are signed with RS256 using `Jwt__SigningKey__PrivateKeyPem`. The public keys are published at `GET /.well-known/jwks.json` (cached for 1 hour) and `GET /.well-known/openid-configuration` returns `issuer` and `jwks_uri`.

Generate the production key pair on a trusted machine (OpenSSL is included with Git Bash):

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-private.pem
openssl pkey -in jwt-private.pem -pubout -out jwt-public.pem
```

1. Paste the full content of `jwt-private.pem` in `Jwt__SigningKey__PrivateKeyPem` and set `Jwt__SigningKey__KeyId` (for example `core-2026-11`).
2. Keep `jwt-public.pem` for the next rotation. Delete `jwt-private.pem` from the machine once it is in Railway; never commit it.

Rotation (manual):

1. Generate a new pair with a new key id.
2. Move the current key to the previous keys: `Jwt__PreviousPublicKeys__0__KeyId` = the old key id and `Jwt__PreviousPublicKeys__0__PublicKeyPem` = the old public key (`BEGIN PUBLIC KEY`). Add `__1__`, `__2__`... for more. A private key there is rejected.
3. Set the new `Jwt__SigningKey__KeyId` and `Jwt__SigningKey__PrivateKeyPem` and redeploy. New tokens use the new key; tokens signed with the old one keep validating because its public key is still published.
4. Remove the previous key after the access token lifetime (`Jwt__AccessTokenMinutes`) plus the JWKS cache time (1 hour) of the satellite apps have passed.

If the private key leaks, rotate without step 2, so tokens signed with it stop validating at once (satellite apps may keep the old JWKS up to 1 hour).

The API does not start with an invalid `Jwt` configuration: missing key outside Development, a PEM that cannot be imported or of the wrong kind, a key under 2048 bits, a malformed or repeated key id, an invalid issuer or lifetimes out of range. The deploy logs show which value failed, never the key.

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
| `Jwt__PreviousPublicKeys__N__KeyId` / `Jwt__PreviousPublicKeys__N__PublicKeyPem` | none | Retired public keys still published in the JWKS during a rotation (`N` = 0, 1, ...). See [JWT signing keys](#jwt-signing-keys). |
| `Jwt__AccessTokenMinutes` | `15` | Access token lifetime, 1..60 (used from #24). |
| `Jwt__RefreshTokenDays` | `30` | Refresh token lifetime, 1..365 (used from #23 / #24). |
| `Serilog__MinimumLevel__Default` | `Information` | Log level of the console output (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). `Debug` also logs the recipient of every sent email. |

The API does not start with an invalid `Smtp` or `Notifications:Worker` configuration (values out of range, unknown `SecureSocket`, only one of `Smtp__Username` / `Smtp__Password`, credentials with `None`). The deploy logs show which value failed.

## First deploy checklist

1. Create the PostgreSQL service and the Href.Core service (from this repository, root `Dockerfile`), with no public domain. Set the health check path of the Href.Core service to `/health`.
2. Set `ConnectionStrings__DefaultConnection`, `Smtp__Username`, `Smtp__Password`, `Jwt__Issuer`, `Jwt__SigningKey__KeyId` and `Jwt__SigningKey__PrivateKeyPem` (see [JWT signing keys](#jwt-signing-keys)).
3. Deploy. In the logs, check that:
   - DbUp applied every migration (`0000_baseline.sql` up to the last `NNNN_*.sql`);
   - the warning `SMTP credentials are not configured` does not appear.
4. From inside the Railway private network (or a temporary private connection), call `GET /health`: it returns `200` with `Healthy`. `GET /.well-known/jwks.json` returns the key with the `kid` you set.
5. Create an application (`POST /api/apps`), queue a test email with its credentials (`POST /api/notifications/email`) and check that it arrives.

Keep this file in sync with `## Environment variables` in `SYSTEM_MAP.md` when a configuration key is added or changed.
