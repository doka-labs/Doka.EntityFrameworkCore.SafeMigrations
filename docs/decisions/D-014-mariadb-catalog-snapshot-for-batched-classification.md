---
id: D-014
status: accepted
date: 2026-10-05
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Catalog source for batched classification in the MySQL provider on MariaDB"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-014 -- Snapshot the scoped catalog for batched classification on MariaDB

## Context and Problem Statement

Batched classification renders one catalog predicate per operation and packs up
to 32 of them into a statement. Every predicate reads `INFORMATION_SCHEMA`
directly, so a statement issues roughly twenty catalog subqueries per operation.

MariaDB materialises each such subquery into an internal temporary table, and
its HEAP engine cannot hold `BLOB` or `TEXT` columns, which the catalog views
carry. Each subquery therefore spills to an on-disk Aria table. Analysing
25,600 operations of the large-mixed-migration contract raised
`Created_tmp_tables` by 3,048,533 and `Created_tmp_disk_tables` by 501,217 on
MariaDB 11.8.8, against 109,095 and zero on MySQL 8.4.11. MySQL 8 keeps the
equivalent work in memory through its TempTable engine.

Where the disk is fast the cost hides: locally MariaDB analyses the contract at
3.049 ms per operation against MySQL's 4.180 ms. On the hosted qualification
runners the same workload takes 2,277-2,357 s on the four MariaDB versions
against 756-767 s on the two MySQL versions, and MariaDB is 7.5 times slower
there than locally while MySQL is 1.8 times slower.

The decision is which catalog source batched classification should read on
MariaDB so that a statement stops paying one on-disk temporary table per
catalog subquery, without weakening what classification verifies.

## Decision Drivers

- Classification must derive metadata from the connected session, not a cached
  model, and keep application tables and migration history unchanged.
- Every facet predicate must stay verbatim and server-side; no facet may move
  into client comparison.
- The fix must not depend on server configuration the library does not own.
- The runtime path that EF executes per migration operation must stay a single
  self-contained statement.
- MySQL must not regress; it has no spill to remove.
- The qualified MariaDB versions 10.11, 11.4, 11.8 and 12.3 must all benefit.

## Considered Options

- Keep the inline INFORMATION_SCHEMA subqueries
- Raise the temporary-table thresholds so the spill stays in memory
- Wrap the catalog source in a common table expression
- Snapshot the scoped catalog into a session temporary table on MariaDB
- Name the catalog relations through a sentinel the renderer binds
- Rewrite classification as a single-pass conditional aggregation for both
  engines

## Decision Outcome

Chosen option: "Snapshot the scoped catalog into a session temporary table on
MariaDB", because it is the only measured shape that removes the per-subquery
on-disk temporary table while leaving every facet predicate unchanged.

Batched classification on MariaDB copies every catalog view the classifier can
name into a `TEMPORARY TABLE` once per operation window, and the rendered
predicates address those tables instead of `INFORMATION_SCHEMA`. The copy uses
`SELECT *` to carry the catalog values the predicates consume; CTAS is not a
guarantee of identical table metadata, so live parity tests remain necessary.
Facet predicates are retained; only structural relation references change.

Templates keep naming the live views. Redirection happens during rendering and
only when a caller supplies a binding, so the runtime generation path performs
no scan and allocates nothing extra. Redirection recognizes bounded unquoted
catalog names in relation positions. Quoted literals, identifiers and comments
are not rewritten, and parameter values are never scanned. Ambiguous SQL text
keeps live relations instead of guessing its interpretation.

Ownership: the provider analyzer owns the snapshot lifecycle; the catalog SQL
builder is untouched and keeps emitting live view names.

Limits that this decision accepts:

- The snapshot binds a window's classification to the session that built it.
- Catalog views are copied successively, not as one atomic catalog snapshot.
  Bound classifications reuse those copies until the window ends; concurrent
  DDL after a view was copied is not refreshed in that window. Prerequisite
  barriers and separately captured diagnostic/transition queries may still
  read live metadata. Runtime execution always performs fresh catalog guards.
- MySQL keeps the live views. The snapshot shape is not available there: MySQL
  rejects a second reference to a temporary table in one statement with error
  1137, and it has no spill to remove.
- Redirection is decided per operation rather than per window. Every explicitly
  qualified operation keeps the live views, including one qualified with the
  connected database, while eligible neighbours read the copy. The qualifier
  visitor includes foreign-key principals. Unqualified operations also need
  incoming dependencies owned by other databases, so KEY_COLUMN_USAGE retains
  both locally owned constraints and rows referencing the connected database.
- The runtime scoped command keeps the live views. It classifies one operation,
  so a snapshot of the catalog cannot amortise.
- Coverage is not negotiable per view. A view the statement reads three times
  accounted for half the spill, so every view is copied and a facet that
  introduces a new one must extend the list.
- The copy is built only when an operation window classifies at least a full
  statement's worth of work, which is 32 operations. Copying nine views costs a
  fixed set of statements, so a migration of a handful of operations stays on
  the live views where the copy would be overhead rather than a saving. That
  threshold keeps the ordinary migration path unchanged.
- Temporary tables explicitly use InnoDB rather than inheriting a possibly
  TEXT-incompatible `default_tmp_storage_engine` such as MEMORY. An unavailable
  InnoDB engine retains live analysis instead of becoming a new requirement.
- Temporary tables have unique per-instance names. Each CTAS is acknowledged
  separately before its name enters the owned set; inline indexes avoid a
  separate ALTER statement. This costs nine fixed create exchanges per window
  and one batched cleanup, in return for exact partial-failure ownership.
  Analysis never drops a preexisting caller temporary table.
- Missing temporary-table privileges and read-only-transaction rejection use
  the live catalog after successful cleanup. That refusal disables further
  snapshot attempts within this analysis call, not globally or across sessions.
  Pooled connections with `ConnectionReset=false` keep the live path before DDL;
  nonpooled connections remain eligible. Other errors and cancellation propagate.
  Cleanup preserves the primary error and closes an unsafe connection. Nonpooled
  close destroys session state; eligible pooled sessions reset before reuse.
  Native connection-string pool clearing is additional protection, not the
  invariant, because MySqlDataSource owns a separate pool.

### Consequences

- Good, because analysing 3,200 operations of the large-mixed-migration contract
  fell from 12,162 ms to 3,358 ms on MariaDB 11.8, a factor of 3.6, with its
  on-disk temporary tables down from 62,331 to 3,273. The replayed single
  statement behind that figure fell from 130.1 ms to 26.4 ms, its on-disk
  temporary tables from 711 to one and its internal temporary tables from 4,453
  to 112.
- Good, because facet predicates are retained, limiting the semantic change
  surface to catalog completeness, relation binding and lifecycle. Regression
  tests still prove parity; unchanged predicates alone are not a proof.
- Good, because the runtime generation path is untouched by construction: with
  no binding supplied there is no scan, which keeps its allocation budget.
- Good, because it needs no server configuration changes or newly mandatory
  privileges; environments prohibiting temporary DDL retain the live path.
  All qualified MariaDB versions remain required qualification targets.
- Good, because the amortisation threshold leaves a small migration, which is
  the ordinary case, on exactly the statements it issued before.
- Bad, because classification now holds session state, so a pooled connection
  must drop the snapshot deterministically even on failure.
- Bad, because the two engines no longer execute the same classification SQL,
  which doubles the live-qualification surface for the catalog source.
- Good, because a recorded local MariaDB 11.8.8 suite completed in 4 m 28 s
  versus earlier 18 m 36 s to 21 m 02 s observations. The final MariaDB and
  MySQL outputs each reported 815 passed and two skipped; MySQL took 13 m 06 s.
  Earlier runs had different test counts and some failures. These are useful
  session observations, not a controlled same-suite speedup or proof that
  MySQL performance was unchanged. They predate the review corrections below.
- Bad, because qualified operations lose the gain and sufficiently large mixed
  windows still pay the fixed copy cost for their unqualified work.

### Confirmation

Source inspection:

- The catalog SQL builder must contain no snapshot table name: `rg -n
  "__doka_sm_cat_" src/Doka.EntityFrameworkCore.SafeMigrations.MySql` must match
  only the snapshot type itself.

Local execution:

- `dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests` must
  pass against MariaDB and against MySQL, the latter proving the live path is
  untouched.
- Relation/template unit tests exercise all nine mappings, quoted and commented
  text, token boundaries, prepared rendering and parameter values. The original
  `CatalogRelationBindingRedirectsEveryRelation` test only exercises table and
  column references; it is not complete relation coverage on its own.
- `GeneratedCommandsReadTheLiveCatalog` pins runtime relation identity;
  `PreparedRuntimeColumnGenerationRemainsBounded` pins generated SQL and its
  local allocation budget. Snapshot lifecycle tests exercise partial creation,
  denied capability, cancellation, name collisions and cleanup failure.

Live database qualification:

- `Analyzer_DoesNotSpillOneTemporaryTablePerCatalogSubquery` asserts that an
  analysis of 32 table definitions raises the session counter
  `Created_tmp_disk_tables` by fewer than one per operation on MariaDB, where the
  live form raises it by roughly twenty, and by exactly zero on MySQL. This is
  the mechanical proof and the regression fence.
- `VarcharNarrowing_GroupsCharacterProofsAndPreservesUtf8AndTrailingSpaces`
  bounds the catalog statements a small analysis may issue at eight. It is the
  fence for the amortisation threshold: building the copy for a single operation
  raised that count to 44.
- `Analyzer_ReachesTheSameVerdictsWithAndWithoutTheSnapshot` classifies a
  matching table, a renamed column, a missing column, a surplus column, a present
  index and an absent one through both paths and requires identical verdicts. It
  proves snapshot activation above the 32-operation threshold and forces the
  comparison live path through database-qualified operations. Every intended
  table and index result is compared with an explicit expected verdict.
- Mixed-window, CHECK-literal and cross-database incoming-FK cases must preserve
  the same decisions. Restricted-login and read-only-transaction cases must
  retain live analysis without modifying caller-owned state. MEMORY/InnoDB
  session-default controls prove that the explicit engine remains eligible;
  reset-disabled pooled connections stay live, while nonpooled controls use
  the copy. Secondary connection-close errors must preserve the primary failure.

Hosted qualification:

- Suite duration is informational evidence, never a hardware-dependent merge
  gate. The
  `Analyzer_OneHundredThousandMixedOperationsRemainBoundedOrderedAndComplete`
  duration in the MariaDB matrix jobs is the hosted counterpart. Compare the
  per-test duration from the run's TRX artifact against the 2,277-2,357 s band
  recorded before this decision, normalising by the median per-test ratio of the
  job pair so that a slower runner is not read as a regression. Record source
  identity, engine image, executed test counts and skips for each comparison;
  the earlier local console summaries do not establish those equivalences.

## Pros and Cons of the Options

### Keep the inline INFORMATION_SCHEMA subqueries

- Good, because every statement is self-contained and reads the live catalog at
  its own execution time, which is the strongest freshness the provider can
  offer.
- Good, because both engines execute identical classification SQL, so the
  qualification surface stays single.
- Bad, because MariaDB pays one on-disk temporary table per catalog subquery,
  measured at 19.6 per operation across an analysis of 25,600 operations.
- Bad, because the cost is unbounded in the operation count and is the
  dominant term of the MariaDB qualification jobs.

### Raise the temporary-table thresholds so the spill stays in memory

- Good, because it needs no library change and would help every query shape on
  the server, not only classification.
- Good, because it is the correct remedy when a spill is caused by result size.
- Bad, because this spill is caused by column type, not size: the HEAP engine
  cannot hold `BLOB` or `TEXT` at all before MariaDB 13.1, and the measured
  spill count was identical for a two-column projection and for a projection
  touching both `longtext` predicates, on tables of four columns.
- Bad, because it would require the library to demand server configuration from
  every operator, which the provider must not do.

### Wrap the catalog source in a common table expression

- Good, because it needs no session state, no lifecycle and no DDL, and it
  would keep one statement self-contained.
- Good, because both engines accept several references to a common table
  expression, so one shape would serve both.
- Bad, because neither engine materialises it once here: measured on MariaDB
  11.8.8 the common-table-expression form produced the same 32 on-disk
  temporary tables as the inline form and ran marginally slower, 5.4 ms against
  5.0 ms. MySQL behaved the same way at 5.6 ms against 5.3 ms.
- Bad, because forcing materialisation would depend on optimiser hints that
  differ between the engines and across the qualified versions.

### Snapshot the scoped catalog into a session temporary table on MariaDB

- Good, because it is measured to remove the spill completely: 0.9 ms and no
  on-disk temporary table for 32 operations, against 5.0 ms and 32 of them.
- Good, because the predicates stay byte-identical, so the risk is confined to
  the relation binding and the snapshot projection rather than spread across
  every facet.
- Bad, because it introduces session-scoped state into an analysis path that
  was stateless, including a drop on the failure path.
- Bad, because MariaDB and MySQL then run different classification SQL, so each
  facet needs live qualification on both.

### Name the catalog relations through a sentinel the renderer binds

- Good, because a sentinel cannot be confused with a live view name, so an
  unbound one fails loudly as an unknown table rather than reading the catalog
  by accident.
- Good, because the builder states its intent explicitly instead of relying on
  the renderer to recognise a view name.
- Bad, because every render path must then resolve the sentinel, including the
  runtime generation path that only ever wants the live views. Measured on 128
  generated column operations that cost 1.60 MB on top of 3.74 MB, a 43 percent
  increase that breaks the generation allocation budget of 4.00 MB.
