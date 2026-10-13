---
id: D-015
status: proposed
date: 2026-10-12
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Owned atomic required-column additions and forward recovery in the MySQL/MariaDB adapter"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-015 -- Own atomic required-column additions and forward recovery

## Context and Problem Statement

An ordinary required-column Ensure without an approved replacement value can
be admissible when its table is empty. A row observation is not a write barrier:
another connection can insert after that observation and before the native ADD.
Native probes on the qualified MySQL/MariaDB engines observed a required ADD
populating existing rows with a type-implied value that neither the migration
nor SafeMigrations approved. This is observed ADD behavior, not a universal
guarantee inferred from MySQL's data-type-default rules: those rules describe
INSERT, REPLACE and UPDATE. MariaDB's Instant ADD documentation separately
allows a NOT NULL addition to use a default implied by its data type.
A longer command timeout or a fresher observation does not close that interval.
The protocol below does not depend on undocumented required-ADD behavior: its
nullable ADD and enforced non-null CHECK must reject an existing or raced NULL.

Doka's exact boolean `RequiresExplicitBackfill` annotation is a reviewed
provider contract, not permission to invent data or ignore dependencies.
Recognizing the annotation must retain the immutable authored target, native
metadata, fingerprint and ordinary policy semantics.

The decision question is how the MySQL/MariaDB adapter can add a supported
required column without an authored default, retain writer-supplied values,
and recover after independently committed DDL stages without acquiring new
privileges, fabricating replacement values or deleting unowned objects.

Implementation work for the bounded fix exists when this record is drafted.
The maintainer authorized concurrent-writer protection, but has not reviewed
this exact decision text. Implementation presence and passing probes do not
make this proposal authoritative or establish release qualification.

## Decision Drivers

- Close the read-then-ADD race with engine-enforced DDL, not inferred quiescence.
- Preserve explicit application values and reject unapproved NULL conversion.
- Keep immutable migration contracts, dependency ranges and EF history intact.
- Bind retained recovery artifacts to one exact pending operation; a reserved
  prefix or equivalent-looking unrelated object must not establish ownership.
- Recover forward after failure or cancellation without destroying the barrier
  or deriving an empty-table certificate from a resumed addition.
- Retain native provider rendering and ordinary matching no-ops, while bounding
  repeated catalog work and avoiding cross-operation catalog caches.
- Use existing native-DDL privileges, without `LOCK TABLES`, newly introduced
  session-setting writes or dependencies on a separate account, service or
  runtime component.
- State engine, SQL-mode, writer and maintenance-window limitations explicitly.

## Considered Options

- Two atomic ALTER stages with an owned enforced CHECK
- Observe emptiness and issue an unguarded native required ADD
- Supply a synthetic non-null default for the required ADD
- Hold LOCK TABLES across the observation and required ADD

## Decision Outcome

Chosen option: "Two atomic ALTER stages with an owned enforced CHECK", because
the first ALTER establishes the column and its non-null invariant together,
and the second preserves that invariant while installing the complete required
target and removing only its certified barrier.

The provider catalog builder owns eligibility and the exact staging contract.
The operation handler renders two native single-column ALTER bodies, verifies
their owner, column and statement boundaries, and attaches typed CHECK clauses
without rewriting arbitrary caller SQL:

1. Add the complete native column as nullable and an enforced
   `column IS NOT NULL` CHECK in one ALTER. Existing or raced rows would receive
   NULL and violate the new constraint, so that ALTER cannot publish fabricated
   replacement values.
2. Modify the exact staged column to the complete required target and remove
   the exact owned CHECK in one ALTER. Values explicitly supplied by intervening
   writers remain unchanged.

Each ALTER is atomic on a qualified InnoDB engine; the pair is not a transaction.
DDL can commit between the stages. The handler keeps one logical operation,
decision, provider resource scope and final postcondition. It neither removes
the provider's backfill marker from authored metadata nor creates a substitute
default. Server-generated identity additions retain their separate contract.

The following boundaries are part of the chosen contract:

- Staging and resume require supported InnoDB, active CHECK enforcement and
  strict conversion semantics. These guards do not change the settings; the
  original native provider setup/cleanup remains in the same resource scope.
  Every actual nullable-to-required Alter requires strict mode, including an
  approved default UPDATE or type widening/narrowing. Matching no-ops and
  already-required ordinary comment/default repairs do not acquire that guard.
