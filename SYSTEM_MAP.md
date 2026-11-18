# System map

Update this file in the same PR that changes any of the items below.

## HTTP endpoints

| Method | Route                             | Controller               | Description                                                                                     |
|--------|-----------------------------------|--------------------------|-------------------------------------------------------------------------------------------------|
| GET    | `/health`                         | `HealthController`       | `200` when the API is alive and PostgreSQL answers `SELECT 1`, else `503`.                      |
| POST   | `/api/apps`                       | `ApplicationsController` | Creates a client application (`201` + `Location` + one-time `clientSecret`; `400`/`409`).       |
| GET    | `/api/apps`                       | `ApplicationsController` | Lists all client applications ordered by `name` (`200`).                                        |
| GET    | `/api/apps/{appKey}`              | `ApplicationsController` | Returns a client application by `appKey` (`200` or `404`).                                      |
| PUT    | `/api/apps/{appKey}`              | `ApplicationsController` | Updates `name`, `description` and `allowSelfRegistration` (`200`, `400`, `404`).                |
| PATCH  | `/api/apps/{appKey}/status`       | `ApplicationsController` | Activates or suspends an application (`Active` \| `Suspended`, `200`, `400`, `404`, `409`).     |
| POST   | `/api/apps/{appKey}/secret/rotate`| `ApplicationsController` | Rotates the application `clientSecret`, invalidating the previous one immediately (`200`, `404`).|
| GET    | `/api/apps/me`                    | `ApplicationsController` | M2M (`Application` policy, `X-App-Key` + `X-App-Secret`): returns the authenticated app (`200`, `401`, `403`). |

## Request pipeline (`Program.cs`)

1. `UseForwardedHeaders`: takes the client IP and scheme from `X-Forwarded-For` / `X-Forwarded-Proto` (`ForwardLimit = 1`: only the rightmost entry, the one added by the Railway proxy, so clients cannot spoof it). Any proxy is trusted, so the container must only be reachable through Railway's proxy.
2. `CorrelationIdMiddleware`: reads or creates `X-Correlation-Id`, returns it in the response and adds `CorrelationId` to every log line.
3. `UseSerilogRequestLogging`: one log line per request.
4. `UseExceptionHandler` + `GlobalExceptionHandler`: unhandled exceptions become RFC 7807 `ProblemDetails` (500). Every `ProblemDetails` includes `instance` and `correlationId`.
5. `UseStatusCodePages`: empty error responses (such as 404 or 405) are formatted as `ProblemDetails`.
6. `UseAuthentication` + `UseAuthorization`: scheme `AppKey` (`AppKeyAuthenticationHandler`, headers `X-App-Key` and `X-App-Secret`, claims `app_id` and `app_key`) and policy `Application`.
7. `AppKeyLogContextMiddleware`: for authenticated applications, adds `AppKey` to the Serilog `LogContext` (logs written later in the request) and to the request completion log line.
8. `MapControllers`.

## Domain

