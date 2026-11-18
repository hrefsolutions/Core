# System map

Update this file in the same PR that changes any of the items below.

## HTTP endpoints

| Method | Route     | Controller         | Description                                                            |
|--------|-----------|--------------------|------------------------------------------------------------------------|
| GET    | `/health` | `HealthController` | `200` when the API is alive and PostgreSQL answers `SELECT 1`, else `503`. |

## Request pipeline (`Program.cs`)

1. `CorrelationIdMiddleware`: reads or creates `X-Correlation-Id`, returns it in the response and adds `CorrelationId` to every log line.
2. `UseSerilogRequestLogging`: one log line per request.
3. `UseExceptionHandler` + `GlobalExceptionHandler`: unhandled exceptions become RFC 7807 `ProblemDetails` (500). Every `ProblemDetails` includes `instance` and `correlationId`.
4. `UseStatusCodePages`: empty error responses (such as 404 or 405) are formatted as `ProblemDetails`.
5. `MapControllers`.

## Domain

| Type                        | Kind      | Notes                                                                 |
|-----------------------------|-----------|-----------------------------------------------------------------------|
| `Common.Domain.Entity`      | base      | `Guid Id` (UUIDv7, assigned on creation). Equality by type + `Id`.   |
| `Common.Domain.IAuditableEntity` | interface | `CreatedAtUtc`, `UpdatedAtUtc` set by `AuditTimestampListener`. |
| `Common.Results.Result` / `Result<T>` / `Error` | result | Expected failures return `Error` (`Validation`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Failure`); `ResultExtensions.ToProblem` converts them to `ProblemDetails` with `errorCode`. |

No business entities yet.

## Persistence

- NHibernate `ISessionFactory` (singleton) and `ISession` (scoped per request), registered in `Common/Persistence/NHibernateExtensions.cs`.
- Dialect `PostgreSQL83Dialect`, driver `NpgsqlDriver`.
- Mappings: every public `ClassMapping<T>` in the API assembly is picked up automatically (`NHibernateExtensions.BuildSessionFactory` also accepts additional assemblies for tests).
- Mapping conventions: tables and columns in `snake_case` (plural table names), explicit `Table(...)`/`Column(...)`, enums stored as strings (`varchar`), `Id` with `Generators.Assigned`, and audit columns with `NHibernateUtil.UtcDateTime` (`timestamptz`).
- Transactions: reads require no explicit transaction; writes open an explicit transaction (`session.BeginTransaction()` + `CommitAsync`).
- Schema: no tables yet. There is no automatic schema generation.

## Environment variables

| Variable                               | Required   | Default       | Notes                                        |
|----------------------------------------|------------|---------------|----------------------------------------------|
| `ASPNETCORE_ENVIRONMENT`               | no         | `Production`  | `Development` loads `appsettings.Development.json`. |
| `PORT`                                 | no         | `8080`        | Container listen port (Railway sets it). The `Dockerfile` entrypoint copies it to `ASPNETCORE_HTTP_PORTS`. |
| `ConnectionStrings__DefaultConnection` | yes (prod) | —             | Npgsql connection string. The app does not start without it. |

## External services

| Service       | Where           | Use                                         |
|---------------|-----------------|---------------------------------------------|
| PostgreSQL 16 | Railway / local (`docker-compose.yml`, port 5432) | Main database. |
| Mailpit       | Local only (SMTP 1025, UI 8025) | Catches outgoing email in development. Not wired into the API yet. |
