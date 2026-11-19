# SafeMigrations sample

This runnable source-construction sample demonstrates the current .NET 10 API:

- a provider-neutral `SampleDbContext`;
- a forward-only heterogeneous Core convergence baseline;
- generated-model-style `ConvergeTableFromModel` calls for users and orders;
- granular convergence of missing columns, keys, constraints, foreign keys,
  and indexes even when a table container already exists;
- PostgreSQL schema and rename operations;
- an allowlisted `RepairIfSafe` column metadata transition whose structural
  type facets exactly match the declared old definition;
- MySQL/MariaDB, PostgreSQL, SQLite, and SQL Server adapter registration touchpoints.

The normal authoring path is `dotnet ef migrations add` with
`LegacyConvergence` selected in provider registration; the design-time
generator writes the calls represented by this sample. `SampleMigrationUsage`
keeps them as executable source so the repository can compile and inspect the
result without requiring a database during the sample build.

`Program.Main` calls the internal `SampleMigrationUsage.Run(TextWriter)` entrypoint
after checking the four adapter-registration touchpoints. The same entrypoint is
called in-process by [linked-source SQLite tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Infrastructure/SqliteSampleExecutionTests.cs); it returns the actual 14 initial
and five independent maintenance operations used in its output. It opens no
database. The tests execute those initial operations through the real SQLite
generator, check the native keys/CHECK/FK/index, replay the stream, verify cascades,
and prove that invalid data and a populated partial required-column addition are
rejected without changing retained data or schema. They do not claim live
MySQL/MariaDB, PostgreSQL or SQL Server qualification.

The canonical `SampleDbContext` accepts explicit provider options and maps the
same lower-case columns, keys, relationship, index and check as the source example.
Its configuration is not an implicit connection string or provider choice.

The no-argument provider registration uses strict scaffolding. Select legacy
convergence only while generating a migration that intentionally adopts
heterogeneous existing tables, then review the source and return registration
to the strict default for subsequent scaffolding.

The explicit design-time configuration is identical for all providers:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations;
using Doka.EntityFrameworkCore.SafeMigrations.MySql;
using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;
using Doka.EntityFrameworkCore.SafeMigrations.Sqlite;
using Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

options.UseMySqlSafeMigrations(configuration =>
{
    configuration.UseScaffoldingMode(
        SafeMigrationScaffoldingMode.LegacyConvergence);
});

options.UsePostgreSqlSafeMigrations(configuration =>
{
    configuration.UseScaffoldingMode(
        SafeMigrationScaffoldingMode.LegacyConvergence);
});

options.UseSqliteSafeMigrations(configuration =>
{
    configuration.UseScaffoldingMode(
        SafeMigrationScaffoldingMode.LegacyConvergence);
});

options.UseSqlServerSafeMigrations(configuration =>
{
    configuration.UseScaffoldingMode(
        SafeMigrationScaffoldingMode.LegacyConvergence);
});
```

`SafeMigrationScaffoldingMode.Strict` is the no-argument default. The enum is
read only while EF scaffolds a new C# migration; changing it does not modify or
reinterpret migrations that already exist.

For the complete generated `Up` and fail-closed `Down` shape, compare all four
[migration authoring paths](../../docs/migration-authoring.md).

Key files:

- `SampleMigrationUsage.cs` builds the shared operation contracts.
- `Migrations/InitialSafeMigrationExample.cs` is the forward-only convergence
  migration.
- `Migrations/MaintenanceSafeMigrationExample.cs` shows an explicit later
  forward fix.
- `Program.cs` proves all provider registration extensions and constructs the
  operation sequences without connecting to a database.

The consuming application configures Doka `UseMySql(...)`, Npgsql
`UseNpgsql(...)`, Microsoft `UseSqlite(...)`, or Microsoft `UseSqlServer(...)` plus the matching
SafeMigrations registration. `SampleDbContext` intentionally contains no
connection string or implicit provider choice.

The sample slices are not intended to run as one combined migration history.
The initial migration's `Down` rejects destructive reconstruction because no
single legacy origin exists. Deployment recovery is backup/restore or an
explicit reviewed forward migration.

Build and run without a database:

```bash
dotnet build samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/Doka.EntityFrameworkCore.SafeMigrations.Sample.csproj --configuration Release
dotnet run --project samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/Doka.EntityFrameworkCore.SafeMigrations.Sample.csproj --configuration Release --no-build --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests.csproj --configuration Release --filter FullyQualifiedName~SqliteSampleExecutionTests
```

For preflight and operational sequencing, see the repository
[README](../../README.md) and
[deployment runbook](../../docs/runbooks/deployment-and-recovery.md).
