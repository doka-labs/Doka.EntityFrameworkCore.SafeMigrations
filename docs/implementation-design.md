# Implementation design

## Architectural objective

SafeMigrations turns an ordered EF Core migration into a deterministic
convergence contract. Provider-neutral code owns intent, policy, expected
definitions, planning, fingerprints, and reports. Provider packages own live
catalog interpretation and SQL generation. The database remains authoritative
for observed state; neither provider tries to reconstruct history from names or
SQL text.

```text
MigrationBuilder extension
  -> sealed SafeMigrationOperation
     -> sealed typed SafeMigrationIntent
        + immutable Expected Definition
        + SafeMigrationPolicy
  -> provider live-state classifier
  -> pure SafeMigrationDecisionPlanner
  -> provider command plan or classified rejection
  -> read-only postcondition verification
```

Core has no compile-time dependency on MySQL, MariaDB, PostgreSQL, Doka's
provider, or Npgsql.

Source ownership follows the hybrid vertical-slice contract in
[Vertical-slice architecture](vertical-slice-architecture.md). Public
namespaces and package boundaries remain stable; core and provider behavior is
co-located by `Schemas`, `Tables`, `Columns`, `Indexes`, and the four constraint
families. Shared lifecycle orchestration remains centralized.

## Package boundaries

### Core

`Doka.EntityFrameworkCore.SafeMigrations` owns:

- the exact `SafeMigrationOperation` envelope;
- the closed set of 20 intent kinds;
- immutable table, column, index, and constraint definitions;
- the closed typed SQL-expression tree and opaque-expression provenance;
- `SafeMigrationPolicy` and `SafeMigrationTableMode`;
- the total, I/O-free `SafeMigrationDecisionPlanner`;
- `ISafeMigrationRunner` and the report contract;
- model and ordered-operation SHA-256 fingerprints;
- unexpected-object inventory;
- reflection-free report JSON and its packaged JSON Schema;
- bounded diagnostic names and low-cardinality telemetry.

It does not generate provider SQL and does not register a relational provider.

### MySQL and MariaDB

`Doka.EntityFrameworkCore.SafeMigrations.MySql` registers exactly one
`IMySqlMigrationOperationHandler` for the exact `SafeMigrationOperation` type.
Doka's public SPI performs constant-time exact-type dispatch and remains owner
of the provider migrations generator. SafeMigrations does not derive from,
replace, reflect over, or copy Doka provider internals.

The handler:

1. validates the actual context connection before migration-history or catalog access;
2. validates the exact envelope and active engine features;
3. renders provider-owned baseline DDL through
   `MySqlMigrationOperationContext.RenderStandardOperation`;
4. creates one typed catalog/runtime plan from the real Doka handler context;
5. records that plan through a scoped capture lease during read-only analysis;
6. creates session-local assertion commands for runtime execution;
7. evaluates data-reading state only after catalog prerequisites pass;
8. executes target DDL only after state and repair preconditions pass;
9. clears session variables and temporary state on every successful plan.

No permanent helper object or stored routine is created. Prepared statements
contain only DDL rendered from typed EF operations and the lazy state query
derived from the typed plan. The analyzer renders the same plan directly with
`DbParameter` values; it never parses generated migration commands or
duplicates Doka's engine-feature profile.

### PostgreSQL

`Doka.EntityFrameworkCore.SafeMigrations.PostgreSql` decorates Npgsql's public
migrations generator boundary. It intercepts only `SafeMigrationOperation` and
delegates every ordinary EF operation to the provider generator. Safe commands
use parameter-free migration SQL because EF migration scripts have no runtime
parameter channel; all identifiers and literals are rendered by Npgsql/EF SQL
helpers and type mappings.

The read-only PostgreSQL analyzer builds parameterized `pg_catalog` queries
directly. Guarded runtime execution uses PostgreSQL anonymous blocks and normal
EF transaction semantics.

## Fail-closed ownership

A safe operation is never encoded as an annotation on an ordinary EF
operation. Without the matching adapter, the provider cannot silently execute
the operation as normal DDL:

- Doka rejects an unowned `SafeMigrationOperation`;
- Npgsql rejects the unknown safe envelope when its adapter is absent;
  incompatible SafeMigrations generator registration also fails closed;