- Required TIMESTAMP Ensure/Alter repairs additionally require
  `@@SESSION.explicit_defaults_for_timestamp = 1`, including repairs with an
  approved fixed default. Special NULL-to-current-time behavior must not
  silently substitute a value. Missing no-default TIMESTAMP additions are not
  certified by this protocol; already matching targets remain comparable.
- Missing required targets with authored SQL defaults retain a separate native
  ADD path, qualified by InnoDB and strict mode, plus explicit-default semantics
  for required TIMESTAMP. Native ADD atomically validates the authored expression
  against affected rows; an expression is not thereby a structural non-null
  backfill proof. A structured SQL literal NULL blocks a populated owner where
  the engine represents the target; an empty owner still needs qualification,
  and a raced row must make ADD fail without fabricated data. Engine/default
  representability remains independent, and matching no-ops do not acquire the
  missing-only guard or a staging CHECK.
- Supported uncast structured TIMESTAMP/DATETIME string defaults can match
  exact native fractional zero-padding at the target precision. MySQL keeps
  DEFAULT_GENERATED's exact `_utf8mb4` serialized-expression display separate
  from non-generated original/plain and zero-padded temporal candidates; MariaDB
  retains its quoted display path. No charset-prefix union, rounding/truncation,
  domain reinterpretation or authored expression/kind/fingerprint mutation is
  authorized by this comparison.
- The deterministic name is `doka_sm_add1_nn_` plus 48 hexadecimal digits,
  within the 64-character limit. It binds the raw authored owner, complete
  target and immutable fingerprint. A matching prefix is not ownership.
  Qualified and omitted raw schemas do not become interchangeable recovery
  contracts, and the active-database qualifier guard is not broadened.
- Physical schema/table lookup follows the server's table-name mode: mode zero
  compares bytes exactly; modes one/two use server-side LOWER and compare the
  resulting bytes. The column, CHECK name/expression and raw fingerprint remain
  exact. Sparse unexpected-object recognition uses the same native owner lookup.
- Complete target facets must match. ENUM/SET keyword spelling can be
  canonicalized, but decoded member case, member order and quoted literal
  contents are significant. Backslash interpretation follows the session mode;
  double-quoted members cannot be treated as literals under ANSI_QUOTES,
  independently of whether backslash decoding is required.
  Unrecognized or malformed domain syntax does not gain a matching certificate.
- A native backslash-comment scope must not change the member domain. If domain
  decoding depends on backslashes, non-matching column creation/repair and table
  creation require NO_BACKSLASH_ESCAPES already enabled; a table or any-column
  comment can affect the full CREATE scope. Exact matching remains comparable
  after native cleanup and Verify. Standard-only escaped-quote staging instead
  requires its original NO_BACKSLASH_ESCAPES-disabled mode without opaque native
  setup. A known Doka backslash-comment scope disables this protocol for missing
  conversion, not an existing exact target's ordinary no-op. An unknown opaque
  setup paired with a protocol certificate is rejected during generation.
  No new mode write or domain rewrite resolves the incompatibility.
- Recovery requires the exact nullable target and enforced owned CHECK, freshly
  requalified for the same operation. Wrong shape, colliding name, disabled
  enforcement or ambiguous pending ownership cannot certify resume or cleanup.
  A completed required target is matching only without the staging CHECK.
- Existing conflicting columns remain Different: ThrowIfDifferent and
  RepairIfSafe reject, while ExistenceOnly retains its explicit no-op without
  resume or CHECK removal. A reserved-name collision with a missing target
  column is Unsupported for every policy.
- Failure and cancellation retain a committed intermediate CHECK. Cleanup does
  not infer which ALTER completed or remove persistent staging artifacts.
  Recovery re-reads the catalog and repeats the same forward operation.
  Recognition of an equivalent stage does not prove who historically created it.
- A resumed Missing/Apply can contain explicit writer rows. Core therefore
  must not infer table emptiness or automatically authorize later PK/FK work.
  Exact source and row certificates retain their ordinary mutation lifetimes.
  An accepted column tombstone proves absence only; it does not satisfy a later
  required ADD's fresh row or engine/session eligibility by claiming empty rows.
- Required repairs reject retained outgoing DELETE/UPDATE SET NULL dependencies
  before backfill UPDATE or DDL; nullable repairs reject retained PRIMARY
  membership. An exact accepted drop discharges only its own dependency. Healthy
  qualified engines reject creating a SET NULL action on the owned CHECK column;
  the conservative resume predicate does not authorize anomalous removal.
