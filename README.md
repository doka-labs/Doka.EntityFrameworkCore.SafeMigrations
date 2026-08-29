# Doka.EntityFrameworkCore.SafeMigrations

[![CI](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml/badge.svg?event=pull_request)](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml)
[![NuGet Core](https://img.shields.io/nuget/vpre/Doka.EntityFrameworkCore.SafeMigrations.svg?label=NuGet%20Core)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations)
[![NuGet MySQL / MariaDB](https://img.shields.io/nuget/vpre/Doka.EntityFrameworkCore.SafeMigrations.MySql.svg?label=NuGet%20MySQL%20%2F%20MariaDB)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.MySql)
[![NuGet PostgreSQL](https://img.shields.io/nuget/vpre/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.svg?label=NuGet%20PostgreSQL)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/badge)](https://scorecard.dev/viewer/?uri=github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations)
[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/14265/badge)](https://www.bestpractices.dev/projects/14265)

SafeMigrations is a fail-closed EF Core 10 migration library for databases whose
starting schema may differ between application instances. It supports one
canonical migration sequence across MySQL, MariaDB, and PostgreSQL without
assuming a common legacy migration history or deleting unknown objects.

The library classifies each operation against the live catalog as `missing`,
`matching`, `different`, `unsupported`, `data_blocked`, or
`prerequisite_missing`. It then applies one provider-neutral policy. An
operation either converges safely, remains an idempotent no-op, or stops with a
stable reason. It never guesses that two unknown objects are semantically
equivalent.

## Platform and packages

- .NET 10 and EF Core 10
- `Doka.EntityFrameworkCore.SafeMigrations`: provider-neutral intent,
  definitions, planning, reports, and `MigrationBuilder` extensions
- `Doka.EntityFrameworkCore.SafeMigrations.MySql`: MySQL and MariaDB adapter on
  the public `Doka.EntityFrameworkCore.MySql` 10.1.1 operation-handler SPI
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`: PostgreSQL adapter on
  Npgsql 10

The declared release-qualification matrix is:

| Provider package | Engines |
| --- | --- |
| `.MySql` | MySQL 8.4 and 9.7; MariaDB 10.11, 11.4, 11.8, and 12.3 |
| `.PostgreSql` | PostgreSQL 14 through 18, with one release-gate cell per supported major |

The CI and release workflows pin the exact patch tags and image digests used
when that matrix executes. The exact successful run, not this table, is release
evidence. See [Support and qualification](docs/support-and-qualification.md).

The first complete delivery targets 10.0.0. The published `10.0.0-rc.1` and
`10.0.0-rc.2` candidates qualified successive revisions of that feature
contract. The source tree prepares `10.0.0-rc.3` with exact native Doka 10.1.1
Guid-format analysis, ordered mixed-migration preflight, and retry-safe GitHub
Release reconciliation. Existing rc.2 migration source and the public API
remain compatible, while strict scaffolding stays the default. [Release
notes](CHANGELOG.md) distinguish published candidates from prepared source. The
badges include prereleases, but only a successful release run and verified
public packages establish availability or qualification.

## Installation

Install one provider package. The core package is included transitively.
Choose an exact version from the published release record and NuGet; replace
the placeholder below before running either command. Release candidates need
their complete prerelease version. Source or changelog entries alone do not
mean that a version is available on NuGet.

```bash
package_version='YOUR_APPROVED_PUBLISHED_VERSION'
dotnet package add Doka.EntityFrameworkCore.SafeMigrations.MySql --version "$package_version"
```

or:

```bash
package_version='YOUR_APPROVED_PUBLISHED_VERSION'
dotnet package add Doka.EntityFrameworkCore.SafeMigrations.PostgreSql --version "$package_version"
```

The [.NET package command](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-package-add)
documents exact-version selection. Verify release identity and content using
[Release verification](docs/security/release-verification.md).

## Provider registration

MySQL and MariaDB use the same SafeMigrations adapter. Doka's server-version
profile determines the active engine capabilities.

```csharp
using Doka.EntityFrameworkCore.MySql;
using Doka.EntityFrameworkCore.SafeMigrations;
using Doka.EntityFrameworkCore.SafeMigrations.MySql;

services.AddDbContext<AppDbContext>(options =>
{
    options.UseMySql(connectionString, serverVersion);
    options.UseMySqlSafeMigrations();
});
```

The MySQL connection must set `Allow User Variables=true` (the
`MySqlConnectionStringBuilder.AllowUserVariables` property). Registration
also validates already-open connections and fails before SafeMigrations
command execution without exposing the connection string.

PostgreSQL registration is additive to `UseNpgsql`:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations;
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

Registration replaces EF Core's scoped `IMigrationsAssembly` so migrations,
the model snapshot, scripts, bundles, and `IMigrator` all use the same
canonical context. The non-generic overload keeps EF's exact runtime-context
behavior. A derived instance context must name its canonical base explicitly:

```csharp
options.UseMySqlSafeMigrations<CoreDbContext>();
options.UsePostgreSqlSafeMigrations<CoreDbContext>();
```

The canonical type must be assignable from the runtime context. PostgreSQL
applications with a custom migrations generator must compose it explicitly:

```csharp
options.UsePostgreSqlSafeMigrations<CustomNpgsqlMigrationsSqlGenerator, CoreDbContext>();
```

## Automatic safe migration scaffolding

SafeMigrations integrates with EF Core's design-time service pipeline. With the
normal direct `Microsoft.EntityFrameworkCore.Design` reference used by
`dotnet ef`, or a direct `Microsoft.EntityFrameworkCore.Tools` reference used
by Visual Studio's Package Manager Console, migration scaffolding automatically
writes safe table and index calls. The Tools package supplies EF Design
transitively; SafeMigrations recognizes both official package layouts. Do not
copy a generated `CreateTable` body into `ExpectedTableDefinition` by hand.

A runtime-only project may reference a SafeMigrations provider without either
design-time package. It builds without a design-service attribute or warning;
EF's tooling rejects scaffolding until a supported design-time package is added.

### Configure the scaffolding mode

`SafeMigrationScaffoldingMode` is the design-time switch used by both provider
registrations:

| Value | Selection | Generated table behavior | Generated rollback |
| --- | --- | --- | --- |
| `Strict` | Default; use for normal migrations | `CreateTableIfNotExists` requires an existing table to match the complete generated definition | `DropTableIfExists` |
| `LegacyConvergence` | Select only while scaffolding a reviewed legacy baseline | `ConvergeTableFromModel` adds missing table children; its source-frozen policy rejects drift by default or repairs the documented safe allowlist | Entire `Down` body throws before DDL |

The no-argument registration selects `Strict`:

```csharp
options.UseMySqlSafeMigrations();
// or: options.UsePostgreSqlSafeMigrations();
```

This is equivalent to the explicit MySQL/MariaDB configuration:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations;

options.UseMySqlSafeMigrations(safeMigrations =>
{
    safeMigrations.UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict);
});
```

PostgreSQL uses the same options contract:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations;

options.UsePostgreSqlSafeMigrations(safeMigrations =>
{
    safeMigrations.UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict);
});
```

For each migration that deliberately adopts heterogeneous legacy
installations, select the mode and policy before scaffolding. Omit
`UseLegacyConvergencePolicy` to retain the fail-closed
`ThrowIfDifferent` default:

```csharp
options.UseMySqlSafeMigrations(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(
            SafeMigrationPolicy.RepairIfSafe);
});
```

or:

```csharp
options.UsePostgreSqlSafeMigrations(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(
            SafeMigrationPolicy.RepairIfSafe);
});
```

Then create and review the migration normally:

```bash
dotnet ef migrations add CoreLegacyConvergence
```

Before selecting a mode, review the complete
[migration authoring guide](docs/migration-authoring.md). It shows the actual
generated `CreateTableIfNotExists` and `ConvergeTableFromModel` source as well
as the supported hand-authored `ExpectedTableDefinition` plus `ConvergeTable`
form, including their different rollback behavior.

The generated `Up` method uses `ConvergeTableFromModel` plus safe index helpers
and writes the selected policy as an explicit named argument. Its `Down` method
throws before DDL because SafeMigrations cannot know which objects predated that
migration. After the legacy baseline sequence has been scaffolded, return
registration to the no-argument strict default for newly created tables. The
selected behavior is frozen in each generated C# migration; changing either
option never reinterprets an existing migration.

The configure callback is available on the canonical-context overloads too:

```csharp
options.UseMySqlSafeMigrations<CoreDbContext>(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});

options.UsePostgreSqlSafeMigrations<CoreDbContext>(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});
```

An application that composes a custom PostgreSQL baseline generator can select
the mode on that overload as well:

```csharp
options.UsePostgreSqlSafeMigrations<CustomNpgsqlMigrationsSqlGenerator, CoreDbContext>(
    safeMigrations =>
    {
        safeMigrations
            .UseScaffoldingMode(
                SafeMigrationScaffoldingMode.LegacyConvergence)
            .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
    });
```

`UseScaffoldingMode` and `UseLegacyConvergencePolicy` configure generated source
only. They do not change how an already generated migration executes and are
not runtime switches for existing migration files. The policy accepts only
`ThrowIfDifferent` and `RepairIfSafe`; `ExistenceOnly`, undefined enum values,
and a non-default legacy policy without `LegacyConvergence` fail during options
configuration.

Automatic rewriting is deliberately bounded to scaffolded `CreateTable`,
`CreateIndex`, and `DropTable` operations. Other EF operations remain ordinary
EF migration operations. When a later migration needs catalog-aware idempotent
handling for a column, constraint, rename, or schema operation, use the
corresponding SafeMigrations builder API and review the resulting contract.
This boundary prevents the design-time layer from silently assigning policies
to operations whose repair or ownership semantics require an explicit choice.

Provider identity annotations on scaffolded columns are captured immutably and
participate in fingerprints, live-catalog comparison, and final DDL. This
preserves MySQL/MariaDB `AUTO_INCREMENT` and PostgreSQL identity semantics.
Doka's `ClientGuid` strategy is retained for replay and hashing but compared as
non-`AUTO_INCREMENT` catalog state because it generates values in the client,
not in the database. HiLo, storage-format, unknown column, and unsupported
operation-level annotations remain `Unsupported` before target DDL instead of
being ignored.

The `*FromModel` helpers are public because generated migrations must compile
against a stable package API. They are scaffolder targets, not required
hand-written boilerplate. For a manually authored index contract, use
`CreateIndexIfNotExists` or `EnsureIndex` instead.

## Policies

Preflight is not a migration policy. It is a separate read-only runner outside
`IMigrator` and EF migration history.

| Policy | Existing matching object | Existing different object |
| --- | --- | --- |
| `ExistenceOnly` | No-op | No-op only where existence semantics are explicit |
| `ThrowIfDifferent` | No-op | Reject |
| `RepairIfSafe` | No-op | Apply a proven allowlisted repair, otherwise reject |

`ExistenceOnly` is intended for a table container in a granular convergence
baseline. It is not a complete table-definition check.

## Heterogeneous legacy convergence

The automatically scaffolded `ConvergeTableFromModel` call solves the case
where one instance has no table, another has an empty copied table, and a third
has only some columns or constraints. It snapshots EF's typed table definition
and emits one existence-only table-container operation followed by granular
operations for every owned column and constraint. Scaffolded indexes follow as
their own safe operations. Those children use the policy written into the
generated call; the default is `ThrowIfDifferent`. With explicit
`RepairIfSafe`, an ordinary existing column is repaired only when its resolved
store type, collation, generated/identity state, row-version state, and provider
annotations already match. The allowlist is limited to nullability, default,
and comment. Tightening nullability is `DataBlocked` when any row contains
`NULL`. Type, collation, computed/generated, identity, row-version, and
provider-annotation drift rejects without mutation. The table container alone
never hides missing children.

`ExpectedTableDefinition` and `ConvergeTable` remain available for advanced
hand-authored contracts, for example when a reviewed migration needs a policy
or expected definition that cannot be inferred from the current EF model. They
are no longer required boilerplate for the normal first convergence migration.
The [migration authoring guide](docs/migration-authoring.md) compares both
convergence forms with the generated strict default.

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
using Doka.EntityFrameworkCore.SafeMigrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

var runner = context.GetService<ISafeMigrationRunner>();
var options = new SafeMigrationRunOptions(
    instanceId: "tenant-7f3d8b1c",
    targetMigrationId: "202608170001_CoreConvergence");

var preflight = await runner.AnalyzePendingMigrationsAsync(context, options, CancellationToken.None);
if (preflight.Status != SafeMigrationReportStatus.Ready)
{
    throw new InvalidOperationException("Deployment requires a reviewed, safe-only ready preflight.");
}

var targetMigration = preflight.TargetMigrationId
    ?? throw new InvalidOperationException("Preflight did not identify a migration target.");

await context.GetService<IMigrator>().MigrateAsync(targetMigration, CancellationToken.None);
```

This narrow example executes only a safe-only `Ready` report and binds execution
to the exact analyzed target, not the latest migration in the assembly.
`ReadyWithProviderOperations` requires separate review and postconditions for
ordinary provider operations; `NoOperations` requires checking intended history
and postconditions rather than executing an unqualified target. A blocked report
must stop deployment. Propagate a deployment cancellation token when available.
Keep the migration assembly fixed and the required write/DDL fences in place;
preflight does not reserve database state. The
[deployment runbook](docs/runbooks/deployment-and-recovery.md) owns these checks
and postflight. EF's [targeted migrator](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.imigrator.migrateasync?view=efcore-10.0)
uses a null target to mean latest, so the example rejects a missing target.

For an explicit execution contract, use `AnalyzeAsync` before migration. Use
`VerifyAsync` afterwards with the reviewed final-state contract. Reuse the same
operations only if every postcondition still describes the final target: an
ensure followed by a rename or drop must not require the old object to remain.
The [postflight procedure](docs/runbooks/deployment-and-recovery.md#postflight)
binds each contract's fingerprint to the same deployment artifact and target.

Reports include provider and engine identity, model and operation-contract
SHA-256 fingerprints, ordered assessments, preserved unexpected objects, and
stable codes. The contract fingerprint covers safe intents, definitions,
policies, operation annotations, and order; ordinary provider operations
contribute only their CLR type, not their SQL or other properties. Retain the
immutable artifact digest and independent review for those operations.
Serialize with `SafeMigrationReportJson`; the package includes
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

SQL-bearing definitions should use the typed `SafeMigrationSql` expression
tree. Typed identifiers, literals, operators, null tests, ranges, lists,
functions, casts, collations, and current date/time values can be rendered for
DDL and compared structurally against provider catalog output. For example:

```csharp
var nonNegative = ExpectedCheckConstraintDefinition.FromExpression(
    "ck_orders_total_non_negative",
    "orders",
    SafeMigrationSql.Binary(
        SafeMigrationSql.Identifier("total"),
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
        SafeMigrationSql.Literal(0)));
```

EF-scaffolded check constraints using this bounded grammar are converted to
the same structured tree automatically. Unsupported SQL stops scaffolding
before a migration file is accepted; use an explicit `FromExpression`
definition for a reviewed equivalent. The complete authoring behavior and
failure boundary are documented in
[Migration authoring paths](docs/migration-authoring.md#generated-check-constraints).

Legacy raw SQL remains representable as opaque input, but opaque expressions
cannot authorize `Matching`; they are classified with
`opaque_sql_expression`. After an identifier rename, an affected opaque facet
is classified with `opaque_expression_rename_projection`. This is deliberate:
neither provider guesses semantic equivalence from SQL text.

MySQL and MariaDB do not provide PostgreSQL-style schema namespaces, so schema
operations are classified as unsupported there. Provider-specific features
such as PostgreSQL filtered, included, operator-class, collation, descending,
and null-distinctness index facets are explicit rather than silently degraded.
An omitted column collation means the exact provider-inferred effective
default, never an ignored comparison facet. Index key direction and null order
distinguish provider default from explicit `ASC`, `DESC`, `NULLS FIRST`, and
`NULLS LAST`.

Collation identity is structured rather than dot-split text:

```csharp
var collation = new SafeMigrationCollationIdentifier(
    name: "tenant.collation",
    schema: "collation_catalog");
```

PostgreSQL resolves the exact schema/name identity to its catalog OID. MySQL
and MariaDB accept unqualified collation names; a schema-qualified identity is
classified as `schema_qualified_collation` before target DDL because those
engines do not expose PostgreSQL-style collation namespaces.

## Multiple DbContext instances

All application instances may use a runtime class derived from one canonical
`CoreDbContext`, but its effective relational model must equal the canonical
migration snapshot. SafeMigrations checks that equality before preflight when
the configured migrations assembly supplies a snapshot. Without a snapshot,
the runner still fingerprints the runtime model but cannot compare it to a
canonical snapshot. Supply an independently established
`expectedModelFingerprint` when using a snapshot-free explicit contract; a
fingerprint computed from the same unchecked instance is not a target-model
proof. Keep the canonical snapshot in normal EF migration deployments.

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
  transaction. A baseline generator that emits a transaction-suppressed command
  for a guarded operation is rejected before that operation executes. Ordinary
  provider operations and externally executed no-transaction scripts have
  separate boundaries described in the deployment runbook.
- PostgreSQL analysis owns one read-only `RepeatableRead` transaction and
  transaction-scoped advisory lock. If the caller already owns a transaction,
  it must be read-only and use `RepeatableRead` or `Serializable`; otherwise
  analysis fails before reading the catalog and leaves that transaction owned
  by the caller.
- Always run postflight and retain its report with deployment evidence.

Analyzer commands are deterministically chunked at 512 MySQL/MariaDB operations
or 128 PostgreSQL operations, 16,000 bound parameters, and 4 MiB of UTF-8
payload. MySQL/MariaDB also cap a chunk at half the live
`max_allowed_packet`. A single operation that exceeds a bound is rejected
before query execution; partial multi-chunk reports are never published.

See [Deployment and recovery](docs/runbooks/deployment-and-recovery.md) and
[Failure codes](docs/runbooks/failure-codes.md).

## Build and qualification

The SDK is fixed by `global.json`.

```bash
dotnet restore Doka.EntityFrameworkCore.SafeMigrations.slnx --locked-mode
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx --configuration Release --no-restore
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj --configuration Release
```

Docker is required for provider tests. CI additionally executes every supported
engine profile, EF CLI/script/bundle paths, merged coverage thresholds,
performance/allocation budgets, deterministic double-pack, isolated
package-only consumers, and SPDX SBOM validation. FsCheck exercises generated
Core and provider invariants with shrunk counterexamples, while the separate
Dependency Review gate rejects newly introduced high-severity vulnerabilities
and dependencies outside the approved license policy before merge.

Each provider matrix cell also persists a live full-runner latency artifact.
It measures 20 full-runner invocations after a warmup against 100 expected
tables before and after adding 1,000 foreign tables with child objects. Each
invocation may execute multiple database roundtrips. Expected assessments
must remain identical, foreign child rows must stay outside the scoped child
inventory, and noisy p95 must remain within `2 * clean p95 + 250 ms`.

Release candidates and stable releases use the same manually dispatched path.
The workflow qualifies and attests exact bytes from current `main` before it
waits at the protected NuGet environment. Only then does the operator create a
signed annotated tag on that qualified commit and approve publication. The
write-capable job uses NuGet Trusted Publishing, verifies public repository
signatures and package content, and creates or verifies an immutable GitHub
Release with the exact six package files, checksums, and SPDX manifest.
Candidates are marked prerelease and never replace the latest stable release.
See [Publication operations](docs/operations/release-publication.md) for the
step-by-step maintainer guide and current readiness, and
[Release process](docs/release-process.md) for the qualification contract.

## Design boundaries

SafeMigrations is not a destructive schema synchronizer. It does not infer
renames, merge or split columns, narrow types, delete unknown objects, repair
conflicting primary keys, or activate constraints over violating data. A
classified rejection is part of the complete product contract.

Further documentation:

- [Documentation index](docs/README.md)
- [Public API reference](docs/api-reference.md)
- [Implementation design](docs/implementation-design.md)
- [Vertical-slice architecture](docs/vertical-slice-architecture.md)
- [Support and qualification](docs/support-and-qualification.md)
- [MySQL and MariaDB DDL behavior](docs/mysql-mariadb-ddl-behavior.md)
- [EF Core and provider upgrade boundary](docs/efcore-provider-upgrade-risk.md)
- [Sample project](samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md)

## Community and support

- [Support channels and safe diagnostic sharing](SUPPORT.md)
- [Contributing and verification requirements](CONTRIBUTING.md)
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy and private vulnerability reporting](SECURITY.md)
- [Governance and responsibilities](GOVERNANCE.md)
- [Project direction](ROADMAP.md)
- [OpenSSF Best Practices evidence mapping](docs/openssf-best-practices.md)

## License

The product is MIT-licensed. See [LICENSE](LICENSE). The adapted
[Code of Conduct](CODE_OF_CONDUCT.md#attribution) is separately licensed under
CC BY-SA 4.0.
