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

1. validates the exact envelope and active engine features;
2. renders provider-owned baseline DDL through
   `MySqlMigrationOperationContext.RenderStandardOperation`;
3. creates one typed catalog/runtime plan from the real Doka handler context;
4. records that plan through a scoped capture lease during read-only analysis;
5. creates session-local assertion commands for runtime execution;
6. evaluates data-reading state only after catalog prerequisites pass;
7. executes target DDL only after state and repair preconditions pass;
8. clears session variables and temporary state on every successful plan.

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
- the PostgreSQL wrapper rejects a safe envelope when registration is absent or
  conflicting;
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

`StrictDefinition` compares the complete owned table shape: ordered columns,
primary key, unique constraints, checks, and foreign keys. Unexpected owned
members reject the strict operation.

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
|---|---|
| `Missing` | The operation target or source does not exist. |
| `Matching` | The relevant live definition satisfies the expected contract. |
| `Different` | The target name exists but the definition or rename target conflicts. |
| `Unsupported` | The active engine cannot represent the requested feature. |
| `DataBlocked` | Existing rows violate a required transition precondition. |
| `PrerequisiteMissing` | A required table does not exist, so dependent state cannot be evaluated safely. |

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

- resolves pending migrations through EF services without writing history;
- validates that a derived runtime context has the explicitly configured
  canonical migration model;
- reads provider/engine/server identity;
- runs ordered safe-operation classification in bounded parameterized chunks;
- projects earlier accepted operations into later preflight observations;
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
Latest EF/Npgsql patch profile. The exact model-differ plus fingerprint path
used by `SafeMigrationRunner` has provider-specific duration/allocation budgets.

Postflight re-runs the live classifier and requires each final postcondition to
hold. Reports are immutable and can be streamed through a caller-owned
`Utf8JsonWriter` without reflection or an intermediate DTO graph.

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
model differ reports no difference from the canonical `ModelSnapshot`.
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
and repeat execution. Doka 10.0.0 executes every handler-authored guard as one
bounded scope with ordered setup, one body, and reverse-order cleanup. Cleanup
runs after success, failure, or cancellation with an independent cancellation
token. A cleanup failure closes the connection and evicts its physical session
from the pool. Recovery remains forward fix or restore from a tested backup; a
heterogeneous convergence baseline has no destructive `Down`.

## Performance and memory

The runtime path has:

- one exact Doka registry lookup and one scoped handler instance per MySQL safe
  operation;
- no reflection, JSON intent serialization, type-name deserialization, or
  service-provider lookup per operation;
- allocations proportional to immutable input snapshots and generated
  commands;
- no database I/O during SQL generation;
- bounded parameterized classification chunks plus a scoped unexpected-object
  inventory per preflight or postflight;
- caller-owned report serialization support;
- bounded telemetry tags without object names or connection data.

The repository gates construction, planning, both provider generators, and
report serialization at 1, 100, and 1000 operations against explicit duration
and allocation budgets in schema-versioned Core, MySQL/MariaDB, and PostgreSQL
sets in `eng/performance-budgets.json`; missing, duplicate, unknown, and
orphaned measurements fail the run. It separately gates
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
- a package-only consumer with no ProjectReference;
- deterministic pairwise legacy states;
- every supported PostgreSQL major, every qualified Doka engine profile, and
  dependency Floor/Latest profiles;
- conservatively merged product line and branch coverage floors;
- byte-identical packages, SBOM, provenance, and NuGet readback.

Primary boundaries are based on the public contracts documented by
[EF Core migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/),
[Doka.EntityFrameworkCore.MySql](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql),
[Npgsql EF Core](https://www.npgsql.org/efcore/), and the database catalog and
DDL documentation linked from the operational guides.
