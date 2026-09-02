---
id: D-009
status: implemented
date: 2026-09-02
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Automatic source-frozen convergence for EF Core model-managed data"
supersedes: []
superseded-by: []
amends: [D-004, D-005, D-008]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-009 -- Converge model-managed data through guarded source transitions

## Context and Problem Statement

EF Core turns `HasData` model differences into `InsertDataOperation`,
`UpdateDataOperation`, and `DeleteDataOperation`. SafeMigrations previously
left those operations provider-owned and unanalyzed. A database which already
contained the same primary-key row could therefore pass structural preflight
and later fail on an unconditional insert. A blind ignore or upsert would avoid
that exception by weakening data integrity: it could hide a different live row,
select an alternate unique conflict, or overwrite a value which changed outside
the migration sequence.

The decision is whether and how newly scaffolded model-managed changes become
safe, idempotent migration operations without requiring authors to duplicate
`HasData` values manually or changing existing migration files.

## Decision Drivers

- Ordinary `dotnet ef migrations add` must produce the safe path automatically.
- Existing equal rows are idempotent; different rows are never silently
  overwritten or deleted.
- Updates and deletes need captured source values that are absent from the
  forward EF operation alone.
- The contract must support composite keys, value converters, nulls, candidate
  keys, and incoming dependencies on MySQL, MariaDB, and PostgreSQL.
- Runtime races and trigger effects must fail their target postcondition.
- Reports and diagnostics must not expose keys or managed values.
- Large model-managed sets need deterministic request, allocation, and command
  bounds without a new public tuning knob.
- Existing compiled migrations and raw hand-authored data operations must keep
  their established behavior.

## Considered Options

- Pair forward and inverse model differences and emit guarded source-frozen
  operations
- Ignore duplicate-key inserts
- Use provider upsert or merge syntax
- Read live values while scaffolding and generate environment-specific source
- Require migration authors to replace every raw data operation manually

## Decision Outcome

Chosen option: "Pair forward and inverse model differences and emit guarded
source-frozen operations", because only the two model states contain enough
information to prove key identity, captured source values, and the intended
target without consulting one developer database.

SafeMigrations decorates the active provider `IMigrationsModelDiffer`, delegates
all provider differences, and completes model-managed store types and the
required candidate/dependency maps from public relational-model metadata. The
C# migration generator pairs forward and inverse rows by schema, table, ordered
key columns, and canonical typed key values. Insert/inverse-delete yields an
ensure, update/inverse-update yields old and new values, and
delete/inverse-insert yields the complete removed row. Missing, duplicate,
contradictory, ambiguous, or unused inverse rows stop scaffolding.

The generated public operations are `EnsureModelManagedDataFromModel`,
`UpdateModelManagedDataFromModel`, and `DeleteModelManagedDataFromModel`. They
use a fixed `ThrowIfDifferent` contract and expose no overwrite policy. Ensure
inserts only absent primary keys. Update and delete repeat the captured source
predicate in compare-and-swap DML. Delete additionally rejects dependent-row
effects which were not discharged by an earlier accepted model-managed delete.
Every mutation validates its target postcondition.

MySQL and MariaDB use `<=>`; PostgreSQL uses `IS NOT DISTINCT FROM`. The
implementation does not use insert-ignore, upsert, conflict-update, or generic
merge syntax. MySQL/MariaDB model-managed mutation requires a transactional
table engine. Normal EF migration transaction ownership remains authoritative;
provider handlers do not introduce a nested transaction.

Rows retain EF operation order and are partitioned at 128 rows or 4,096 value
cells, whichever limit is reached first. Ordered preflight projection stores
only identities touched by the migration. Compact provider evidence and
fingerprints retain canonical hashes, not a second copy of report-visible raw
values.

The transformation applies only to newly scaffolded, exactly paired
model-managed operations while SafeMigrations scaffolding is enabled. Existing
migration source and hand-authored raw data operations remain provider-owned and
unanalyzed. `HasData` remains in the model and snapshot. General bootstrap,
mutable, secret, environment-specific, or large data belongs in `UseSeeding`,
`UseAsyncSeeding`, or an application-owned workflow.

### Consequences

- Good, because an equal legacy row becomes an idempotent no-op instead of a
  duplicate-key failure.
- Good, because update/delete races and dependency effects fail closed without
  an implicit overwrite, cascade, nulling, or defaulting authorization.
- Good, because authors keep one canonical `HasData` declaration and normal EF
  tooling.
- Good, because provider separation and existing migration compatibility remain
  intact.
- Bad, because migration source and generated scripts continue to contain the
  model-managed values, as EF's source-controlled data contract requires.
- Bad, because ambiguous provider/model differences now stop scaffolding and
  require the model or migration transition to be made explicit.
- Bad, because large data sets remain inappropriate for `HasData` even with
  bounded operation generation.

### Confirmation

- Run Core contract, pairing, projection, privacy, and total-planner tests.
- Run MySQL 8.4/9.7, MariaDB 10.11/11.4/11.8/12.3, and PostgreSQL 14-18 live
  model-managed suites, including positive, negative, concurrency, trigger,
  cancellation, composite-key, dependency-action, retry, and replay cases.