| Type                        | Kind      | Notes                                                                 |
|-----------------------------|-----------|-----------------------------------------------------------------------|
| `Common.Domain.Entity`      | base      | `Guid Id` (UUIDv7, assigned on creation). Equality by type + `Id`.   |
| `Common.Domain.IAuditableEntity` | interface | `CreatedAtUtc`, `UpdatedAtUtc` set by `AuditTimestampListener`. |
| `Common.Results.Result` / `Result<T>` / `Error` | result | Expected failures return `Error` (`Validation`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Failure`); `ResultExtensions.ToProblem` converts them to `ProblemDetails` with `errorCode`. |
| `Modules.Applications.Application` | entity | Client (satellite) application (`AppKey`, `Name`, `Description`, `Status` [`Active`\|`Suspended`], `ClientSecretHash` [SHA-256 hex], `ClientSecretRotatedAtUtc`, `AllowSelfRegistration`, `CreatedAtUtc`, `UpdatedAtUtc`). |
| `Modules.Applications.ApplicationService` | service | Scoped service that orchestrates `Application` CRUD, status transitions and secret rotation over `ISession` with explicit transactions. |
| `Modules.Applications.ClientSecretGenerator` | service | Generates 32-byte base64url secrets (43 chars) and verifies SHA-256 hashes with `CryptographicOperations.FixedTimeEquals`. |
| `Modules.Audit.AuditLog` | entity | Immutable append-only audit entry (ISO 9001: `OccurredAtUtc`, `Action`, `EntityType`, `EntityId`, `ActorType` [`User`\|`Application`\|`System`\|`Anonymous`], `ActorId`, `AppKey`, `OldValues` [`jsonb`], `NewValues` [`jsonb`], `CorrelationId`, `IpAddress`, `UserAgent`). |
| `Modules.Audit.IAuditService` / `AuditService` | service | Scoped service that records `AuditLog` entries on the active `ISession`, enriching actor, app, correlation ID, IP and user agent from `IHttpContextAccessor`. |

## Persistence

- NHibernate `ISessionFactory` (singleton) and `ISession` (scoped per request), registered in `Common/Persistence/NHibernateExtensions.cs`.
- Dialect `PostgreSQL83Dialect`, driver `NpgsqlDriver`.
- Health: `DatabaseHealthService` (scoped, `Common/Persistence/`) runs `SELECT 1` for `GET /health`.
- Mappings: every public `ClassMapping<T>` in the API assembly is picked up automatically (`NHibernateExtensions.BuildSessionFactory` also accepts additional assemblies for tests).
- Mapping conventions: tables and columns in `snake_case` (plural table names), explicit `Table(...)`/`Column(...)`, enums stored as strings (`varchar`), `Id` with `Generators.Assigned`, and audit columns with `NHibernateUtil.UtcDateTime` (`timestamptz`).
- Transactions: reads require no explicit transaction; writes open an explicit transaction (`session.BeginTransaction()` + `CommitAsync`).
- Schema migrations: DbUp (`dbup-postgresql`, `Common/Persistence/DatabaseMigrator.cs`) applies forward-only embedded SQL scripts from `src/Href.Core.Api/Database/Migrations/NNNN_description.sql` on startup (`WithTransactionPerScript`), recording executed scripts in the `schemaversions` journal table. In `Development`, `EnsureDatabase.For.PostgresqlDatabase` creates the database if it does not exist.
- Tables:
  - `applications` (`0001_create_applications.sql`, mapped by `ApplicationMapping`): `id`, `app_key` (unique index `ux_applications_app_key`), `name`, `description`, `status` (`CHECK IN ('Active', 'Suspended')`), `client_secret_hash`, `client_secret_rotated_at_utc`, `allow_self_registration`, `created_at_utc`, `updated_at_utc`.
  - `audit_logs` (`0002_create_audit_logs.sql`, mapped by `AuditLogMapping` with `Mutable(false)` and `JsonbType`): append-only table protected by trigger `audit_logs_block_mutation` (`BEFORE UPDATE OR DELETE`), with indexes on `occurred_at_utc DESC`, `(app_key, occurred_at_utc DESC)`, `correlation_id`, and `(entity_type, entity_id)`.

## Environment variables

| Variable                               | Required   | Default       | Notes                                        |
|----------------------------------------|------------|---------------|----------------------------------------------|
| `ASPNETCORE_ENVIRONMENT`               | no         | `Production`  | `Development` loads `appsettings.Development.json`. |
| `PORT`                                 | no         | `8080`        | Container listen port (Railway sets it). The `Dockerfile` entrypoint copies it to `ASPNETCORE_HTTP_PORTS`. |
| `ConnectionStrings__DefaultConnection` | yes (prod) | —             | Npgsql connection string. The app does not start without it. |
| `Database__MigrateOnStartup`           | no         | `true`        | When `true`, `DatabaseMigrator` runs embedded DbUp SQL migrations before accepting requests. |

## External services

| Service       | Where           | Use                                         |
|---------------|-----------------|---------------------------------------------|
| PostgreSQL 16 | Railway / local (`docker-compose.yml`, port 5432) | Main database. |
| Mailpit       | Local only (SMTP 1025, UI 8025) | Catches outgoing email in development. Not wired into the API yet. |