- multiple owners for the same exact operation type are rejected;
- provider-owned ordinary operations continue through the base provider.

Integration tests prove that missing and conflicting registration writes
neither target DDL nor the EF history row.

## Expected definitions

Definitions snapshot enumerable input exactly once and expose read-only
collections. The column contract distinguishes:

- CLR type and explicit store type;
- nullability, Unicode, maximum length, fixed length, and row-version facets;
- precision and scale;
- structured collation identity and comment;
- no default, literal default including literal `null`, and SQL default;
- computed expression and stored/virtual form.

Indexes contain ordered key definitions with direction plus provider facets for
filter, included columns, operator classes, collations, null ordering,
null-distinctness, and MySQL prefix lengths. Constraints retain ordered columns,
principal identity, referential actions, and check SQL.

SQL-bearing facets use a typed expression tree whose identifier, literal,
operator, cast, collation, function, and current-value roles are explicit.
Providers render these nodes and compare only catalog representations whose
structural equivalence they can prove. Legacy raw SQL is opaque and returns a
stable `Unsupported` reason; it never authorizes `Matching`. Core renames only
typed identifier nodes. Opaque dependent SQL becomes unproven after a rename
and remains fail-closed.

Comparison reads structured catalog metadata. It does not globally lowercase
or strip whitespace from expressions because doing so can change quoted
literals. A null column collation means the provider-inferred effective
default and is compared exactly; it is never a wildcard.

`SafeMigrationCollationIdentifier` carries schema and name as separate ordinal
fields, so a dot inside either identifier is data rather than a parser
separator. PostgreSQL resolves the identity to one catalog OID. MySQL and
MariaDB support only an unqualified collation name and classify a qualified
identity as unsupported before target DDL.

MySQL 8.4 and 9.7 expose typed check and generated-column expressions through
`INFORMATION_SCHEMA` in canonical and parser-display forms. The latter escapes
identifier punctuation and can expose non-ASCII identifier bytes as Latin-1
code points. The MySQL adapter therefore adds that exact, token-bounded display
form only for expressions rendered from the closed typed tree. MariaDB uses the
canonical form. Opaque SQL never enters this compatibility path, and both
engine families retain negative value-, operator-, and identifier-drift tests.

## Table modes and convergence

EF Core's design-time service pipeline supplies the provider model differ and
C# migration generator. SafeMigrations replaces only the public C# generator
services at design time, delegates operation rendering to EF Core, validates
the expected generated call shape, and substitutes the safe table/index method
name. This preserves provider-rendered arguments and annotations without
forking EF Core's generator implementation. An unexpected upstream output
shape stops scaffolding instead of producing ambiguous source.

Provider package `buildTransitive` assets add EF's
`DesignTimeServicesReferenceAttribute` to a consuming startup assembly that
directly references the EF Design package or the EF Tools package that supplies
Design transitively. A project with neither package is intentionally treated as
runtime-only and receives no design-service attribute or warning. Runtime
service-provider identity excludes scaffolding mode and legacy policy because
both change generated source only and do not alter runtime service registration.
The selected values are read from that context's options by the design-time
service provider and become literal calls and arguments in the generated
migration.

`Strict` rewrites table creation, index creation, and table removal.
`LegacyConvergence` rewrites the same forward table/index operations but
replaces `Down` with a deterministic exception: adopted legacy objects have no
provable destructive inverse. Other EF operations are delegated unchanged so
their policy cannot be guessed by the scaffolder.

The generated table call also freezes either `ThrowIfDifferent` or
`RepairIfSafe`. Repair-capable `EnsureColumnIntent` analysis separates mutable
nullability, default, and comment facets from invariant store type, collation,
generation/identity, row-version, and provider-annotation facets. A repair is
eligible only for an ordinary column with matching invariants. Tightening
nullability performs a catalog and data precondition and classifies existing
nulls as `DataBlocked`. MySQL/MariaDB delegates the complete replacement
definition to Doka's `AlterColumnOperation` renderer; PostgreSQL delegates facet
deltas to Npgsql. Apply and repair SQL are distinct guarded branches and share
the same postcondition. Both adapters first prove target-column existence from
the catalog before compiling or executing a data-reading null probe. A missing
target therefore remains `Missing` or `DataBlocked` according to add safety and
never fails with an engine-level unknown-column error. Explicit
`AlterColumnIntent` repairs continue to execute their reviewed provider
baseline; only inferred `EnsureColumnIntent` repair needs a separately rendered
branch.

