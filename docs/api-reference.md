# Public API reference

This guide explains the public inputs, outputs, and failure boundaries without
requiring readers to study the implementation. The exact signatures, parameter
documentation, and nullability annotations ship as XML documentation beside
each `lib/net10.0` assembly. After installing the provider package, use IDE
completion/Quick Documentation for the selected package version. The
[Core](../src/Doka.EntityFrameworkCore.SafeMigrations/PublicAPI.Shipped.txt),
[MySQL/MariaDB](../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/PublicAPI.Shipped.txt),
and [PostgreSQL](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/PublicAPI.Shipped.txt)
API baselines are review inventories, not substitutes for this guide or XML.
The initial public surface shipped with `10.0.0-rc.1`; `10.0.0-rc.2` added
source-frozen legacy-convergence policy selection and provider-context
validation. `10.0.0-rc.3` preserved that public API and existing
migration-source compatibility while adding exact native Doka 10.1.1
Guid-format analysis and ordered mixed-migration preflight. The 10.0.0 stable
source promotes that exact public contract without a public API or generated
migration-source delta. The published 10.0.1 and 10.0.2 maintenance releases
preserve that public API. Stable 10.1.0 source adds two scaffolder-facing
index-prefix methods while qualifying Doka 10.3.0's typed migration metadata;
all earlier signatures remain compatible. Strict scaffolding remains the
default. A successful release run and exact-version public package readback
remain the authority for a published API.

## Packages and registration

Core types use `Doka.EntityFrameworkCore.SafeMigrations`. The provider-specific
registration namespaces append `.MySql` or `.PostgreSql`.

| Entry point | Inputs and result | Boundary |
| --- | --- | --- |
| `UseMySqlSafeMigrations()` | Configured EF options builder; returns the builder | Supports both call orders with Doka `UseMySql`; declares the required user-variable capability for MySQL and MariaDB |
| `UsePostgreSqlSafeMigrations()` | Configured EF options builder; returns the builder | Add after `UseNpgsql` |
| `UseMySqlSafeMigrations(configure)` | Safe scaffolding mode plus normal MySQL/MariaDB registration | `Strict` is the builder default; selection is written into new migration source |
| `UsePostgreSqlSafeMigrations(configure)` | Safe scaffolding mode plus normal PostgreSQL registration | Same source-frozen design-time contract |
| `UseMySqlSafeMigrations<TCanonicalMigrationContext>()` | Canonical context type on the non-generic options builder | Derived runtime type must be assignable and preserve the canonical model |
| `UsePostgreSqlSafeMigrations<TCanonicalMigrationContext>()` | Canonical context type | Same model/assembly/history boundary |
| `UsePostgreSqlSafeMigrations<TBaselineGenerator, TCanonicalMigrationContext>()` | Selected Npgsql-compatible generator and canonical context | Composes ordinary and safe baseline generation explicitly |
| `AddEntityFrameworkDokaMySqlSafeMigrations()` | Application-owned EF internal service collection | Also register Doka's base provider services |
| `AddPostgreSqlSafeMigrations()` | Application-owned EF internal service collection | Also register Npgsql's base provider services |

Typed options-builder overloads retain the `TContext` return type. Their type
parameter lists differ from the non-generic builder overloads; use IDE
signature help, rather than interpreting `TContext` as the canonical context.
The external-service-provider overloads can explicitly select canonical
context/generator configuration as documented in their XML reference.

Registration does not create a database or execute migrations. MySQL/MariaDB
registration calls Doka 10.3.0's `RequireUserVariables()`. Doka supplies
`AllowUserVariables=true` when a provider-owned connection string omitted the
option and rejects an explicit contradiction. Caller-owned `DbConnection` and
`MySqlDataSource` instances are never mutated; they must already specify
`AllowUserVariables=true` and `GuidFormat=Binary16`. Every connection path must
use matched-row semantics (`UseAffectedRows=false`). SafeMigrations validates
its command connection again before guarded execution. See
[registration examples](../README.md#provider-registration).

## Scaffolding configuration

Import the provider namespace for the registration extension and the Core
namespace for its shared configuration types:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations;
using Doka.EntityFrameworkCore.SafeMigrations.MySql;
// or: using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;
```

The configure callback receives `SafeMigrationOptionsBuilder`. Its complete
current public configuration surface is:

| Member | Contract |
| --- | --- |
| `UseScaffoldingMode(SafeMigrationScaffoldingMode.Strict)` | Explicitly selects the default strict generated table contract |
| `UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)` | Selects object-granular generated convergence for a reviewed legacy baseline |
| `UseLegacyConvergencePolicy(SafeMigrationPolicy.ThrowIfDifferent)` | Explicitly selects the fail-closed default for generated legacy child operations |
| `UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)` | Allows generated legacy child operations to apply only provider-proven allowlisted repairs |

Both methods return the same builder for fluent composition. A null configure
callback, undefined enum value, `ExistenceOnly` as the legacy policy, or a
non-default legacy policy without `LegacyConvergence` is rejected during options
configuration. Both selected values are written into new migration source; they
are not consulted when an existing migration executes.

The callback is available on every registration shape that can select a
scaffolding mode:

```csharp
options.UseMySqlSafeMigrations(configuration =>
{
    configuration
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});

