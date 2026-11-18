# Changelog

Cambios relevantes de Href.Core. Versionado [SemVer](https://semver.org/lang/es/); la versión vive en `src/Href.Core.Api/Href.Core.Api.csproj` (`<Version>`).

## Próxima versión

### Agregado

- Entidades `EmailTemplate` y `Notification` (`Modules/Notifications/`) con sus mappings NHibernate y la migración `0003_create_notifications_and_email_templates.sql` (tablas `email_templates` y `notifications`, índice único `ux_email_templates_key` e índices `ix_notifications_status_next_attempt_at_utc` e `ix_notifications_app_key_created_at_utc`). Los cuerpos `html_body` y `text_body` son `TEXT`.
- Máquina de estados de `Notification` (`Pending` → `Processing` → `Sent` | `Failed`) con reintentos. El intento se cuenta al reclamar (`MarkProcessing` incrementa `Attempts` y se rechaza con `Notifications.InvalidTransition` si `Attempts >= MaxAttempts`, por lo que nunca hay un 4.º intento). Tras el 1.º fallo vuelve a `Pending` con espera de 1 minuto, tras el 2.º con 5 minutos, y tras el 3.º pasa a `Failed`. `RecoverStuck` libera una notificación trabada en `Processing` (por ejemplo tras la caída de un worker) sin tocar `Attempts`: pasa a `Failed` con `LastError` = `Processing timed out` si ya no quedan intentos, o vuelve a `Pending` lista para reclamarse. Con tres reclamos seguidos de una caída la notificación termina `Failed`. Desde `Sent` y `Failed` no hay transiciones; una transición inválida devuelve el error `Conflict` `Notifications.InvalidTransition` sin modificar el estado. `LastError` se conserva como historial tras un envío exitoso.
- Concurrencia optimista en `Notification` (columna `version`, `Version` en el mapping): dos sesiones que reclaman la misma notificación no pueden ambas confirmar el cambio (la segunda recibe `StaleObjectStateException`). El SQL directo que modifique una notificación debe reflejar los métodos de dominio y siempre setear `version = version + 1` y `updated_at_utc`. Reclamo: `attempts = attempts + 1`, `status = 'Processing'`, `processing_started_at_utc`, solo para filas con `attempts < max_attempts`. Recuperación: no toca `attempts`, `status = CASE WHEN attempts >= max_attempts THEN 'Failed' ELSE 'Pending' END` y `processing_started_at_utc = NULL`.
- Validación de entradas de `Notification` y `EmailTemplate`: se rechaza `NUL` en todos los textos (cuerpos incluidos) y cualquier carácter de control o separador U+2028/U+2029 en los campos de una sola línea; las transiciones exigen `DateTime` con `Kind` UTC (`ArgumentException` en caso contrario); `LastError` se trunca a 2000 sin dejar un surrogate alto huérfano.

## 0.1.1 - 2026-11-18

### Corregido

- El `AppKey` de la aplicación autenticada ahora llega a los logs: `AppKeyLogContextMiddleware` lo agrega al `LogContext` de Serilog y a la línea de cierre del request, y el formato de consola lo muestra (`[{AppKey}]`). En v0.1.0 el handler lo agregaba dentro de un método `async` y se perdía.
- `PATCH /api/apps/{appKey}/status` solo acepta `Active` o `Suspended`: valores numéricos como `"1"` devuelven `400 Applications.InvalidStatus` en lugar de suspender la aplicación.
- `POST /api/apps` concurrentes con la misma `appKey` devuelven `409 Applications.AppKeyTaken` en lugar de `500` cuando choca el índice único `ux_applications_app_key`.
- La IP del cliente (por ejemplo, en `AuditLog.IpAddress`) se toma de `X-Forwarded-For` (`UseForwardedHeaders`, una sola entrada: la que agrega el proxy de Railway) en lugar de la IP del proxy.

### Cambiado

- `HealthController` ya no usa `ISession`: el chequeo `SELECT 1` vive en `DatabaseHealthService`.

### Documentación

- `CLAUDE.md`: ciclo de verificación al implementar (feedback loop) para cualquier agente y lista de trampas conocidas encontradas en la revisión de v0.1.0.

## 0.1.0 - 2026-11-18

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
- Suite de tests de integración con Testcontainers (`Testcontainers.PostgreSql`, `postgres:16-alpine`) y `WebApplicationFactory<Program>` (`PostgresFixture`, `ApiFactory`, `PersistenceIntegrationTests` y `HealthEndpointIntegrationTests`).
- Entidad `Application` (`Modules/Applications/`), generador/verificador de `ClientSecret` (32 bytes base64url + SHA-256 en tiempo constante), mapping NHibernate (`ApplicationMapping`) y migración `0001_create_applications.sql`.
- Endpoints de administración de aplicaciones en `ApplicationsController` (`POST /api/apps`, `GET /api/apps`, `GET /api/apps/{appKey}`, `PUT /api/apps/{appKey}`, `PATCH /api/apps/{appKey}/status` y `POST /api/apps/{appKey}/secret/rotate`) con tests de integración HTTP.
- Autenticación M2M de aplicaciones cliente (`AppKeyAuthenticationHandler`, esquema `AppKey`, policy `Application`, headers `X-App-Key` + `X-App-Secret`, claims `app_id`/`app_key`, enriquecimiento de `AppKey` en Serilog `LogContext`) y endpoint `GET /api/apps/me`.
- Base de auditoría inmutable ISO 9001 (`AuditLog`, `AuditLogMapping` con `Mutable(false)`, `JsonbType`, `IAuditService` / `AuditService` y migración `0002_create_audit_logs.sql` con trigger `audit_logs_block_mutation`).

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
