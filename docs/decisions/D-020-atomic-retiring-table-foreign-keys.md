---
id: D-020
status: proposed
date: 2026-11-09
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Exact-window SQLite foreign-key retirement without an absent target rebuild model"
supersedes: []
superseded-by: []
amends: [D-013]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-020 -- Certify foreign keys on atomically retiring tables

## Context and Problem Statement

A migration may remove a populated private table cycle while preserving host
tables. Generated Down removes a foreign key before dropping both owners.
Those retiring tables are intentionally absent from the final target model.
The ordinary SQLite generator cannot rebuild such an owner; SafeMigrations
also correctly refuses an unproved missing-model rebuild.

The missing capability is a complete retirement proof, not permission to
rebuild an unknown table or discard arbitrary live schema objects.

## Decision Drivers

- Preserve surviving tables, references, artifacts, rows and migration history.
- Bind proof to exact operations and one owned atomic structural window.
- Preserve cancellation, rollback, error priority and original FK mode.
- Avoid copying populated rows into a table immediately destined for deletion.

## Considered Options

- Elide only certified foreign-key removal on a table retired in the same atomic window
- Synthesize a temporary rebuild model from live schema text
- Require consumers to replace generated retirement with raw SQL

## Decision Outcome

Chosen option: "Elide only certified foreign-key removal on a table retired in the same atomic window",
because deletion of the exact owner makes a separate physical FK removal
unnecessary while retaining explicit logical policy and final-state checks.
This architecture is proposed for review; executable code alone is not an
approval record.

Extend D-013 with an operation-local retirement certificate. The target owner
must be absent from the target model and explicitly dropped later in the same
atomic structural batch. A later DROP in cumulative history, another migration
or across SQL/data boundaries is not proof. Owner recreation, renames or other
unsupported lifetime changes cannot inherit the certificate. A copied operation
with the same name is not the certified operation.

Analyze the FK normally, retaining matching/missing and policy behavior. Only
after complete batch acceptance may execution omit that certified physical FK
drop from the native operation list. The explicit table drops remain under the
existing owned transaction and FK-mode suspension/restoration. Logical
projection and fresh final postconditions still require the absent owner.
No new fallback PRAGMA path or independently committed history write is added.

Surviving external references remain blockers, regardless of action or the
initial FK enforcement setting. Unknown triggers, views, generation behavior,
strict/without-rowid shapes and provider-specific options retain their existing
proof requirements. The certificate never authorizes an unrelated rebuild or
an unmodeled surviving owner. Script generation remains unsupported.

Retained modeled host tables can still require an ordinary safe rebuild while
the private component retires. Preserve native retained-column ordinals through
the existing EF rewrite capture and exact compiler-created owner identities.
Explicit model `ColumnOrder` remains authoritative, including explicit `main`
schema mappings. Change only reconstructed column declarations, never named
row-copy projections, source operations or the target model. Renames preserve
identity; drop/re-add starts a new column lifetime. Fresh/recreated and ordinary
EF-owned owners retain provider ordering. The same rule applies to upgrades.

### Consequences

- Good, because populated cyclic retirement avoids a redundant rebuild and copy.
- Good, because existing transaction and verification ownership remains intact.
- Bad, because the proof is intentionally scoped and cannot cross batch barriers.
- Bad, because unknown artifacts require explicit support rather than guessing.

### Confirmation

Run isolated file/in-memory populated cyclic migrations through Up, Down and Up
again, verifying host rows, indexes, history and original FK mode. Test exact
reference binding, absent/wrong-window DROP, renames/recreation, external child
references, unknown artifacts and every relevant refusal. Inject a later failure
and cancellation to verify rollback and cleanup. Run the full SQLite suite and
native CLI/bundle checks. Existing script rejection must remain intact.
Compare retained host physical column order and facets after both key ADD and
DROP, including explicit model ordering, aliases, row preservation and rollback.
These acceptance procedures do not assert a completed test run or approval.

## Pros and Cons of the Options

### Elide only certified foreign-key removal on a table retired in the same atomic window

- Good, because the terminal schema is proved without reconstructing discarded rows.
- Bad, because analysis and execution must consume the same immutable certificate.

### Synthesize a temporary rebuild model from live schema text

- Good, because a complete native schema model could support broader rebuilds.
- Bad, because that introduces a second schema-authoring authority and must prove
  all options and artifacts, rather than solving the bounded retirement case.

### Require consumers to replace generated retirement with raw SQL

- Good, because consumers can choose an engine-specific retirement procedure.
- Bad, because it bypasses the promised scaffolded lifecycle and typed evidence.

## More Information

D-013 remains authoritative for modeled rebuilds and transaction ownership.
The [SQLite guide](../sqlite-behavior.md) owns operational guidance. History is
written by EF after the structural batch and is not falsely described as part
of the owned DDL transaction. Source-generated data operations remain separate.

### Re-evaluation Triggers

- SQLite or EF introduces direct supported FK removal without a table rebuild.
- A consumer requires retirement across a structural or data boundary.
- A supported native artifact changes retirement side effects.

### Decision History

- 2026-11-09: Decision recorded with status proposed.

### Implementation References

- [Rebuild ownership](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/Analysis/SqliteRebuildArtifactContract.cs)
- [SQLite analyzer](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/Analysis/SqliteSafeMigrationProviderAnalyzer.cs)
- [Atomic structural execution](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/SqlGeneration/SqliteSafeMigrationBatchCommand.cs)
- [Retained native column order](../../src/Doka.EntityFrameworkCore.SafeMigrations.Sqlite/SqlGeneration/SqliteRebuildColumnOrder.cs)
- [SQLite tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests)

### Sources

- [SQLite ALTER TABLE](https://www.sqlite.org/lang_altertable.html)
  (primary source; retrieved 2026-11-09)
- [SQLite foreign keys](https://www.sqlite.org/foreignkeys.html)
  (primary source; retrieved 2026-11-09)
- [SQLite DROP TABLE](https://www.sqlite.org/lang_droptable.html)
  (primary source; retrieved 2026-11-09)
- [EF Core SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations)
  (primary source; retrieved 2026-11-09)