options.UseMySqlSafeMigrations<CoreDbContext>(configuration =>
{
    configuration
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});

options.UsePostgreSqlSafeMigrations(configuration =>
{
    configuration
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});

options.UsePostgreSqlSafeMigrations<CoreDbContext>(configuration =>
{
    configuration
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
});

options.UsePostgreSqlSafeMigrations<CustomNpgsqlMigrationsSqlGenerator, CoreDbContext>(
    configuration =>
    {
        configuration
            .UseScaffoldingMode(
                SafeMigrationScaffoldingMode.LegacyConvergence)
            .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);
    });
```

Typed `DbContextOptionsBuilder<TContext>` overloads expose the same callback.
Their first generic type argument is the runtime context because it cannot be
inferred when an extension method supplies additional generic arguments; use
IDE signature help for the exact typed overload.

## Design-time scaffolding

`SafeMigrationScaffoldingMode.Strict` is the default. The generated operation
mapping is:

| EF operation | `Strict` source | `LegacyConvergence` source |
| --- | --- | --- |
| `CreateTable` | `CreateTableIfNotExists` | `ConvergeTableFromModel` |
| Single-column `CreateIndex` | `CreateIndexIfNotExistsFromModel`, or the prefix-aware counterpart on MySQL/MariaDB | Same |
| Multi-column `CreateIndex` | `CreateCompositeIndexIfNotExistsFromModel`, or the prefix-aware counterpart on MySQL/MariaDB | Same |
| Generated rollback of `CreateTable` | `DropTableIfExists` | Entire `Down` body rejects before DDL |

Every generated `ConvergeTableFromModel` call contains an explicit `policy`
argument. The compatibility default is `ThrowIfDifferent`. `RepairIfSafe`
allows only nullability, default, and comment changes on an ordinary column
whose invariant provider catalog shape already matches. Doka's typed contract
must recognize every MySQL/MariaDB column annotation. Nullability tightening
with existing `NULL` values is `DataBlocked`; type, collation, generated,
identity, row-version, contradictory metadata, and unsupported drift remains
fail-closed.

The [migration authoring guide](migration-authoring.md) contains complete
generated strict and legacy-convergence migrations plus the equivalent
hand-authored `ExpectedTableDefinition` form, including provider annotations,
separate index calls, and rollback behavior.

Doka `ClientGuid` remains in the captured column contract but compares like
non-`AUTO_INCREMENT` state because generation occurs in the EF client. HiLo,
storage-format, unknown column, and unsupported operation-level annotations are
classified `Unsupported` before target DDL instead of being ignored.

The provider package contributes a `buildTransitive` assembly attribute when
the consuming project directly references `Microsoft.EntityFrameworkCore.Design`
or `Microsoft.EntityFrameworkCore.Tools`. The latter supplies EF Design as a
transitive dependency. EF Core then discovers the SafeMigrations
`IDesignTimeServices` implementation and composes its C# migration generator
after the selected database provider's design services. A runtime-only project
with neither package receives no attribute or warning. Changing the mode later
affects only future scaffolding; existing C# migration files retain their
original method calls.

Every generated migration contains an explicit
`using Doka.EntityFrameworkCore.SafeMigrations;` directive. Generated source
therefore resolves SafeMigrations extension methods and policy types without a
consumer-owned global using. Generation fails closed if EF emits an unexpected
using-directive shape instead of producing source whose dependencies are
implicit. SafeMigrations does not rewrite existing migration files; add the
explicit import when adopting an older generated or hand-authored migration
that lacks it.

Generated migration bodies use file-scoped namespaces and collection
expressions for the array arguments SafeMigrations controls. This keeps normal
EF output compatible with the repository's warning-level namespace and
constant-array analyzers without marking reviewable migration source as
auto-generated or suppressing diagnostics.

The design-time replacement does not rewrite add/alter/drop column, constraint,
rename, or schema operations. Those retain EF behavior unless the migration
author selects an explicit SafeMigrations API. This is an intentional policy
boundary, not a partial interpretation of those operations.

## MigrationBuilder operations

These extensions append sealed `SafeMigrationOperation` envelopes; they do
not execute SQL at construction time. Single-operation methods return
`OperationBuilder<SafeMigrationOperation>`. `ConvergeTable` appends multiple
operations and returns the original `MigrationBuilder`.

| Family | Explicit definition API | Familiar builder API and other operations |
| --- | --- | --- |
| Schema | `EnsureSchemaExists` | `DropSchemaIfExists` |
| Table | `EnsureTable`, `ConvergeTable` | `CreateTableIfNotExists<TColumns>`, `ConvergeTableFromModel<TColumns>`, `DropTableIfExists`, `RenameTableIfExists` |
| Column | `EnsureColumn`, `AlterColumnIfDifferent` | `AddColumnIfNotExists<T>`, `DropColumnIfExists`, `RenameColumnIfExists` |
| Index | `EnsureIndex` | `CreateIndexIfNotExists`, `CreateIndexIfNotExistsFromModel`, `CreateCompositeIndexIfNotExistsFromModel`, `CreateIndexWithPrefixesIfNotExistsFromModel`, `CreateCompositeIndexWithPrefixesIfNotExistsFromModel`, `DropIndexIfExists`, `RenameIndexIfExists` |
| Primary key | `EnsurePrimaryKey` | `AddPrimaryKeyIfNotExists`, `DropPrimaryKeyIfExists` |
| Unique constraint | `EnsureUniqueConstraint` | `AddUniqueConstraintIfNotExists`, `DropUniqueConstraintIfExists` |
| Check constraint | `EnsureCheckConstraint` | `AddCheckConstraintIfNotExists`, `DropCheckConstraintIfExists` |
| Foreign key | `EnsureForeignKey` | `AddForeignKeyIfNotExists`, `DropForeignKeyIfExists` |

Definitions specify the target name, parent table where applicable, optional
schema, ordered members, and expected facets. Explicit ensure APIs require a
`SafeMigrationPolicy`; familiar create/add overloads generally default to
`ThrowIfDifferent`. Schema and drop/rename helpers select their defined policy
internally. Check the overload rather than assuming every method takes policy.

`EnsureTable` additionally requires `SafeMigrationTableMode`: strict owned
definition or convergence container. `ConvergeTable` always emits an
existence-only container followed by granular children under the supplied
policy, defaulting to strict comparison. A separate index sequence must target
the same table/schema. It does not discover which legacy objects you intended
to own. Follow the [legacy convergence example](../README.md#heterogeneous-legacy-convergence).

`ConvergeTableFromModel` captures the typed EF table callback and emits the
same object-granular contract without a hand-authored
`ExpectedTableDefinition`. The two `*FromModel` index helpers similarly capture
EF's generated single- or multi-column index call. They remain public so
scaffolded migration source has a stable package target. Hand-written
migrations normally use `CreateIndexIfNotExists` or `EnsureIndex` for indexes.
The two prefix-aware methods accept exactly one non-negative entry per key.
Zero captures a complete key and a positive value becomes the key's explicit
prefix length. They exist so provider-projected migration source does not leak
Doka annotations onto a custom outer operation.

`Drop*IfExists` operations are explicitly destructive when the target exists;
the name means idempotent absence, not recovery or undo. Rename methods require
explicit source/target identity and reject occupied targets. A missing source
can be an idempotent no-op. The built-in rename postcondition checks source
absence, not the destination's complete definition. Verify the destination
through explicit ensure operations in the contract or independent checks;
rename postflight alone is not proof of destination equivalence.
`AlterColumnIfDifferent` takes the target definition, nullable old definition,
and policy; an absent or mismatching old definition does not authorize repair.

## Expected definitions

| Type | Principal inputs |
| --- | --- |
| `ExpectedTableDefinition` | Table/schema, columns, primary key, unique/check/foreign constraints, comment |
| `ExpectedColumnDefinition` | Name, CLR/store type, nullability, length/precision/scale, Unicode/fixed-length/row-version facets, collation, comment, default/computed expression |
| `ExpectedIndexDefinition` | Name, table/schema, ordered keys, uniqueness and provider-specific filter/include/index facets |
| `ExpectedIndexKeyDefinition` | Column or expression, ordering, null order, provider-specific key facets |
| `ExpectedPrimaryKeyDefinition` | Name, table/schema, ordered columns |
| `ExpectedUniqueConstraintDefinition` | Name, table/schema, ordered columns |
| `ExpectedCheckConstraintDefinition` | Name, table/schema and check expression; prefer `FromExpression` |
| `ExpectedForeignKeyDefinition` | Name, dependent/principal identities, ordered column pairs and referential actions |
| `SafeMigrationCollationIdentifier` | Exact name and optional schema, never a dot-split combined string |

Collections are snapshotted by the definitions. Invalid names, enum values,
contradictory facets, or incompatible literal values fail construction. A
representable definition may still be unsupported by the selected engine;
construction success does not imply runtime support.

Column annotations captured from EF's typed table callback are immutable,
ordered, and included in definition equivalence and contract fingerprints.
The MySQL/MariaDB adapter accepts Doka `None` and `AutoIncrement` generation
and compares `AUTO_INCREMENT`; PostgreSQL accepts `None`, identity-always, and
identity-by-default strategies and compares `pg_attribute.attidentity`.
Unknown or malformed provider column annotations and all unmodeled
operation-level annotations are classified `Unsupported` before target DDL.

`SafeMigrationDefaultValue` distinguishes no default, typed literal (including
literal `null`), and SQL/expression defaults. The familiar
`AddColumnIfNotExists<T>(defaultValue: null)` means no literal default; use the
explicit definition API for a literal SQL `NULL` distinction. An omitted
collation requires the provider-inferred effective default; it is not a
wildcard that disables comparison.

## SQL expressions and policy

`SafeMigrationSql` constructs a closed expression tree with `Identifier`,
`Literal`, `Unary`, `Binary`, `IsNull`, `IsNotNull`, `Between`, `In`,
`Function`, `Cast`, `Collate`, and `Current`. Operators and current values are
enums. The provider validates supported semantics and renders each token in
its own context. Do not concatenate user data into SQL or type grammar.

`Opaque` and `ProviderFragment` retain explicit SQL provenance, but neither
proves catalog equivalence. They can produce an unsupported classification;
they are not an escape hatch around strict comparison. See
[expression examples](../README.md#supported-operations) and engine boundaries
in [support](support-and-qualification.md#qualified-capability-boundaries).

`SafeMigrationDecisionPlanner.Plan` maps kind, observed state, policy, and
proven repair capability to a `SafeMigrationDecision` without I/O. The public
state/policy/action enums are closed contracts; invalid enum values reject.
`ExistenceOnly`, `ThrowIfDifferent`, and `RepairIfSafe` do not change what the
provider can prove. See the [policy table](../README.md#policies).

## Read-only runner

Resolve `ISafeMigrationRunner` from the configured context. Every method takes
the context, immutable `SafeMigrationRunOptions`, and a cancellation token and
returns `Task<SafeMigrationRunReport>`:

| Method | Additional input | Result |
| --- | --- | --- |
| `AnalyzePendingMigrationsAsync` | Pending sequence resolved through EF history and configured migration assembly | Preflight report |
| `AnalyzeAsync` | Explicit ordered `IReadOnlyList<MigrationOperation>` | Preflight report including earlier safe operations and conditional structural postconditions of recognized ordinary EF operations |
| `VerifyAsync` | Explicit ordered operations whose effective final postconditions must hold | Postflight report; the final safe writer for an exact resource supersedes its earlier safe writers, without projecting provider-owned effects |

`SafeMigrationRunOptions` requires a nonempty pseudonymous `instanceId` and
optionally takes `targetMigrationId` and `expectedModelFingerprint`. The
caller must generate a non-sensitive ID; validation is not automatic
pseudonymization. The pending runner resolves the target against migration
history. Explicit-sequence callers supply the actual sequence themselves.

Model comparison uses the configured migrations assembly's snapshot when one
exists. A missing snapshot does not itself reject analysis: the runner still
computes the runtime fingerprint. For a snapshot-free contract, provide an
independently established `expectedModelFingerprint` if target-model equality
is required. Omitting both leaves no external target-model comparison.

Bind execution to the non-null target actually analyzed, keep the migration
assembly unchanged, and review ordinary provider operations separately.
Recognized ordinary table/column operations may satisfy a later safe
prerequisite in the ordered projection, but remain
`provider_owned_not_analyzed`; this conditional projection is not an analysis
or approval of their DDL.
Typed EF insert, update, and delete-data operations preserve only structural
table/column facts for a later non-unique index. They invalidate all earlier
projected and live pre-batch data-safety proofs, so a later unique index or
missing data-validating constraint remains blocked until separately provable.
A later structural provider operation does not restore row-level certainty.
Raw SQL and unknown provider operations still discard all projection facts.
Calling EF migration with a null target means latest, which can exceed a
specifically targeted preflight.

`VerifyAsync` checks the effective final postconditions against the live
catalog; it does not replay history or apply preflight projection. For repeated
safe writes to the same exact resource, the last writer is authoritative and
earlier assessments use `postcondition_superseded`. Provider-owned operations
never participate in that reduction. The same ordered operations can therefore
describe drop/recreate or successive-definition execution and final
verification. A rename still proves only source absence, so complete
destination verification requires an explicit destination ensure or a
separately reviewed final-state contract. Freeze the chosen contract before
execution and bind its fingerprint to the same artifact, target migration, and
model. Require equal preflight/postflight contract fingerprints only when the
lists are identical. See [postflight](runbooks/deployment-and-recovery.md#postflight).

Pass your cancellation token, or `CancellationToken.None` when intentionally
uncancellable. Do not use a `DbContext` concurrently. Analysis opens/closes a
connection only when it owns that open; it does not assume ownership of a
caller transaction. PostgreSQL caller transactions must be read-only and use
`RepeatableRead` or `Serializable`. Analysis never calls `Migrate` for you.

## Provider analyzer SPI

Provider packages implement `ISafeMigrationProviderAnalyzer`. Before any
runner method reads pending history, resolves model/environment state, acquires
a lock, opens a connection, or queries a catalog, it calls
`ValidateContext(DbContext)`. The method is synchronous and side-effect free:
it validates already configured provider state but performs no database I/O.
MySQL/MariaDB uses it to inspect the actual current connection's
`AllowUserVariables` setting, including replacement connections that share an
EF internal service provider. PostgreSQL currently validates only the non-null
context contract because it has no equivalent connection-string prerequisite.

## Reports, serialization, and failure

Reports include schema version, generation time, mode/status, instance and
target identity, provider/engine/server identity, model and operation-contract
fingerprints, ordered `SafeMigrationAssessment` entries, and unexpected objects.
Collections are immutable. `SafeMigrationUnexpectedObject` identifies preserved
objects; it is not an instruction to remove them.

`SafeMigrationContractFingerprint.Create(operations)` fingerprints ordered
safe intents, definitions, policies, and operation annotations. Ordinary
provider operations contribute only their CLR type name, not their properties
or SQL text. The fingerprint is therefore not a digest of the complete
migration artifact. Keep that artifact's independent digest and review ordinary
operations separately.

| Status | Meaning |
| --- | --- |
| `NoOperations` | No operation was assessed; verify intended target/history separately |
| `Ready` | Preflight permits the safe sequence subject to external gates; postflight confirms all supplied safe postconditions |
| `ReadyWithProviderOperations` | Ordinary EF/provider operations need independent review and postconditions |
| `Blocked` | One or more operations reject; do not execute/continue deployment |

`SafeMigrationReportJson.SerializeToUtf8Bytes(report)` returns a new
byte array. `Write(writer, report)` uses a caller-owned `Utf8JsonWriter` and
does not replace the caller's lifetime management. The packaged
[JSON Schema](../schemas/safe-migration-run-report-v1.schema.json)
defines wire codes and nullable fields. Treat the report as sensitive; it can
identify schema objects even though telemetry excludes them.

Invalid input can throw `ArgumentException`/derived exceptions; canonical model
drift throws `SafeMigrationModelMismatchException`; invalid integration can
throw `InvalidOperationException`; provider/database failures retain their
provider exception category. Cancellation is not converted into a successful
partial report. Stable assessment and runtime categories are documented in
[failure codes](runbooks/failure-codes.md). A blocked report is a result, not
necessarily an exception. Never interpret absence of an exception as permission
to ignore its status.

`SafeMigrationDiagnostics` publishes ActivitySource/Meter and metric names.
See [observability](runbooks/observability.md) for supported tags, measurement
boundaries, and privacy handling.
