# Railway deployment instructions

How to configure the Href.Core service on Railway. Railway builds the root `Dockerfile` and passes the service variables to the container as environment variables.

ASP.NET Core reads environment variables as configuration keys: a double underscore (`__`) separates sections, so `Smtp__Password` is the key `Smtp:Password` of `appsettings.json`. A variable set in Railway overrides the value in `appsettings.json`.

> [!WARNING]
> Versions before v0.4.0 have no admin authentication. With them, keep the Railway service **private** (no public domain). From v0.4.0 on, the admin endpoints (`/api/apps`, `/api/users`, `/api/apps/{appKey}/permissions`, `/roles`, `/users` and `/api/notifications/templates`) require a bearer token of a core admin. The first admin is created with the `Bootstrap__*` variables (see [First admin](#first-admin)).

## Variables to set

These must exist in Railway before the first deploy.

| Variable | Value | Notes |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | Npgsql connection string to the Railway PostgreSQL service | **Secret.** The API does not start without it. Use the Npgsql key-value format, not the `postgresql://` URL that Railway shows as `DATABASE_URL`. With Railway reference variables (replace `Postgres` with the name of the database service): `Host=${{Postgres.PGHOST}};Port=${{Postgres.PGPORT}};Database=${{Postgres.PGDATABASE}};Username=${{Postgres.PGUSER}};Password=${{Postgres.PGPASSWORD}}` |
| `Smtp__Username` | `href.notifier@gmail.com` | Gmail account that sends the emails. Set together with `Smtp__Password`. |
| `Smtp__Password` | Gmail App Password (16 characters) | **Secret.** An App Password of `href.notifier@gmail.com`, never the account password. Store it only in Railway. Without credentials the API starts, but every email fails and a startup warning `SMTP credentials are not configured` is logged. |
| `Jwt__Issuer` | Public HTTPS base URL of the Core, for example `https://core-production.up.railway.app` | Used as the token `iss` and as the base of `jwks_uri`, so satellite apps must be able to reach it. Scheme and host (optional port) only: no path, query or fragment; a trailing `/` is removed. `http` is rejected except for loopback hosts, so a private Railway address (`*.railway.internal`) is not valid. See [Public domain](#public-domain). |
| `Jwt__SigningKey__KeyId` | Key id, for example `core-2026-11` | Published as `kid`. 1 to 64 characters among `A-Z`, `a-z`, `0-9`, `.`, `_`, `-`. Use a new id on every rotation. |
| `Jwt__SigningKey__PrivateKeyPem` | RSA private key in PEM | **Secret.** See [JWT signing keys](#jwt-signing-keys). PKCS#8 (`BEGIN PRIVATE KEY`) or PKCS#1 (`BEGIN RSA PRIVATE KEY`), at least 2048 bits (3072 recommended), not encrypted. Paste it with its line breaks or on one line. The API does not start without it. |

Outbound SMTP needs the Railway Pro plan or higher. After an upgrade, redeploy the service.

## First admin

The admin frontend's backend logs in through the reserved `core` application, which migration `0008` creates. A core admin is a user with access to `core` and the `admin` role, which carries the `core.admin` permission. Nobody knows the `core` secret after the migration, and nobody is an admin yet. Set these variables for the first deploy of v0.4.0:

| Variable | Value | Notes |
|---|---|---|
| `Bootstrap__CoreClientSecret` | A random secret of at least 32 characters, no whitespace (for example `openssl rand -base64 48 \| tr -d '\n/+='`) | **Secret.** The client secret of the `core` application. Store it in the admin frontend's backend. |
| `Bootstrap__AdminEmail` | Email of the first admin | If a verified user with that email exists, it keeps its password and gets admin access. An unverified one gets the bootstrap password first. |
| `Bootstrap__AdminPassword` | Password of the first admin | **Secret.** 10 to 128 characters. Ignored only for an existing verified user. |
| `Bootstrap__AdminFullName` | Optional, default `Administrator` | Only used when the user is created. |

1. Set the variables and deploy. The logs show `Bootstrap gave core admin access to user ...` and the warnings to remove the variables.
2. From the admin frontend's backend, call `POST /api/auth/login` with `X-App-Key: core`, `X-App-Secret: <core secret>` and the admin's email and password. Use the `accessToken` as `Authorization: Bearer ...` on the admin endpoints.
3. Remove `Bootstrap__AdminEmail`, `Bootstrap__AdminPassword` and `Bootstrap__CoreClientSecret` from Railway and redeploy. While `Bootstrap__CoreClientSecret` is set, every restart sets the `core` secret again and undoes any rotation done through `POST /api/apps/core/secret/rotate`.

The bootstrap does nothing while some user holds `core.admin` and can log in (active, with a verified email). If every admin is locked, unverified or without the role, set the admin variables again to recover access. When the admins are locked, use an email that does not belong to a locked user: the bootstrap does not unlock users.

The `core` application has these protections, and each attempt returns `409`:

- it cannot be suspended or opened to self-registration;
- its `core.admin` permission and its `admin` role cannot be deleted;
- the role cannot lose that permission.

## Public domain

`Jwt__Issuer` needs the public HTTPS domain of the service: in Railway, Settings → Networking → Generate Domain (`https://<name>.up.railway.app`) or a custom domain. Generating it makes the service public. Do that only when the deployed version includes admin authentication (#53), which ships in v0.4.0, the same release as the `Jwt__*` variables. Before that, keep the service private, as the warning above says.

## JWT signing keys

Access tokens are signed with RS256 using `Jwt__SigningKey__PrivateKeyPem`. The public keys are published at `GET /.well-known/jwks.json`, and `GET /.well-known/openid-configuration` returns `issuer` and `jwks_uri`. Both responses send `Cache-Control: public, max-age=3600` for HTTP caches.

Satellite apps that use `AddJwtBearer(options => options.Authority = ...)` do not follow that header. They keep the keys in memory and download them again:

- every 12 hours (`ConfigurationManager.DefaultAutomaticRefreshInterval`, changeable with `AutomaticRefreshInterval`);
- when a token arrives with an unknown `kid` (`RefreshOnIssuerKeyNotFound`, on by default), at most once every 5 minutes.

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
3. Set the new `Jwt__SigningKey__KeyId` and `Jwt__SigningKey__PrivateKeyPem` and redeploy. New tokens use the new key. Satellite apps download the new key the first time they see its `kid`. If a satellite downloaded the keys less than 5 minutes before, it can reject new tokens until those 5 minutes pass. Tokens signed with the old key keep validating because its public key is still published.
4. Remove the previous key once every token signed with it has expired: `Jwt__AccessTokenMinutes` plus the clock skew of the satellites (5 minutes by default in JwtBearer) after the redeploy of step 3.

To avoid even those 5 minutes, publish the new public key first: add it as one more `Jwt__PreviousPublicKeys__N__*` entry (the list publishes any extra public key) and redeploy. Wait 12 hours, so that every satellite has downloaded the keys again. Then do steps 2 and 3.

If the private key leaks:

1. Rotate immediately, without step 2, so the leaked key is no longer published.
2. Restart every satellite app, or have it request a refresh of the keys. A token forged with the leaked key carries a `kid` the satellite already knows, so it does not trigger a download, and the satellite keeps accepting it for up to 12 hours (the automatic refresh interval).

The API does not start with an invalid `Jwt` configuration: missing key outside Development, a PEM that cannot be imported or of the wrong kind, a key under 2048 bits, a malformed or repeated key id, an invalid issuer (with a path, not `https` outside loopback) or lifetimes out of range. The deploy logs show which value failed, never the key.

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
| `AuditLogs__Export__MaxRows` | `100000` | Maximum rows of one `GET /api/audit-logs/export`, 1..100000. A larger match returns `400 AuditLogs.ExportTooLarge`. |
| `AuditLogs__Export__ChunkSize` | `1000` | Rows read per database query while streaming an export, 1..10000. |
| `Serilog__MinimumLevel__Default` | `Information` | Log level of the console output (`Verbose`, `Debug`, `Information`, `Warning`, `Error`, `Fatal`). `Debug` also logs the recipient of every sent email. |

The API does not start with an invalid `Smtp`, `Notifications:Worker` or `AuditLogs:Export` configuration (values out of range, unknown `SecureSocket`, only one of `Smtp__Username` / `Smtp__Password`, credentials with `None`). The deploy logs show which value failed.

## First deploy checklist

1. Create the PostgreSQL service and the Href.Core service (from this repository, root `Dockerfile`), with no public domain before v0.4.0 (admin authentication). Set the health check path of the Href.Core service to `/health`.
2. Set `ConnectionStrings__DefaultConnection`, `Smtp__Username` and `Smtp__Password`. From v0.4.0 on, also do the following:
   - generate the public domain (see [Public domain](#public-domain)) and set `Jwt__Issuer` to it;
   - set `Jwt__SigningKey__KeyId` and `Jwt__SigningKey__PrivateKeyPem` (see [JWT signing keys](#jwt-signing-keys));
   - set the `Bootstrap__*` variables (see [First admin](#first-admin)).
3. Deploy. In the logs, check that:
   - DbUp applied every migration (`0000_baseline.sql` up to the last `NNNN_*.sql`);
   - the warning `SMTP credentials are not configured` does not appear;
   - from v0.4.0 on, `Bootstrap gave core admin access to user ...` appears.
4. Call `GET /health`: it returns `200` with `Healthy`. `GET /.well-known/jwks.json` returns the key with the `kid` you set.
5. Log in as the admin (see [First admin](#first-admin)). With the bearer token, create an application (`POST /api/apps`). Queue a test email with its credentials (`POST /api/notifications/email`) and check that it arrives.
6. Remove the `Bootstrap__*` variables and redeploy.

Keep this file in sync with `## Environment variables` in `SYSTEM_MAP.md` when a configuration key is added or changed.
