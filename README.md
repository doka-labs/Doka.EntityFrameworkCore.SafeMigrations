# Doka.EntityFrameworkCore.SafeMigrations

[![CI](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml/badge.svg)](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations)
[![NuGet MySQL](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.MySql.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.MySql)
[![NuGet PostgreSQL](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

SafeMigrations is a fail-closed EF Core 10 migration library for databases whose
starting schema may differ between application instances. It supports one
canonical migration sequence across MySQL, MariaDB, and PostgreSQL without
assuming a common legacy migration history or deleting unknown objects.

The library classifies each operation against the live catalog as `missing`,
`matching`, `different`, `unsupported`, or `data_blocked`. It then applies one
provider-neutral policy. An operation either converges safely, remains an
idempotent no-op, or stops with a stable reason. It never guesses that two
unknown objects are semantically equivalent.

## Platform and packages

- .NET 10 and EF Core 10
- `Doka.EntityFrameworkCore.SafeMigrations`: provider-neutral intent,
  definitions, planning, reports, and `MigrationBuilder` extensions
- `Doka.EntityFrameworkCore.SafeMigrations.MySql`: MySQL and MariaDB adapter on
  the public `Doka.EntityFrameworkCore.MySql` operation-handler SPI
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`: PostgreSQL adapter on
  Npgsql 10

The qualified engine matrix is:

| Provider package | Engines |
|---|---|
| `.MySql` | MySQL 8.4 and 9.7; MariaDB 10.11, 11.4, 11.8, and 12.3 |
| `.PostgreSql` | PostgreSQL 14 through 18, with one release-gate cell per supported major |

The CI and release workflows pin the exact patch tags and image digests used as
release evidence. See [Support and qualification](docs/support-and-qualification.md).

## Installation

Install one provider package. The core package is included transitively.

```bash
dotnet add package Doka.EntityFrameworkCore.SafeMigrations.MySql
```

or:

```bash
dotnet add package Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
```

## Provider registration

MySQL and MariaDB use the same SafeMigrations adapter. Doka's server-version
profile determines the active engine capabilities.

```csharp
using Doka.EntityFrameworkCore.MySql;
using Doka.EntityFrameworkCore.SafeMigrations.MySql;

services.AddDbContext<AppDbContext>(options =>
{
    options.UseMySql(connectionString, serverVersion);
    options.UseMySqlSafeMigrations();
});
```

PostgreSQL registration is additive to `UseNpgsql`:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UsePostgreSqlSafeMigrations();
});
```

Applications that deliberately own EF Core's internal service provider must
also register the matching provider services:

```csharp
services.AddEntityFrameworkDokaMySql();
services.AddEntityFrameworkDokaMySqlSafeMigrations();
```

or:

```csharp
services.AddEntityFrameworkNpgsql();
services.AddPostgreSqlSafeMigrations();
```

Missing or conflicting SafeMigrations integration fails before target DDL and
before the migration history row is written.

## Policies

Preflight is not a migration policy. It is a separate read-only runner outside
`IMigrator` and EF migration history.

| Policy | Existing matching object | Existing different object |
|---|---|---|
| `ExistenceOnly` | No-op | No-op only where existence semantics are explicit |
| `ThrowIfDifferent` | No-op | Reject |
| `RepairIfSafe` | No-op | Apply a proven allowlisted repair, otherwise reject |

`ExistenceOnly` is intended for a table container in a granular convergence
baseline. It is not a complete table-definition check.

## Heterogeneous legacy convergence

`ConvergeTable` solves the case where one instance has no table, another has an
empty copied table, and a third has only some columns or constraints. It emits
one existence-only table-container operation followed by strict granular
operations for every owned column and constraint.

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    var users = new ExpectedTableDefinition(
        "users",
        [
            new ExpectedColumnDefinition("id", typeof(Guid), isNullable: false),
            new ExpectedColumnDefinition(
                "email",
                typeof(string),
                isNullable: false,
                maxLength: 320),
            new ExpectedColumnDefinition(
                "display_name",
                typeof(string),
                isNullable: true,
                maxLength: 200),
        ],
        primaryKey: new ExpectedPrimaryKeyDefinition("pk_users", "users", ["id"]),
        uniqueConstraints:
        [
            new ExpectedUniqueConstraintDefinition(
                "uq_users_email",
                "users",
                ["email"]),
        ]);

    migrationBuilder.ConvergeTable(users);
}
```

For an existing table, missing nullable or default-bearing columns can be
added. Unsafe `NOT NULL` additions, conflicting definitions, duplicate unique
values, orphaned foreign keys, and violated checks stop before their target
DDL. Unknown extra objects are reported and preserved.

The convergence baseline should be forward-only. Its `Down` method must reject
automatic reconstruction of an unknown legacy origin; recovery uses a tested
backup/restore path or a forward fix.

## Read-only preflight and postflight

Resolve `ISafeMigrationRunner` from the configured context. Use a pseudonymous
instance ID, never a host name, database name, credential, or connection
string.

```csharp
var runner = context.GetService<ISafeMigrationRunner>();
var options = new SafeMigrationRunOptions(
    instanceId: "tenant-7f3d8b1c",
    targetMigrationId: "202608170001_CoreConvergence");

var preflight = await runner.AnalyzePendingMigrationsAsync(context, options);
if (preflight.Status == SafeMigrationReportStatus.Blocked)
{
    throw new InvalidOperationException("SafeMigrations preflight blocked deployment.");
}

await context.Database.MigrateAsync();
```

For an explicit operation contract shared by a migration and deployment tool,
use `AnalyzeAsync` before migration and `VerifyAsync` after migration. Reports
include provider and engine identity, model and operation-contract SHA-256
fingerprints, ordered assessments, preserved unexpected objects, and stable
codes. Serialize with `SafeMigrationReportJson`; the package includes
`schemas/safe-migration-run-report-v1.schema.json`.

Do not encode a preflight-only operation inside `Migration.Up`. EF would record
the migration as applied after successful command execution even when the
target DDL was intentionally omitted.

## Supported operations

The sealed `SafeMigrationOperation` envelope covers all of these families:

- ensure and drop schema
- ensure, drop, and rename table
- ensure, drop, rename, and alter column
- ensure, drop, and rename index
- ensure and drop primary key
- ensure and drop unique constraint
- ensure and drop check constraint
- ensure and drop foreign key

Expected definitions snapshot all input collections and model relevant facets.
Defaults distinguish no default, literal `null`, typed literals, and SQL
expressions. Provider catalog queries are parameterized; identifiers and DDL
are rendered through provider SQL services.

MySQL and MariaDB do not provide PostgreSQL-style schema namespaces, so schema
operations are classified as unsupported there. Provider-specific features
such as PostgreSQL filtered, included, operator-class, collation, descending,
and null-distinctness index facets are explicit rather than silently degraded.

## Multiple DbContext instances

All application instances may use a runtime class derived from one canonical
`CoreDbContext`, but its effective relational model must equal the canonical
migration snapshot. SafeMigrations checks that equality before preflight.

Instance-specific schema extensions require a separate `DbContext`, migration
assembly, and history table. A different target model per instance cannot share
one deterministic Core migration sequence.

## Operational contract

- Run one migrator per database. Provider migration locks serialize competing
  replicas against the same database; different databases may migrate in
  parallel.
- Keep out-of-band DDL disabled during preflight and migration.
- Establish a write fence or maintenance window for data-sensitive constraints
  and backfills.
- MySQL and MariaDB DDL can commit implicitly. Their guard uses session-local
  temporary state and prepared DDL, not stored routines, and requires no
  `CREATE ROUTINE` privilege.
- PostgreSQL guarded operations participate in the normal EF migration
  transaction unless the migration explicitly suppresses transactions.
- Always run postflight and retain its report with deployment evidence.

See [Deployment and recovery](docs/runbooks/deployment-and-recovery.md) and
[Failure codes](docs/runbooks/failure-codes.md).

## Build and qualification

The SDK is fixed by `global.json`.

```bash
eng/verify-vertical-slices.sh
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

Docker is required for provider tests. CI additionally executes every supported
engine profile, EF CLI/script/bundle paths, Floor and Latest dependency
profiles, performance/allocation budgets, deterministic double-pack, an
isolated package-only consumer, and SPDX SBOM validation.

Stable releases are tag-driven. They publish the exact qualified bytes through
NuGet Trusted Publishing, create SLSA provenance and SBOM attestations, verify
NuGet repository signatures and package contents by readback, and only then
create the GitHub Release. See [Release process](docs/release-process.md).

## Design boundaries

SafeMigrations is not a destructive schema synchronizer. It does not infer
renames, merge or split columns, narrow types, delete unknown objects, repair
conflicting primary keys, or activate constraints over violating data. A
classified rejection is part of the complete product contract.

Further documentation:

- [Implementation design](docs/implementation-design.md)
- [Vertical-slice architecture](docs/vertical-slice-architecture.md)
- [Support and qualification](docs/support-and-qualification.md)
- [MySQL and MariaDB DDL behavior](docs/mysql-mariadb-ddl-behavior.md)
- [EF Core and provider upgrade boundary](docs/efcore-provider-upgrade-risk.md)
- [Sample project](samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md)

## License

MIT. See [LICENSE](LICENSE).
