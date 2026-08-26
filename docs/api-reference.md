# Public API reference

This guide explains the public inputs, outputs, and failure boundaries without
requiring readers to study the implementation. The exact signatures, parameter
documentation, and nullability annotations ship as XML documentation beside
each `lib/net10.0` assembly. After installing the provider package, use IDE
completion/Quick Documentation for the selected package version. The Core,
[MySQL/MariaDB](../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/PublicAPI.Unshipped.txt),
and [PostgreSQL](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/PublicAPI.Unshipped.txt)
API baselines are review inventories, not substitutes for this guide or XML.

## Packages and registration

Core types use `Doka.EntityFrameworkCore.SafeMigrations`. The provider-specific
registration namespaces append `.MySql` or `.PostgreSql`.

| Entry point | Inputs and result | Boundary |
| --- | --- | --- |
| `UseMySqlSafeMigrations()` | Configured EF options builder; returns the builder | Add after Doka `UseMySql`; supports both MySQL and MariaDB |
| `UsePostgreSqlSafeMigrations()` | Configured EF options builder; returns the builder | Add after `UseNpgsql` |
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
requires `Allow User Variables=true` on the actual connection. Missing or
conflicting adapter ownership fails closed. See
[registration examples](../README.md#provider-registration).

## MigrationBuilder operations

These extensions append sealed `SafeMigrationOperation` envelopes; they do
not execute SQL at construction time. Single-operation methods return
`OperationBuilder<SafeMigrationOperation>`. `ConvergeTable` appends multiple
operations and returns the original `MigrationBuilder`.

| Family | Explicit definition API | Familiar builder API and other operations |
| --- | --- | --- |
| Schema | `EnsureSchemaExists` | `DropSchemaIfExists` |
| Table | `EnsureTable`, `ConvergeTable` | `CreateTableIfNotExists<TColumns>`, `DropTableIfExists`, `RenameTableIfExists` |
| Column | `EnsureColumn`, `AlterColumnIfDifferent` | `AddColumnIfNotExists<T>`, `DropColumnIfExists`, `RenameColumnIfExists` |
| Index | `EnsureIndex` | `CreateIndexIfNotExists`, `DropIndexIfExists`, `RenameIndexIfExists` |
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
| `AnalyzeAsync` | Explicit ordered `IReadOnlyList<MigrationOperation>` | Preflight report including projected earlier safe operations |
| `VerifyAsync` | The same explicit contract whose final conditions are required | Postflight report |

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
Calling EF migration with a null target means latest, which can exceed a
specifically targeted preflight.

Pass your cancellation token, or `CancellationToken.None` when intentionally
uncancellable. Do not use a `DbContext` concurrently. Analysis opens/closes a
connection only when it owns that open; it does not assume ownership of a
caller transaction. PostgreSQL caller transactions must be read-only and use
`RepeatableRead` or `Serializable`. Analysis never calls `Migrate` for you.

## Reports, serialization, and failure

Reports include schema version, generation time, mode/status, instance and
target identity, provider/engine/server identity, model and operation-contract
fingerprints, ordered `SafeMigrationAssessment` entries, and unexpected objects.
Collections are immutable. `SafeMigrationUnexpectedObject` identifies preserved
objects; it is not an instruction to remove them.

| Status | Meaning |
| --- | --- |
| `NoOperations` | No operation was assessed; verify intended target/history separately |
| `Ready` | Analyzed safe operations allow proceeding, subject to deployment fences |
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
