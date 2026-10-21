---
id: D-017
status: proposed
date: 2026-10-20
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Bounded generated expressions, native identity and ordered source dependencies"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-017 -- Generated-expression and dependency contracts

## Context and Problem Statement

EF-generated computed-column and CHECK SQL can express guarded arithmetic that
SafeMigrations previously retained as opaque text. SQLite also omitted declared
generated-column facets during safe creation, then rejected its own schema.
Parsing alone does not establish native types, determinism, nullability,
storage, identifier binding or the safety of later source changes.

The decision question is how to support these expressions without changing
native semantics or treating a future model as current database evidence.
Implementation began before this record. This review draft does not reconstruct
an earlier approval date or imply approval of its complete rationale.

## Decision Drivers

- Preserve captured native numeric typing and identifier/literal roles.
- Keep expression grammar, resource use and eligibility checks bounded.
- Retain current ordered dependency lifetimes in preflight and runtime.
- Avoid structural application-row scans, administrative dependency privileges,
  automatic cascading removal and hidden storage changes.
- Reuse accepted-state inventories and existing batched metadata capture.

## Considered Options

- Bounded captured expressions with native certificates
- Keep provider expression SQL opaque
- Admit expressions by parsing or preparation alone
- Add managed constraints or persistence for required results

## Decision Outcome

Chosen option: "Bounded captured expressions with native certificates", because
it permits reviewed generated semantics while retaining independent native
binding and dependency checks.

Core owns immutable searched CASE, bounded parsing, captured-source provenance
and ordered accepted state. Providers own SQL rendering, native identity,
type/storage eligibility, required-result proofs and dependency restrictions.
The original numeric spelling survives capture; a simplified tree must not
silently erase an overloadable operator role. Parsing authorizes neither
arbitrary functions nor custom native bindings.

The maintainer explicitly selected strict semantic non-NULL proofs for required
MariaDB and virtual SQL Server results instead of helper CHECK constraints.
Distinct SQL Server case-sensitive inputs that a case-insensitive tempdb compiler
cannot preserve are specifically unsupported, as selected by the maintainer;
supported aliases do not become interchangeable physical columns.

The maintainer also authorized generated-source DROP checks. Surviving
dependencies block removal; explicit dependent-first removal remains possible.
No generated column is automatically removed. PostgreSQL 14's direct AUTO
dependency form requires care because native source removal can remove the
generated member even without CASCADE.

For PostgreSQL source DROP, the maintainer selected an explicit table lock and
fresh classification under Read Committed semantics. Missing lock authority or
an older Repeatable Read/Serializable snapshot must reject the destructive
operation, not silently grant rights or change isolation. ALTER ownership alone
does not establish the separate LOCK privilege. Initial absence remains a
non-mutating no-op, not a permission to execute unchecked DROP.

Generated-set completeness is separate from rows, emptiness and full physical
layout. Native empty-survivor evidence cannot survive an incompatible owner
lifetime or erase a newer accepted generated member. MySQL/MariaDB authored or
model candidates need complete fresh native equality before proving
nondependency. Partial candidates are never merged into fabricated completeness;
unknown facets and opaque effects are not known nulls or absence.

MariaDB DROP has an additional metadata-only sufficient proof: the actual
physical source name is absent in every native identifier encoding from all
surviving bound expressions. It does not prove a model or generated-set
emptiness, evaluate SQL, or override independent blockers. MySQL's persisted
expression spelling does not borrow MariaDB's bound-rendering guarantee.
SQLite likewise separates native identifier absence from full expression
semantics. Rebuild ownership may retain exactly identical opaque native/model
SQL; it does not normalize distinct opaque expressions into equality.

Source presence and dependency absence are different facts. SQL Server must
retain an independent native DROP refusal through a neutral column
confirmation; only exact accepted native-qualified dependency removals can
discharge their own blockers. SQLite must likewise retain a current native
retained-FK rebuild refusal without carrying stale row evidence across writes
or owner replacement. These follow-effect corrections reuse existing scoped
captures and accepted-state callbacks, not a second complete schema inventory.

### Consequences

- Good, because expression identity survives scaffolding, fingerprints, native
  execution, replay and independent final verification.
- Good, because shared accepted proofs avoid a second schema inventory while
  runtime guards retain native authority.
- Bad, because valid native SQL outside the bounded grammar or eligibility
  contract remains unsupported; this is not universal SQL support.
- Bad, because pending source changes can reject when current membership or
  binding is unproven. That does not authorize editing published migrations,
  skipping guards or rewriting history.
- Bad, because PostgreSQL source DROP needs separate lock authority and fresh
  Read Committed semantics; restricted owners, stale snapshots and unsupported
  inherited/partition owners cannot execute that destructive path.
- Bad, because compiler/catalog checks add work and native SQL Server
  qualification requires supported x64 execution.

### Confirmation

These acceptance procedures are not a claim that every matrix ran locally:

- Run the full Core suite. CASE traversal, native source, fingerprints,
  rename metadata, native-empty certificates and opaque-boundary negatives
  must pass without changing original operation identities.
- Run each provider suite on independently qualified engines. Cover fresh
  creation, populated addition, native adoption/drift, CHECKs, explicit storage,
  required/nullable results and index eligibility.
- Cover standalone/sparse/complete dependency declarations, unrelated/missing
  sources, dependent-first removal, owner recreation, rejected/opaque prefixes
  and source additions after initial native absence.
- Confirm native DROP blockers survive matching source NoOps and unrelated
  metadata changes. Explicit dependency removal must remain usable; row writes
  and replaced owners must not borrow stale SQLite row-safety evidence.
