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
3. creates session-local catalog state and assertion commands;
4. executes target DDL only after the state and repair preconditions pass;
5. exposes raw read-only classifier expressions to the analyzer;
6. clears session variables and temporary state on every successful plan.

No permanent helper object or stored routine is created. Prepared statements
contain only DDL rendered from typed EF operations. Catalog literals are
extracted into `DbParameter` values for preflight.

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
- collation and comment;
- no default, literal default including literal `null`, and SQL default;
- computed SQL and stored/virtual form.

Indexes contain ordered key definitions with direction plus provider facets for
filter, included columns, operator classes, collations, null ordering,
null-distinctness, and MySQL prefix lengths. Constraints retain ordered columns,
principal identity, referential actions, and check SQL.

Comparison reads structured catalog metadata. It does not globally lowercase
or strip whitespace from expressions because doing so can change quoted
literals. Provider-specific normalization is limited to representation known to
be semantically irrelevant.

## Table modes and convergence

`StrictDefinition` compares the complete owned table shape: ordered columns,
primary key, unique constraints, checks, and foreign keys. Unexpected owned
members reject the strict operation.

`ConvergenceContainer` checks only that the target name denotes a table. It is
used by `ConvergeTable`, which immediately emits granular strict operations for
every required child object. This prevents an existing copied empty table from
hiding missing columns while preserving unknown extra objects.

## State and policy

Each provider must classify exactly one state:

| State | Meaning |
|---|---|
| `Missing` | The operation target or source does not exist. |
| `Matching` | The relevant live definition satisfies the expected contract. |
| `Different` | The target name exists but the definition or rename target conflicts. |
| `Unsupported` | The active engine cannot represent the requested feature. |
| `DataBlocked` | Existing rows violate a required transition precondition. |

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
- validates that a derived runtime context has the canonical migration model;
- reads provider/engine/server identity;
- runs every ordered safe-operation classification as one parameterized
  database command per preflight or postflight;
- projects earlier accepted operations into later preflight observations;
- reports ordinary provider-owned operations as not analyzable;
- inventories unexpected additive objects without deleting them;
- emits model and operation-contract fingerprints.

Postflight re-runs the live classifier and requires each final postcondition to
hold. Reports are immutable and can be streamed through a caller-owned
`Utf8JsonWriter` without reflection or an intermediate DTO graph.

Preflight cannot eliminate time-of-check/time-of-use drift. Deployment must
prevent out-of-band DDL and data writes that invalidate checked constraints.
Runtime guards and postflight remain authoritative.

## EF history and context ownership

SafeMigrations uses normal EF migration execution and history. A successful
migration receives one history row only after all of its commands complete. A
failed MySQL/MariaDB migration may have committed earlier DDL because the
server performs implicit DDL commits; retry converges from that partial state.

All application instances share one canonical Core model, migration assembly,
ordered migration sequence, and Core history table. A derived runtime context
is accepted only when EF's relational model differ reports no difference from
the canonical `ModelSnapshot`. Schema-bearing instance extensions use a
separate context and history.

## Concurrency and recovery

Provider migration locks serialize multiple migrators for the same database.
Different databases can proceed independently. SafeMigrations adds no process
global lock or mutable static cache.

Every multi-command provider plan is idempotent at command boundaries. Tests
cover failure after earlier standard DDL, same-session recovery after a guard
failure, and repeat execution. Recovery is forward fix or restore from a tested
backup; a heterogeneous convergence baseline has no destructive `Down`.

## Performance and memory

The runtime path has:

- one exact Doka registry lookup and one scoped handler instance per MySQL safe
  operation;
- no reflection, JSON intent serialization, type-name deserialization, or
  service-provider lookup per operation;
- allocations proportional to immutable input snapshots and generated
  commands;
- no database I/O during SQL generation;
- one parameterized classification batch plus one family-oriented unexpected-
  object inventory query per preflight or postflight;
- caller-owned report serialization support;
- bounded telemetry tags without object names or connection data.

The repository gates construction, planning, both provider generators, and
report serialization at 1, 100, and 1000 operations against explicit duration
and allocation budgets in `eng/performance-budgets.json`.

These budgets are deterministic regression and allocation gates for the
qualified runner profile. They are not a substitute for a statistically
controlled, hardware-normalized microbenchmark laboratory when absolute or
cross-machine performance claims are required.

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