- Bad, because it requires rewriting the relation at all fifty builder sites,
  which enlarges the change without changing any emitted SQL.

### Rewrite classification as a single-pass conditional aggregation for both engines

- Good, because it also helps MySQL, measured at 1.0 ms against 5.3 ms for 32
  operations, and it satisfies MySQL's single-reference restriction.
- Good, because one shape would then serve both engines and the snapshot would
  be referenced exactly once per statement.
- Bad, because every facet predicate must be restated as an aggregate over one
  pass, which rewrites the whole catalog SQL builder rather than its relation
  binding, and each restatement can change what the facet accepts.
- Bad, because an aggregate over one pass cannot express a facet that needs a
  correlated lookup in another catalog view without reintroducing a join.

## More Information

### Re-evaluation Triggers

- MariaDB 13.1 or later becomes the lowest qualified MariaDB version. Its HEAP
  engine holds `BLOB` and `TEXT`, which removes the spill this decision works
  around; the snapshot should then be measured against the inline shape again
  and removed if it no longer pays.
- MySQL lifts the restriction that forbids a second reference to a temporary
  table in one statement. The snapshot would then become available to MySQL and
  this decision's engine split should be revisited.
- The runtime scoped command becomes batch-shaped, carrying more than one
  operation per statement. A snapshot could then amortise there as well.
- A facet is added that reads a catalog view the snapshot does not copy. The
  list must then be extended, because a view read only three times was measured
  to carry half the spill.
- Index tuning on the snapshot becomes worthwhile. Extending the keys to cover
  the GROUP BY columns was measured at -2.2 percent, inside the run-to-run
  spread, and neutralising every GROUP_CONCAT at 3.4 percent; both were rejected
  as drivers.

### Decision History

- 2026-10-05: Decision recorded with status accepted.
- 2026-10-05: Redirection moved from per-window to per-operation. A window-wide
  gate disabled the copy whenever any operation named another database, which
  the large-mixed-migration contract does every thirty-first operation, so the
  copy never activated for the workload that motivated this decision and
  measured no change at all. Per-operation binding is sound for the reason
  recorded under the outcome's limits.
- 2026-10-05: Review corrections require quote-aware structural redirection,
  cross-database incoming-FK completeness, narrow live fallback, unique owned
  temporary tables and failure-safe cleanup. Parity tests must prove activation;
  suite evidence distinguishes changed test counts, skips and historical timing.

### Implementation References

- Evidence, raw logs and the refuted hypotheses:
  `artifacts/performance/mariadb-tmp-table-spill/README.md`.
- Relation redirection and the snapshot projection:
  `src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlCatalogRelations.cs`
  and `MySqlCatalogSnapshot.cs`.
- Snapshot lifecycle in batched classification:
  `src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.cs`.
- Counter-based live proof and offline shape guards:
  `tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests`.

### Sources

- MySQL 8.4 Reference Manual, Internal Temporary Table Use in MySQL (`https://dev.mysql.com/doc/refman/8.4/en/internal-temporary-tables.html`; primary source; retrieved 2026-10-05)
- MariaDB 13.1 Feature in Focus: BLOB, TEXT, JSON and GEOMETRY Support in the HEAP Engine (`https://mariadb.org/mariadb-13-1-feature-in-focus-blob-text-json-and-geometry-support-in-the-heap-engine/`; primary source; retrieved 2026-10-05)
- MariaDB Documentation, MEMORY Storage Engine (`https://mariadb.com/docs/server/server-usage/storage-engines/memory-storage-engine`; primary source; retrieved 2026-10-05)
- MariaDB Documentation, Created_tmp_disk_tables (`https://mariadb.com/docs/reference/mdb/status-variables/Created_tmp_disk_tables/`; primary source; retrieved 2026-10-05)
- MariaDB Documentation, CREATE TABLE (`https://mariadb.com/docs/server/server-usage/tables/create-table`; primary source; retrieved 2026-10-05)
- MariaDB Documentation, START TRANSACTION (`https://mariadb.com/docs/server/reference/sql-statements/transactions/start-transaction`; primary source; retrieved 2026-10-05)
- MySqlConnector, Command Cancellation (`https://mysqlconnector.net/overview/command-cancellation/`; primary source; retrieved 2026-10-05)
- MySqlConnector, Connection Options (`https://mysqlconnector.net/connection-options/`; primary source; retrieved 2026-10-05)
- MariaDB Documentation, default_tmp_storage_engine (`https://mariadb.com/docs/server/server-management/variables-and-modes/server-system-variables#default_tmp_storage_engine`; primary source; retrieved 2026-10-05)