- Column dependency capture uses at most 32 tagged parameterized requests per
  statement and eight statements per transport batch within the existing
  payload bound. Deduplication admits only verified current-database aliases,
  identical raw owners, case-insensitive
  members and the same SET NULL/PRIMARY role; independent operation-reference
  lifetimes remain intact. Exact five-field width, validated sentinels, ordinals,
  roles, strict boolean/integral-zero-or-one values and complete physical-name
  fanout precede publication. Completed FK sets are immutable across consumers.
  Neither a partial transport result nor batching establishes new row/source
  permission or skips runtime freshness.
- Catalog-only status is captured once per operation, after identity and
  prerequisite guards. Classification, repair eligibility, NULL-probe selection
  and diagnostics consume that closed status. Final postcondition reads the live
  catalog independently after DDL and requires both the target and guard absence.
  Neither status nor recovery recognition is a cross-operation freshness proof.
- Stateful native cleanup requires the exact certified Doka SQL-mode scope:
  two setup fragments and one restore. Operation-owned entry state is initialized
  before guards; immediately before setup, after the decision assertion, current
  mode capture and entry marking are atomic. Only an entered scope can restore
  its captured mode, and bookkeeping is reset afterward. Pre-setup rejection
  must never restore NULL or stale state. Unknown stateful cleanup is refused;
  commands without cleanup retain their ordinary path. Actual restore failure
  retains the unsafe-connection boundary, not permission to reuse the session.

MariaDB writers can disable CHECK enforcement in their own sessions. The stage
is not a promise that every writer is blocked: final strict NOT NULL conversion
still refuses a raced NULL rather than coercing it, retaining the barrier on
failure. Applications must account for inserts omitting the required value
failing between the stages or after interruption. Additional ALTER work and
engine metadata locks require a maintenance-window assessment; this decision
does not promise instant DDL, online execution or application-wide quiescence.

### Consequences

- Good, because each successful transition preserves the required invariant
  without a fabricated default, and explicit writer values survive recovery.
- Good, because exact ownership and a fresh final postcondition prevent prefix
  cleanup, stale complete-state promotion and unowned CHECK removal.
- Good, because the native renderer, authored metadata, EF history and dependency
  ranges remain unchanged; the change is isolated to the MySQL/MariaDB adapter
  and its ordered recovery certificate consumed by Core.
- Good, because factoring one closed catalog status avoids repeating the full
  ownership proof in every branch without treating row safety as catalog state.
- Bad, because two ALTERs add DDL and catalog work and can retain a durable
  intermediate CHECK after failure. This is a recovery contract, not free work
  or a measured general performance improvement.
- Bad, because writers omitting the new value can fail until recovery, and an
  independent writer can bypass MariaDB CHECK enforcement in its own session.
- Bad, because uncertified engines, modes, domain syntax and no-default TIMESTAMP
  additions are refused rather than converted using implicit engine defaults.

### Confirmation

The following are acceptance procedures and expected outcomes, not assertions
that these commands, all engine cells or approval gates have completed.

Source inspection:

- Review the catalog protocol, native body validation, fact capture and recovery
  inventory references below. Require no newly introduced session-mode writes
  beyond preserved certified native-provider setup/cleanup, no LOCK TABLES,
  no invented default, no generic prefix skip and no persistent-CHECK cleanup.
  Verify that the fresh final postcondition is independent of captured status.
- Compare the pinned Doka 10.4.2 model-differ and renderer sources with the locked
  provider contract. The bool marker remains validated and immutable; no package
  minimum is raised to enable this protocol.

Local execution:

```bash
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj --configuration Release --filter 'FullyQualifiedName~MySqlRequiredColumnAdd|FullyQualifiedName~MySqlExplicitBackfillMetadata'
dotnet test tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj --configuration Release --filter 'FullyQualifiedName~RequiredColumnRecovery'
```

Expected: exact native bodies and resource scope, one catalog-status capture,
separate ordinary repair, immutable metadata, unknown-marker rejection,
malformed status rejection, native owner ambiguity and domain decoding controls
pass. Core retains the resume certificate through enrichment but does not infer
empty rows from it; a genuinely fresh empty addition remains the positive control.
Require the staging-copy regression to retain every immutable target facet and
annotation, with its reflected member inventory failing on an unreviewed new
definition member. Native cleanup-binding controls must preserve certified
outer whitespace and reject changed or appended statement shapes rather than
silently losing the acquired-scope marker.

Live database qualification:

- Run the focused `AtomicRequiredAdd`, `RequiredDefault` and `ExplicitBackfill`
  tests using the engine/image selection documented in
  [Contributing](../../CONTRIBUTING.md). Repeat on independently selected MySQL
  and MariaDB images from the current qualification matrix; a default MariaDB
  run is not MySQL or all-version evidence.
- Require fresh application, matching rerun and interrupted forward recovery to
  preserve the exact target, defaults, explicit writer values and fingerprint.
  Require wrong owner/guard/column, mode-off, non-InnoDB, ambiguity and reserved
  collision cases to retain their policy-specific refusal without cleanup.
- Require the adversarial two-connection concurrency tests to insert before the
  first stage, omit or supply a value between stages, bypass MariaDB enforcement
  and cancel after committed staging. Raced NULL must never become an implicit
  value; refusal must retain original rows and the committed barrier. The
  explicit-value and subsequent forward-recovery controls must succeed.
- Require exact ENUM/SET domain controls to succeed and case-changed or reordered
  members to preserve original values and their guard without matching/resume.
  Exercise quote/escape modes without folding literal contents into equivalence.
- Require native comment-scope domain tests to reject non-matching standard-mode
  work before domain/comment change, admit the already-enabled-mode positive
  control, and preserve exact matching after cleanup/Verify. Compare stored member
  and comment bytes independently from the original mode and fingerprint.
  Standard-only escaped-quote scopes must refuse unproved setup reinterpretation.
- Require temporal SQL-literal display controls to admit exact zero-padding and
  reject fractional truncation, invalid calendars and other temporal domains
  without changing the original literal or fingerprint. MySQL generated/plain
  controls must retain exact expression-marker separation and refuse different
  charset prefixes or quote payloads impersonating calendar values.
  Runtime double-quoted
  ENUM/SET controls must reject ANSI_QUOTES independently of backslash need.
- Require native-scope rejection controls with uninitialized and stale prior
  save variables to preserve the actual mode, CONNECTION_ID and original
  rejection, without target mutation. Failure/cancellation after entered setup
  must restore the captured mode; an injected real restore failure must retain
  any preceding primary failure and invalidate the unsafe connection. Unknown
  stateful cleanup shapes must refuse generation; no-cleanup controls remain
  unchanged.
- Require required SQL-default ADD controls to reject permissive/non-InnoDB
  sessions and temporal implicit-default modes before DDL. Known SQL NULL on a
  populated owner remains DataBlocked where the target is representable; a
  qualified empty ADD and matching rerun are positive controls. A writer racing
  the empty observation must leave its original row intact and the target column
  absent, not acquire an implicit value. Engine representability refusals remain
  distinct from the session and row-safety verdicts.
- Require metadata visibility, runtime prerequisite precedence, sparse recovery
  inventory and native owner-mode controls. Modes unavailable on a particular
  initialized host need separately configured qualified fixtures, not an inferred
  result from that host's default mode.
- Require dependency-capture controls to bound requests and parameters while
  retaining every physical SET NULL name and PRIMARY verdict. Wrong/missing
  sentinel, ordinal, role, boolean or duplicate-name evidence must refuse
  publication. Qualified-alias controls can share facts; raw-owner/role and
  independent unsupported/source controls must not acquire another certificate.

Hosted and human evidence:

- The required [quality matrix](../../.github/workflows/quality-gates.yml),
  package/tooling and coverage checks remain blocking. Local live tests or source
  review do not replace a required cell. Native SQL Server live qualification
  still needs its supported x86-64 host; ARM skips do not prove shared-Core parity.
- Record source identity, exact engine/image, commands, executed/skipped counts
  and retained artifacts for completed checks. Runtime, SQL-size and allocation
  comparisons are informational evidence, not hardware-sensitive merge gates.
- The decision maker must review this exact contract before its status becomes
  accepted. Implemented additionally requires repository confirmation evidence;
  tests alone must not silently promote authority.

## Pros and Cons of the Options

### Two atomic ALTER stages with an owned enforced CHECK

- Good, because the engine publishes the nullable column and its barrier
  together, then preserves the invariant while removing only the owned barrier.
- Good, because independently committed staging has an inspectable forward
  recovery state without inventing data or additional lock privileges.
- Bad, because extra DDL and a possibly retained CHECK affect writers and require
  exact ownership, policy, mode and final-postcondition qualification.

### Observe emptiness and issue an unguarded native required ADD

- Good, because it uses one native ALTER and is sufficient when an independently
  enforced maintenance window actually guarantees no concurrent writes.
- Bad, because the library's observation cannot establish that guarantee; a
  raced row can acquire an unapproved implicit value before DDL completes.

### Supply a synthetic non-null default for the required ADD

- Good, because a deterministic generated default can make a required ADD
  mechanically possible on populated tables with one native DDL statement.
- Bad, because a library-invented default changes row meaning, can violate keys
  or relationships, and is not authorized by absence of a migration default or
  by the provider backfill marker. Explicit approved defaults remain separate.

### Hold LOCK TABLES across the observation and required ADD

- Good, because an effective exclusive write lock can close the race for writers
  obeying the same engine lock domain during the protected interval.
- Bad, because it introduces a lock privilege and session/connection ownership
  contract, disrupts writers and requires separate qualification of DDL implicit
  commits and lock release. That is not the existing native-DDL provider boundary.

## More Information

This proposal complements [D-004](D-004-analysis-and-execution-lifecycle.md)
on guarded execution and recovery, [D-005](D-005-bounded-evidence-and-privacy.md)
on bounded immutable evidence, and
[D-011](D-011-lossless-column-repair-and-actionable-diagnostics.md) on lossless
repair. It does not supersede or amend those records. The operation-window
catalog facts continue to use the binding boundary described by
[D-014](D-014-mariadb-catalog-snapshot-for-batched-classification.md), while
runtime and final postcondition guards read fresh catalog state.