- Exercise mandatory sync/async runtime without optional preflight. SQLite
  rebuilds must preserve retained artifacts or refuse before mutation; projected
  presence must not bypass ownership qualification.
- Run actual EF CLI/bundle/tooling and package-consumer gates in the canonical
  guides. Verify data, history and postconditions, not only SQL text.
- Record timing/allocation evidence informationally. ARM exclusions and
  emulated diagnostics do not qualify native x64 SQL Server releases.

## Pros and Cons of the Options

### Bounded captured expressions with native certificates

- Good, because parsed dependencies and original SQL can retain native semantics.
- Bad, because comparison and binding must carry dialect, owner and lifetime
  provenance across multiple execution paths.

### Keep provider expression SQL opaque

- Good, because unknown syntax cannot accidentally inherit a safety proof.
- Bad, because ordinary guarded arithmetic and CHECKs remain unusable even
  when a bounded interpretation is available.

### Admit expressions by parsing or preparation alone

- Good, because native preparation checks useful syntax without reading rows.
- Bad, because preparation does not prove generated ALTER dependencies, result
  facets or the identity of native overloads.

### Add managed constraints or persistence for required results

- Good, because physical constraints can enforce values independently of a proof.
- Bad, because they add schema ownership or change the chosen virtual/stored
  contract; the maintainer selected strict expression proof instead.

## More Information

This complements the public-provider, lifecycle and SQLite ownership decisions,
without superseding them. Syntax, capabilities and recovery remain in canonical
guides. No new dependency, release, grant or server durability change is
authorized by this record.

### Re-evaluation Triggers

- A provider changes generated metadata, dependency representation, native
  compiler binding or storage semantics.
- A required new expression has independently testable type, determinism and
  dependency contracts.
- Native qualification contradicts a proof or exposes provenance,
  artifact-ownership, batching or allocation regressions.

### Decision History

- 2026-10-20: Decision recorded with status proposed. The maintainer authorized
  implementation and explicitly selected strict non-NULL proof, targeted SQL
  Server identity refusal and additional DROP checks during this task. The
  maintainer also selected targeted PostgreSQL refusal rather than rights or
  isolation changes after native lock/snapshot counterprobes, and authorized
  the additional ordinary SQL Server DROP projection correction. These
  choices do not imply approval of this whole retrospective rationale or a
  completed native qualification matrix.

### Implementation References

- [Expression authoring](../migration-authoring.md#generated-columns)
- [Accepted-state projection](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationPreflightProjection.cs)
- [MySQL/MariaDB](../mysql-mariadb-ddl-behavior.md#generated-columns)
- [PostgreSQL](../postgresql-behavior.md#generated-columns)
- [SQLite](../sqlite-behavior.md#generated-columns)
- [SQL Server](../sqlserver-behavior.md#computed-columns)
- [Contribution gates](../../CONTRIBUTING.md)
- [Qualification matrix](../support-and-qualification.md)

Representative regression contracts:

- [Captured source semantics](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Expressions/SafeMigrationCapturedExpressionSourceTests.cs)
- [Ordered generated dependencies](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Columns/SafeMigrationPreflightProjectionTests.GeneratedDependencies.cs)
- [MySQL/MariaDB dependency candidates](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlGeneratedColumnDependencyContractCatalogTests.cs)
- [PostgreSQL native dependencies](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Columns/PostgreSqlGeneratedDropIntegrationTests.cs)
- [SQLite rebuild ownership](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Integration/SqliteProjectedRebuildOwnershipTests.cs)
- [SQL Server projected dependencies](../../tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests/Infrastructure/SqlServerComputedDropProjectionTests.cs)

### Sources

- MySQL generated-column restrictions (`https://dev.mysql.com/doc/refman/8.4/en/create-table-generated-columns.html`; primary source; retrieved 2026-10-20)
- MySQL generated ALTER (`https://dev.mysql.com/doc/refman/8.4/en/alter-table-generated-columns.html`; primary source; retrieved 2026-10-20)
- MariaDB generated columns (`https://mariadb.com/docs/server/reference/sql-statements/data-definition/create/generated-columns`; primary source; retrieved 2026-10-20)
- PostgreSQL 14 dependency ownership (`https://github.com/postgres/postgres/blob/REL_14_24/src/backend/catalog/heap.c#L2282-L2312`; primary source; retrieved 2026-10-20)
- PostgreSQL 18 dependency ownership (`https://github.com/postgres/postgres/blob/REL_18_6/src/backend/catalog/pg_attrdef.c#L104-L121`; primary source; retrieved 2026-10-20)
- PostgreSQL 14 lock privileges and snapshots (`https://www.postgresql.org/docs/14/sql-lock.html`; primary source; retrieved 2026-10-20)
- PostgreSQL 18 lock privileges and snapshots (`https://www.postgresql.org/docs/18/sql-lock.html`; primary source; retrieved 2026-10-20)
- SQLite generated facets (`https://www.sqlite.org/gencol.html`; primary source; retrieved 2026-10-20)
- SQL Server computed definition (`https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-table-computed-column-definition-transact-sql`; primary source; retrieved 2026-10-20)
- SQL Server schema-bound column dependencies (`https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql?view=sql-server-ver17`; primary source; retrieved 2026-10-20)
- SQL Server automatic-statistics lifetime (`https://learn.microsoft.com/en-us/sql/t-sql/statements/create-statistics-transact-sql?view=sql-server-ver17#auto_drop---on--off`; primary source; retrieved 2026-10-20)
