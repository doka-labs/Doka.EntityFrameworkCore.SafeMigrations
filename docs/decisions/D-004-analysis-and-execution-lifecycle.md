---
id: D-004
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Preflight, runtime, postflight, connection ownership, and failure recovery"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-004 -- Separate read-only analysis from guarded execution and recovery

## Context and Problem Statement

Operators need to inspect every installation before changing it, but successful
execution of an EF migration has history consequences. An analysis switch
inside Migration.Up could execute no target DDL yet still look like a
successfully applied migration.

Analysis and execution also see different points in time. MySQL/MariaDB DDL
can commit before a later command fails; PostgreSQL snapshot consistency does
not prevent every application write or out-of-band DDL. A result called safe
must therefore identify its temporal, transaction, and resource boundaries.

The decision is how to separate observation from mutation while preserving
shared classification, reliable cleanup, and honest recovery semantics.

## Decision Drivers

- Analysis does not execute target DDL or write migration history.
- Runtime guards and postflight use the same expected-definition semantics.
- Connection, transaction, analysis-lock, and cancellation ownership are
  explicit and tested on success and failure.
- Multiple migrators for one database must not race through EF execution;
  different databases remain independently deployable.
- A failed command must not return contaminated session state to a pool.
- Recovery must account for engine-specific partial commits without pretending
  that an unknown legacy state has a universal inverse.

## Considered Options

- Separate read-only analysis with guarded EF execution and postflight
- Analysis mode inside Migration.Up
- Preflight approval followed by unguarded DDL
- One transaction promising atomic migration across all engines

## Decision Outcome

Chosen option: "Separate read-only analysis with guarded EF execution and postflight",
because observation must not advance execution history and a past observation
cannot replace runtime checks.

The runner exposes pending-migration analysis, explicit-operation preflight,
and postflight verification separately from EF execution. It validates the
canonical model, obtains provider observations in bounded batches, and
projects accepted earlier safe operations into later preflight assessments.
Recognized ordinary EF table and column operations may contribute only their
deterministic structural postconditions to a later safe prerequisite. They
remain explicitly `provider_owned_not_analyzed`, keep the report at
`ReadyWithProviderOperations`, and require independent review and postcondition
evidence. Provider-owned effects invalidate complete projected shapes that may
have become stale; an unrecognized operation invalidates all accumulated
projection facts rather than carrying an unknown effect forward.

The runner opens and closes a connection only when it owns that opening.
Provider analysis scopes own resources they create; caller-owned transactions
remain caller-owned. Cancellation is propagated to database operations and
checked at explicit boundaries rather than converted into a successful
report.

PostgreSQL analysis creates a read-only RepeatableRead transaction when it
needs one and takes a transaction-scoped, database-local analysis advisory
lock. An existing transaction is accepted only if read-only and RepeatableRead
or Serializable. An unsuitable caller transaction is rejected, not silently
changed or committed.

MySQL/MariaDB analysis uses the provider's migration lock. That lock is not a
general application-write fence. Neither provider's analysis lock is a promise
that all future data and DDL remain unchanged. Deployment orchestration must
exclude conflicting application writes and out-of-band schema changes for the
required window.

Runtime execution remains in EF's migration path. Safe-operation guards
evaluate prerequisites before unsafe data access or target DDL and verify
postconditions as applicable. Doka executes MySQL/MariaDB handler scopes with
cleanup after success, failure, and cancellation using its independent cleanup
token. Cleanup failure closes the connection and evicts the affected physical
session from reuse.

EF records success history only after the migration completes. Earlier
MySQL/MariaDB DDL may nevertheless be committed if a later command fails.
PostgreSQL tests verify rollback for the supported transactional path, not a
universal rollback guarantee for every possible provider command.

A retry reclassifies the actual remaining state. The operator chooses a
reviewed forward fix or restores a tested backup when needed. SQL scripts
require their own execution fence, stop-on-error policy, and failed-session
disposal; they do not inherit EF's runtime lock or finally-style cleanup.

### Consequences

- Good, because preflight cannot accidentally record a migration as applied.
- Good, because the guard, report, and postcondition contracts expose failure
  rather than silently accepting state drift or incomplete recovery.
- Bad, because deployment still needs per-installation coordination, backup,
  privilege, and evidence handling outside the library.
- Bad, because MySQL/MariaDB partial commits require forward-state reasoning;
  a rollback call alone cannot restore an unknown starting schema.

### Confirmation

Run both provider lifecycle and lifecycle-edge-case suites through the full
engine matrix defined by support qualification. Require independent evidence
for these boundaries:

- read-only preflight/postflight and unchanged history;
- matching and schema-changing derived contexts;
- PostgreSQL acceptance of qualified caller transactions and rejection of
  read-write or weaker-isolation transactions without taking their ownership;
