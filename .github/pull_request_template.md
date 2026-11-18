<!--
Base: develop. Only releases target main (develop → main). Epic issues target their epic/<id>-<slug> branch.
Title: Conventional Commits, describes only the change and never names branches. E.g.: feat(api): add customer endpoint
Release with version bump: chore: release vX.Y.Z
-->

Closes #

## What changes and why

<!-- The user-visible problem (or risk) and how this PR resolves it. -->

## Changes

- 

## Data / database

- [ ] No schema changes.
- [ ] Includes schema changes, NHibernate mappings or migrations. Manual steps:

## Release

- [ ] Does not change the version.
- [ ] Bumps the version to x.y.z (`src/Href.Core.Api/Href.Core.Api.csproj`, `CHANGELOG.md`).
- [ ] Includes user-facing changes.

## Verification

- [ ] `dotnet build Href.Core.sln -c Release` succeeds without errors.
- [ ] `dotnet test Href.Core.sln -c Release` passes. New or modified tests:
- [ ] Manual check against the API (curl / HTTP client), if applicable.

To test:

1. 

## Before merging

- [ ] No inline comments.
- [ ] No secrets or local files (`.env`, `appsettings.*.local.json`, credentials).
- [ ] Pending manual steps:
