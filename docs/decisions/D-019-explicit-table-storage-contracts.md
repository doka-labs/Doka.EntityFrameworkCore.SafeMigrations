---
id: D-019
status: proposed
date: 2026-11-09
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Immutable provider-certified InnoDB table metadata from scaffolding to runtime"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-019 -- Preserve explicit table storage contracts

## Context and Problem Statement

Doka emits an explicit InnoDB storage annotation for mapped tables. Core's
annotation-free generic table conversion rejects this operation, even with the
MySQL adapter registered. Removing the annotation would lose a transactional
storage requirement. Merely allowing its native DDL would leave replay and
preflight unable to distinguish a same-name nontransactional table.

The question is how to retain this provider-owned coordinate without a MySQL
dependency in Core or unrestricted metadata passthrough.

## Decision Drivers

- Preserve the authored storage requirement across source and native execution.
- Keep unknown annotations and unsupported providers fail-closed.
- Retain existing public constructor signatures and unannotated fingerprints.
- Reuse existing immutable annotation capture and catalog queries.

## Considered Options

- Capture provider-certified table facets in the existing immutable contract
- Pass annotated table operations directly to the native provider
- Reject all explicit table storage metadata

## Decision Outcome

Chosen option: "Capture provider-certified table facets in the existing immutable contract",
because native creation, analysis and replay must agree on the same complete
contract. This record proposes the implementation architecture for review;
implementation authorization does not itself establish architectural approval.

Reuse Core's immutable provider-annotation representation on expected tables.
Capture the complete annotated operation through an operation-factory table
builder before freezing the definition. Reconstruct native annotations from
that owned snapshot, and preserve them across expression normalization,
renames and ordered projection. Fingerprint extensions are conditional on
nonempty metadata, preserving the existing empty-contract encoding.

The MySQL adapter certifies the bounded InnoDB annotation only. Its table
catalog predicate checks the expected engine in strict and convergence modes.
An explicit engine must also inform projected required-column and foreign-key
eligibility instead of inheriting an unrelated server default. Unsupported
metadata and engine drift do not authorize an automatic engine conversion.
PostgreSQL, SQLite and SQL Server reject this foreign provider coordinate.

Separately certify the known historical engine, charset and collation facets
that Doka copies onto identity-only table DROP/RENAME and removed PK/UNIQUE
operations. Native identity operations do not consume these facets; normalized
source must not append them to an already frozen safe wrapper or invent a
historical engine precondition. Unknown or malformed metadata remains rejected.
This bounded identity normalization does not weaken table creation or matching.

### Consequences

- Good, because the same storage promise governs scaffold, apply and replay.
- Good, because the existing table predicate can verify the engine without an
  additional per-table database roundtrip.
- Bad, because source generation needs factory capture instead of appending an
  annotation after the definition was frozen.
- Bad, because engines outside the explicitly certified set remain unsupported.

### Confirmation

Run the isolated MySQL table-scaffolding and native storage-contract tests.
Require strict/legacy source compilation, snapshot no-diff, both registration
orders, apply/replay and preservation of existing data/history. Counterprobes
must reject non-InnoDB owners, malformed/unknown annotations and foreign-provider
use. Core tests must retain metadata through copies and preserve historical
unannotated fingerprint fixtures. Run affected provider suites and CLI/bundle
and package-consumer checks. Compile complete scaffolded Up/Down, execute Down
and replay, and retain negative historical-annotation probes. These are
acceptance procedures, not claims that
the present proposal has completed native or hosted qualification.

## Pros and Cons of the Options

### Capture provider-certified table facets in the existing immutable contract

- Good, because ownership and verification stay in the existing foundation.
- Bad, because every contract-copy boundary must preserve the new coordinate.

### Pass annotated table operations directly to the native provider

- Good, because the native provider already renders engine syntax.
- Bad, because passthrough alone cannot enforce matching schema on replay.

### Reject all explicit table storage metadata

- Good, because no unsupported metadata can be discarded silently.
- Bad, because a complete and verifiable InnoDB contract remains unusable.

## More Information

The [MySQL/MariaDB guide](../mysql-mariadb-ddl-behavior.md) owns user guidance.
This adds no general annotation-bag public API, cross-provider dependency,
server-default mutation or new engine-repair permission.

### Re-evaluation Triggers

- A consumer needs another explicitly verifiable storage engine or table facet.
- Doka changes its storage annotation or native rendering contract.
- A provider introduces new table-copy or normalization paths.

### Decision History

- 2026-11-09: Decision recorded with status proposed.

### Implementation References

- [Expected table definition](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features/Tables/ExpectedTableDefinition.cs)
- [Table builders](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features/Tables/SafeMigrationBuilderExtensions.Tables.cs)
- [MySQL adapter](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderOperationAdapter.cs)
- [MySQL table catalog](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/Tables/MySqlSafeMigrationCatalogSqlBuilder.Tables.cs)
- [MySQL tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests)

### Sources

- [MySQL 8.4 CREATE TABLE](https://docs.oracle.com/cd/E17952_01/mysql-8.4-en/create-table.html)
  (primary source; retrieved 2026-11-09)
- [MariaDB CREATE TABLE](https://mariadb.com/docs/server/server-usage/tables/create-table.md)
  (primary source; retrieved 2026-11-09)