| EF operation | `Strict` source | `LegacyConvergence` source |
| --- | --- | --- |
| `CreateTable` | `CreateTableIfNotExists` | `ConvergeTableFromModel` |
| Single-column `CreateIndex` | `CreateIndexIfNotExistsFromModel` | Same |
| Multi-column `CreateIndex` | `CreateCompositeIndexIfNotExistsFromModel` | Same |
| Generated rollback of `CreateTable` | `DropTableIfExists` | Entire `Down` body rejects before DDL |

The `*FromModel` methods are stable public targets for generated migration
source. They capture EF's provider-rendered operation into immutable expected
definitions; they are not a second runtime discovery layer.

The typed table callback creates EF operations in memory and immediately
converts them to immutable expected definitions. Provider column annotations
are snapshotted, fingerprinted, restored to baseline DDL, and compared through
the provider catalog. Unsupported annotation value types fail during capture;
unmodeled operation annotations classify unsupported before DDL.

`StrictDefinition` compares the complete owned table shape: ordered columns,
primary key, unique constraints, checks, and foreign keys. Unexpected owned
members reject the strict operation. MySQL and MariaDB expose a unique index in
both `STATISTICS` and `TABLE_CONSTRAINTS`; full-batch analysis therefore admits
only unique-index names present in the same expected operation catalog. Normal
EF runtime generation receives operations one at a time, so it derives the
same bounded name set from EF's target relational model and caches that
projection for the scoped handler. Generation without either evidence source
remains fail-closed. This normalization does not admit an unrelated unique key.
The behavior follows the
official [MySQL `TABLE_CONSTRAINTS` contract](https://dev.mysql.com/doc/refman/8.4/en/information-schema-table-constraints-table.html)
and [MariaDB `TABLE_CONSTRAINTS` contract](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-table_constraints-table).

`ConvergenceContainer` checks only that the target name denotes a table. It is
used by `ConvergeTable`, which immediately emits granular operations for every
required child object using the supplied policy (`ThrowIfDifferent` by
default). Choosing `ExistenceOnly` explicitly relaxes child-definition checks;
an existing table never skips emitting the child operations. This prevents a
copied empty table from hiding missing columns while preserving unknown extra
objects.

## State and policy

Each provider must classify exactly one state:

| State | Meaning |
| --- | --- |
| `Missing` | The operation target or source does not exist. |
| `Matching` | The relevant live definition satisfies the expected contract. |
| `Different` | The target name exists but the definition or rename target conflicts. |
| `Unsupported` | The active engine cannot represent the requested feature. |
| `DataBlocked` | Existing rows violate a required transition precondition. |
| `PrerequisiteMissing` | A required table or referenced column does not exist, so dependent state cannot be evaluated safely. |

The pure planner maps operation kind, state, policy, and repair capability to
one action. It is total over all defined enum combinations and performs no
allocation-backed discovery, SQL generation, service lookup, or I/O.

Repair is an allowlist, not a general reconciliation algorithm. Missing
nullable/default/computed columns and additive indexes or constraints can be
safe after data preconditions. Alter-column repair requires the live column to
match the declared old definition and permits only the implementation's
lossless metadata/default transition. Type narrowing, collation changes,
renames, primary-key reconstruction, and violated constraints reject.

## Preflight and postflight

Preflight is a separate API. `ISafeMigrationRunner`:

- invokes provider context validation before pending-history, model, environment,
  lock, catalog access, or connection opening;
- resolves pending migrations through EF services without writing history;
- validates that a derived runtime context has the explicitly configured
  canonical migration model;
- reads provider/engine/server identity;
- runs ordered safe-operation classification in bounded parameterized chunks;
- projects earlier accepted safe operations and recognized deterministic
  structural postconditions of ordered ordinary EF operations into later
  preflight observations;
- reports ordinary provider-owned operations as not analyzable;
- inventories unexpected additive objects without deleting them;
- emits model and operation-contract fingerprints.

The model fingerprint is a versioned, provider-bound SHA-256 envelope over an
ordinally sorted relational metadata stream. It covers tables, columns, keys,
foreign keys, indexes, checks, sequences, views, queries, functions, stored
procedures, and classified migration annotations. Values are length-prefixed
and streamed directly into the hash. The contract does not depend on EF Core's
debug-string format; an unknown migration annotation value fails closed.

Facet-isolation tests mutate every serialized relational family independently.
Golden digests run in separate provider test processes and again under the
committed EF/Npgsql dependency graph. The exact model-differ plus fingerprint path
used by `SafeMigrationRunner` has provider-specific duration/allocation budgets.

Postflight re-runs the live classifier without preflight projection and
requires every supplied postcondition to hold simultaneously. The caller
supplies an explicitly reviewed final-state contract; an execution sequence
whose intermediate objects are later renamed, dropped, or altered is not
automatically such a contract. Bind the final-state contract and its fingerprint
to the same artifact, model, and target migration as execution. The
[postflight runbook](runbooks/deployment-and-recovery.md#postflight) owns these
checks. Reports are immutable and can be streamed through a caller-owned
`Utf8JsonWriter` without reflection or an intermediate DTO graph.

Operation-contract fingerprints include safe intent, expected definitions,
policy, and ordering. Ordinary provider operations contribute only their CLR
type marker. Their properties and SQL require separate review and the digest
of the immutable deployment artifact; they are not fully bound by that hash.
Recognized ordinary create/add/alter/drop/rename table and column operations may
contribute a conditional structural postcondition to later prerequisite
projection. Their assessment remains `provider_owned_not_analyzed`, and no
facet or data-safety claim is inferred beyond the bounded projection facts.

Each chunk contains at most 512 MySQL/MariaDB operations or 128 PostgreSQL
operations, 16,000 bound parameters, and 4 MiB of UTF-8 SQL plus parameter
payload. The lower PostgreSQL cap prevents planner-memory exhaustion on the
supported PostgreSQL 14 baseline. MySQL/MariaDB additionally uses half the live
`max_allowed_packet` as an upper bound. Repeated typed values are interned
within a chunk, global ordinals span chunks, and results are published only
after every chunk succeeds. PostgreSQL holds one read-only `RepeatableRead`
snapshot and transaction-scoped analysis advisory lock across analysis. This
analysis lock is not an application write fence. A caller-owned
transaction is accepted only when it is read-only and uses `RepeatableRead` or
`Serializable`; weaker or read-write transactions fail before catalog access
and remain caller-owned. MySQL/MariaDB uses the provider migration lock;
out-of-band DDL remains prohibited during the window.

Preflight cannot eliminate time-of-check/time-of-use drift. Deployment must
prevent out-of-band DDL and data writes that invalidate checked constraints.
Runtime guards and postflight remain authoritative.

The projection tracks only prerequisites established by accepted earlier safe
operations or deterministic structural postconditions of recognized earlier
ordinary EF operations. Add/create/alter/drop/rename table and column operations
update compact presence and unique-index safety facts in operation order; they
do not create complete projected table definitions. For a unique index on an
existing table, a live `PrerequisiteMissing` result becomes projected `Missing`
only when every referenced column is known and a newly added key column is
nullable, non-computed, has no non-null default, and uses default null-distinct
semantics. Other unique transitions remain blocked. Unrecognized provider
operations do not invent projection facts, and every ordinary operation still
requires independent review and postcondition evidence. A recognized ordinary
operation invalidates any complete projected table image that its provider-owned
side effects could make stale. An unrecognized operation discards all projection
facts, because arbitrary DDL or data changes cannot safely carry earlier
inferences forward.

`AnalyzePendingMigrationsAsync` calls provider validation before EF's
`IHistoryRepository.GetAppliedMigrationsAsync` path. This ordering is required
because that EF service queries the history table; see the official
[`IHistoryRepository` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.ihistoryrepository?view=efcore-10.0).

## EF history and context ownership

SafeMigrations uses normal EF migration execution and history. A successful
migration receives one history row only after all of its commands complete. A
failed MySQL/MariaDB migration may have committed earlier DDL because the
server performs implicit DDL commits; retry converges from that partial state.

All application instances share one canonical Core model, migration assembly,
ordered migration sequence, and Core history table. SafeMigrations replaces
the scoped `IMigrationsAssembly`; the generic registration names the canonical
context explicitly and affects runtime migration discovery, `IMigrator`,
`dotnet ef`, scripts, and bundles. A derived runtime context is accepted only
when its type is assignable to that canonical context and EF's relational
model differ reports no difference from the canonical `ModelSnapshot`, when
one is present. Without a snapshot, analysis still fingerprints the runtime
model but cannot compare it to the canonical snapshot. An independently
established `ExpectedModelFingerprint` can bind a snapshot-free analysis;
keep the canonical snapshot for normal EF migration deployments.
Schema-bearing instance extensions use a separate context and history.

PostgreSQL composes a custom provider migrations generator only through the
explicit typed registration overload. The adapter delegates ordinary
operations and SafeMigrations baselines through that selected generator; it
does not silently replace an application customization with the Npgsql
default.

## Concurrency and recovery

Provider migration locks serialize multiple migrators for the same database.
Different databases can proceed independently. SafeMigrations adds no process
global lock or mutable static cache.

Every multi-command provider plan is idempotent at command boundaries. Tests
cover failure after earlier standard DDL, same-session recovery after a guard
failure, cancellation during blocked DDL, cleanup failure with pool eviction,
and repeat execution. Doka 10.1.2 executes every handler-authored guard as one
bounded scope with ordered setup, one body, and reverse-order cleanup. Cleanup
runs after success, failure, or cancellation with an independent cancellation
token. A cleanup failure closes the connection and evicts its physical session
from the pool. Recovery remains forward fix or restore from a tested backup; a
heterogeneous convergence baseline has no destructive `Down`.

## Performance and memory

The runtime path has:

- one exact Doka registry lookup/dispatch and one guarded command scope per
  MySQL/MariaDB safe operation, reusing the scoped handler instance;
- no reflection, JSON intent serialization, type-name deserialization, or
  service-provider lookup per operation;
- input/model-, command-, assessment-, and catalog-inventory-dependent
  allocations; individual request bounds are not a whole-run memory cap;
- no database I/O during SQL generation;
- bounded parameterized classification chunks plus a scoped unexpected-object
  inventory per preflight or postflight;
- caller-owned report serialization support;
- bounded telemetry tags without object names or connection data.

The repository gates construction, planning, both provider generators, and
report serialization at 1, 100, and 1000 operations against strict allocation
ceilings and coarse wall-clock ceilings in schema-versioned Core,
MySQL/MariaDB, and PostgreSQL sets in `eng/performance-budgets.json`; missing,
duplicate, unknown, and orphaned measurements fail the run. The broad duration
ceilings account for shared hosted-runner CPU variance and only detect gross
regressions. It separately gates
the canonical snapshot initialization, `IMigrationsModelDiffer` comparison,
and model fingerprint path used by the runner.

Provider integration tests measure 20 complete pooled runner calls against 100
expected tables in clean and noisy catalogs. The noisy catalog adds 1,000
foreign tables with columns and indexes. Each matrix cell persists p50/p95,
assessment counts, and unexpected-object counts; noisy p95 must remain within
`2 * clean p95 + 250 ms`, and foreign child objects must not escape the
server-side expected-table scope.

The construction budgets are deterministic regression and allocation gates;
the live measurements are same-runner relative SLO evidence rather than an
absolute cross-machine throughput claim.

## Verification surfaces

Release qualification covers:

- direct generator tests and real catalogs;
- `Database.MigrateAsync` and `IMigrator.MigrateAsync`;
- migration history success and failure paths;
- normal, idempotent, and no-transaction scripts;
- `dotnet ef database update` and Migration Bundle;
- external internal-service-provider registration;
- package-only consumers with no ProjectReference for direct Design,
  Tools-only, and runtime-only dependency layouts;
- deterministic pairwise legacy states;
- every supported PostgreSQL major, every qualified Doka engine profile, and
  locked dependency graph;
- conservatively merged product line and branch coverage floors;
- byte-identical packages, SBOM, provenance, and NuGet readback.

Primary boundaries are based on the public contracts documented by
[EF Core migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/),
[EF Core design-time tools architecture](https://learn.microsoft.com/en-us/ef/core/miscellaneous/internals/tools),
[Doka.EntityFrameworkCore.MySql](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql),
and [Npgsql EF Core](https://www.npgsql.org/efcore/), retrieved 2026-08-27,
plus the database catalog and DDL documentation linked from the operational
guides.
