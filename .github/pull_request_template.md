<!--
Base: develop. A main solo llegan releases (develop → main). Los issues de una épica apuntan a su rama epic/<id>-<slug>.
Título: Conventional Commits, describe solo el cambio y nunca nombra ramas. Ej.: feat(api): add customer endpoint
Release con bump de versión: chore: release vX.Y.Z
-->

Cierra #

## Qué cambia y por qué

<!-- El problema que ve el usuario (o el riesgo) y cómo lo resuelve este PR. -->

## Cambios

- 

## Datos / base de datos

- [ ] Sin cambios en el esquema.
- [ ] Hay cambios de esquema, mappings de NHibernate o migraciones. Pasos manuales:

## Release

- [ ] No cambia la versión.
- [ ] Sube la versión a x.y.z (`src/Href.Core.Api/Href.Core.Api.csproj`, `CHANGELOG.md`).
- [ ] Hay cambios que ve el usuario.

## Verificación

- [ ] `dotnet build Href.Core.sln -c Release` sin errores.
- [ ] `dotnet test Href.Core.sln -c Release` en verde. Tests nuevos o modificados:
- [ ] Prueba manual contra la API (curl / cliente HTTP), si aplica.

Para probar:

1. 

## Antes de mergear

- [ ] Sin comentarios en línea.
- [ ] Sin secretos ni archivos locales (`.env`, `appsettings.*.local.json`, credenciales).
- [ ] Pasos manuales pendientes:
