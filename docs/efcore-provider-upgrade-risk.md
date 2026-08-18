# EF Core and provider upgrade boundary

SafeMigrations integrates with two different public provider boundaries. An
upgrade is accepted only after the full package, engine, tooling, and
dependency-profile gates pass; compilation alone is insufficient.

## MySQL and MariaDB boundary

The `.MySql` package consumes the public
`Doka.EntityFrameworkCore.MySql.Migrations` operation-handler SPI:

- `IMySqlMigrationOperationHandler`;
- `MySqlMigrationOperationContext`;
- `RenderStandardOperation`;
- immutable handler result and command contracts;
- canonical engine feature projection.

SafeMigrations registers one exact handler for `SafeMigrationOperation`. It
does not replace or derive from the Doka migrations generator. This removes the
former Pomelo/internal-generator coupling, but the SPI remains a versioned
binary and behavioral dependency. A Doka update must preserve exact dispatch,
baseline rendering, command ordering, feature projection, diagnostics, and
failure behavior.

## PostgreSQL boundary

The `.PostgreSql` package composes Npgsql's
`NpgsqlMigrationsSqlGenerator` behind `IMigrationsSqlGenerator`. Ordinary EF
operations are delegated unchanged. Safe operations are classified and wrapped
by SafeMigrations before provider-rendered commands are returned.

Npgsql generator constructor changes, migration command semantics, catalog
representation, or SQL normalization can affect the adapter even when the
project still compiles. PostgreSQL major upgrades can also change catalog
shape. PostgreSQL 18, for example, exposes `NOT NULL` constraints as catalog
constraint rows, so tests must select owned constraint families rather than
count every `pg_constraint` row.

## Qualified dependency ranges

Central package declarations use bounded ranges. Lockfiles establish the Floor
profile. `eng/verify-dependency-profile.sh` creates a fresh source snapshot,
forces the current Latest patch profile, confirms exact resolved versions,
builds, and runs all three suites without modifying committed Floor lockfiles.

The declared range must never be widened beyond the profiles actually tested.
Stable SafeMigrations packages must depend on a stable Doka package; the package
content gate rejects a prerelease Doka dependency during a stable tag run.

## Required upgrade evidence

Every EF, Doka, Npgsql, or supported database update requires:

1. locked Floor restore and warning-free Release build;
2. Latest profile restore with exact lockfile assertions;
3. core planner, fingerprint, definition, report, and model-guard tests;
4. all supported MySQL/MariaDB and PostgreSQL engine endpoints;
5. missing, matching, different, unsupported, and data-blocked states;
6. `Database.MigrateAsync`, `IMigrator`, history, missing/conflicting adapter,
   parallel migrator, least-privilege, and recovery tests;
7. normal, idempotent, and no-transaction script generation;
8. `dotnet ef database update` and Migration Bundle;
9. deterministic pack, exact contents, package-only consumer, and Public API
   validation;
10. performance/allocation budgets and SPDX SBOM validation.

If any behavior changes, update expected definitions or provider logic only
after determining whether the new behavior is semantically correct for every
supported engine. Do not normalize a failing comparison until quoted literals,
identifier case, ordering, and provider semantics have been checked.

## Version research

Version pins and support statements are fast-stale. Update them from primary
sources at author time:

- [.NET and .NET SDK support policy](https://dotnet.microsoft.com/platform/support/policy)
- [EF Core releases and planning](https://learn.microsoft.com/ef/core/what-is-new/)
- [NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning)
- [Npgsql EF Core release notes](https://www.npgsql.org/efcore/release-notes/)
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html)
- [MariaDB release criteria](https://mariadb.org/about/release-criteria/)
- [Doka.EntityFrameworkCore.MySql repository](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql)

The exact release evidence belongs in the workflow run, lockfiles, package
SBOM, and final plan-to-ship reconciliation, not in an unchecked comment.
