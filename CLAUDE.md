# Href.Core: instrucciones para Claude

API REST en .NET 10 (Controllers) con NHibernate (`Mapping.ByCode`) sobre PostgreSQL, Serilog y xUnit. Despliegue en Railway con el `Dockerfile` de la raíz.

- Código, namespaces (`Href.Core.*`), commits, títulos y descripciones de PR: en inglés.
- `CHANGELOG.md` y esta guía: en español.
- Mapa del sistema: `SYSTEM_MAP.md`.

## Estructura

- `src/Href.Core.Api/`: la API. `Common/` tiene la base compartida (`Domain`, `Persistence`, `Results`, `Web`).
- `tests/Href.Core.Tests/`: tests xUnit.
- Versión: `<Version>` en `src/Href.Core.Api/Href.Core.Api.csproj`.

## Convenciones de código

- Sin repositorios genéricos ni AutoMapper.
- Separación por módulo en 3 capas:
  - **Controller** (`*Controller`): adaptador HTTP puro (ruteo, códigos de estado y conversión `Result` → `ToProblem`). No usa `ISession` ni maneja transacciones.
  - **Application Service** (`*Service`, registrado como `Scoped`): orquesta el caso de uso, consulta y persiste con `ISession` directo, abre/commitea la transacción y devuelve `Result` / `Result<T>`.
  - **Entidad de dominio** (`Entity`): contiene las reglas de negocio e invariantes puras.
- Transacciones:
  - Lecturas: sin transacción explícita.
  - Escrituras: `using var transaction = session.BeginTransaction();` ... `await transaction.CommitAsync(cancellationToken);` explícito dentro del servicio del módulo. Sin filtro implícito de transacción por request.
- Errores esperados: `Result` / `Result<T>` con `Error`. Las excepciones son para fallas inesperadas (las atrapa `GlobalExceptionHandler`).
- Entidades: heredan de `Entity`, miembros `virtual`. Mapping con `ClassMapping<T>` público en el assembly de la API (se registra solo).
- Convenciones de nombres y mappings de base de datos:
  - Tablas y columnas en `snake_case`, con nombres de tabla en plural (ej.: `applications`).
  - `Table(...)` y `Column(...)` explícitos en todos los mappings.
  - Enums guardados como strings (`varchar`).
- `Id` con `Generators.Assigned`. Para entidades nuevas: `session.Save` / `Persist`.
- Entidades auditables: implementan `IAuditableEntity`; columnas `timestamptz` (`created_at_utc` y `updated_at_utc`) mapeadas con `NHibernateUtil.UtcDateTime`:
  - `Property(x => x.CreatedAtUtc, m => { m.Column("created_at_utc"); m.Type(NHibernateUtil.UtcDateTime); m.NotNullable(true); m.Update(false); });`
  - `Property(x => x.UpdatedAtUtc, m => { m.Column("updated_at_utc"); m.Type(NHibernateUtil.UtcDateTime); m.NotNullable(true); });`
- Migraciones de esquema (DbUp, `DatabaseMigrator`):
  - Scripts forward-only en `src/Href.Core.Api/Database/Migrations/`, nombrados `NNNN_description.sql` (ej.: `0001_create_applications.sql`), embebidos en el assembly (`EmbeddedResource`) y aplicados al arrancar (controlado por `Database:MigrateOnStartup`, por defecto `true`).
  - Un script mergeado nunca se edita; cualquier cambio posterior va en un script nuevo.
  - Un script por PR que modifique el esquema, listado en la sección `Data / database` del PR.
  - Tabla de journal de DbUp: `schemaversions`.
- Auditoría inmutable (`IAuditService`, `AuditLog`):
  - `RecordAsync` se invoca dentro de la misma transacción e `ISession` del cambio de negocio.
  - En `OldValues` y `NewValues` se pasan objetos/records de snapshot (serializados como `jsonb` en camelCase), nunca entidades de NHibernate directas, y **nunca** secretos, contraseñas ni hashes.

## Ramas

- `main`: solo releases. Es lo que se publica en producción (Railway).
- `develop`: rama base y default. Todo PR de feature, fix o dependencias va acá.
- Las ramas de trabajo salen siempre de `develop`: `feat/<slug>`, `fix/<slug>`, `docs/<slug>`, `chore/<slug>`, `refactor/<slug>`.
- Épicas:
  1. Se crea `epic/<id>-<slug>` desde `develop`.
  2. Cada issue de la épica tiene su rama, que sale de la rama de la épica, y su PR apunta a la épica. Excepción: si el usuario lo autoriza explícitamente, el issue se trabaja directo en la rama de la épica.
  3. Una vez que el PR de un issue se mergea a la rama de la épica, se espera confirmación del usuario y se cierra el issue con un comentario coherente sobre la completitud de la tarea.
  4. Con todos los issues mergeados, se abre el PR de la épica → `develop`.
  5. Después, el release `develop` → `main`.
- Si hay otra sesión de Claude trabajando en la misma carpeta, no se cambia de rama ahí: se trabaja en un `git worktree` dentro de `.claude/worktrees/` (ignorado por git).

## Commits

