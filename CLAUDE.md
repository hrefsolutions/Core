# Href.Core: instrucciones para Claude

API REST en .NET 10 (Controllers) con NHibernate (`Mapping.ByCode`) sobre PostgreSQL, Serilog y xUnit. Despliegue en Railway con el `Dockerfile` de la raíz.

- Código, namespaces (`Href.Core.*`), commits y títulos de PR: en inglés.
- Descripciones de PR, `CHANGELOG.md` y esta guía: en español.
- Mapa del sistema: `SYSTEM_MAP.md`.

## Estructura

- `src/Href.Core.Api/`: la API. `Common/` tiene la base compartida (`Domain`, `Persistence`, `Results`, `Web`).
- `tests/Href.Core.Tests/`: tests xUnit.
- Versión: `<Version>` en `src/Href.Core.Api/Href.Core.Api.csproj`.

## Convenciones de código

- Sin repositorios genéricos ni AutoMapper. Los handlers/controllers usan `ISession` de NHibernate directo.
- Errores esperados: `Result` / `Result<T>` con `Error`. Las excepciones son para fallas inesperadas (las atrapa `GlobalExceptionHandler`).
- Entidades: heredan de `Entity`, miembros `virtual`. Mapping con `ClassMapping<T>` público en el assembly de la API (se registra solo).
- `Id` con `Generators.Assigned`. Para entidades nuevas: `session.Save` / `Persist`.
- Entidades auditables: implementan `IAuditableEntity`; columnas `timestamptz` mapeadas con `NHibernateUtil.UtcDateTime`.

## Ramas

- `main`: solo releases. Es lo que se publica en producción (Railway).
- `develop`: rama base y default. Todo PR de feature, fix o dependencias va acá.
- Las ramas de trabajo salen siempre de `develop`: `feat/<slug>`, `fix/<slug>`, `docs/<slug>`, `chore/<slug>`, `refactor/<slug>`.
- Épicas:
  1. Se crea `epic/<id>-<slug>` desde `develop`.
  2. Cada issue de la épica tiene su rama, que sale de la rama de la épica, y su PR apunta a la épica.
  3. Con todos los issues mergeados, se abre el PR de la épica → `develop`.
  4. Después, el release `develop` → `main`.
- Si hay otra sesión de Claude trabajando en la misma carpeta, no se cambia de rama ahí: se trabaja en un `git worktree` dentro de `.claude/worktrees/` (ignorado por git).

## Commits

- En inglés, Conventional Commits: `type(scope): subject`. Ej.: `feat(api): add customer endpoint`, `fix(persistence): handle null audit timestamp`.
- Scopes sugeridos: `api`, `persistence`, `domain`, `infra`, `deps`, `ci`.
- Sin `Co-Authored-By: Claude` ni otros trailers de atribución.
- Los commits de release van directo a `develop` con el mensaje `chore: release vX.Y.Z`.

## Pull requests

- Base `develop`. Excepciones: releases (a `main`) e issues de una épica (a su rama).
- Título en Conventional Commits, describe solo el cambio. Nunca nombra ramas: nada de "(develop → main)".
- Release con bump de versión: `chore: release vX.Y.Z`. Release sin bump: el título del cambio que lleva.
- Se crea con `gh pr create --body-file <archivo>`, con la descripción completa armada sobre `.github/pull_request_template.md`.
- Siempre con `--assignee Scerutti` y labels desde el principio:
  - Tipo: `enhancement`, `bug`, `documentation`, `dependencies`, `limpieza`, `security`.
  - Área: `api`, `persistence`, `domain`, `infra`.
  - Un PR de release lleva el tipo de lo que publica.
- Sin "🤖 Generated with Claude Code" ni links de sesión en la descripción.
- La descripción sigue todas las secciones del template, en el mismo orden. Las secciones que no aplican no se borran: quedan con la casilla "sin cambios" marcada.

Ejemplo:

```bash
gh pr create --base develop --title "feat(api): add customer endpoint" --body-file .claude/tmp/pr-body.md --assignee Scerutti --label enhancement --label api
```

## Contenido obligatorio en cada PR

- Una entrada en `CHANGELOG.md`, bajo `## Próxima versión`.
- `SYSTEM_MAP.md` actualizado en el mismo PR si cambian rutas, entidades/tablas, mappings, variables de entorno o servicios externos.
- Sin comentarios en línea. Solo se permiten:
  - `///` (XML doc) justo encima de una declaración;
  - directivas de herramientas (`#pragma warning disable`, `// ReSharper disable ...`).
  - Nada de `//` adentro de métodos, comentarios al final de una línea, separadores de sección ni código comentado.

## Verificación

- CI en GitHub Actions (`.github/workflows/ci.yml`): en cada `pull_request` y cada push a `main`, con `contents: read`. Pasos: `dotnet restore`, `dotnet build` (equivale al typecheck) y `dotnet test`.
- La imagen Docker la construye Railway.
- Claude no corre build, test ni lint en local: indica qué comando correr y el usuario le pasa el resultado.

  ```bash
  dotnet build Href.Core.sln -c Release
  ```

  ```bash
  dotnet test Href.Core.sln -c Release
  ```

## Releases

- Se agrupan varios issues por release, no uno por PR. Antes de abrir un release, Claude pregunta qué bloque va.
- El PR de release `develop` → `main`:
  - sube `<Version>` en `Href.Core.Api.csproj` según el cambio más grande (semver);
  - renombra `## Próxima versión` a `## X.Y.Z - AAAA-MM-DD` en `CHANGELOG.md` y deja una `## Próxima versión` vacía arriba;
  - lista en orden todos los pasos manuales (migraciones/SQL, variables de entorno en Railway, otros).
- Después del merge: GitHub release con tag `vX.Y.Z` en el commit de merge y la sección del CHANGELOG como notas.

  ```bash
  gh release create vX.Y.Z --target main --title "vX.Y.Z" --notes-file .claude/tmp/release-notes.md
  ```

- Los issues y las épicas se cierran a mano en el release: los PR a ramas que no son la default no los cierran solos.

## Flujo para features planificadas

1. Claude escribe el plan y lo pasa en el chat.
2. Espera aprobación antes de escribir código.
3. Con la aprobación, crea un worktree desde `develop` (o desde la rama de la épica) e implementa.
4. Revisa el resultado, abre el PR y pasa el link.
5. Cuando el PR está aprobado y el bloque está completo, abre el PR de release.

## Dependabot

- `.github/dependabot.yml`: nuget, github-actions, docker y docker-compose, mensual, a `develop`.
- Minor y patch agrupados en un PR por ecosistema; los major llegan de a uno.
- Majors ignorados (se migran a mano junto con .NET): `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Npgsql*`, `Serilog.AspNetCore`, `NHibernate`, imágenes `dotnet/sdk` y `dotnet/aspnet`, `postgres`.
