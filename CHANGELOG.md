# Changelog

Cambios relevantes de Href.Core. Versionado [SemVer](https://semver.org/lang/es/); la versión vive en `src/Href.Core.Api/Href.Core.Api.csproj` (`<Version>`).

## Próxima versión

### Agregado

- Bootstrap de la API en .NET 10 (Controllers) con Serilog, NHibernate (`Mapping.ByCode`) y PostgreSQL.
- `GET /health`: estado de la API y de la base (`SELECT 1`), público (`[AllowAnonymous]`) y cubierto por tests unitarios.
- Manejo global de errores en formato RFC 7807 (`ProblemDetails`) y header `X-Correlation-Id`.
- Result pattern (`Result`, `Result<T>`, `Error`) y entidades base con timestamps de auditoría.
- `docker-compose.yml` para desarrollo (PostgreSQL 16 + Mailpit) y `Dockerfile` para Railway.
- Flujo de trabajo: CI, Dependabot, template de PR, `CLAUDE.md` y `SYSTEM_MAP.md`.
- `ResultExtensions.ToProblem` para convertir `Result` y `Error` (`Validation`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Failure`) en respuestas `ProblemDetails` con `errorCode`, y `UseStatusCodePages` para respuestas HTTP de error sin cuerpo.
- Overload interno de `NHibernateExtensions.BuildSessionFactory` con soporte para assemblies de mappings adicionales en tests (`InternalsVisibleTo("Href.Core.Tests")`) y tests unitarios de registro y configuración de NHibernate.
- Tests con `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`) para `AuditTimestampListener` (`CreatedAtUtc` y `UpdatedAtUtc`) y convención de mapping de columnas de auditoría en `CLAUDE.md`.
- Migraciones de esquema con DbUp (`dbup-postgresql`, `DatabaseMigrator`): scripts SQL embebidos en `Database/Migrations/` aplicados al arrancar (`Database:MigrateOnStartup`, journal `schemaversions`).

### Cambiado

- `global.json` fija el SDK en .NET 10 (`10.0.100` con `rollForward: latestFeature`) y el CI lo usa.
- `docker-compose.yml` incluye el servicio `api` (imagen del `Dockerfile`, ya configurada contra el PostgreSQL del compose) bajo el profile `container`: `docker compose up -d` sigue levantando solo PostgreSQL y Mailpit, y `docker compose --profile container up -d --build` levanta también la API en `http://localhost:8080`.

### Corregido

- El contenedor escucha en `PORT` vía `ASPNETCORE_HTTP_PORTS`, sin el warning de override de `--urls` al arrancar.
- `.env.example` refleja las variables del contenedor y el README lo usa con `docker run --env-file`.
- `Result` valida que `Error` no sea `null` al construirse.

### Documentación

- README: cómo dejar la API corriendo con `dotnet watch` (Hot Reload) sin F5 en cada cambio.
- Template de PR en inglés y guía `CLAUDE.md` actualizada con el cierre de issues al mergear en la rama de la épica.
