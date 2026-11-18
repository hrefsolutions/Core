# Changelog

Cambios relevantes de Href.Core. Versionado [SemVer](https://semver.org/lang/es/); la versión vive en `src/Href.Core.Api/Href.Core.Api.csproj` (`<Version>`).

## Próxima versión

## 0.1.0 - 2026-11-17

### Agregado

- Bootstrap de la API en .NET 10 (Controllers) con Serilog, NHibernate (`Mapping.ByCode`) y PostgreSQL.
- `GET /health`: estado de la API y de la base (`SELECT 1`).
- Manejo global de errores en formato RFC 7807 (`ProblemDetails`) y header `X-Correlation-Id`.
- Result pattern (`Result`, `Result<T>`, `Error`) y entidades base con timestamps de auditoría.
- `docker-compose.yml` para desarrollo (PostgreSQL 16 + Mailpit) y `Dockerfile` para Railway.
- Flujo de trabajo: CI, Dependabot, template de PR, `CLAUDE.md` y `SYSTEM_MAP.md`.