- No commitear ni pushear hasta que el usuario confirme que el build y los tests pasaron y dé el OK explícito. Si corresponde dividir en más de un commit lógico, se hace recién en ese momento.
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
- La descripción (en inglés) sigue todas las secciones del template, en el mismo orden. Las secciones que no aplican no se borran: quedan con la casilla correspondiente ("No schema changes", "Does not change the version") marcada.

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
- Tests de integración (`tests/Href.Core.Tests/Integration/`):
  - Usan Testcontainers (`postgres:16-alpine` en `PostgresFixture`) y `ApiFactory` (`WebApplicationFactory<Program>`).
  - Marcados con `[Trait("Category", "Integration")]` (para correr solo unit tests sin Docker: `dotnet test Href.Core.sln --filter Category!=Integration`).
  - Aislamiento: cada test trabaja con sus propios datos únicos (IDs, claves o emails generados por test) en lugar de truncar tablas.

  ```bash
  dotnet build Href.Core.sln -c Release
  ```

  ```bash
  dotnet test Href.Core.sln -c Release
  ```

## Ciclo de verificación al implementar (feedback loop)

Aplica a cualquier agente que implemente en este repo (Claude, Gemini u otro). Regla de base: **lo que se declara tiene que estar verificado**. Si algo no se pudo verificar, se dice explícitamente; no se da por hecho.

1. **Antes de escribir código**, listar cada criterio de aceptación y cada comportamiento que se va a anunciar (en el `CHANGELOG.md`, el `SYSTEM_MAP.md` o el PR) y, al lado, cómo se verifica: el nombre del test o el chequeo manual.
2. **Test primero cuando se pueda**: escribir el test del comportamiento y confirmar que fallaría sin el cambio. Un test que también pasa con el código roto no verifica el requisito.
3. **Implementar** el cambio mínimo que hace pasar ese test.
4. **Autorrevisar el diff** antes de entregarlo:
   - contra las convenciones de esta guía (capas, transacciones, mappings, sin comentarios en línea);
   - contra entradas hostiles: valores inválidos, numéricos o combinados en enums, vacíos/espacios, mayúsculas, `null`;
   - contra concurrencia: dos requests iguales al mismo tiempo (chequeo-y-después-insert, índices únicos);
   - contra el entorno real: Railway detrás de un proxy, contexto `async`, orden del pipeline de middlewares.
5. **Cotejar lo declarado**: cada línea del `CHANGELOG.md`, del `SYSTEM_MAP.md` y de la descripción del PR tiene que estar respaldada por un test o un chequeo del punto 1. Si no, se corrige el texto o se agrega la verificación.
6. **Entregar al usuario** para build y tests. Si algo falla, se corrige la causa raíz y se vuelve al punto 4.
7. **En el PR**, las casillas se marcan solo si son ciertas, y la sección `Verification` dice qué se verificó y cómo.

## Trampas conocidas

Errores que ya pasaron en este repo (revisión de v0.1.0, PR #67):

- `LogContext.PushProperty` (o cualquier `AsyncLocal`) dentro de un método `async` se pierde cuando el método termina: no llega a quien lo llamó. Para enriquecer los logs de un request se usa un middleware que envuelve `next` y, para la línea final del request, `IDiagnosticContext`.
- Sin esquema de autenticación por defecto, `HttpContext.User` lo carga la policy en `UseAuthorization`, no `UseAuthentication`. Todo middleware que lea el usuario va después de `UseAuthorization`.
- `Enum.TryParse` acepta números (`"1"`) y combinaciones (`"Active,Suspended"`). Para entradas de la API se valida contra los nombres exactos.
- Chequear que algo no existe y después insertarlo no es atómico. La fuente de verdad es el índice único: se atrapa la violación (`23505`) y se devuelve el `Error` de dominio (409), no un 500.
- Detrás del proxy de Railway, `RemoteIpAddress` es la IP del proxy si no se usa `UseForwardedHeaders`.
- Un template de Serilog solo muestra en consola las propiedades que nombra (`{AppKey}`); agregar la propiedad al evento no alcanza para verla.

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

- Los issues de una épica se cierran (con un comentario coherente sobre la completitud de la tarea) luego de mergear su PR a la rama de la épica y recibir confirmación del usuario; la épica se cierra al completar el ciclo.

## Flujo para features planificadas

1. Claude escribe el plan y lo pasa en el chat.
2. Espera aprobación antes de escribir código.
3. Con la aprobación, crea la rama (o worktree) desde `develop` (o desde la rama de la épica) e implementa siguiendo el ciclo de verificación, dejando los cambios sin commitear.
4. Espera a que el usuario corra el build (`Ctrl+Shift+B`) y los tests (`Test → Run All Tests`) en Visual Studio y dé el OK.
5. Con el OK, arma el/los commits, pushea, abre el PR y pasa el link.
6. Una vez que el PR del issue se mergea a la rama de la épica, espera confirmación del usuario y cierra el issue con un comentario coherente sobre la completitud de la tarea.
7. Cuando el PR está aprobado y el bloque está completo, abre el PR de release.

## Dependabot

- `.github/dependabot.yml`: nuget, github-actions, docker y docker-compose, mensual, a `develop`.
- Minor y patch agrupados en un PR por ecosistema; los major llegan de a uno.
- Majors ignorados (se migran a mano junto con .NET): `Microsoft.AspNetCore.*`, `Microsoft.Extensions.*`, `Npgsql*`, `Serilog.AspNetCore`, `NHibernate`, imágenes `dotnet/sdk` y `dotnet/aspnet`, `postgres`.
