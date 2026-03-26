# Sample Project

This sample shows the intended usage shape for `Doka.EntityFrameworkCore.SafeMigrations`.

It includes:

- a minimal `DbContext`
- a reusable consolidated-initial-migration example in `SampleMigrationUsage.BuildUpOperations(...)`
- a reusable maintenance-migration example in `SampleMigrationUsage.BuildMaintenanceOperations(...)`
  with rename, alter, schema, and primary-key operations
- a reusable maintenance rollback example in `SampleMigrationUsage.BuildMaintenanceRollbackOperations(...)`
- a legacy `SafeMigrationStrictMode` example in `SampleMigrationUsage.BuildLegacyStrictModeExamples(...)`
- concrete `Migration` types in:
  - `Migrations/InitialSafeMigrationExample.cs`
  - `Migrations/MaintenanceSafeMigrationExample.cs`
- provider-registration touchpoints for MariaDB and PostgreSQL in `Program.cs`

Important notes:

- `SampleDbContext` intentionally does not embed a concrete provider or connection string.
- The consuming application must configure `UseMySql(...)` or `UseNpgsql(...)` and then apply `UseMariaDbSafeMigrations()` or `UsePostgreSqlSafeMigrations()`.
- The sample methods are separate example slices. They are meant to illustrate usage patterns, not to be executed as one combined migration.
- The maintenance example now shows the less-common helpers too:
  `EnsureSchemaExists`, `DropSchemaIfExists`, `AddPrimaryKeyIfNotExists`, and `DropPrimaryKeyIfExists`.
- The two `Migration` classes show the intended difference between a consolidated initial migration and a later follow-up maintenance migration.

Build it with:

```bash
dotnet build samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/Doka.EntityFrameworkCore.SafeMigrations.Sample.csproj
```