- cancellation before catalog access and during owned-connection use;
- same-database migrator serialization and different-database concurrency;
- MySQL/MariaDB failure after standard DDL, no success history, and retry;
- PostgreSQL rollback/retry in the supported transactional path;
- Doka cleanup after guard failure and cancellation, plus physical-session
  eviction when cleanup itself fails;
- postflight rejection when the expected final state was not reached.

The deployment runbook additionally requires a tested restore path and
execution fencing. A script smoke test does not prove cleanup after every
failure; its executor responsibilities must be tested separately. Listing
these procedures does not assert that a hosted deployment or fresh matrix run
has occurred as part of documenting this decision.

## Pros and Cons of the Options

### Separate read-only analysis with guarded EF execution and postflight

- Good, because inspection, mutation, and verification have distinguishable
  ownership and evidence without inventing a second history mechanism.
- Bad, because the operator must coordinate the temporal boundary between them.

### Analysis mode inside Migration.Up

- Good, because callers can reach the logic through the familiar migration
  entry point and reuse migration construction.
- Bad, because EF observes successful migration completion, not the author's
  intent to make the run observational, so history can become misleading.

### Preflight approval followed by unguarded DDL

- Good, because a separately enforced immutable maintenance window can make
  deployment simple for a tightly controlled database.
- Bad, because a changed row or catalog object between checks and execution
  would bypass the library's required runtime decision boundary.

### One transaction promising atomic migration across all engines

- Good, because genuinely transactional DDL can simplify rollback and failure
  reasoning for a qualified operation set on a suitable engine.
- Bad, because MySQL/MariaDB implicit commits and transaction-suppressed
  commands make this promise invalid for the supported product boundary.

## More Information

D-002 defines the target schema and operation ownership. D-003 defines the
provider extension boundary. D-005 defines evidence and resource bounds.
A blocked report is a classified outcome, not necessarily an exception; a
clean preflight is evidence for the observed window, not a future guarantee.

Provider migration locks serialize cooperating migrators, not every external
database client. No process-global lock is introduced for independent
installations.

### Re-evaluation Triggers

- EF or a provider changes lock lifetime, history timing, transaction
  suppression, or command cleanup behavior.
- A supported engine changes DDL commit or catalog snapshot semantics.
- An operational incident exposes a retry, cancellation, session-eviction, or
  maintenance-window assumption not covered by the current evidence.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing analysis and execution ownership documented retrospectively without inventing earlier approval dates.
- 2026-08-26: Doka-format revision makes partial commits, caller ownership, and the limits of analysis locks explicit.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. Analysis, guarded execution, model validation, and connection ownership are implemented and verified by the referenced provider lifecycle tests.
- 2026-08-29: Preflight retained the provider-operation boundary while adding
  ordered conditional projection for deterministic ordinary table and column
  postconditions required by later safe operations.

### Implementation References

- [Runner and connection ownership](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationRunner.cs)
- [MySQL/MariaDB analyzer and analysis lock](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.cs)
- [PostgreSQL analyzer and snapshot scope](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Analysis/PostgreSqlSafeMigrationProviderAnalyzer.cs)
- [MySQL/MariaDB lifecycle tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Lifecycle/MySqlSafeMigrationIntegrationTests.Lifecycle.cs)
- [MySQL/MariaDB edge cases](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Lifecycle/MySqlSafeMigrationIntegrationTests.Lifecycle.EdgeCases.cs)
- [PostgreSQL lifecycle tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Lifecycle/PostgreSqlSafeMigrationIntegrationTests.Lifecycle.cs)
- [PostgreSQL edge cases](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Lifecycle/PostgreSqlSafeMigrationIntegrationTests.Lifecycle.EdgeCases.cs)
- [MySQL/MariaDB DDL behavior](../mysql-mariadb-ddl-behavior.md)
- [Deployment and recovery](../runbooks/deployment-and-recovery.md)

### Sources

- [EF Core migration execution and script-locking boundary](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) (primary source; retrieved 2026-08-26)
- [MySQL 8.4 implicit-commit statements](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html) (primary source; retrieved 2026-08-26)
- [MariaDB implicit-commit statements](https://mariadb.com/docs/server/reference/sql-statements/transactions/sql-statements-that-cause-an-implicit-commit) (primary source; retrieved 2026-08-26)
- [PostgreSQL 18 transaction isolation](https://www.postgresql.org/docs/18/transaction-iso.html) (primary source; retrieved 2026-08-26)
- [Doka 10.1.1 scoped-command cleanup contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.1.1/docs/migration-operation-handlers.md) (primary source; retrieved 2026-08-29)