The [MySQL/MariaDB guide](../mysql-mariadb-ddl-behavior.md#atomic-required-column-additions)
owns operational limitations and recovery states; the
[failure-code runbook](../runbooks/failure-codes.md) owns diagnostic handling.
Neither this record nor those procedures authorizes tag creation, publication,
manual data replacement or bypass of the protected release environment.

### Re-evaluation Triggers

- A qualified engine changes multi-clause ALTER atomicity, CHECK validation,
  default/TIMESTAMP semantics, domain display or metadata identity rules.
- A provider renderer no longer supplies an exactly owned single-column native
  ALTER with equivalent setup/cleanup scope, or the marker contract changes.
- A reproduced execution fabricates a row value, loses an explicit writer value,
  removes an unowned CHECK or promotes resume into an empty-table certificate.
- Measured catalog/DDL overhead warrants a different implementation preserving
  the same proof, ownership, concurrency and recovery invariants.
- A separately approved application-wide maintenance or locking contract changes
  the write-domain boundary; a local observation alone is not that trigger.

### Decision History

- 2026-10-12: Decision recorded with status proposed.
- 2026-10-12: Existing implementation work is recorded for review. Authorization
  of the concurrency scope is not acceptance of this text; historical approval
  dates or completed qualification results are not reconstructed.
- 2026-10-12: Missing SQL-default additions retain separately qualified native
  atomic validation. A parsed default, including SQL NULL, is not automatically
  a non-null row-replacement certificate.
- 2026-10-12: Native ENUM/SET comment-mode scope qualification preserves the
  original decoded member domain instead of borrowing an in-scope match.
- 2026-10-12: Certified native session-scope restoration is bound to actual
  setup entry; temporal display matching preserves exact zero-padding only.
- 2026-10-12: Source provenance distinguishes observed required-ADD behavior
  from MySQL's documented DML defaults and MariaDB's documented Instant ADD
  defaults. The chosen protocol does not rely on implicit ADD replacement.

### Implementation References

- [Catalog eligibility, ownership and status](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/Columns/MySqlSafeMigrationCatalogSqlBuilder.Columns.RequiredAddProtocol.cs).
- [Native atomic bodies and shared scope](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/SqlGeneration/MySqlSafeMigrationOperationHandler.RequiredColumnAdd.cs).
- [Certified native setup-entry and conditional cleanup](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/SqlGeneration/MySqlSafeMigrationOperationHandler.ProviderCleanup.cs).
- [Bounded operation-local fact capture](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.RequiredColumnAddFacts.cs).
- [Bounded column dependency transport](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.ColumnForeignKeys.Capture.cs)
  and [reference-bound dependency publication](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.ColumnForeignKeys.cs).
- [Sparse inventory ownership](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.RequiredColumnAddRecovery.cs)
  and [native owner resolution](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationProviderAnalyzer.RequiredColumnAddRecovery.Owners.cs).
- [Column matching, domain decoding and repair guards](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/Columns/MySqlSafeMigrationCatalogSqlBuilder.Columns.cs).
- [ENUM/SET domain and native mode qualification](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/Columns/MySqlSafeMigrationCatalogSqlBuilder.Columns.EnumSet.cs).
- [Catalog regression tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlRequiredColumnAddCatalogTests.cs),
  [generation tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlRequiredColumnAddGenerationTests.cs)
  and [recovery ownership tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlRequiredColumnAddRecoveryCandidateTests.cs).
- [Live state and recovery tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.AtomicRequiredAdd.cs),
  [adversarial writer/cancellation tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.AtomicRequiredAddConcurrency.cs)
  and [native owner-mode tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.AtomicRequiredAdd.OwnerCase.cs).
- [Default/TIMESTAMP tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.RequiredDefaults.cs)
  and [ENUM/SET domain tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.RequiredDefaults.EnumDomain.cs).
- [Required SQL NULL ADD tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.RequiredDefaults.SqlNullAdds.cs).
- [Native ENUM/SET comment-mode tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.RequiredDefaults.EnumCommentMode.cs)
  and [projected ADD eligibility tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.RequiredDefaults.ProjectedAdds.cs).
- [Native scope rejection, error/cancellation and restore-failure tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Columns/MySqlSafeMigrationIntegrationTests.Columns.NativeScopeLifecycle.cs).
- [Certified native statement-binding tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlProviderCleanupBindingTests.cs)
  and [complete nullable staging-copy tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlRequiredColumnAddStagingContractTests.cs).
- [Core resume-versus-empty certificate tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationPreflightProjectionTests.RequiredColumnRecovery.cs).
- [Dependency transport, deduplication and malformed/cancelled-evidence tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlColumnDependencyTransportTests.cs).

### Sources

- MySQL 8.4 ALTER TABLE and enforced CHECK validation (`https://dev.mysql.com/doc/refman/8.4/en/alter-table.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 implicit defaults for INSERT, REPLACE and UPDATE, not an ADD guarantee (`https://dev.mysql.com/doc/refman/8.4/en/data-type-defaults.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 atomic DDL and its transaction boundary (`https://dev.mysql.com/doc/refman/8.4/en/atomic-ddl.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 identifier case sensitivity (`https://dev.mysql.com/doc/refman/8.4/en/identifier-case-sensitivity.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 string literals and session quote/escape modes (`https://dev.mysql.com/doc/refman/8.4/en/string-literals.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 ENUM member representation and order (`https://dev.mysql.com/doc/refman/8.4/en/enum.html`; primary source; retrieved 2026-10-12)
- MySQL 8.4 SET member representation and order (`https://dev.mysql.com/doc/refman/8.4/en/set.html`; primary source; retrieved 2026-10-12)
- MariaDB atomic DDL (`https://mariadb.com/docs/server/reference/sql-statements/data-definition/atomic-ddl`; primary source; retrieved 2026-10-12)
- MariaDB Instant ADD COLUMN and type-implied NOT NULL defaults (`https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-online-ddl/instant-add-column-for-innodb`; primary source; retrieved 2026-10-12)
- MariaDB session CHECK enforcement (`https://mariadb.com/docs/server/server-management/variables-and-modes/server-system-variables#check_constraint_checks`; primary source; retrieved 2026-10-12)
- MariaDB TIMESTAMP automatic values (`https://mariadb.com/docs/server/reference/data-types/date-and-time-data-types/timestamp#automatic-values`; primary source; retrieved 2026-10-12)
- Doka 10.4.2 immutable model-differ source (`https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/b2408bfbe29517d742734d06d848081cdb9e1de0/src/Doka.EntityFrameworkCore.MySql/Internal/Migrations/MySqlMigrationsModelDiffer.cs`; primary source; retrieved 2026-10-12)
- Doka 10.4.2 immutable column-renderer source (`https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/b2408bfbe29517d742734d06d848081cdb9e1de0/src/Doka.EntityFrameworkCore.MySql/Internal/Migrations/MySqlMigrationsSqlGenerator.Columns.cs`; primary source; retrieved 2026-10-12)
