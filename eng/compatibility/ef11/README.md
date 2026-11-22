# EF11 integration verification

.NET/EF Core 11 is the framework graph for SafeMigrations 11.0.0.
The repository uses published Doka 11.0.0; the former opt-in props, nested SDK
selection and local-package prerequisite are removed. All provider projects,
tooling consumers and benchmarks inherit the root SDK/framework contract.
This guide describes verification commands, not completed release qualification.

## Restore and build

Run from the repository root with SDK 11.0.100 installed:

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode \
  -p:WarningsNotAsErrors=
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx \
  --configuration Release --no-restore --disable-build-servers -m:1 \
  -p:WarningsNotAsErrors=
```

An intentional dependency update regenerates reviewed repository locks first;
verification then uses locked restore. Do not bypass downgrade/audit diagnostics
or use stale .NET 10 binaries as .NET 11 evidence.

## Targeted Core/MySQL regression

The charset/collation comparison and migration-assembly regressions are regular
tests, not an excluded compatibility profile. For one MariaDB qualification cell:

```bash
project='tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj'
image="$(bash eng/select-database-image.sh mariadb123)"

SAFE_MIGRATIONS_MYSQL_ENGINE=mariadb \
SAFE_MIGRATIONS_MYSQL_VERSION=12.3.3 \
SAFE_MIGRATIONS_MYSQL_IMAGE="$image" \
dotnet test "$project" --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~MySqlEf11DefaultTextIntegrationTests|FullyQualifiedName~MySqlEf11MigrationAssemblyTests' \
  --logger 'trx;LogFileName=ef11-default-text.trx' \
  --results-directory artifacts/ef11/default-text/mariadb123
```

Repeat independently with the exact image, engine and version for MySQL 8.4/9.7
and MariaDB 10.11/11.4/11.8/12.3. Read current versions from the canonical
[image manifest](../../../docker/database-images.Dockerfile); do not copy a
mutable tag or treat one default MariaDB run as evidence for another cell.
The shared fixtures own isolated databases and clean up only owned resources.

## Contract under comparison

- Equivalent option-level and fluent model defaults produce no model diff.
- Explicit/inherited column collation remains a real model change for ordinary
  strings and Char36 GUIDs; the generated target snapshot remains authoritative.
- The design-time marker and generated safe operations remain present;
  provider-owned baseline operations are not mistaken for guarded coverage.
- Actual Up/Down and replay preserve retained data and JSON, ASCII, Binary16
  and native UUID contracts. Different live defaults and unsafe NULL data remain
  rejected rather than declared equivalent to a configured option.
- Snapshot generation forwards `latestMigrationId`. Additional migration
  assemblies invalidate cached views while preserving canonical ownership and
  duplicate-ID rejection. The latest assembly supplies the snapshot; an absent
  snapshot does not silently fall back to a historical one.

The complete provider suites and existing CLI/script/bundle/package consumers
remain mandatory beyond this focused comparison. PostgreSQL, SQLite and SQL
Server are part of the normal .NET 11 source graph. Native x64 SQL Server
qualification cannot be replaced by local ARM platform skips. No hosted line
activation or publication follows from these local commands.

## Framework references

- [Upgrade and dependency contract](../../../docs/support-and-qualification.md#upgrade-to-1100)
- [EF11 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/breaking-changes)
- [EF11 migrations assembly](https://github.com/dotnet/efcore/blob/v11.0.0/src/EFCore.Relational/Migrations/Internal/MigrationsAssembly.cs)
- [EF11 generator contract](https://github.com/dotnet/efcore/blob/v11.0.0/src/EFCore.Design/Migrations/Design/IMigrationsCodeGenerator.cs)
