# Changelog

Cambios relevantes de Href.Core. Versionado [SemVer](https://semver.org/lang/es/); la versión vive en `src/Href.Core.Api/Href.Core.Api.csproj` (`<Version>`).

## Próxima versión

## 0.6.1 - 2026-11-24

Cierra los hallazgos de la auditoría E2E de v0.6.0 (#115, #116, #117, #118, #119).

### Agregado

- Rol de base de datos con mínimo privilegio (#117):
  - **Variables nuevas, opcionales:** `ConnectionStrings__Migrations` (la conexión dueña del esquema, que corre las migraciones; si falta se usa `DefaultConnection`) y `Database__RuntimeRole` (el rol con el que se conecta la API).
  - **Permisos:** con el rol configurado, al arrancar y después de las migraciones se le otorga lectura y escritura sobre las tablas, solo `SELECT` e `INSERT` sobre `audit_logs` y nada sobre `schemaversions`, y nunca `TRUNCATE`. Se aplica en cada arranque, así que cubre también las tablas nuevas.
  - **Validación:** la API no arranca si el nombre no es un identificador en minúsculas, si el rol no existe, si es superusuario, si es el mismo usuario dueño o si es dueño de `audit_logs` (directamente o como miembro del rol dueño). Con `Database__RuntimeRole` configurado y las migraciones apagadas, se registra un warning porque no se otorga nada.
  - **Aviso:** fuera de Development se registra un warning si la API se conecta como superusuario o como dueña de `audit_logs`.
  - **Pasos en Railway:** en `docs/instructions.md` (`## Least-privilege database role`).
- Freno de logins por cuenta (#118): `POST /api/auth/login` admite 20 intentos cada 15 minutos por email, sumando todas las aplicaciones e IPs.
  - **Al pasarse:** responde `429 RateLimit.Exceeded` con `Retry-After`, antes de verificar la contraseña, igual para emails que existen y que no, y registra `LOGIN_FAILED` con motivo `Throttled` (con el id del usuario si el email existe).
  - **Reinicio:** una contraseña correcta libera la cuenta.
  - **Alcance:** el contador vive en memoria y vale para una sola instancia, como los otros límites.
  - **Compromiso aceptado:** quien conozca un email puede bloquear su login hasta 15 minutos.
  - **Detalle técnico:** `Error` suma el tipo `TooManyRequests` y un `RetryAfter`, que `ToProblem` convierte en `429` con el header.

### Corregido

- Los cambios de aplicaciones no quedaban en la auditoría (#115). Ahora `POST /api/apps`, `PUT /api/apps/{appKey}`, `PATCH /api/apps/{appKey}/status` y `POST /api/apps/{appKey}/secret/rotate` registran `APPLICATION_CREATED`, `APPLICATION_UPDATED`, `APPLICATION_STATUS_CHANGED` y `APPLICATION_SECRET_ROTATED`.
  - **Contenido:** cada registro se graba en la misma transacción que el cambio, con el admin como actor, el `app_key` de la aplicación afectada y el antes y el después, sin el secreto ni su hash. Un `PUT` sin cambios no registra nada.
  - **Bloqueo:** esas escrituras leen la fila con `FOR UPDATE`, para que el "antes" sea correcto aunque lleguen dos requests a la vez.
- `TRUNCATE audit_logs` borraba todo el registro, porque el trigger solo bloqueaba `UPDATE` y `DELETE` (#116). La migración `0011_block_audit_logs_truncate.sql` agrega un trigger `BEFORE TRUNCATE` que lo rechaza para cualquier usuario. Un superusuario todavía puede borrar el trigger o desactivarlo; por eso #117 hace que la API corra con un rol sin esos privilegios.
- `SYSTEM_MAP.md` decía que la auditoría de templates tenía actor `Anonymous` y listaba `APPLICATION_*` como si se registrara (#119).

## 0.6.0 - 2026-11-24

### Agregado

- Documentación interactiva de la API y guía de integración (#31):
  - **`/openapi/v1.json`** (OpenAPI con `Microsoft.AspNetCore.OpenApi`) y **`/scalar`** (Scalar, con "Try it"), anónimos y activos por defecto. La variable nueva `OpenApi__Enabled=false` los apaga (`404`).
  - **Esquemas de seguridad:** `AppKey` + `AppSecret` (headers `X-App-Key` / `X-App-Secret`) y `Bearer` (JWT de admin). Cada operación pide el de su policy y documenta su `401` / `403` con el `errorCode`, el `429` si tiene rate limit y el `413` si limita el tamaño del body. Un test recorre todos los endpoints y verifica que el documento coincida con su policy.
  - **Respuestas:** los 51 endpoints declaran sus respuestas (`ProducesResponseType`) y listan sus `errorCode` en la descripción (`/// <response>`). El esquema `ProblemDetails` documenta `errorCode` y `correlationId`.
  - **Detrás del proxy de Railway**, la URL del servidor del documento sale `https`.
  - **`docs/integration-guide.md`:** registro de la app y su secret, headers M2M y `X-End-User-Ip`, envío de emails, login/refresh/logout desde el backend, flujos de cuenta y páginas que aloja la app, validación del JWT en un satélite .NET con `AddJwtBearer`, formato de errores y rate limits.
  - `SYSTEM_MAP.md` ahora lista los `409` que faltaban: `Applications.Reserved`, `Applications.AlreadyActive` / `AlreadySuspended`, `Authorization.Reserved` y `Authorization.AccessAlreadyGranted`.
- Consulta del registro de auditoría (#29): **`GET /api/audit-logs`**, solo para admin (`core.admin`).
  - **Filtros** opcionales, combinados con AND; los valores vacíos se ignoran:
    - `from` y `to`: inclusivos, en ISO 8601. Sin zona horaria se toman como UTC. Con solo la fecha (`yyyy-MM-dd`, día UTC), `from` empieza a las 00:00 y `to` cubre el día entero; un `+` del offset se manda como `%2B`. Si `from` es posterior a `to`, devuelve `400 AuditLogs.InvalidRange`, y una fecha ilegible `400 AuditLogs.InvalidDate`.
    - `actorType`: solo los nombres exactos (`User`, `Application`, `System`, `Anonymous`). Números, combinaciones u otras mayúsculas devuelven `400 AuditLogs.InvalidActorType`.
    - `appKey`, `actorId`, `action`, `entityType`, `entityId` y `correlationId`: coincidencia exacta. Un valor más largo que su columna o con caracteres de control devuelve `400 AuditLogs.InvalidFilter`.
  - **Paginado:** `page` desde 1 y `pageSize` de 1 a 200 (50 por defecto), siempre ordenado por `occurredAtUtc DESC, id DESC`. Responde `{ items, page, pageSize, totalCount }` con `Cache-Control: no-store`.
  - `oldValues` y `newValues` salen como objetos JSON, no como texto.
  - **Limitación conocida:** cada consulta hace un `count(*)` exacto y las páginas profundas usan `OFFSET`; con una tabla que solo crece, un conteo estimado o paginado por keyset queda como mejora a futuro.
  - **Migración `0010_audit_logs_query_indexes.sql`:** los índices por fecha y por `app_key` ahora incluyen `id`, y hay uno nuevo por `actor_id`. Así las consultas por fecha, aplicación y actor leen en el orden del índice, sin ordenar en memoria; tests con `EXPLAIN` sobre SQL equivalente lo verifican.
- Exportación del registro de auditoría (#30): **`GET /api/audit-logs/export?format=csv|json`**, solo para admin, con los mismos filtros que la consulta y sin paginado.
  - **Orden y corte:** del registro más viejo al más nuevo. Al empezar se fija un corte (el momento del request): lo que se registra después, incluida la auditoría de la propia exportación, no entra. Una escritura que ya estaba en curso al empezar (hora anterior al corte, pero confirmada después) puede aparecer en el archivo, así que en ese caso raro el archivo puede tener alguna fila más que el `rowCount` auditado (nunca más que el límite).
  - **CSV:** UTF-8 con BOM para Excel, RFC 4180 con fila de encabezado, y `oldValues`/`newValues` como texto JSON. Una celda que empieza con `=`, `+`, `-`, `@`, tab o CR lleva un `'` adelante, para que la planilla no la ejecute como fórmula (CSV injection).
  - **JSON:** un array con los mismos campos que la consulta; los snapshots salen como objetos JSON.
  - **Descarga:** `Content-Disposition: attachment; filename="audit-logs-AAAAMMDD-HHmmss.csv|json"` (hora UTC del corte) y `Cache-Control: no-store`.
  - **Streaming:** lee de a 1.000 filas con keyset sobre `(occurred_at_utc, id)` y escribe la respuesta a medida que avanza, sin juntar todo el resultado en memoria.
  - **Límite:** más de 100.000 filas devuelve `400 AuditLogs.ExportTooLarge` y no audita nada; hay que acotar los filtros. Un `format` distinto de `csv` o `json` (exacto) devuelve `400 AuditLogs.InvalidFormat`.
  - **Auditoría:** cada exportación registra `AUDIT_LOG_EXPORTED` con el formato, los filtros, el corte y la cantidad de filas, antes de empezar a mandar el archivo.
  - **Variables nuevas (opcionales):** `AuditLogs__Export__MaxRows` (100.000 por defecto, 1..100.000) y `AuditLogs__Export__ChunkSize` (1.000 por defecto, 1..10.000).

### Corregido

- Un body más grande que el límite del endpoint (64 KB en `/api/auth/*`; 8 MB en `POST /api/notifications/email` y en la administración de templates) devolvía `500` y quedaba registrado como error, porque `GlobalExceptionHandler` convertía en `500` la excepción de Kestrel. Ahora devuelve `413` con `errorCode` `Request.TooLarge`, y cualquier otro request ilegible su `4xx` con `Request.Invalid`; los dos se registran como warning. Lo encontró la review del PR de #31, que es el que publica ese `413` en el contrato.

## 0.5.0 - 2026-11-23

### Agregado

- Registro propio y verificación de email (#26). Los llama el backend satélite con sus credenciales M2M:
  - **`POST /api/auth/register`:**
    - solo para aplicaciones con `AllowSelfRegistration`; si no, `403 Auth.SelfRegistrationDisabled`, y `core` nunca lo tiene;
    - crea el usuario sin verificar, con acceso a la aplicación con su rol por defecto (o sin roles si no tiene uno);
    - encola la plantilla `verify-email` con un link válido por 24 h y audita `USER_REGISTERED` y `ACCESS_GRANTED`, todo en una transacción;
    - un email repetido, en cualquier aplicación y sin importar mayúsculas, devuelve `409 Auth.EmailAlreadyRegistered`, también con requests simultáneos.
  - **`POST /api/auth/verify-email`:** consume el token, verifica el email y audita `EMAIL_VERIFIED`. Si el usuario está activo, encola `welcome-user`; uno bloqueado queda verificado pero no recibe la bienvenida.
    - Un token usado, vencido, revocado, de otra aplicación o de otro propósito devuelve `400 Auth.InvalidOneTimeToken`. Es un código nuevo, distinto del `401 Auth.InvalidToken` de admin.
    - Dos usos simultáneos del mismo token dan un `204` y un `400`.
  - **`POST /api/auth/resend-verification`:** responde siempre `202` y solo envía si el usuario existe, está activo, no está verificado y tiene acceso a la aplicación.
    - Revoca los tokens de verificación anteriores del usuario, de cualquier aplicación.
    - Si el usuario recibió un token hace menos de 60 segundos, no manda nada, para que nadie pueda llenarle la casilla de mails.
  - **Orden de los locks:** todos los flujos de cuenta toman primero la fila del usuario y después la del token. Así `verify-email` y `resend-verification` simultáneos no se trancan entre sí; un test lo verifica de forma determinista.
  - **URLs de las páginas** (`verifyEmailUrl`): se validan antes de ponerlas en un email firmado por el Core. Tienen que ser `https` (`http` solo en localhost), sin usuario ni contraseña, sin fragmento, de hasta 2048 caracteres (también una vez escapada) y sin un parámetro `token` propio, separado con `&` o con `;`. Si no, devuelven `400 Auth.InvalidCallbackUrl`.
  - **Entidad `UserToken`** y migración `0009_create_user_tokens.sql`: tokens de un solo uso atados al usuario y a la aplicación; se guarda solo su SHA-256. Queda lista para el reset de contraseña de #27.
  - **Limitación conocida:** el cuerpo del email encolado, con el link y el token, queda en texto plano en `notifications` hasta que se limpie. Borrar esos cuerpos después del envío queda como mejora a futuro.
- Recuperación de contraseña (#27). La llama el backend satélite con sus credenciales M2M:
  - **`POST /api/auth/forgot-password`** (`{ email, resetPasswordUrl }`): responde siempre `202`, salvo `400 Auth.InvalidCallbackUrl` si la URL de la página es inválida (mismas reglas que `verifyEmailUrl`).
    - Solo envía si el usuario existe, está activo y tiene acceso a la aplicación. Si recibió un token de reset hace menos de 60 segundos, no manda nada: así nadie le llena la casilla ni le invalida el link pidiendo uno nuevo cada vez.
    - Revoca los tokens de reset anteriores del usuario, de cualquier aplicación, emite uno nuevo válido por 30 minutos, encola la plantilla `reset-password` y audita `PASSWORD_RESET_REQUESTED`.
  - **`POST /api/auth/reset-password`** (`{ token, newPassword }`): responde `204`.
    - Guarda la contraseña nueva, regenera el security stamp, consume el token y revoca todas las sesiones del usuario en todas las aplicaciones (`PasswordReset`). Audita `PASSWORD_RESET` y, si había sesiones activas, `SESSIONS_REVOKED`.
    - Una contraseña débil devuelve `400 Users.WeakPassword` sin consumir el token. Un token usado, vencido, revocado, de otra aplicación o de verificación, o de un usuario bloqueado o sin acceso a la aplicación, devuelve `400 Auth.InvalidOneTimeToken` (el issue pedía `Auth.InvalidToken`, que es el `401` de admin).
    - No verifica el email: un usuario sin verificar igual necesita `verify-email` para hacer login.
    - Toma los locks en el mismo orden que el login: el advisory lock de refresh tokens, la fila del usuario y la del token. Dos usos simultáneos del mismo token dan un `204` y un `400`.
  - **Cambio de email por admin** (`PUT /api/users/{id}`): revoca los tokens de un solo uso abiertos del usuario (verificación y reset), así un link enviado a la dirección anterior deja de servir.
  - **Limitación conocida:** `forgot-password` y `resend-verification` siempre responden `202`, pero con una cuenta existente tardan más, así que el tiempo de respuesta puede revelar si un email está registrado.
- Rate limiting en los endpoints de auth y auditoría de logins fallidos (#28):
  - **Header `X-End-User-Ip`** (opcional): el backend satélite manda la IP de su usuario final. Se usa solo en requests autenticados con credenciales M2M (no en los de admin con Bearer) y si es una IP válida; si no, se usa la IP de la conexión. En producción el satélite **tiene** que mandarlo: sin él, todos sus usuarios comparten un mismo cupo.
  - **Límites** (en memoria, ventana fija de 1 minuto, por `AppKey` + IP del usuario final; una IPv6 cuenta como su red /64):
    - 10 requests entre `login`, `register`, `forgot-password` y `resend-verification`;
    - 60 requests de `refresh`.
  - **Al pasarse:** `429` como `ProblemDetails` con `errorCode` `RateLimit.Exceeded` y el header `Retry-After`.
  - **`LOGIN_FAILED`:** cada login fallido queda auditado en su propia transacción, con el email normalizado (vacío si no es un email válido: nunca se guarda lo que se tipeó), el motivo (`UnknownEmail`, `InvalidPassword`, `EmailNotVerified`, `UserLocked` o `NoAccessToApplication`) y la IP del usuario final. Nunca guarda la contraseña.
  - **Auditoría:** `audit_logs.ip_address` ahora guarda la IP del usuario final cuando el satélite la manda.

### Seguridad

- Logs sin entradas falsificadas (alertas `cs/log-forging` de CodeQL):
  - `GlobalExceptionHandler` ya no escribe el método ni el path del request en el log de la excepción. Los tiene la línea de fin del request, con el mismo `CorrelationId`.
  - Fuera de `Development`, la consola escribe una línea JSON por evento (`time`, `level`, `message`, `exception` y cada propiedad como atributo), el formato de logs estructurados de Railway. Los saltos de línea quedan escapados, así que un path con `%0A` ya no puede agregar una línea de log falsa, y un stack trace es una sola entrada. En `Development` sigue el formato de texto.
  - El sink de consola pasó de `appsettings.json` al código (`Common/Logging/ConsoleLogging`).

### Documentación

- `SECURITY.md`: política de seguridad. Indica que solo el último release recibe fixes y que una vulnerabilidad se reporta en privado con el reporte privado de GitHub (pestaña Security → Report a vulnerability), nunca en un issue público. También fija los plazos de respuesta (5 días hábiles para el acuse de recibo y 15 para la evaluación) y el alcance.

## 0.4.0 - 2026-11-23

### Agregado

- Autenticación de los endpoints de admin con JWT (#53):
  - **Rutas protegidas:** `/api/apps` (salvo `GET /api/apps/me`), permisos, roles y accesos por aplicación, `/api/users` (con las sesiones) y `/api/notifications/templates`. Exigen `Authorization: Bearer` con un access token de la aplicación reservada `core` (audiencia `core`, `typ: at+jwt`) que tenga el permiso `core.admin`.
  - **Respuestas de error:**
    - sin token, o con uno inválido, vencido, de otra audiencia o de otro tipo: `401 Auth.InvalidToken`;
    - con un token válido sin `core.admin`: `403 Auth.AdminRequired`;
    - las credenciales M2M no sirven en estas rutas.
  - **Red de seguridad:** un test recorre todos los endpoints y falla si alguno no declara su acceso.
  - **Auditoría:** las escrituras de admin registran como actor al usuario del token y como `app_key` a `core`.
  - **Migración `0008_seed_core_admin.sql`:** crea la aplicación `core`, el permiso `core.admin` y el rol `admin`, con ids fijos.
  - **Datos reservados:** `core` no se puede suspender ni abrir al registro propio, el permiso y el rol no se pueden borrar, y el rol no puede perder `core.admin`. Esos intentos devuelven `409 Applications.Reserved` / `Authorization.Reserved`.
  - **Bootstrap del primer admin** (`Bootstrap__AdminEmail`, `Bootstrap__AdminPassword`, `Bootstrap__AdminFullName` y `Bootstrap__CoreClientSecret`), al arrancar:
    - fija el secreto de `core`, que si no nadie conocería;
    - si ningún admin puede hacer login (activo y con el email verificado), crea el admin con el email verificado o le da acceso a un usuario existente, siempre con un advisory lock y auditado como `System`. Un usuario existente verificado conserva su contraseña. Uno sin verificar recibe primero la contraseña del bootstrap, así quien registró ese email no puede quedarse con la cuenta de admin;
    - con una configuración inválida, la API no arranca.
  - **Paquete:** `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.15.
- Claves de firma JWT (RS256) desde variables de entorno (`Modules/Auth/`, #22): sección `Jwt` con `Issuer`, `SigningKey:KeyId`, `SigningKey:PrivateKeyPem` (PEM PKCS#8 o PKCS#1, mínimo 2048 bits, se recomiendan 3072), `PreviousPublicKeys[]` para seguir validando tokens durante una rotación, `AccessTokenMinutes` (15, de 1 a 60) y `RefreshTokenDays` (30, de 1 a 365). Se valida al arrancar: fuera de Development la API no arranca sin clave, con un PEM que no se puede importar o de otro tipo (una clave pública como clave de firma, una privada como clave anterior), con una clave chica, con `kid` mal formados o repetidos, con un issuer que no es una URL `https` (se admite `http` solo en loopback) o que tiene ruta (las rutas `/.well-known` están en la raíz), o con duraciones fuera de rango; los mensajes nunca incluyen la clave. En Development sin clave se genera una clave efímera de 3072 bits y se loguea un warning (los tokens mueren al reiniciar).
- `GET /.well-known/jwks.json` (anónimo): publica las claves públicas (la actual primero y después las anteriores) con su `kid`, solo `n` y `e`, con `Cache-Control: public, max-age=3600`. Un token firmado con la clave actual valida contra el JWK publicado y uno firmado con otra clave no.
- `GET /.well-known/openid-configuration` (anónimo): devuelve solo `issuer` y `jwks_uri`, para que las aplicaciones satélite usen `AddJwtBearer(o => o.Authority = ...)` sin código propio (un test recorre el mismo descubrimiento que usa JwtBearer y valida un token del Core con las claves que encuentra).
- Logout y revocación de sesiones (#25):
  - **`POST /api/auth/logout` (M2M):** revoca toda la familia del refresh token como `Logout` y audita `LOGOUT`.
    - Responde `204` para cualquier token: uno vacío, desconocido, de otra aplicación, ya revocado o de una familia sin tokens activos no cambia nada, y un segundo logout no audita.
    - Un token ya rotado cuenta como reuso, igual que en el refresh: la familia se revoca como `ReuseDetected` y se audita `REFRESH_TOKEN_REUSE_DETECTED`.
    - Las demás sesiones del usuario siguen activas.
  - **`GET /api/users/{id}/sessions`:** lista las sesiones activas con la aplicación, el inicio, la última rotación, el vencimiento, la IP y el user agent.
  - **`DELETE /api/users/{id}/sessions?appKey=`:** revoca todas las sesiones, o las de una aplicación, como `AdminRevoked`, audita `SESSIONS_REVOKED` y responde `200 { revokedSessions }`. Un `appKey` presente pero vacío devuelve `404` y nunca revoca todas.
  - Estos dos endpoints requieren el token de admin de #53.
  - **Bloqueo de un usuario:** ahora revoca sus sesiones como `UserLocked` en la misma transacción.
  - **Concurrencia:** todas las escrituras de refresh tokens usan el mismo lock por usuario. El bloqueo de un usuario lo toma antes que la fila del usuario, en el mismo orden que el login, así que no se traban entre sí. Un refresh y una revocación simultáneos no dejan ninguna sesión viva.
  - **Limitación conocida:** un access token ya emitido sigue siendo válido hasta que vence (como máximo 15 minutos).
- `POST /api/auth/login` y `POST /api/auth/refresh` (#24): los llama el backend satélite con sus credenciales M2M, y la aplicación sale de esas credenciales, nunca del body.
  - **Login:** devuelve un access token RS256 y un refresh token. El access token dura 15 minutos y lleva `typ: at+jwt` (RFC 9068), `kid`, `aud` y `client_id` = el `AppKey`, `sub`, `email`, `name`, `roles[]`, `permissions[]` y `jti`.
  - **Credenciales inválidas:** un email desconocido, mal formado o vacío y una contraseña incorrecta, vacía o ausente devuelven el mismo `401 Auth.InvalidCredentials`, siempre después de verificar exactamente un hash. Así el tiempo de respuesta no revela qué emails existen.
  - **Contraseña fuera del lock:** la contraseña se verifica antes de tomar el lock por usuario. Una ráfaga de contraseñas incorrectas no bloquea las sesiones del usuario ni agota las conexiones.
  - **Fila del usuario bloqueada:** el usuario se vuelve a leer con `FOR UPDATE`, así un bloqueo o un cambio de email hecho por un admin al mismo tiempo no se pisa.
  - **Estado de la cuenta:** el bloqueo, la falta de verificación del email y la falta de acceso a la aplicación devuelven 403, y solo se informan después de una contraseña correcta.
  - **Efectos del login:** registra el último login, actualiza un hash de contraseña desactualizado y audita `LOGIN_SUCCEEDED` en la misma transacción.
  - **Refresh:** rota el token y vuelve a leer los roles y permisos, así que un cambio de permisos se aplica en el siguiente refresh. Un token de otra aplicación responde como uno desconocido (`401 Auth.InvalidRefreshToken`).
  - **Reuso:** un token ya rotado revoca toda la familia y audita `REFRESH_TOKEN_REUSE_DETECTED`.
  - **Concurrencia:** las escrituras de tokens de un usuario se serializan con un advisory lock de PostgreSQL. Dos refresh simultáneos del mismo token dan un 200 y un reuso, y la familia queda revocada.
  - **Cache:** las respuestas llevan `Cache-Control: no-store`.
  - **Fuera de este cambio:** los logins fallidos todavía no se auditan y no hay rate limiting (#28).
- Entidad `RefreshToken` (`Modules/Auth/`, #23) con su mapping y la migración `0007_create_refresh_tokens.sql`:
  - El token es opaco y se guarda solo su SHA-256. El texto en claro (32 bytes aleatorios en base64url) se devuelve una sola vez, al emitirlo.
  - Los tokens se agrupan por `FamilyId`. Cada uso revoca el token como `Rotated` y emite un sucesor en la misma familia, que vence a partir de ese momento según la duración que pasa quien llama. La conexión con `Jwt:RefreshTokenDays` llega en #24. El vencimiento es deslizante y no hay tope absoluto por familia, por decisión aceptada.
  - `HashToken` devuelve `null` para un token vacío o ausente, así que buscarlo no da error.
  - Presentar un token ya rotado devuelve `Auth.RefreshTokenReused`, y quien llama revoca toda la familia como `ReuseDetected` (solo los tokens activos; los ya revocados conservan su motivo). Un token revocado por otro motivo devuelve `Auth.RefreshTokenRevoked` y uno vencido, `Auth.RefreshTokenExpired`; ningún error cambia el estado.
  - Motivos de revocación: `Rotated`, `Logout`, `ReuseDetected`, `PasswordReset`, `UserLocked` y `AdminRevoked`.
  - La IP y el user agent se normalizan: la IP se descarta si es demasiado larga o tiene caracteres de control, y el user agent pierde los `NUL` y se trunca a 512 sin partir un carácter.
  - La columna `version` (concurrencia optimista) impide que dos rotaciones simultáneas del mismo token confirmen las dos: la segunda falla, así que un token nunca tiene dos sucesores. No ordena una rotación frente a una revocación de familia o de usuario en otra transacción. Por eso, los servicios de #24 y #25 tienen que serializar las escrituras de tokens de cada usuario. Esta regla está documentada en `SYSTEM_MAP.md`.
  - La tabla `refresh_tokens` tiene:
    - FKs a `users`, a `applications` y a sí misma (`replaced_by_token_id`, diferida hasta el commit);
    - el índice único `ux_refresh_tokens_token_hash`, un índice por familia y un índice parcial de los tokens activos por usuario y aplicación;
    - `CHECK` sobre el motivo, la coherencia de la revocación, el reemplazo (que existe solo y siempre con `Rotated`) y el vencimiento.
  - La limpieza de tokens vencidos queda para más adelante y tiene que borrar familias completas.
- Paquete `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 (y `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.23.0 solo en los tests).

### Corregido

- La línea de log de cada request (`UseSerilogRequestLogging`) se escribe en el logger de Serilog del host, el del contenedor de DI, y no en el `Log.Logger` estático. `AddSerilog` usa `preserveStaticLogger: true`.
  - **Antes:** el estático era global al proceso, lo pisaba cada host que arrancaba, y `Program.cs` lo cerraba al terminar. Con varios hosts en un mismo proceso, como en los tests de integración, el log de un request podía ir a otro host o a un logger ya cerrado. Por eso `GetMe_WithValidCredentials_AddsAppKeyToRequestCompletionLog` fallaba de forma intermitente.
  - **En producción:** hay un solo host y el comportamiento no cambia.
  - **Test de regresión:** `RequestCompletionLog_GoesToTheHostLoggerEvenWhenTheStaticLoggerIsReplaced`.

### Cambiado

- El contenedor local (`docker compose --profile container` y `docker run --env-file`) sigue corriendo como `Production`, así que necesita una clave: `docker-compose.yml` la toma de la variable `JWT_SIGNING_PRIVATE_KEY_PEM` y `.env.example` suma las variables `Jwt__*`. El `README.md` explica cómo generar una clave local en una línea.
- `.gitignore` ignora `*.pem` y `*.key`.

### Documentación

- `docs/instructions.md`: sección "First admin" (variables `Bootstrap__*`, login del admin y por qué hay que sacar las variables después). Se reemplaza la advertencia de mantener el servicio privado: queda solo para versiones anteriores a v0.4.0.
- `docs/instructions.md`: variables `Jwt__*` para Railway; el issuer es el dominio público del servicio, que se genera recién con v0.4.0 (incluye #53). También explica cómo generar la clave de producción con OpenSSL, la rotación manual con `PreviousPublicKeys` y qué hacer si la clave se filtra. Las aplicaciones con JwtBearer refrescan las claves cada 12 horas o ante un `kid` desconocido; por eso, ante una filtración, hay que reiniciarlas.

### Infraestructura

- CI endurecido (`.github/workflows/ci.yml`), con jobs en paralelo y acciones fijadas por SHA:
  - **Reglas del repositorio en cada PR:**
    - falla si se edita, borra o renombra una migración que ya existe en la rama base;
    - falla si el PR no toca `CHANGELOG.md`, salvo los PRs de Dependabot.
  - **Reglas de release, solo en PRs a `main` (producción):**
    - el origen tiene que ser `develop`;
    - si cambia `<Version>`, tiene que subir;
    - `CHANGELOG.md` tiene que tener la sección `## X.Y.Z - AAAA-MM-DD` y una `## Próxima versión` vacía;
    - el tag no puede existir.
  - **Paquetes NuGet con vulnerabilidades conocidas:** bloquean hacia `main` y solo avisan en `develop`.
  - **Escaneo de la imagen Docker con Trivy** (HIGH/CRITICAL con fix disponible): bloquea hacia `main` y solo avisa en `develop`.
  - **Resultados de tests:** se generan en TRX y, si fallan, se suben como artifact junto con los dumps de cuelgues.
  - **Push a `develop`:** el CI también corre en cada push, no solo en `main`.
- CodeQL (`.github/workflows/codeql.yml`): análisis `security-extended` de C# en cada PR, en cada push a `main` y `develop`, y todos los lunes.
- `docs/branch-protection.md`: las reglas de protección de `main` y `develop` (checks requeridos, sin push directo, CodeQL bloqueante) y los comandos `gh` para aplicarlas. Son configuración del repositorio y las aplica un admin.
- Protección de ramas aplicada (2026-11-23):
  - `main` exige un PR y los checks `repository-rules`, `release-rules`, `build-and-test`, `docker` y `analyze`. Prohíbe el push directo, el force push y el borrado, también para el admin.
  - `develop` exige un PR y los checks `repository-rules`, `build-and-test` y `docker`.
  - Ninguna de las dos exige la rama actualizada, porque `main` acumula un merge por release.
  - Como `develop` ya no acepta push directo, el commit de release pasa por un PR `chore/release-vX.Y.Z`.
  - El bloqueo por alertas de CodeQL queda pendiente en la web.

## 0.3.0 - 2026-11-19

### Agregado

- Endpoints de administración de permisos, roles y accesos por aplicación bajo `/api/apps/{appKey}` (públicos hasta #53): `permissions` (listar, ver, crear, cambiar la descripción y borrar), `roles` (listar, ver, crear con permisos e `isDefault`, editar nombre, descripción e `isDefault`, reemplazar permisos y borrar) y `users` (listar paginado los usuarios con acceso y sus roles, asignar roles —otorga el acceso si no lo tenía— y revocar). Claves de permiso o roles desconocidos o de otra aplicación devuelven `400` listándolos; nombres de rol repetidos sin distinguir mayúsculas y claves repetidas devuelven `409`. Toda escritura bloquea la fila de la aplicación, así que las escrituras concurrentes de una aplicación se serializan (por ejemplo, queda un solo rol por defecto). Cada cambio se audita con el `app_key` de la aplicación; al borrar un permiso o un rol se audita también cada rol o acceso del que se quitó, y las escrituras sin cambios no generan fila de auditoría. Se agregan `GET` de un permiso y de un rol (destino del `Location` de los `POST`) y `PUT` de la descripción de un permiso, que no estaban en el plan de #21.
- Endpoints de administración de usuarios (`/api/users`, públicos hasta #53): alta (`POST`, el usuario nace con el email verificado), listado paginado con búsqueda por email o nombre sin distinguir mayúsculas y filtro por estado (`GET`), detalle con las aplicaciones a las que accede y sus roles (`GET /{id}`), edición de nombre y email (`PUT /{id}`, un email distinto borra la verificación) y bloqueo/desbloqueo (`POST /{id}/lock` y `/unlock`). No hay `DELETE`: los usuarios se bloquean, no se borran. Cada cambio se audita en la misma transacción (`USER_CREATED`, `USER_UPDATED`, `USER_EMAIL_CHANGED`, `USER_LOCKED`, `USER_UNLOCKED`) y una edición sin cambios no genera fila de auditoría. Un email repetido devuelve `409 Users.EmailTaken`, también con requests simultáneos. Las respuestas nunca incluyen el hash de la contraseña ni el `SecurityStamp`.
- Entidad global `User` (`Modules/Users/`) con su mapping NHibernate y la migración `0005_create_users.sql` (tabla `users`, índice único `ux_users_email`, `CHECK` de `status` y `ck_users_lock_state`, que exige `locked_at_utc` solo cuando el usuario está `Locked`). El email se guarda sin espacios y en minúsculas, así que un email repetido con otras mayúsculas lo rechaza el índice único. Nombre completo de 1 a 150 caracteres en una sola línea. Métodos de dominio `Create`, `SetPassword`, `VerifyPassword`, `UpdateProfile`, `ChangeEmail` (un email distinto borra la verificación), `MarkEmailVerified`, `Lock`, `Unlock` y `RegisterLogin`, con errores `Users.*`.
- Hash de contraseñas con `PasswordHasher<User>` de ASP.NET Core Identity (PBKDF2-HMAC-SHA512, formato versionado), registrado como `IPasswordHasher<User>` singleton. Si al verificar el hash usa parámetros viejos, se reemplaza por uno nuevo de la misma contraseña.
- `PasswordPolicy` (NIST SP 800-63B): de 10 a 128 caracteres contados como code points, no solo espacios, sin `NUL` y sin reglas de composición. Error `Users.WeakPassword`.
- `SecurityStamp` en `User`: se regenera al cambiar la contraseña, al cambiar el email por uno distinto, al bloquear y al desbloquear, para poder invalidar los tokens emitidos antes.
- Modelo de autorización por aplicación (`Modules/Authorization/`): `Permission` (clave como `orders.read`, única por aplicación), `Role` (nombre único por aplicación sin distinguir mayúsculas, permisos de su misma aplicación, a lo sumo un rol por defecto por aplicación) y `UserApplicationAccess` (un acceso por usuario y aplicación, con roles de esa misma aplicación). `GetPermissionKeys()` devuelve la unión ordenada y sin repetidos de los permisos de los roles del acceso. Las asignaciones entre aplicaciones distintas se rechazan con `Authorization.CrossApplicationPermission` y `Authorization.CrossApplicationRole`.
- Migración `0006_create_authorization.sql`: tablas `permissions`, `roles`, `role_permissions`, `user_application_accesses` y `user_application_access_roles`, con FKs a `applications` y `users`, índices únicos `ux_permissions_application_id_key`, `ux_roles_application_id_name` (sobre `lower(name)`), `ux_roles_application_id_default` (parcial, `WHERE is_default`) y `ux_user_application_accesses_user_id_application_id`. Borrar un rol lo saca de todos los accesos, borrar un permiso lo saca de todos los roles y revocar un acceso borra sus vínculos (`ON DELETE CASCADE` en las tablas intermedias).
- Auditoría de identidad: nuevas acciones `USER_EMAIL_CHANGED`, `USER_LOCKED`, `USER_UNLOCKED`, `ROLE_DELETED`, `ROLE_PERMISSIONS_CHANGED`, `PERMISSION_CREATED`, `PERMISSION_UPDATED`, `PERMISSION_DELETED`, `ACCESS_GRANTED`, `ACCESS_ROLES_CHANGED` y `ACCESS_REVOKED`; snapshots `UserAuditSnapshot` (nunca incluye el hash de la contraseña, el `SecurityStamp` ni el último login), `RoleAuditSnapshot`, `PermissionAuditSnapshot` y `AccessAuditSnapshot`; y helpers `RecordUserChangeAsync`, `RecordRoleChangeAsync`, `RecordPermissionChangeAsync` y `RecordAccessChangeAsync` que completan `EntityType`, `EntityId` y los valores anteriores y nuevos dentro de la transacción de quien llama. Los cambios de roles, permisos y accesos se auditan con el `app_key` de la aplicación afectada.

### Cambiado

- `AuditActions`: se reemplazan `USER_LOCK_CHANGED` y `PERMISSION_ASSIGNED` (sin uso) por `USER_LOCKED` / `USER_UNLOCKED` y `ROLE_PERMISSIONS_CHANGED`.
- `TextRules` pasa de `Modules/Notifications/` a `Common/Domain/` para reusar la validación de email y de texto de una línea.

### Documentación

- `docs/instructions.md`: instrucciones de despliegue en Railway con cada variable de entorno que usa la API (las que hay que cargar, las que no hay que tocar y las opcionales con su valor por defecto) y la lista de verificación del primer deploy. Enlazado desde `README.md` y `SYSTEM_MAP.md`.

## 0.2.0 - 2026-11-19

### Agregado

- Entidades `EmailTemplate` y `Notification` (`Modules/Notifications/`) con sus mappings NHibernate y la migración `0003_create_notifications_and_email_templates.sql` (tablas `email_templates` y `notifications`, índice único `ux_email_templates_key` e índices `ix_notifications_status_next_attempt_at_utc` e `ix_notifications_app_key_created_at_utc`). Los cuerpos `html_body` y `text_body` son `TEXT`.
- Máquina de estados de `Notification` (`Pending` → `Processing` → `Sent` | `Failed`) con reintentos. El intento se cuenta al reclamar (`MarkProcessing` incrementa `Attempts` y se rechaza con `Notifications.InvalidTransition` si `Attempts >= MaxAttempts`, por lo que nunca hay un 4.º intento). Tras el 1.º fallo vuelve a `Pending` con espera de 1 minuto, tras el 2.º con 5 minutos, y tras el 3.º pasa a `Failed`. `RecoverStuck` libera una notificación trabada en `Processing` (por ejemplo tras la caída de un worker) sin tocar `Attempts`: pasa a `Failed` con `LastError` = `Processing timed out` si ya no quedan intentos, o vuelve a `Pending` lista para reclamarse. Con tres reclamos seguidos de una caída la notificación termina `Failed`. Desde `Sent` y `Failed` no hay transiciones; una transición inválida devuelve el error `Conflict` `Notifications.InvalidTransition` sin modificar el estado. `LastError` se conserva como historial tras un envío exitoso.
- Concurrencia optimista en `Notification` (columna `version`, `Version` en el mapping): dos sesiones que reclaman la misma notificación no pueden ambas confirmar el cambio (la segunda recibe `StaleObjectStateException`). El SQL directo que modifique una notificación debe reflejar los métodos de dominio y siempre setear `version = version + 1` y `updated_at_utc`. Reclamo: `attempts = attempts + 1`, `status = 'Processing'`, `processing_started_at_utc`, solo para filas con `attempts < max_attempts`. Recuperación: no toca `attempts`, `status = CASE WHEN attempts >= max_attempts THEN 'Failed' ELSE 'Pending' END` y `processing_started_at_utc = NULL`.
- `TemplateRenderer` (`ITemplateRenderer`, singleton) que reemplaza placeholders `{{Key}}` (se admiten espacios `{{ Key }}`, claves `[A-Za-z0-9_.]+`, búsqueda sin distinguir mayúsculas) en una sola pasada: los valores se codifican como HTML solo en `HtmlBody` (seguro en texto de elementos y atributos entre comillas; las URL en `href`/`src` las valida quien llama y los valores no deben venir pre-codificados). En el asunto se reemplazan por espacios los caracteres de control y U+2028/U+2029 solo de los *valores* insertados; el texto literal de la sobrecarga con partes crudas no se sanea y lo valida `Notification.Queue`. Las variables faltantes (o `null`) devuelven `Templates.MissingVariables` listándolas todas; las claves duplicadas sin distinguir mayúsculas devuelven `Templates.DuplicateVariables` (spellings ordenados, hasta 10, 100 caracteres cada uno); las claves vacías, con espacios o fuera de `[A-Za-z0-9_.]+` se ignoran como las sobrantes; la sintaxis inválida queda literal. Cada parte renderizada tiene un tope (asunto 500, cuerpos 512 K caracteres) y lo excedido devuelve `Templates.RenderedContentTooLarge`; el render se corta apenas se excede el tope. Un asunto o cuerpo `null` en la sobrecarga con partes crudas lanza `ArgumentNullException`. Incluye `GetPlaceholders`.
- Validación de entradas de `Notification` y `EmailTemplate`: se rechaza `NUL` en todos los textos (cuerpos incluidos) y cualquier carácter de control o separador U+2028/U+2029 en los campos de una sola línea; las transiciones exigen `DateTime` con `Kind` UTC (`ArgumentException` en caso contrario); `LastError` se trunca a 2000 sin dejar un surrogate alto huérfano.
- Migración `0004_seed_default_email_templates.sql` con las plantillas de sistema por defecto (`welcome-user`, `verify-email`, `reset-password` y `delivery-failed-alert`) en español, HTML simple responsive con CSS en línea más cuerpo de texto. DbUp la aplica una sola vez (journal `schemaversions`) y, además, `INSERT ... ON CONFLICT (key) DO NOTHING` no pisa filas existentes, así que las ediciones posteriores se conservan; cambiar una plantilla por defecto requiere una migración nueva con `UPDATE`. Variables documentadas en `SYSTEM_MAP.md`.
- `IEmailSender` y `SmtpEmailSender` (`Modules/Notifications/Email/`, MailKit 5.0.0): arma un `MimeMessage` con HTML y alternativa de texto, abre una conexión por mensaje (conecta, autentica solo si hay `Username`, envía y desconecta, con `Timeout` y `CancellationToken`) y lanza la excepción si falla, para que el worker la registre. Registra `Email sent to {ToEmail}` sin asunto, cuerpo ni contraseña. `EmailMessage` rechaza destinatarios inválidos y saltos de línea o caracteres de control en el asunto (`ArgumentException`).
- `SmtpOptions` (sección `Smtp`: `Host`, `Port`, `SecureSocket` [`None` | `StartTls` | `SslOnConnect`], `Username`, `Password`, `FromEmail`, `FromName`, `TimeoutSeconds`) validada al arrancar (`ValidateOnStart`): la API no arranca sin una configuración `Smtp` válida (host y remitente obligatorios, puerto 1..65535, `SecureSocket` definido, `Username` y `Password` ambos o ninguno, sin credenciales con `None`). `appsettings.json` trae los valores de Gmail (`smtp.gmail.com`, 465, `SslOnConnect`, `href.notifier@gmail.com`) sin secretos; `appsettings.Development.json` y el servicio `api` de `docker-compose.yml` usan Mailpit. Pasos manuales en Railway: `Smtp__Username` y `Smtp__Password` (App Password de Gmail, solo en Railway). Límite de Gmail: unos 500 destinatarios por día. Railway solo permite SMTP saliente en el plan Pro o superior (redeploy tras el upgrade). `SecureSocket` acepta solo los nombres exactos (se valida el texto crudo de la configuración, así `0`, `1`, `None,StartTls` o `starttls` se rechazan) y un aviso al arrancar indica si faltan credenciales con conexión segura; `Email sent` se registra en Debug.
- Tests: validación de `SmtpOptions`, `EmailMessage` y tests de integración con un contenedor Mailpit (entrega con tildes y emoji, falla con servidor inalcanzable, la API no arranca sin `Smtp:Host`).
- Entrega asíncrona de notificaciones (patrón outbox): la API solo encola y `NotificationBackgroundWorker` (`BackgroundService`, `Modules/Notifications/Worker/`) entrega. En cada iteración, en un scope nuevo, `NotificationDispatcher` recupera las notificaciones trabadas y despacha un lote; si el lote vino lleno sigue de inmediato y si no espera `PollIntervalSeconds`. Si todas las notificaciones reclamadas tuvieron un error inesperado (no de envío), espera aunque el lote haya venido lleno (`DispatchResult`). Una falla de la recuperación se registra y no frena el despacho; un error en una iteración se registra y el loop continúa (el worker nunca baja la API).
- Reclamo de lotes con SQL nativo en su propia transacción, confirmada antes de enviar: CTE con `FOR UPDATE SKIP LOCKED` que refleja `MarkProcessing` (`attempts + 1`, `Processing`, `processing_started_at_utc`, `updated_at_utc`, `version + 1`), ordenado por `next_attempt_at_utc, id`, solo filas vencidas con `attempts < max_attempts`. Dos reclamos concurrentes nunca devuelven la misma fila. El reclamo devuelve `id` y `version`: antes de enviar se verifica que la fila siga en `Processing` con esa versión; si otra instancia la recuperó o la volvió a reclamar, se omite con un Warning (evita el doble envío).
- Un solo reloj: el reclamo, la recuperación y las transiciones usan el `TimeProvider` de DI como parámetro `timestamptz`; nunca `now()` de PostgreSQL.
- Envío y registro por notificación en su propia sesión: éxito → `Sent`; excepción → `RegisterFailure` (reintento a 1 y 5 minutos, `Failed` tras el 3.º intento). Un error en una notificación no frena al resto del lote. Si la recuperación tocó la fila mientras se enviaba, la concurrencia optimista (`version`) rechaza el registro y se loguea un Warning. El registro no usa el token de parada: si el host se detiene después de un envío exitoso, igual se registra `Sent`. Si el envío funcionó pero no se pudo registrar, se loguea un Error y cuenta como error inesperado.
- Nueva transición de dominio `Notification.ReleaseUnsent`: desde `Processing` devuelve la notificación a `Pending` (`NextAttemptAtUtc` = ahora) y descuenta el intento (`Attempts - 1`, nunca menos de 0), conservando `LastError`. El worker la usa para las notificaciones del lote que no llegaron a enviarse al detenerse el host y para los envíos cancelados por el token de parada: una parada nunca consume un intento, nunca deja una notificación en `Failed` ni genera alerta. Si el servidor ya había aceptado el mensaje cancelado, puede reenviarse (at-least-once).
- Alerta de entrega fallida: cuando una notificación termina `Failed` (por fallos o por recuperación) y tiene `NotifyOnFailureTo`, se encola en la misma transacción una notificación con la plantilla `delivery-failed-alert`, el mismo `AppKey` y `CorrelationId`, destinatario `NotifyOnFailureTo` y sin `NotifyOnFailureTo` (sin bucles). Si falta la plantilla o falla el render, se registra un Error y el `Failed` se confirma igual.
- Recuperación de notificaciones trabadas en `Processing` por más de `StuckAfterMinutes` (caída o redeploy): SQL que refleja `RecoverStuck` (vuelven a `Pending` con los mismos intentos, o a `Failed` con `Processing timed out` y alerta si no quedan intentos). Constante `Notification.ProcessingTimedOutError`.
- Opciones `Notifications:Worker` (`NotificationWorkerOptions`: `Enabled` [true], `PollIntervalSeconds` [5, 1..3600], `BatchSize` [20, 1..500], `StuckAfterMinutes` [10, 1..1440]) validadas al arrancar. Variables en Railway (opcionales): `Notifications__Worker__Enabled`, `__PollIntervalSeconds`, `__BatchSize`, `__StuckAfterMinutes`. `NotificationWorkerStartupCheck` registra un Warning al arrancar si `StuckAfterMinutes * 60 < BatchSize * Smtp:TimeoutSeconds`; los valores por defecto (10 min, 20, 30 s) quedan justo en el límite y no lo registran.
- Logs del procesamiento de cada notificación con `NotificationId`, `AppKey` y `CorrelationId` en el `LogContext` de Serilog. Nunca se loguean los cuerpos. El dispatcher no loguea asuntos ni direcciones, pero `SmtpEmailSender` registra el destinatario en Debug (`Email sent to {ToEmail}`) y los mensajes de error SMTP, que se loguean tal cual, pueden contener direcciones.
- Entrega al menos una vez (at-least-once): un correo puede enviarse dos veces si el envío funcionó pero no se pudo registrar el resultado, o si un envío cancelado por una parada ya había sido aceptado por el servidor; documentado en `SYSTEM_MAP.md` (`## Background processing`).
- Tests: integración del dispatcher contra PostgreSQL con un `IEmailSender` falso (éxito, reintentos con backoff, 3 fallos con y sin alerta, alerta que también falla, reclamos concurrentes, tamaño de lote, recuperación, concurrencia optimista, verificación de versión, fila borrada tras el reclamo, envío no registrado, parada del host también en el último intento, propiedades del `LogContext`), el SQL de producción sin filtro en una base propia creada por test, el worker dentro de la API completa, entrega punta a punta contra Mailpit, `ReleaseUnsent`, el loop del worker con `FakeTimeProvider`, el aviso de arranque y la validación de `NotificationWorkerOptions`. `ApiFactory` deshabilita el worker por defecto.
- API de notificaciones para aplicaciones cliente (`Modules/Notifications/NotificationsController`, `NotificationService`, todas con la policy `Application`; el `AppKey` sale siempre de las credenciales, nunca del body). `POST /api/notifications/email` encola un email en modo plantilla (`templateKey` exacto y sensible a mayúsculas + `variables`) o en modo HTML (`html` + `subject`, `text` opcional); mezclar modos, no indicar ninguno, `html` sin `subject` o `variables` con datos en modo HTML devuelve `400 Notifications.InvalidContent`. Plantilla inexistente: `404 Templates.NotFound`; errores del renderer (`Templates.MissingVariables`, `Templates.DuplicateVariables`, `Templates.RenderedContentTooLarge`) y de `Notification.Queue` (`InvalidRecipient`, `InvalidSubject`, `InvalidNotifyOnFailureTo`, ...) pasan como `400`. Responde `202` con `Location: /api/notifications/{id}` y `{ id, status: "Pending" }`; el `CorrelationId` de la notificación es el `X-Correlation-Id` del request. `html` y `text` admiten hasta 512 KB medidos en bytes UTF-8 (`400 Notifications.ContentTooLarge`, solo para HTML externo: la ruta de plantilla se limita por caracteres renderizados, `Templates.RenderedContentTooLarge`) y el request hasta 8 MB (`[RequestSizeLimit]`, `413` en Kestrel; alcanza para dos cuerpos de 512 KB escapados como secuencias de 6 bytes).
- `GET /api/notifications` lista solo las notificaciones de la aplicación, de la más nueva a la más vieja (`created_at_utc DESC, id DESC`), con `status` (nombres exactos: `Pending`, `Processing`, `Sent`, `Failed`; cualquier otro valor, incluidos números, minúsculas y combinaciones, devuelve `400 Notifications.InvalidStatus`), `from` (inclusive) y `to` (exclusivo) sobre `CreatedAtUtc` (`from > to` devuelve `400 Notifications.InvalidDateRange`), `page` (por defecto 1, menor a 1 devuelve `400 Notifications.InvalidPage`) y `pageSize` (por defecto 50, de 1 a 200, fuera de rango devuelve `400 Notifications.InvalidPageSize` sin recortar). Responde `{ items, page, pageSize, totalCount }`; los items y `GET /api/notifications/{id}` usan una proyección que nunca carga `html_body` ni `text_body`. El id de otra aplicación o inexistente devuelve el mismo `404 Notifications.NotFound`; un id que no es GUID devuelve `404`.
- `NOTIFICATION_QUEUED` se registra con `IAuditService` en la misma transacción que la notificación (si la auditoría falla, no queda la notificación). Si la sesión ya tiene una transacción activa (un llamador que compone varios cambios), `NotificationService` se une a ella y el llamador confirma o revierte; si no, abre y confirma la suya. El `NewValues` es un snapshot con `id`, `toEmail`, `templateKey`, `subject` (solo en modo HTML; `null` en modo plantilla porque un asunto renderizado puede contener valores de variables), `status`, `notifyOnFailureTo` y `correlationId`; nunca los cuerpos ni las `variables` (pueden contener tokens).
- `Notification.Queue` valida un tope de cuerpos (`Notification.BodyMaxLength` = 512 K caracteres para `HtmlBody` y `TextBody`, `Notifications.ContentTooLarge`) como defensa en profundidad.
- Documentado en `SYSTEM_MAP.md`: `lastError` es el texto crudo del error SMTP o de transporte y puede describir la infraestructura de correo; `from`/`to` requieren offset o `Z` (`+` codificado como `%2B`; sin offset se interpreta en hora local del servidor, UTC en Railway); los valores no parseables devuelven el `ValidationProblemDetails` del framework sin `errorCode`; `?status=` vacío equivale a sin filtro.
- Tests: integración de los tres endpoints contra PostgreSQL (modo plantilla y HTML, combinaciones inválidas, variables faltantes y duplicadas, plantilla inexistente, destinatario y campos con saltos de línea, límite de 512 KB en el borde y por bytes UTF-8, body de 6 MB escapado dentro del límite del request, auditoría sin cuerpos ni variables ni asunto de plantilla, atomicidad ante una falla de auditoría, unirse a la transacción activa del llamador con commit y rollback, aislamiento entre aplicaciones, paginación y orden estable, filtros de estado (incluido `status` vacío) y fechas, parámetros inválidos, 401 y 403) y unitarios del tope de cuerpos de `Notification.Queue`. El valor del `[RequestSizeLimit]` se verifica por reflexión (`IRequestSizeLimitMetadata`): `TestServer` no aplica el límite, así que el `413` no se probó de punta a punta.
- API de administración de plantillas de email en `/api/notifications/templates` (`EmailTemplatesController` + `EmailTemplateService`): `GET` (lista ordenada por `key`, sin cuerpos ni paginación), `GET /{key}` (plantilla completa con `placeholders` detectados), `POST` (crea; `201` + `Location`), `PUT /{key}` (reemplaza el contenido; la `key` no cambia y una `key` o `isSystem` en el body se ignoran), `DELETE /{key}` (`204`) y `POST /{key}/preview` (`{ variables }` → `{ subject, html, text }`, sin guardar nada ni auditar). Las claves se comparan de forma exacta (distingue mayúsculas): una clave que no es un slug en minúsculas o con otra capitalización devuelve `404 Templates.NotFound`.
- Protección de las plantillas de sistema: no se pueden borrar (`409 Templates.SystemTemplate`), los clientes no pueden crearlas (`isSystem` del body se ignora) y su actualización debe conservar todos los placeholders que usa el Core (`SystemTemplateRequirements`: `welcome-user`, `verify-email`, `reset-password` y `delivery-failed-alert`, contando asunto + HTML + texto en conjunto, sin distinguir mayúsculas) o devuelve `400 Templates.MissingRequiredPlaceholders`, y no puede agregar otros (`400 Templates.UnknownPlaceholders`, porque cada envío fallaría con `Templates.MissingVariables`). Un test verifica que ese mapa coincide con las plantillas sembradas por `0004`.
- Crear con una clave repetida devuelve `409 Templates.KeyTaken`, también ante requests concurrentes: el índice único `ux_email_templates_key` decide y los perdedores reciben `409`, nunca `500`. Nuevos errores de validación `Templates.ContentTooLarge` (HTML o texto de más de 524288 caracteres) y `EmailTemplate.IsValidKey`/`ValidateContent` en el dominio.
- Auditoría de cambios de plantillas: `TEMPLATE_CREATED` (valores nuevos), `TEMPLATE_UPDATED` (valores viejos y nuevos) y la nueva acción `TEMPLATE_DELETED` (valores viejos), con `EntityType = EmailTemplate`, `EntityId` = `Id` de la plantilla y un snapshot (`key`, `name`, `description`, `subject`, `htmlBody`, `textBody`, `isSystem`) en la misma transacción. El contenido de las plantillas no es secreto, por lo que se guarda completo; es el historial de versiones anteriores. Un cambio rechazado no registra nada.
- Las notificaciones ya encoladas conservan el texto de su `TemplateKey` aunque se borre la plantilla (no hay clave foránea).
- Advertencia: estos endpoints no tienen autenticación hasta #53 (v0.4.0), igual que `/api/apps`: cualquiera que pueda llegar a la API puede leer, reescribir y borrar plantillas (phishing desde el remitente del Core). El despliegue debe mantenerse privado hasta #53.
- `PUT` y `DELETE` leen la plantilla con `SELECT ... FOR UPDATE` dentro de la transacción: dos borrados simultáneos (o una edición y un borrado) dan un éxito y `404`, nunca `500`, y el `OldValues` de cada auditoría es el valor previo real. Un `PUT` sin cambios devuelve `200` y no registra auditoría. Una plantilla de sistema sin entrada en `SystemTemplateRequirements` conserva exactamente sus placeholders actuales; `delivery-failed-alert` debe renderizar con valores de peor caso o devuelve `400 Templates.SystemTemplateNotRenderable`. Las respuestas de POST y PUT traen marcas de tiempo con precisión menor al microsegundo, mientras que lo guardado (y los GET posteriores) se trunca a microsegundos.
- Tests: `EmailTemplatesEndpointIntegrationTests` (cada ruta, protecciones de plantillas de sistema, concurrencia, auditoría y consistencia con la migración `0004`) y pruebas de dominio de `EmailTemplate`.

### Cambiado

- CI: chequeo de reglas de código (sin comentarios en línea ni caracteres U+2028/U+2029), build con warnings como errores (`-warnaserror`), detección de tests colgados (`--blame-hang-timeout 5m`), timeout de 20 minutos y build de la imagen Docker que usa Railway.
- Los tests pasan `TestContext.Current.CancellationToken` a las llamadas asíncronas (warnings xUnit1051) y no usan el literal `null` en `TheoryData<string?>` (warnings CS8625).
- `AuditActions.NotificationEnqueued` (`NOTIFICATION_ENQUEUED`) pasa a llamarse `AuditActions.NotificationQueued` (`NOTIFICATION_QUEUED`). Aún no se registraba en producción.

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
