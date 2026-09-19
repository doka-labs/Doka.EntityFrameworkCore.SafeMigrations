---
id: D-013
status: implemented
date: 2026-09-18
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "SQLite provider integration and atomic model-owned table rebuilds"
supersedes: []
superseded-by: []
amends: [D-001, D-002, D-003, D-004, D-005, D-006, D-008, D-009]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-013 -- Add SQLite through official-provider composition and atomic rebuilds

## Context and Problem Statement

SafeMigrations already supports MySQL/MariaDB and PostgreSQL, but applications
also use SQLite for embedded deployments and tests. SQLite exposes a materially
different migration contract: most constraint or column changes require a table
rebuild, schema state is stored as SQL text, attached databases create separate
namespaces, and standalone scripts cannot express catalog-dependent branching.

The decision is how to add SQLite without weakening fail-closed classification,
copying EF provider internals, losing unmodeled schema artifacts, or publishing
a package whose tooling and release paths differ from the existing adapters.

## Decision Drivers

- Provider-owned SQL generation and future EF compatibility must remain intact.
- Every rebuild must be atomic and preserve only fully modeled artifacts.
- Preflight, runtime execution, postflight, replay, and unexpected inventory
  must share one catalog identity contract.
- Unsupported schema text or provider capabilities must reject before mutation.
- The package must qualify independently and have no dependency on another
  SafeMigrations provider adapter.

## Considered Options

- Compose the official provider and execute proven rebuild batches atomically
- Implement a standalone SQLite migrations generator
- Limit SQLite to create-if-missing operations
- Treat SQLite as a testing-only compatibility layer

## Decision Outcome

Chosen option: "Compose the official provider and execute proven rebuild
batches atomically", because EF remains owner of SQLite DDL while SafeMigrations
adds catalog classification, policy, guards, and postconditions.

The new `Doka.EntityFrameworkCore.SafeMigrations.Sqlite` package depends only
on Core, the bundle-neutral EF Core SQLite provider core, and dependency-
injection abstractions. Applications retain ownership of their native SQLite
or SQLCipher bundle. Its composed generator delegates ordinary operations to
the official SQLite generator. The connected engine must be SQLite 3.46.1 or
later, which is the minimum engine version documented by the EF Core SQLite
provider used by this package.
Safe operations use a live analyzer over `sqlite_schema` and PRAGMA metadata.

Operations that require a table rebuild form one contiguous structural segment.
The segment is analyzed completely before its first command. Accepted ordinary
EF operations are generated list-wide so the official provider can coordinate
related rewrites. A rebuild disables foreign-key enforcement before opening one
local transaction, validates every effective final postcondition before commit,
then restores the original enforcement mode. When enforcement was originally
enabled, retained violations fail preflight and the affected relationship
closure must also pass `foreign_key_check`. A disabled original mode is
preserved without introducing that stronger validation contract. EF's later
migration-history write is a separate step and is not covered by the rebuild
transaction.

Preflight initializes and analyzes each pending migration's cumulative target
model rather than substituting the latest runtime model. Its deferred read
transaction avoids an immediate writer reservation but can still delay writer
commits in rollback-journal mode, so qualification and production guidance
retain a controlled migration window.

Rebuild authorization requires exact model ownership. Unknown triggers, views,
virtual tables, expression or partial indexes, physical constraint-name or
key-facet drift, and provider constraint options not represented by the EF
target model fail closed, as do `STRICT` and `WITHOUT ROWID`. Only `main` is
supported; attached database
qualifiers reject. Safe-operation script generation rejects before partial
output because SQLite cannot represent the required conditional guards.
Runtime migration and Migration Bundles remain supported.

D-001's vertical-slice rule remains authoritative for feature ownership. The
SQLite adapter has one bounded provider-composition exception: catalog capture,
rebuild authorization, and command execution are provider-wide mechanisms
because one SQLite table rebuild jointly materializes several feature intents.
They remain separate from Core feature semantics, contain no new public
operation family, and are tested through every affected feature contract. This
exception does not permit unrelated provider behavior to accumulate in those
composition types.

### Consequences

- Good, because SQLite shares the public Core operation and report contracts.
- Good, because provider upgrades retain the official DDL implementation.
- Good, because a failed rebuild rolls back its schema and copied data.
- Good, because package, tooling, performance, coverage, and release evidence
  remain independent from the other adapters.
- Bad, because a safe migration containing SQLite safe operations cannot be
  delivered as a standalone SQL script.
- Bad, because legitimate but unmodeled SQLite artifacts require explicit
  modeling or a separately reviewed migration strategy.
- Bad, because Microsoft.Data.Sqlite cannot interrupt native work already
  running through its asynchronous surface.

### Confirmation

- Exercise every operation family across missing, matching, different,
  unsupported, data-blocked, and prerequisite-missing states.
- Verify initial application, idempotent replay, postflight, cancellation,
  concurrent execution, model-managed data, and atomic rollback.