- Run `eng/verify-ef-tooling.sh` and require safe generated calls, no raw
  HasData-derived calls, ready initial-deployment preflight, identical generated
  operation replay, successful scripts/bundles, and exact history.
- Run package-only consumers and public API/package-content qualification.
- Run the 100,000 mixed-operation analysis and 50,000 mixed-row execution gates
  plus model-managed allocation benchmarks.
- Keep release acceptance separate from implementation status. Hosted matrix,
  package publication, and public readback remain mandatory release gates, but
  they do not change whether this architectural decision is implemented in the
  repository source.

## Pros and Cons of the Options

### Pair forward and inverse model differences and emit guarded source-frozen operations

- Good, because it derives the missing old state from EF's canonical source and
  target models without reading one installation.
- Good, because one provider-neutral contract can retain provider-specific type
  and null-safe comparison semantics.
- Bad, because pairing and type completion add a strict design-time compatibility
  boundary which must be requalified on EF/provider updates.

### Ignore duplicate-key inserts

- Good, because an already-present primary key would not stop execution.
- Bad, because equal and different rows become indistinguishable and unrelated
  integrity failures can be hidden.

### Use provider upsert or merge syntax

- Good, because providers offer concise syntax for common insert/update flows.
- Bad, because unique-arbiter and trigger semantics differ by provider and do
  not prove the source-frozen primary-key transition.

### Read live values while scaffolding and generate environment-specific source

- Good, because one database could supply missing old values.
- Bad, because heterogeneous installations have no canonical live state and
  migration source would depend on the developer environment.

### Require migration authors to replace every raw data operation manually

- Good, because the resulting source could state the complete contract.
- Bad, because it duplicates model-managed data, invites transcription errors,
  and makes the safe path less usable than ordinary EF tooling.

## More Information

D-008 remains authoritative for EF design-time discovery, generator composition,
strict/legacy selection, and source freezing. This decision adds exact
model-differ decoration and data-operation ownership. D-004 remains
authoritative for read-only analysis, EF execution, transaction ownership, and
recovery; this decision adds compare-and-swap/postcondition semantics. D-005
remains authoritative for bounded evidence and telemetry privacy; this decision
adds row/cell limits and value non-disclosure.

### Re-evaluation Triggers

- EF Core changes the public model-differ/data-operation contracts or no longer
  supplies a usable inverse model difference.
- A supported provider changes value conversion, transaction, trigger,
  referential-action, or null-safe comparison behavior.
- Evidence shows that a supported model-managed type or dependency cannot be
  represented without weakening compare-and-swap identity.
- Resource measurements invalidate the established row, cell, payload, or
  allocation bounds.

### Decision History

- 2026-09-02: Decision recorded with status proposed.
- 2026-09-02: Dominic Kalkbrenner selected automatic source-frozen convergence
  with fixed fail-closed semantics; status changed from proposed to accepted.
- 2026-09-02: Implemented the Core, MySQL/MariaDB, and PostgreSQL vertical
  slices; automatic EF tooling integration; guarded execution; bounded
  projection; privacy controls; public API and documentation; positive,
  negative, replay, concurrency, resource, and package qualification. Status
  changed from accepted to implemented. Hosted release qualification and public
  package readback remain publication gates.

### Implementation References

- [Core model-managed-data slice](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features/ModelManagedData)
- [Model-differ decoration](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationMigrationsModelDiffer.cs)
- [Forward/inverse pairing](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationModelManagedDataPairer.cs)
- [MySQL/MariaDB slice](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/ModelManagedData)
- [PostgreSQL slice](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Features/ModelManagedData)
- [Real EF tooling qualification](../../eng/verify-ef-tooling.sh)
- [Deployment and recovery](../runbooks/deployment-and-recovery.md)

### Sources

- [EF Core model-managed data](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding) (primary source; retrieved 2026-09-02)
- [EF Core applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) (primary source; retrieved 2026-09-02)
- [EF Core 10.0.11 MigrationsScaffolder](https://github.com/dotnet/efcore/blob/v10.0.11/src/EFCore.Design/Migrations/Design/MigrationsScaffolder.cs) (primary source; retrieved 2026-09-02)
- [EF Core 10.0.11 MigrationsModelDiffer](https://github.com/dotnet/efcore/blob/v10.0.11/src/EFCore.Relational/Migrations/Internal/MigrationsModelDiffer.cs) (primary source; retrieved 2026-09-02)
- [MySQL 8.4 INSERT ON DUPLICATE KEY UPDATE](https://dev.mysql.com/doc/refman/8.4/en/insert-on-duplicate.html) (primary source; retrieved 2026-09-02)
- [MariaDB null-safe equal operator](https://mariadb.com/docs/server/reference/sql-structure/operators/comparison-operators/null-safe-equal) (primary source; retrieved 2026-09-02)
- [PostgreSQL comparison functions](https://www.postgresql.org/docs/current/functions-comparison.html) (primary source; retrieved 2026-09-02)
- [PostgreSQL foreign-key actions](https://www.postgresql.org/docs/current/ddl-constraints.html) (primary source; retrieved 2026-09-02)
