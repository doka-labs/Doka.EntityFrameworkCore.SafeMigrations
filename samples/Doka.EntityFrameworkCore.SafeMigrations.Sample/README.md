# SafeMigrations sample

This compilable sample demonstrates the current .NET 10 API:

- a provider-neutral `SampleDbContext`;
- a forward-only heterogeneous Core convergence baseline;
- complete `ExpectedTableDefinition` instances for users and orders;
- granular convergence of missing columns, keys, constraints, foreign keys,
  and indexes even when a table container already exists;
- PostgreSQL schema and rename operations;
- an allowlisted `RepairIfSafe` column metadata transition whose structural
  type facets exactly match the declared old definition;
- MySQL/MariaDB and PostgreSQL adapter registration touchpoints.

Key files:

- `SampleMigrationUsage.cs` builds the shared operation contracts.
- `Migrations/InitialSafeMigrationExample.cs` is the forward-only convergence
  migration.
- `Migrations/MaintenanceSafeMigrationExample.cs` shows an explicit later
  forward fix.
- `Program.cs` proves both provider registration extensions and constructs the
  operation sequences without connecting to a database.

The consuming application configures either Doka `UseMySql(...)` plus
`UseMySqlSafeMigrations()`, or Npgsql `UseNpgsql(...)` plus
`UsePostgreSqlSafeMigrations()`. `SampleDbContext` intentionally contains no
connection string or implicit provider choice.

The sample slices are not intended to run as one combined migration history.
The initial migration's `Down` rejects destructive reconstruction because no
single legacy origin exists. Deployment recovery is backup/restore or an
explicit reviewed forward migration.

Build and run without a database:

```bash
dotnet build samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/Doka.EntityFrameworkCore.SafeMigrations.Sample.csproj --configuration Release
dotnet run --project samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/Doka.EntityFrameworkCore.SafeMigrations.Sample.csproj --configuration Release --no-build --no-restore
```

For preflight and operational sequencing, see the repository
[README](../../README.md) and
[deployment runbook](../../docs/runbooks/deployment-and-recovery.md).