- Verify virtual and stored generated columns, semantic aliases, expression and
  partial indexes, triggers, views, virtual tables, and attached databases.
- Scaffold and apply through EF CLI and a Migration Bundle twice; reject script
  generation without leaving output.
- Run warning-free build, merged coverage, allocation/performance budgets,
  package-only consumption, deterministic pack, SBOM, and release readback.

## Pros and Cons of the Options

### Compose the official provider and execute proven rebuild batches atomically

- Good, because SafeMigrations owns safety without reimplementing provider DDL.
- Bad, because custom command wrappers must preserve EF transaction semantics.

### Implement a standalone SQLite migrations generator

- Good, because all emitted SQL would be locally controlled.
- Bad, because it would copy provider behavior and drift from EF Core updates.

### Limit SQLite to create-if-missing operations

- Good, because it avoids rebuild complexity.
- Bad, because normal maintenance migrations would have an incomplete contract.

### Treat SQLite as a testing-only compatibility layer

- Good, because no public support promise would be required.
- Bad, because production embedded deployments would remain unprotected.

## More Information

D-001 remains authoritative for package isolation. D-003 owns provider
composition, D-004 owns lifecycle and recovery, D-005 owns evidence bounds,
D-006 owns publication, D-008 owns scaffolding, and D-009 owns model-managed
data. This decision adds SQLite-specific mechanics without changing those Core
contracts.

### Re-evaluation Triggers

- SQLite adds directly usable guarded migration primitives.
- EF Core changes its rebuild, script, or migration-lock contract.
- Microsoft.Data.Sqlite gains interruptible asynchronous execution.
- Supporting attached databases becomes an explicit product requirement.
- Qualified workloads exceed the current parameter or allocation bounds.

### Decision History

- 2026-09-18: Dominic Kalkbrenner selected a complete additional SQLite NuGet
  package parallel to the existing adapters; status changed from proposed to
  accepted.
- 2026-09-18: Implemented provider composition, live analysis, atomic rebuilds,
  tooling, package/release integration, tests, and documentation; status changed
  from accepted to implemented.
- 2026-09-18: Qualified bundle-neutral provider-core ownership, the SQLite
  3.46.1 engine floor, list-wide official-generator composition, foreign-key-
  safe rebuild transactions, per-migration target models, and the bounded
  provider-composition exception to D-001.
- 2026-09-18: Closed static-review findings for ordered rebuild segments,
  physical artifact identity, complete modeled shape, attached-database
  qualifiers, ASCII-folded SQLite identities, escaped identifiers, and
  bounded catalog evidence.
- 2026-09-19: Closed review findings for incoming table-drop dependencies,
  legacy rename mode, cross-table trigger dependencies, inherited index
  collations, exact transient EF backfill defaults, retained foreign-key
  violations, and real overlapping writer qualification.
- 2026-09-19: Extended ordered table-drop projection through newly introduced
  foreign keys and renames, excluded self-references, aligned converted
  backfill defaults with EF provider literals, and completed trivia-aware and
  high-byte identifier parsing.

### Implementation References

- [SQLite package](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite)
- [SQLite analyzer](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/Analysis/SqliteSafeMigrationProviderAnalyzer.cs)
- [SQLite generator](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/SqlGeneration/SqliteSafeMigrationsSqlGenerator.cs)
- [SQLite tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests)
- [SQLite behavior](../sqlite-behavior.md)

### Sources

- [EF Core SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
  (primary source; retrieved 2026-09-18)
- [EF Core SQLite provider](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/)
  (primary source; retrieved 2026-09-18)
- [Microsoft.Data.Sqlite custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
  (primary source; retrieved 2026-09-18)
- [SQLite ALTER TABLE](https://www.sqlite.org/lang_altertable.html)
  (primary source; retrieved 2026-09-18)
- [SQLite generated columns](https://www.sqlite.org/gencol.html)
  (primary source; retrieved 2026-09-18)
- [SQLite transactions](https://www.sqlite.org/lang_transaction.html)
  (primary source; retrieved 2026-09-18)
- [SQLite foreign keys](https://www.sqlite.org/foreignkeys.html)
  (primary source; retrieved 2026-09-18)
- [SQLite DROP TABLE](https://www.sqlite.org/lang_droptable.html)
  (primary source; retrieved 2026-09-18)
- [SQLite PRAGMA statements](https://www.sqlite.org/pragma.html)
  (primary source; retrieved 2026-09-18)
- [SQLite CREATE INDEX](https://www.sqlite.org/lang_createindex.html)
  (primary source; retrieved 2026-09-18)
- [SQLite identifier comparison](https://www.sqlite.org/c3ref/stricmp.html)
  (primary source; retrieved 2026-09-18)
- [SQLite comments](https://www.sqlite.org/lang_comment.html)
  (primary source; retrieved 2026-09-19)
- [SQLite tokenizer requirements](https://www.sqlite.org/draft/tokenreq.html)
  (primary source; retrieved 2026-09-19)
