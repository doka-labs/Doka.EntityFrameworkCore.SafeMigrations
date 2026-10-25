# MySQL and MariaDB DDL behavior

Version 10.6.0 adds certified generated/CHECK contracts and native
independent DROP proofs. Review the
[upgrade notes](support-and-qualification.md#upgrade-to-1060) for conditional
contract-fingerprint changes, dependency floors and conservative boundaries.

Owned runtime diagnostics introduced in 10.5.0 identify the authored owner
and established rejection reason after native cleanup. Their managed outer exception and script
result transport are described in [diagnostics](diagnostics.md#runtime-provider-differences);
ordinary native errors, decisions and forward-recovery requirements remain intact.

The 10.4.11 patch qualifies sparse mixed column/index/FK prerequisites
against independently captured native endpoints, key/backing identities and
accepted physical lifetimes. Native table-case equality is shared by live
transition/backfill catalogs; connection-free generation selects exact/folded
alternatives only under verified runtime guards. Unsafe multi-valued FK support
and automatic-index name collisions remain blocking. Current row safety is not
inferred from target-model nullability or a future operation.

The adapter preserves exact column proofs after owned FK removals,
supports data-proven nullability/backfill transitions, and uses two atomic
InnoDB stages with an owned enforced CHECK for supported required additions.
Interrupted additions recover only from their exact owned intermediate state;
the two stages are not one transaction. Strict SQL mode, TIMESTAMP semantics,
ENUM/SET domains and retained SET NULL/PRIMARY dependencies remain independently
guarded. Comment matching is byte-exact, and native cleanup is bound to acquired
setup. Runtime checks remain fresh even when analysis uses session-owned
catalog copies. The 10.5.0 diagnostic API/schema/exception and Doka dependency
changes are documented in [support and qualification](support-and-qualification.md).
Migration source, policies and history remain unchanged; no server durability
setting is modified.
Earlier bounded analysis/runtime optimizations remain. Required data scans and
DDL can still dominate large-table workloads; timing comparisons are informational.

## Operational summary

The `.MySql` package supports both MySQL and MariaDB through
`Doka.EntityFrameworkCore.MySql`. Its SafeMigrations guard is rerunnable and
its decision/assertion state is session-local, but MySQL-family DDL is not an
atomic migration transaction.

- No stored procedure, permanent guard table, or fixed global helper name is
  used. A required-column addition can leave its exact owned intermediate CHECK
  until forward recovery completes; see [atomic additions](#atomic-required-column-additions).
- The migration principal does not need `CREATE ROUTINE`.
- Decision/assertion state lives in the current session and temporary database
  objects; the intermediate CHECK is deliberately persistent across failures.
- Prepared SQL contains provider-rendered DDL, not caller-provided SQL text.
- SafeMigrations declares its user-variable capability through Doka 10.4.0.
  Doka supplies `AllowUserVariables=true` for a provider-owned string only when
  the option was omitted; contradictory owned values fail closed.
- Caller-owned `DbConnection` and `MySqlDataSource` inputs are never mutated.
  They must already set `AllowUserVariables=true` and
  `GuidFormat=Binary16`.
- Every connection path must retain matched-row semantics
  (`UseAffectedRows=false`).
- The runner validates the actual current `DbConnection` before pending EF
  history, model, environment, lock, catalog access, or connection opening.
  Replacing a connection cannot inherit validation from an EF-cached service
  provider.
- An explicit EF table schema is treated as a MySQL/MariaDB database
  qualifier. It must exactly equal the active connection's `DATABASE()` value.
- A failure can occur after earlier DDL committed; retry must converge from the
  observed partial state.

## Current-database qualification

Doka preserves EF table and view `Schema` metadata as MySQL/MariaDB database
qualification. SafeMigrations accepts the explicit qualifier only when it
exactly equals the database selected by the active connection. The verified
qualifier and an omitted qualifier then share one physical identity in the
expected catalog, unexpected-object inventory, ordered projection, generated
transition catalogs, postflight final-writer projection, DDL, constraints,
indexes, and model-managed data.

SQL generation cannot inspect the active connection. When an ordered
SafeMigrations stream contains exactly one explicit database qualifier,
generated cross-operation catalogs may merge that qualifier with omitted
qualifiers only because every SafeMigrations command in the stream receives
the same runtime `DATABASE()` guard. A foreign qualifier therefore rejects the
first otherwise unqualified command before DDL or DML. Multiple distinct
qualifiers remain separate and their shared guard cannot succeed, so an
ambiguous stream also fails before its first SafeMigrations mutation.

The identity check runs before catalog prerequisites, row probes, or target
SQL. A different qualifier, or an explicit qualifier on a connection without a
selected database, is `Unsupported` with `database_qualifier_mismatch`. The
adapter does not remove the qualifier, inspect a same-named current-database
object as a substitute, or perform cross-database DDL/DML. This preserves the
isolation boundary even when both databases contain the same table name.

EF may scaffold `EnsureSchema` when a MySQL/MariaDB model uses
`HasDefaultSchema(currentDatabase)`. For the selected database this operation
is an idempotent no-op because the connection already proves its existence.
Dropping the selected database remains outside SafeMigrations and is rejected
with `schema_operations`. A drop naming another database is rejected first
with `database_qualifier_mismatch`; PostgreSQL-style independent schema
namespaces remain a provider mismatch.

The identity guard deliberately compares the configured qualifier and
`DATABASE()` byte-for-byte. MySQL and MariaDB can change database and table
name comparison with `lower_case_table_names`, and that setting also depends
on server initialization and platform. SafeMigrations does not broaden a
reviewed database identity according to that environment-specific setting.
Use the exact `DATABASE()` spelling consistently in model metadata and across
environments:

- [MySQL identifier case sensitivity](https://dev.mysql.com/doc/refman/8.4/en/identifier-case-sensitivity.html)
- [MariaDB identifier case sensitivity](https://mariadb.com/docs/server/reference/sql-structure/sql-language-structure/identifier-case-sensitivity)

MySQL documents `db_name.tbl_name` as database-qualified table identity, and
MariaDB documents that an omitted database qualifier resolves to the current
database. Both engines expose that selected identity through `DATABASE()`:

- [MySQL CREATE TABLE](https://dev.mysql.com/doc/refman/8.4/en/create-table.html)
- [MySQL current database](https://dev.mysql.com/doc/refman/8.4/en/getting-information.html)
- [MariaDB identifier qualifiers](https://mariadb.com/docs/server/reference/sql-structure/sql-language-structure/identifier-qualifiers)
- [MariaDB DATABASE](https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/information-functions/database)

## Why multiple commands are required

The engines do not provide one native statement for every required branch:

```text
missing       -> execute target DDL
matching      -> no-op
different     -> reject or apply an allowlisted repair
unsupported   -> reject
data-blocked  -> reject before target DDL
```

MySQL also prohibits `SIGNAL`, `RESIGNAL`, and `GET DIAGNOSTICS` in prepared
statements. SafeMigrations therefore separates prerequisite capture, lazy
live-state capture, assertion, prepared target DDL, postcondition capture, and
cleanup inside one provider-executed command scope.

## Session-local guard shape

The provider handler emits a plan equivalent to:

1. create the session-local assertion table;
2. verify an explicit database qualifier against the active connection;
3. evaluate catalog-only table and referenced-column prerequisites only after
   the identity check succeeds;
4. classify live state and evaluate only the still-required repair and data
   proofs, after prerequisites exist;
5. run a non-DDL assertion that fails for a rejected state;
6. select provider-rendered DDL or an inert statement;
7. `PREPARE` and `EXECUTE` the selected statement;
8. evaluate and assert the target postcondition;
9. always deallocate the prepared statement, restore an entered certified
   provider session scope, clear SafeMigrations variables, and drop the temporary
   assertion table.

For repair-capable `EnsureColumn` plans, a fresh exact column match is checked
after database identity, prerequisites, and any state-evaluation guard succeed.
An exact match skips repair eligibility and repair row probes; missing or
different columns retain their required classification and data checks. This
is an operation-local runtime check, not reuse of a preflight result or an
earlier operation's proof. An intervening DDL or DML operation therefore cannot
inherit a previous match.

Ensure repairs and explicit alterations share fresh nullability-dependency
guards. A required target cannot retain a local `ON DELETE SET NULL` or
`ON UPDATE SET NULL` FK, and a nullable target cannot retain PRIMARY membership.
These predicates qualify repair, not an already matching no-op or the ordinary
guarded missing-column addition. They run before a repair's backfill UPDATE or
column DDL; a valid data proof does not remove an incompatible dependency.

Repair-capable plans classify state before evaluating the final repair
precondition, which is needed only for `Different`. Separate SQL statements
establish the state before this predicate reads it; grouping those statements
does not change their order. Physical eligibility probes still precede full
classification where required to admit a data-safety query.
The repair expression uses catalog checks and already captured operation-local
row proofs. It does not prepare target-row queries before their guards succeed.
Post-DDL postconditions and rejection assertions remain independent checks.
The per-operation assertion table and its default storage engine are unchanged.
These runtime reductions also apply when explicit preflight analysis is disabled.

Names and catalog literals are generated by EF/Doka SQL helpers. During
analysis, scoped capture leases invoke Doka generation in 512-operation windows
and record typed runtime plans produced with Doka's real server-version and
feature context. The complete migration-level expected unique-index catalog is
shared across those windows. The analyzer renders at most 32 operations into
one optimizer-visible statement and transports at most eight statements in one
parameterized ADO.NET batch. It does not parse command text. Preflight does not
run target DDL, create an EF history row, or create a stored routine.

The same capture records complete expected unique-index definitions by table.
MySQL and MariaDB publish each unique index through both `INFORMATION_SCHEMA.STATISTICS`
and `TABLE_CONSTRAINTS`; strict table classification accepts that duplicate
catalog identity only for an expected index in the complete migration. Unknown
unique keys still classify the strict table as different and remain visible in
the unexpected-object inventory. Outside analysis, EF passes its target
relational model to runtime generation; the scoped handler caches the unique
index names from that model so a completed strict batch remains idempotent.
When neither batch nor target-model evidence is available, the alias is not
accepted.

Every schema- or data-bearing safe operation returns exactly one Doka 10.4.0
scoped migration command. Its bounded fragment list contains ordered setup,
exactly one guarded provider body, and reverse-order cleanup. The
single-provider-command case retains the established prepared-statement hot
path. A multi-command provider transition, such as a non-null backfill followed
by column DDL, stays behind one decision and receives one final postcondition.
The internal design-time-services guard instead returns Doka's explicit
commandless consumed result. A data-reading classifier adds setup fragments for
lazy state evaluation without adding EF command boundaries.

At runtime, owned prepared-SQL assignments include their immediately following
`PREPARE`/`EXECUTE`/`DEALLOCATE` controls in the same final string allocation.
The baseline assignment includes only `PREPARE`; its execution and postcondition
remain in the separate guarded body. Semicolons and exact statement order are
preserved, including dependent user-variable assignments. No completed large
assignment string is copied to append controls.

Adjacent bounded SafeMigrations-owned setup fragments are also grouped in
their original order to reduce database transport exchanges. Each copied group is
limited to 4096 UTF-8 bytes and the largest previous individual setup/body
payload, whichever is smaller. Groupable owned assignments retain immutable
text and hexadecimal-UTF-8 pieces until their final group is known, then write
directly into that group's string through spans. They do not allocate a complete
assignment string only to copy it into the group. Large independent fragments
are written immediately from the source span, without a deferred descriptor
array; already materialized SQL retains its original string reference. UTF-8
accounting preserves split surrogate pairs in raw text while separately encoded
prepared-statement values remain independent. Rented encoding buffers live only
for the synchronous write and are returned in `finally`. The original
128-fragment and 1,048,576-character scope limits are checked before grouping;
controls fused directly into assignments still count as original fragments.
An exact-match prefix stays in its existing evaluation fragment, and state
classification plus its final repair predicate remain one logical fragment.
Separate SQL statements do not introduce additional provider fragments.
Grouping cannot admit a scope that the provider would otherwise reject. The
concatenated script text is unchanged. Opaque provider setup remains an
independent boundary, as do the guarded body and every cleanup command. Keeping
cleanup independent lets later cleanup run even if an earlier cleanup fails.

Direct rendering reduces intermediate allocation introduced by grouping;
grouping can still allocate more total bytes than the earlier ungrouped path.
These are cumulative SQL-generation allocations, not retained or peak memory.
Fewer command dispatches do not guarantee faster large-table scans or DDL.

Grouping does not combine operations or cache live state. Every required
identity, prerequisite, data-safety, decision, and postcondition check remains
operation-local, immediately around its body. Caller cancellation is forwarded,
and cleanup retains its independent cancellation token. The configured command
timeout applies to each fused or grouped setup command instead of each former fragment;
it is not a separate timeout per SQL statement. Fusing controls can increase the
largest individual assignment dispatch by its fixed ASCII control suffix, while
the total SQL payload and original full-scope text limit remain unchanged. See
[MySqlConnector command cancellation](https://mysqlconnector.net/overview/command-cancellation/).
No server durability, storage-engine, or transaction setting is changed. Actual
DDL can still incur the documented implicit commits.

The scoped handler also retains at most one immutable decision-SQL assignment,
keyed only by operation kind, conflict policy, and structural repair capability.
Replacing that entry does not retain an operation, model, or catalog evidence.
Every required identity, prerequisite, row-safety, repair-precondition, and
postcondition query is evaluated afresh; the cached SQL stores no verdict.
Invalid planner inputs fail without replacing the last valid entry. The key and
SQL are published together through
[.NET volatile reference access](https://learn.microsoft.com/en-us/dotnet/api/system.threading.volatile?view=net-10.0);
this does not enable concurrent use of a DbContext or migration generator.

## Read-only catalog transport

Read-only catalog analysis omits prerequisite transport only for the exact
builder-certified constant `TRUE`. Nonconstant predicates, including database
qualification, remain barriers before row-dependent queries. Independent
diagnostic, narrowing-eligibility and already-qualified row-probe statements
share bounded native batch transport; compatible wrappers retain sequential
execution, timeout, cancellation, parameters and exact result validation.
No runtime proof is reused across operations. This reduces network roundtrips
on MariaDB; MySqlConnector does not guarantee the same wire benefit on MySQL.
See [MySqlConnector batching](https://mysqlconnector.net/api/mysqlconnector/mysqlbatchtype/).

Physical key-environment and unexpected-object reads also use this transport.
Their complete 512-table or 512-value statements remain intact; batching does
not reduce those metadata chunks to 32 values. Each transport retains the
eight-statement, aggregate parameter and 4 MiB bounds, further limited to half
the live `max_allowed_packet`. Every result belongs to its submitted statement;
incomplete or misbound metadata fails rather than publishing a partial inventory.

On MariaDB, an operation window with at least 32 unresolved, unqualified
operations may copy the catalog views into uniquely named InnoDB session
temporary tables. The explicit engine keeps TEXT-capable catalog copies
independent of `default_tmp_storage_engine`, including a MEMORY default.
Reusing those rows avoids repeated INFORMATION_SCHEMA materialization;
it does not cache application data or skip any facet predicate. The foreign-key
copy includes constraints owned by the current database and incoming references
from other databases. Explicitly qualified operations keep live catalog reads,
including within a mixed window. MySQL and generated migration commands always
keep their live catalog reads.

The copies do not form an atomic database snapshot: catalog views are captured
successively, and later concurrent DDL is not refreshed inside the operation
window. Runtime execution still performs fresh guards. Redirection recognizes
catalog relation tokens, not arbitrary text, so SQL literals, quoted identifiers
and comments are preserved.

Temporary-table DDL requires `CREATE TEMPORARY TABLES` and is not allowed inside
a MariaDB read-only transaction. When that optional optimization is prohibited,
analysis uses the live catalog; an unavailable InnoDB engine also retains that
path. Analysis does not require a privilege grant or a new
configuration switch. Pooled connections with `ConnectionReset=false` also keep
the live path before any temporary DDL; nonpooled connections remain eligible.
Other errors and cancellation are not treated as fallback
success. Successful creates establish per-table ownership, and cleanup only
drops owned copies. Partial initialization and failed classification also clean
up; cleanup errors never replace an existing analysis exception. If cleanup
cannot safely complete, the affected connection is closed. A nonpooled close
destroys its session; an eligible pooled session resets before reuse. Healthy
caller-open connections remain open.

See [D-014](decisions/D-014-mariadb-catalog-snapshot-for-batched-classification.md),
[MariaDB temporary tables](https://mariadb.com/docs/server/server-usage/tables/create-table)
[MariaDB transaction modes](https://mariadb.com/docs/server/reference/sql-statements/transactions/start-transaction)
and [MySqlConnector connection options](https://mysqlconnector.net/connection-options/).
Timing improvements are measurements, not correctness or merge thresholds.

## Model-managed data

Newly scaffolded model-managed data uses typed parameters. Keys and ordinary
scalar values use MySQL/MariaDB's null-safe `<=>` operator. Native JSON values
use engine-specific document equality: MySQL casts the expected relation value
to JSON before `<=>`, while MariaDB combines explicit SQL-NULL branches with
`JSON_EQUALS` because its JSON type is LONGTEXT. Equivalent whitespace and
object-member order are idempotent; array order, duplicate elements, JSON
`null`, SQL `NULL`, and JSON-like text retain distinct contracts.
Ensure emits conditional plain inserts. Update and delete are compare-and-swap
operations whose predicates include the captured source row and whose guarded
scope validates the final state. The implementation does not use `INSERT
IGNORE` or `INSERT ... ON DUPLICATE KEY UPDATE`: those forms can hide integrity
errors or select the first matching unique index without proving that the
model-managed primary-key row is the intended row.

Ordered projection treats key-column labels with the provider's separate
case-insensitive column identity. Row hashes, self-key collision exclusion,
recorded value lookup and postflight final-writer identity therefore agree
when two operations spell the same physical column differently. Reordering a
composite predicate is equivalent only when every label retains its paired
typed value. Table/database identity and typed values remain independent;
mathematical uniqueness proof uses the same candidate column set, without
changing physical index/FK member order or their qualification guards.
Unicode canonicalization retains any fold rejected by the certified comparer;
it is not an unrestricted invariant-uppercase or row-value transformation.
The captured authored labels and operation fingerprints are not rewritten.

Model-managed mutation requires a transactional table engine. A
non-transactional target is `Unsupported` before DML. The normal EF migrator
owns transaction creation. Doka's handler scope participates in that transaction
and performs its session-local cleanup, but it does not begin an independent
nested transaction.

Delete analysis reads live incoming foreign keys and compares them with the
source-model dependency maps frozen into the migration. `RESTRICT`, `NO ACTION`,
`CASCADE`, and `SET NULL` are all treated as potentially observable dependent
effects unless every affected model-managed dependent row was removed by an
earlier accepted operation. InnoDB rejects `SET DEFAULT`, so SafeMigrations does
not invent that action for MySQL/MariaDB. A concurrent source change, dependent
insert, or trigger-created target drift fails the DML or its postcondition; it
never becomes a successful overwrite or cascade.

The values appear in migration source and generated scripts because that is
EF's model-managed-data contract. SafeMigrations excludes them from reports,
telemetry, stable reason codes, and exception messages. See
[migration authoring](migration-authoring.md#model-managed-data-from-hasdata)
for the consumer boundary.

Catalog parameters and inline guard SQL share one mapping resolver. A non-null
model-managed value is resolved by its original CLR type and captured store
type, then converted by the provider mapping. This keeps `char(1)`, enum,
Char36 Guid, Binary16 Guid, date/time, binary, JSON, and nullable values
consistent between analysis and guarded execution. Unsupported CLR/store-type
pairs fail before target DML.

## Database-default projection

Doka 10.3.x renders `AlterDatabaseOperation` only as an optional
`ALTER DATABASE CHARACTER SET` default. That operation does not rewrite an
existing table, column, index, constraint, or row, so the MySQL/MariaDB analyzer
retains table-scoped preflight facts across it. It remains visible as
`provider_owned_not_analyzed` and therefore retains the
`ReadyWithProviderOperations` review boundary. The exemption belongs to the
versioned Doka adapter rather than provider-neutral Core; another provider's
operation with the same EF type remains opaque unless that provider proves its
own effect.

Multi-command provider baselines retain their command order, but their
boundaries are combined under one guarded decision and final postcondition.
If any command also carries provider cleanup fragments, SafeMigrations rejects
the whole baseline before guarded execution: per-command and failure cleanup
cannot both be run exactly once with the current command contract. A single
scoped command retains the provider's cleanup behavior.

## Automatic legacy column repair

A generated legacy-convergence migration retains `ThrowIfDifferent` unless its
source explicitly selects `RepairIfSafe`. That policy can repair nullability,
default, and comment drift on an ordinary existing column. It can also accept a
provider-proven ordinary `VARCHAR` widening, a live-data-verified `VARCHAR`
narrowing, a declared-domain-safe transition into the `TEXT` family, a
text-family widening, a live-data-verified text-to-`VARCHAR(n)` transition, or
the exact compatible Boolean transition from `BIT(1)` to `TINYINT(1)`.

Explicit `AlterColumnIfDifferent` and generated
`AlterColumnIfDifferentFromModel` operations share these transition checks when
their policy is `RepairIfSafe`. They additionally require a complete, matching
old definition and never create a missing source column. The reviewed provider
alter operation remains the DDL baseline, including an explicitly permitted
non-null default backfill. An exact target match short-circuits repair probes.
Analysis keeps operation-specific source eligibility separate from shared
physical row probes: two operations targeting the same length cannot borrow
each other's old-definition approval.

Accepted foreign-key and PRIMARY removals and ordinary nonunique BTREE creation
do not by themselves change intrinsic column facets or stored values. Ordered
preflight can therefore retain a supported matching column
proof, or an exact source-proven value-preserving nullability alteration with
unchanged layout, without first converging the complete table.
Required-to-nullable relaxation and nullable-to-required tightening with no NULL
rows can retain that certificate, including several such alterations on
different columns of the same owner. Only nullability and validated boolean
backfill review metadata may differ;
the provider's live source and row certificate remain required. Authored
`oldDefinition` metadata alone is not a live-source proof. Intervening data or
opaque operations, changed physical source facets, uncertified key changes, and
unresolved or newly accepted FK dependencies cannot reuse that certificate.
A default backfill UPDATE invalidates row proofs even when other column facets
remain unchanged.
Independent `Unsupported` and `DataBlocked` results remain blocking. Relaxing
nullability still requires the explicit repair policy and cannot make a retained
primary-key column nullable. Removing a composite PRIMARY first discharges its
membership requirement, not the original source, row or remaining-FK checks.
Preflight captures intrinsic eligibility separately from current PRIMARY
membership; runtime rechecks both against the live schema before DDL. Recreating
the PRIMARY, changing its owner's lifetime or injecting source drift cannot
reuse the removal proof. An `EnsureColumn` without authored old metadata must
have native matching evidence or complete, independently safe nullability-only
facet evidence; no widening or default change inherits this bounded certificate.

The distinction follows the native separate column, index and constraint
operations documented in [MariaDB ALTER TABLE](https://mariadb.com/docs/server/reference/sql-statements/data-definition/alter/alter-table)
and the required PRIMARY nullability contract in
[MySQL CREATE TABLE](https://dev.mysql.com/doc/refman/8.4/en/create-table.html).

### Lossless signed-integer alterations

Explicit `AlterColumnIfDifferent` and generated model-bound alterations with
`RepairIfSafe` can widen the supported signed built-in integer domain. The
source/target CLR types must agree with their declared native storage family;
nullability, collation, comment, generation and other non-type facets remain
unchanged. Only an absent default or the same supported signed literal default
is admitted. This does not authorize unsigned/zerofill conversion, arbitrary SQL
defaults, numeric narrowing, identity changes or a source-free Ensure repair.

The exact native old definition, InnoDB/strict-session prerequisites, row and
inline-record limits, composite-index capacity and FK dependencies, including
incoming FKs owned by another database, are checked independently. Three
individually fitting alterations cannot borrow three copies
of the same remaining row budget: accepted sibling targets participate in the
cumulative physical proof. Runtime repeats the native guards before DDL.
These checks retain the existing [single-DDL-writer contract](#concurrency);
they are not a lock against out-of-band schema changes.
Native column-name aliases use the same case-insensitive identity for target
overlays and key membership; table/schema identity retains its verified native
profile and is not folded by this column rule.

A captured ordinary index removal may preserve intrinsic source/value evidence,
not the complete index/key namespace. Unsupported index roles receive no such
certificate. Earlier opaque or unqualified mutations, replaced owners and stale
row/source proofs cannot acquire one from a later removal. An interrupted prefix
with already absent indexes can start a new sibling lifetime only from an
independently certified non-data alteration on the unchanged owner.

Widening proves range inclusion, not online or metadata-only execution; review
the maintenance window for native table/index rebuilds. The signed storage
domains are documented by [MySQL](https://dev.mysql.com/doc/refman/8.4/en/integer-types.html)
and [MariaDB](https://mariadb.com/docs/server/reference/data-types/numeric-data-types/numeric-data-type-overview).

Type-changing explicit alterations additionally require a conservative InnoDB
in-page row bound for the captured row format and page size, as well as the
declared row and dependent-key limits. Ordered projection checks the cumulative
accepted column shape and retained keys, not each alteration in isolation.
An unsupported or unknown storage shape cannot establish repair permission.

For a type-changing nullable-to-required alteration that uses a default
backfill, the replacement must be a supported literal fitting both the old and
target domains, and conversion must be strict. A merely non-null SQL expression
does not prove this additional requirement. Accepted backfills invalidate
subsequent preflight row proofs because their `UPDATE` can fire triggers.
Where a live or stream-declared unique key or check constraint can restrict the
replacement, the type-changing path additionally requires proof that no row
needs backfilling. Analysis and generated runtime commands use the same
normalized transition contract, including constraints not yet in the catalog.
Supplementary Unicode characters in source or target default literals are not
admitted by this type-changing path: the qualified catalog can expose a lossy
default representation, which cannot prove exact identity or the postcondition.
This restriction does not exclude supplementary characters in stored row data.
On MySQL, this type-changing path also rejects target expression defaults with
a quote in their literal payload while `NO_BACKSLASH_ESCAPES` is enabled, before
any backfill or DDL. MariaDB and plain `VARCHAR` literal defaults do not have
this quote-only restriction.

MariaDB `TEXT` defaults containing backslashes or control characters remain
unsupported by this type-changing path because expression serialization can
change the subsequent `INSERT DEFAULT` value. This restriction does not apply
to `VARCHAR` defaults or existing row data.

Known structured string-default leaves and literal `TEXT` defaults are rendered
as UTF-8 hex conversion expressions before delegation to Doka. Unlike a string
literal that the engine can serialize with backslash escapes, this expression
retains its bytes after table reopening under a different SQL mode. Empty
strings keep their mode-independent quoted form. Create, add, explicit alter
and full-definition repair use the same default materialization; computed,
CHECK and index expressions retain their ordinary renderer. Opaque SQL and
GUID, numeric and temporal defaults are not rewritten. Exact catalog comparisons
distinguish actual control characters from literal backslash sequences. This encoding
does not by itself expand the type-changing repair permissions above. See
[MySQL expression-default rules](https://dev.mysql.com/doc/refman/8.4/en/data-type-defaults.html)
for persisted expressions and SQL-mode dependence.

An existing MySQL expression literal containing a backslash, quote, NUL,
newline, carriage return or Ctrl-Z cannot prove a mode-independent default.
Strict policy reports it as different; an otherwise eligible explicit
`EnsureColumn` with `RepairIfSafe` can replace the default without rewriting
existing rows. Ordinary non-generated `CHAR`/`VARCHAR` literal defaults keep
their exact value comparison and are not rejected merely for containing these
characters.

MySQL's INFORMATION_SCHEMA can truncate literal binary-default text at NUL.
Neither a matching prefix nor a successful CREATE proves the complete default.
For these defaults, SafeMigrations qualifies the current base table, column
and literal default kind before preparing an exact native `DEFAULT(column)`
comparison. The comparison uses an impossible table join, not a row scan,
and accounts for fixed `BINARY(n)` zero padding. Analysis batches these
metadata-selected proofs; runtime evidence is operation-local, refreshed
after DDL and cleared after success, rejection, failure or cancellation.
Operations without binary-default proofs do not acquire this extra work.

Native literal defaults remain literals. Explicit structured binary-expression
defaults retain their separate complete SQL identity and are not decoded as
text. MySQL itself permits `DEFAULT(column)` only for literal defaults, so
expression-default columns are excluded before native statement preparation,
not hidden inside an unexecuted SQL CASE branch. See the
[MySQL DEFAULT function contract](https://dev.mysql.com/doc/refman/8.4/en/miscellaneous-functions.html#function_default).

MariaDB literal and untyped structured binary defaults use binary hex
conversion without changing CLR/store mappings or converting bytes to text.
This preserves exact metadata on MariaDB 10.11, whose catalog can otherwise
serialize binary defaults as escaped strings and replace invalid UTF-8 with
question marks. Ambiguous legacy quoted metadata cannot certify question-mark
or non-ASCII byte identities; exact hex/expression identity or a reviewed
default-only repair is required. MariaDB `DEFAULT(column)` retains its native
binary value. Neither provider requires a SHOW CREATE query or table-row scan
for this validation.

A `VARCHAR` length repair requires the same nonbinary character family,
character set, effective collation, generated/identity/row-version state,
provider metadata, and compatible dependent indexes. A column on either side
of a foreign key remains blocked because MySQL/MariaDB require a coupled type
transition and a single-column repair cannot own both sides. A text target must
contain the complete declared source byte domain. An ordinary dependent index
must already use an explicit valid prefix; SafeMigrations never invents one.
`FULLTEXT` retains its provider-supported full-column shape. Widening needs no
row-value query. Narrowing groups and deduplicates candidates for the same table
into one bounded character-length scan. It uses `CHAR_LENGTH`, not byte
`LENGTH`, returns only whether a violating row exists, and never returns a value
or key. The successful proof can require a complete table scan. A normal B-tree
index does not make that predicate a seek automatically.

For an `EnsureColumn` nullable-to-required repair, runtime evaluates the NULL
proof once after database, table, column and physical repair qualification.
Classification and repair eligibility consume that same operation-local result
and the same materialized physical repair invariant.
A physically `NOT NULL` column, a missing column or an ineligible physical
shape needs no NULL scan. The result is initialized for every operation and
discarded by independent cleanup, including rejection, provider failure,
timeout and cancellation. It is never reused after another operation or across
analysis and runtime. Live analysis uses an inline conditional with the same
physical-nullability and repair-invariant eligibility, without trusting session
variables. Length and NULL blockers remain independent.
The cache is not a schema lock; the deployment must still exclude out-of-band
DDL while migration operations execute.

A no-NULL result can still require a full scan of the eligible column. This
optimization removes duplicate proof work; it does not cap examined rows,
change durability settings or eliminate the storage engine's own DDL work.
Generated proof tokens are expanded only outside quoted names and literals.
Eligibility SQL retains shared source fragments until the final runtime
assignment, avoiding an unused combined copy in analysis captures.

The narrowing proof is repeated immediately before mutation. Strict conversion
behavior plus the complete target postcondition remains authoritative if a
concurrent writer reaches the table after preflight. SafeMigrations never emits
`IGNORE`; one overlength or concurrently inserted value fails without
truncation. Cancellation, timeout, an incomplete result, or changed catalog
shape is failure, not evidence that the data fits.

`BIT(1)` has only the non-null values zero and one, both exactly representable
by `TINYINT(1)`. The repair is available only for a CLR `bool`/nullable `bool`
target with compatible nullability, literal Boolean default, provider metadata,
and dependencies. It does not add a database check constraint. `BIT(2..64)`, a
non-Boolean target, and the reverse `TINYINT(1) -> BIT(1)` remain blocked.

SafeMigrations' Doka metadata projection must recognize every captured provider
annotation and prove that Guid storage and value-generation metadata are
consistent with the complete column shape. Existing `NULL` values block a repair to `NOT NULL`
unless a structurally proven non-null default permits Doka's guarded backfill
before target DDL. Other store-family, collation, generated-value, row-version,
and unsupported provider-metadata differences remain fail-closed.
Type-changing explicit alterations additionally require the literal
source-and-target representability and strict-mode conditions described above.

This proof accepts non-null literals and parsed, typed SQL expressions with a
guaranteed non-null shape, such as a current value or `COALESCE` with a proven
non-null argument. Raw SQL text, casts, binary arithmetic, and column
references do not qualify.

MySQL and MariaDB require `MODIFY COLUMN` to carry the complete target column
definition. SafeMigrations therefore asks Doka to render that complete
definition and separately proves that no unmodeled `EXTRA` modifier would be
erased. `ON UPDATE`, `INVISIBLE`, generated-column, auto-increment, and unknown
modifiers reject the repair. MySQL's catalog-only `DEFAULT_GENERATED` marker is
accepted because the modeled default still participates in the target
postcondition. The add and repair statements remain separate guarded branches,
and both must satisfy the same full catalog postcondition. A session-local
prepared classifier reads column data only after a catalog-only guard has
proved that the target exists. Missing safe additions remain `Missing`; an
unsafe missing `NOT NULL` addition to a populated table remains `DataBlocked`.

Every accepted string-family or Boolean repair reports
`SafeMigrationOperationalImpact.TableRewritePossible`. Lossless does not mean
instant or online: a `VARCHAR` widening can cross the one-byte/two-byte encoded
length boundary, and a string-family transition may copy or rebuild a table,
rebuild indexes, or wait for a metadata lock. Review table size, row format,
page size, key limits, server profile, and maintenance window independently.

## Explicit backfill metadata

The exact boolean `Doka:MySql:RequiresExplicitBackfill` annotation is recognized
as Doka's backfill safety marker, not an unsupported physical column facet.
Its value remains in the immutable definition, fingerprint and authored
operation. Unknown annotation names, duplicate markers and non-boolean marker
values remain unsupported. No dependency-version increase is required.

For a supported missing required column without an approved replacement value,
SafeMigrations renders a separate nullable add clone with the provider marker
intact and uses the atomic-add protocol below. The original contract is
unchanged. A populated table is `DataBlocked`; an already matching column
remains a no-op on retry. Client-side GUID generation does not prove a backfill
value for existing rows, and SafeMigrations does not manufacture one.

For a same-layout nullable-to-required alteration without an approved non-null
default, a fresh operation-local NULL probe must prove that no row needs a
replacement before the provider-rendered guarded alteration executes. Existing
NULL values remain blocking. A supported explicit non-null default retains the
independently approved provider backfill path and its row-proof invalidation;
the marker does not waive source, dependency, conversion or target-postcondition
checks.

Every actual nullable-to-required Alter needs `STRICT_TRANS_TABLES` or
`STRICT_ALL_TABLES`, including same-layout changes, widening/narrowing and an
approved explicit non-null default UPDATE. That UPDATE remains available under
its independent proof, but it cannot cover a NULL inserted afterward.
Permissive MODIFY could coerce that NULL to an implicit value different from
the approved replacement; strict conversion must reject it. A required-target
Ensure repair likewise needs strict mode while its live column is nullable.
Exact matching targets and already-required ordinary comment/default repairs
do not acquire this conversion requirement. SafeMigrations reads the session
mode without changing it; the check does not establish a transaction lock or
prevent a concurrent write.

A required TIMESTAMP Ensure or Alter repair additionally requires
`@@SESSION.explicit_defaults_for_timestamp = 1`, so native MODIFY cannot acquire
an implicit current-time default or replace NULL data through legacy temporal
semantics. This includes an approved fixed default: it does not prevent legacy
TIMESTAMP handling from replacing a later NULL with the current time.
Matching remains unaffected. SafeMigrations checks this setting without
changing it; missing no-default TIMESTAMP additions retain the separate
unsupported boundary described below.

A missing required target with an authored SQL default keeps the native
required ADD rather than the no-default two-stage CHECK protocol. That ADD
requires InnoDB and strict SQL mode; a required TIMESTAMP additionally needs
`@@SESSION.explicit_defaults_for_timestamp = 1`. The diagnostic for unavailable
missing-state qualification is `column_required_sql_add_unavailable`.
The native statement atomically validates its authored default against the
affected rows. It must reject a NULL or invalid conversion rather than coerce
an unapproved value; SafeMigrations does not replace the default or change the
session. Matching existing columns do not acquire these missing-only guards.

A structured SQL literal NULL is not a non-null replacement proof. Where the
engine can represent that required target, a populated owner is `DataBlocked`
before ADD. An empty owner still requires the strict InnoDB qualification;
an intervening row makes the native ADD fail atomically instead of creating a
column with an invented value. Engine representability remains independent:
MariaDB rejects required SQL NULL-default contracts rather than borrowing
MySQL's empty-table representation. Other supported parsed SQL defaults may
use native ADD validation, but their presence does not prove structural
non-nullability or authorize a backfill UPDATE. Opaque expressions remain
unsupported, and dependency/source guards are not waived.

An explicit nullable-to-required alteration and a required-target Ensure repair
also check active local `ON DELETE SET NULL` and `ON UPDATE SET NULL`
dependencies before any backfill UPDATE or column DDL.
The captured physical FK names let ordered preflight discharge an accepted exact
drop, not assume every FK using the column was removed. A new accepted SET NULL
FK restores the blocker, and runtime reads the current catalog independently.
The diagnostic is `column_set_null_dependency`; neither NULL-free rows nor an
explicit default makes the retained FK compatible with a required column.

Analysis captures column dependencies through tagged parameterized statements
with at most 32 distinct requests, eight statements per transport batch and the
existing payload bounds. Only verified current-database aliases, unchanged raw
table owners, native case-insensitive member identities and the same dependency
role can share a request. A required
target's SET NULL facts are not borrowed by a nullable target's PRIMARY role;
every consumer retains its independently qualified operation-reference lifetime.
There is no global table/schema case folding or cross-analysis cache.

Each request has an explicit absence sentinel. All physical DELETE/UPDATE SET
NULL names remain separate complete rows rather than a truncated concatenation
or first-match shortcut; PRIMARY membership is validated separately. Exact
five-field width, ordinal, role, strict boolean/integral-zero-or-one and completeness
checks precede publication. Completed FK sets are immutable across their
operation-reference consumers. Failed transport, cancellation or malformed rows
cannot publish a partially captured dependency certificate. Runtime still reads
the live catalog before its repair. Batching does not skip fresh facts, grant a
repair or establish a general timing gain.

Conversely, a later FK ensure requesting SET NULL cannot consume a required
projected local column, including a column made required by an earlier accepted
Ensure repair. A captured replacement reports
`projected_foreign_key_set_null_unrepresentable` instead of borrowing stale
nullable metadata or promoting a previous matching/missing result.

The Doka contract, established in 10.4.2 and retained by the locked 10.6.2,
removes synthetic defaults and marks additions
or nullable-to-required alterations requiring explicit backfill. Its provider
generator rejects required additions without an explicit default, computed
expression or server-generated value:

- [Doka 10.4.2 model differ](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/b2408bfbe29517d742734d06d848081cdb9e1de0/src/Doka.EntityFrameworkCore.MySql/Internal/Migrations/MySqlMigrationsModelDiffer.cs)
- [Doka 10.4.2 column SQL generator](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/b2408bfbe29517d742734d06d848081cdb9e1de0/src/Doka.EntityFrameworkCore.MySql/Internal/Migrations/MySqlMigrationsSqlGenerator.Columns.cs)

## Atomic required-column additions

The [decision record](decisions/D-015-owned-atomic-required-column-additions.md)
documents the proposed architectural rationale and its confirmation boundaries.

A supported ordinary required-column Ensure with no default uses
two InnoDB ALTER statements under one logical decision and final postcondition:

1. Add the complete provider-rendered column as nullable and add an enforced
   `column IS NOT NULL` CHECK in that same ALTER. Existing or raced rows would
   receive NULL and violate the CHECK, so that ALTER cannot publish a fabricated
   replacement value.
2. Modify the exact staged column to its complete required target and remove
   the owned CHECK in that same ALTER. Explicit values supplied by intervening
   writers are retained, not replaced.

Each transition is atomic, not the pair. DDL can commit between them. This is
neither a transaction spanning both ALTERs nor an application write lock.
SafeMigrations emits no `LOCK TABLES`, needs no additional lock privilege, and
adds no SQL-mode or CHECK-enforcement writes to this protocol. The original
native provider setup/cleanup remains within the shared resource scope.
It requires supported InnoDB, strict conversion and active CHECK enforcement
before staging or resume.
An unsupported engine, guard collision, unenforced CHECK or changed column
shape cannot acquire a fallback that invents row values.

The deterministic CHECK name is `doka_sm_add1_nn_` plus 48 hexadecimal digits,
fitting the 64-character identifier limit. Its identity is derived from the
authored owner, target and contract fingerprint. A matching prefix proves no
ownership and never causes a catalog object to be hidden or removed. Raw
qualified and omitted owner qualifiers have distinct recovery identities;
retry the same authored operation rather than rewriting its schema contract.
Recovery resolves the physical schema/table owner according to
`@@lower_case_table_names`: mode zero compares bytes exactly, while modes one
and two use server-side `LOWER` and compare the resulting bytes. Sparse inventory
recognition uses that same native owner resolution, not a CLR case-folding
substitute. The column, CHECK name/clause and raw recovery fingerprint remain
exact. This lookup neither broadens the explicit current-database qualifier
guard nor borrows ownership from an unrelated object.
Byte-exact catalog comparisons use length-free `CAST(expression AS BINARY)`
instead of the deprecated unary `BINARY` form. No cast length, padding or
truncation is introduced; Unicode bytes, trailing spaces and NULL retain their
existing comparison contract. See the
[MySQL cast contract](https://dev.mysql.com/doc/refman/8.4/en/cast-functions.html)
and [MariaDB cast contract](https://mariadb.com/docs/server/reference/sql-functions/string-functions/cast),
retrieved 2026-10-12.

Column comments are exact contract values, not case-insensitive identifiers.
Single-column matching, strict-table matching, nullable staging ownership and
`column_comment_digest` diagnostics share the same byte-exact predicate. Case,
Unicode bytes and trailing spaces remain significant; an omitted authored
comment and an empty native comment remain equivalent. A case-only mismatch
therefore cannot certify atomic-add recovery; ordinary comment repair still
requires its existing policy and eligibility.
The targeted native regressions reproduced false matching and false recovery
on MariaDB. Their MySQL counterpart already distinguished the same case-only
drift before the explicit cast; this is not evidence of a MySQL regression or
a universal native-catalog collation rule. MySQL's
[INFORMATION_SCHEMA collation guidance](https://dev.mysql.com/doc/refman/8.4/en/charset-collation-information-schema.html)
describes general comparison behavior and exceptions; the provider's explicit
predicate avoids inheriting either engine's native metadata equality.
The source was retrieved 2026-10-13.

| Durable state | Forward handling |
| --- | --- |
| Column and owned CHECK absent | Classify fresh prerequisites and stage the addition. |
| Exact nullable target and exact enforced owned CHECK | Requalify and resume the final ALTER for the same operation. |
| Exact required target and no owned CHECK | Completed matching no-op. |
| Required target with the staging CHECK still present | Existing conflict; no resume or automatic artifact removal. |
| Wrong owner/shape/expression, name collision or unenforced CHECK | No staging/resume certificate; preserve evidence and do not delete a conflicting object. |

Existing conflicting columns remain `Different`: `ThrowIfDifferent` and
`RepairIfSafe` reject, while `ExistenceOnly` retains its explicit no-op contract
without resuming or removing the CHECK. A reserved guard-name collision while
the target column is missing is `Unsupported`, so no policy can create a
column using someone else's object as its barrier.

Failure or cancellation does not prove which ALTER committed. Re-read the
durable catalog and retry the same forward operation; cleanup deliberately
retains the intermediate CHECK. Unexpected-object analysis recognizes only an
exact staging object bound to one matching pending Ensure and a fresh resume
predicate, never every object sharing its prefix. A resumed addition can contain
explicit writer rows. Its `Missing`/`Apply` result therefore does not certify an
empty table or automatically authorize a subsequent primary or foreign key.
An accepted column tombstone proves absence, not empty rows. A following
required ADD still needs its operation-local row and engine/session eligibility;
it cannot borrow a fresh-table certificate from the dropped column.
If the ordered required ADD has no current row certificate,
`projected_required_add_rows_unproven` is `PrerequisiteMissing` and the blocked
decision is `prerequisite_missing`. Obtain a fresh operation-bound proof or
author an explicit reviewed backfill; resume recognition and stale table
lifetimes do not justify treating rows as absent.
This catalog recognition proves the equivalent staging invariant, not who
historically created it. A retained local DELETE/UPDATE SET NULL FK is refused
conservatively as `Unsupported` with `column_set_null_dependency`. Qualified
engines reject creating that action on the owned CHECK column, so it is not an
ordinary reachable staging state. The predicate does not authorize removal of
an anomalous dependency; compatible CASCADE dependencies are not removed.

Between the ALTERs, inserts omitting the new value can fail; after interruption
that restriction can last until recovery. MariaDB writers can independently
disable CHECK enforcement in their own sessions, so the barrier is not a
promise that every writer is blocked. The final strict NOT NULL conversion
still rejects NULL instead of coercing it, without removing the barrier on
failure. The additional ALTER and engine-managed locks can add DDL work; review
the maintenance window rather than treating this as instant or online DDL.
Migration source, immutable metadata and EF history semantics are unchanged.

The protocol's catalog-only status is materialized once per operation and used
by classification, repair qualification, NULL-probe eligibility and diagnostics.
Final postcondition evaluation reads the live catalog independently after DDL;
the status is not a cross-operation cache. This factoring does not make the
additional DDL or catalog work free, and no timing improvement is implied.

The no-default protocol requires a supported ordinary intrinsic column shape
and validated provider metadata; server-generated identity additions keep
their separate provider contract. ENUM/SET matching canonicalizes keyword and
native literal display, but preserves decoded member case and order. Backslash
interpretation follows the active session mode; double-quoted members are not
accepted as string literals under ANSI_QUOTES, independently of whether the
member domain needs backslash decoding. An unrecognized or malformed
domain cannot acquire matching or recovery by folding its literal contents.

If a column comment contains a backslash, Doka's existing native comment scope
can temporarily enable NO_BACKSLASH_ESCAPES. For table creation, a table or any
column comment can affect every ENUM/SET column in that statement. When the
member domain depends on backslash interpretation, non-matching creation or
repair therefore requires that mode already enabled. Otherwise the diagnostic
is `column_native_enum_mode_conflict`: the in-scope DDL/postcondition could
describe another domain than the original session sees after cleanup.
These eligibility guards neither change the original mode nor rewrite members.
Exact matching states remain no-ops and must still verify after native cleanup.

The distinct standard-only escaped-quote staging form requires
NO_BACKSLASH_ESCAPES disabled and no opaque native setup scope. A known Doka
backslash-comment scope disables that staging protocol and refuses missing
conversion, while an existing exact target can retain its ordinary no-op.
An unknown opaque setup paired with a protocol certificate is rejected during
generation. The original-mode certificate cannot survive an unproved setup's
reinterpretation; no authored domain is rewritten to force compatibility.

TIMESTAMP additions are excluded because session-dependent implicit defaults
need a separate full-transition certificate.
Unsupported missing shapes use `column_atomic_add_unavailable`,
not an unguarded native ADD. Completed matching states are still comparable.
See [MariaDB TIMESTAMP automatic values](https://mariadb.com/docs/server/reference/data-types/date-and-time-data-types/timestamp#automatic-values).

MySQL documents enforced-CHECK validation within ALTER and atomic InnoDB DDL;
MariaDB documents atomic ALTER and independently mutable CHECK enforcement:

- [MySQL 8.4 ALTER TABLE](https://dev.mysql.com/doc/refman/8.4/en/alter-table.html)
- [MySQL 8.4 atomic DDL](https://dev.mysql.com/doc/refman/8.4/en/atomic-ddl.html)
- [MariaDB atomic DDL](https://mariadb.com/docs/server/reference/sql-statements/data-definition/atomic-ddl)
- [MariaDB CHECK-enforcement variable](https://mariadb.com/docs/server/server-management/variables-and-modes/server-system-variables#check_constraint_checks)

## Index prefixes and physical key limits

Doka 10.3.0 exposes ordered index-prefix metadata through its typed public
migration-operation contract. At design time, SafeMigrations consumes that
metadata, removes the provider annotation from the operation EF renders, and
writes the values into the generated SafeMigrations call. Zero represents the
complete key. A positive prefix uses character-count semantics for character
columns and byte-count semantics for binary columns.

At runtime, a missing BTREE index is eligible for DDL only after catalog
analysis proves its complete maximum key width. The proof reads the live
InnoDB engine, row format, `@@innodb_page_size`, column store families,
character widths, numeric precision/scale, temporal precision, and explicit
prefixes. `COMPACT` and `REDUNDANT` use the 767-byte limit. A 4 KiB or 8 KiB
page uses 768 or 1536 bytes respectively; supported page profiles of 16 KiB or
larger use 3072 bytes.

An overlong ordinary key rejects as
`index_prefix_required_for_key_limit`. An unknown-width, expression,
unsupported access-method, or full `TEXT`/`BLOB` key rejects as
`index_key_length_unverifiable`. A prefix longer than its column or a prefix on
a scalar key also rejects. Existing indexes remain catalog-comparable, so an
already materialized provider-supported index is not rejected merely because
SafeMigrations does not own a creation proof for its access method. No path
silently truncates or invents a prefix.

If an earlier accepted column operation changes an indexed definition, the
ordered preflight discards the live width conclusion. It evaluates projected
key parts from their complete target store definitions and combines them with
bounded catalog shapes for unchanged composite parts. The proof retains the
actual table engine and row format, or the database defaults for a table being
created, plus `@@innodb_page_size`. Unique-index duplicate evidence is evaluated
before a stale physical-width rejection can be replaced with projected
`Missing`. A drop or column invalidation also removes any earlier accepted
semantic index alias, so historical evidence cannot produce a later `NoOp`.

MySQL and MariaDB implement an ordinary unique constraint as a physical unique
index. Ordered projection therefore mirrors compatible BTREE definitions across
both operation families, and a drop through either family invalidates the same
physical identity and all of its aliases. Prefix, expression, provider-option,
and `PRIMARY` definitions are not cross-kind aliases; `PRIMARY` is reserved for
the primary-key contract. Exact-name collisions are resolved before differently
named semantic matches.
Case-insensitive index-member names and typed column/key-member names follow
their separate provider comparers in ordered projection and postflight,
including shared UNIQUE/index aliases. Table and database identities retain
their native owner contract; default/domain literals are not case-folded as
member names. An ordinary index target named PRIMARY retains its immutable
unsupported result even after accepted earlier operations.
An index rename with an existing source rejects either endpoint named `PRIMARY`
as `Different`, before native DDL, and runtime retains `doka_sm_different`.
A missing source remains an idempotent no-op; reserved-name refusal does not
invent source presence. Use the separate primary-key contract for a reviewed
key replacement, not an ordinary rename. See the
[MySQL index-rename rules](https://dev.mysql.com/doc/refman/8.4/en/alter-table.html)
(primary source; retrieved 2026-10-12).

The same physical feasibility gate applies to missing and replacement ordinary
indexes, primary keys, and unique constraints. It checks the complete key width,
the InnoDB row format and page size, the 16-part limit, and bounded knowledge of
every key column before any earlier drop can execute. Constraint column order is
matched by ordinal catalog rows, not `GROUP_CONCAT`, whose default session limit
can truncate 16 maximum-length identifiers.

Candidate-index matching groups `INFORMATION_SCHEMA.STATISTICS` rows by index
name and checks the complete key contract with ordinal aggregates. This avoids
correlated catalog queries for each key part while preserving uniqueness,
visibility, method, sort, and prefix checks. The number of catalog references
in generated guard SQL stays constant as key width grows; the key predicates
still grow with the definition. This is not an absolute command-time guarantee.

## Ordered index replacement

Automatic scaffolding emits `DropIndexIfExists` for an EF `DropIndex`
operation. Preflight also recognizes a manually retained ordinary provider
`DropIndexOperation`. Once the exact table, schema, and index name has an
accepted drop, a following ordinary column BTREE ensure is projected
as missing. The live analyzer still proves referenced columns, physical key
width, and unique-data safety before that projection. Unsupported,
data-blocked, and prerequisite-missing results are never converted to
readiness. A differently named semantic index conflict is also retained because
dropping the expected name cannot remove that object.

EF represents a drop and a create as ordered migration operations; the
SafeMigrations projection follows that order without mutating the database
during preflight. See the EF Core 10
[`MigrationBuilder.DropIndex` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.migrationbuilder.dropindex?view=efcore-10.0)
(retrieved 2026-09-01).

## Generated columns

EF-captured computed SQL and generated CHECKs use the bounded structured
expression path described in
[migration authoring](migration-authoring.md#generated-columns). Searched CASE
keeps its branch order and optional ELSE, including conditional overflow
guards. Catalog comparison accepts only the provider's finite native display
forms for that exact tree. Identifier punctuation, operator/literal changes,
source binding and virtual/stored identity remain significant; arbitrary SQL
text is not normalized into equivalence. Comparison preserves byte-exact
quoted literal ranges while handling supported native identifier casing and
display forms; literal-case drift is not identifier equivalence. Typed string
leaves needing stable escape or Unicode serialization use the bounded hex
conversion renderer consistently in DDL, catalog candidates and CHECK probes.
Validated captured native SQL is preserved rather than rewritten through that
typed-literal path.

Captured bracket or double-quoted identifiers that lack the MySQL/MariaDB
native quoting contract reject with `captured_expression_dialect_unverified`.
Captured supplementary Unicode literal text that MariaDB's native catalog
cannot retain exactly rejects with
`captured_expression_literal_catalog_unverifiable` before target DDL; lossy
question-mark catalog text is not accepted as identity. A caller-authored
typed expression can use stable serialization instead. SQL Server's captured
type-first CONVERT syntax remains `structured_convert_dialect` here.

Captured CAST text must itself use the native CAST grammar. Column-type aliases
that the public typed API can translate, such as `BIGINT` or `VARCHAR`, do not
therefore become valid native captured CAST targets; unproved targets reject
with `captured_expression_cast_target_unverified`. Unquoted identifier roots
also need native keyword qualification before source-bearing SQL is submitted.
MySQL uses its catalog's reserved-keyword bit. MariaDB has no equivalent bit,
so suspect roots require an owned operation/session-local native parse-only
certificate rather than a blanket ban on legal names such as `id` or `value`.
Unproved roots remain `captured_expression_identifier_keyword_unverified`.
The certificate does not execute the authored application expression or scan
application rows; failure and cancellation cannot transfer proof ownership to
another operation or session.

Generated expressions have a deterministic product capability boundary,
separate from generic SQL rendering. CURRENT date/time values and clock calls
such as NOW reject with `generated_column_nondeterministic_expression`.
MySQL and MariaDB stored-generation rules prohibit the relevant clock forms;
MariaDB can accept some virtual forms natively, but those do not establish this
adapter's deterministic/indexed contract. This is not a claim that every
rejected MariaDB virtual expression is invalid SQL. Ordinary clock defaults
are unaffected. The finite generated-function contract admits supported
`ABS`/`LOWER` arity-one calls and COALESCE forms; an unknown or unproved call
remains `generated_column_function_unverified` rather than invoking a UDF to
discover its behavior. Passing this capability check does not supply the
separate non-null proof below.

MySQL supports a physical `NOT NULL` generated-column facet and compares it
normally. MariaDB's generated-column grammar does not expose that facet and
its native catalog reports generated columns as nullable. SafeMigrations keeps
that physical fact instead of ignoring `IS_NULLABLE` globally. A logically
required MariaDB generated result is accepted only when a separate bounded
semantic proof shows that every successful evaluation produces a non-NULL
result using currently verified ordinary source columns.

This proof is deliberately narrower than the expression renderer. The target
must itself have a supported signed-integral storage type, including the
Boolean alias; an integral result does not prove a safe arbitrary target
conversion. It admits supported signed-integer operands, non-NULL integral
literals and eligible arithmetic,
comparisons and searched CASE branches. Every possible result branch needs the
proof, including an explicit non-NULL ELSE. Nullable, unsigned or generated
value-producing source dependencies, unknown functions/casts, division/modulo,
NULL result branches and an omitted ELSE cannot establish it. A CASE condition
need not itself be non-NULL: UNKNOWN can select ELSE, whose result is proved
independently. An unproved required result remains
`generated_column_nullability`. The proof reads structural source metadata,
not application rows. A changed source type/nullability or an unproved ordered
source replacement invalidates that certificate and requires requalification.

Every referenced column in conditions, results and ELSE must separately bind
to the current local owner. A missing dependency rejects with
`generated_column_dependency_missing`; an unsupported cross-owner qualifier
rejects with `generated_column_dependency_qualification`. A CASE result needing
no value-producing source does not waive its condition's binding proof.

MySQL additionally forbids generated references to an `AUTO_INCREMENT` source;
`generated_column_identity_dependency` retains that restriction for both live
and accepted projected operands. Nullable generated results do not waive it.

A single MySQL source-column rename cannot redefine generated expressions
that depend on that source. `generated_column_rename_dependency` rejects that
case; `generated_column_rename_unproven` retains an unresolved complete
dependency set. An unrelated rename needs either native absence of generated
members or an exact current complete-set certificate for parsed definitions.
`PREPARE` success alone does not certify ALTER dependencies. MariaDB's separate
native rename behavior is not restricted by this MySQL-only gate. See
[MySQL ALTER generated columns](https://dev.mysql.com/doc/refman/8.4/en/alter-table-generated-columns.html).

Source-column DROP also checks surviving generated dependencies before DDL on
both engines. A conditional authored prefix or model candidate must match the
complete native surviving generated set, including expression and storage;
the removed member is excluded. An empty native set needs no candidate.
Incomplete or opaque evidence is `generated_column_drop_unproven`; a proven
surviving reference is `generated_column_drop_dependency`. Explicitly dropping
the generated member first remains supported. No generated column is removed
automatically, and application rows are not scanned to prove nondependency.
The guard uses the existing column catalog, not the virtual-only administrative
dependency tables requiring `PROCESS`. It does not add temporary-clone rights
or treat successful `PREPARE` as proof that an ALTER can execute.

MariaDB additionally accepts an independent DROP when none of its surviving
native generated expressions contains the actual physical source name in any
native identifier encoding (plain, doubled backtick or doubled double quote).
This metadata-only sufficient proof does not parse or execute the expressions,
require their unrelated facets to match a model, add a database roundtrip, or
certify the absence of generated columns. A possible occurrence inside a string,
function or longer identifier remains unproven without a complete certificate.
Preflight, runtime and scripts use the same fresh catalog test. A concurrent
dependent introduced after that observation still meets native ALTER's atomic
dependency refusal; the observation is not an independent schema lock.
MariaDB's bound expression printer uses the physical source identity; MySQL's
persisted expression spelling is a different contract and does not use this
fallback. See the native [identifier printer and catalog rendering](https://github.com/MariaDB/server/blob/mariadb-11.8.3/sql/sql_show.cc)
and [bound field identity](https://github.com/MariaDB/server/blob/mariadb-11.8.3/sql/item.cc).

MariaDB DROP source lookup folds case without merging accent-distinct names.
A missing target therefore remains a no-op even when the catalog's collation
equates it with another physical column. The same predicate qualifies runtime
dependency checks and replay postconditions; it does not rename or remove that
other column.

The remaining certificate requirement can reject a previously attempted DROP
on an unmapped owner with surviving
generated members. An operation-bound model candidate or earlier explicit
generated-member contract can supply a candidate, but native completeness and
equality are still mandatory. Pending analysis uses each migration's historical
target; manual SQL generation uses only its explicitly supplied model. A richer
current design-time model is not silently substituted. EF runtime metadata that
omits comment/collation cannot supply known-null facets. Authored-prefix and
model candidates remain separate whole sets; partial candidates are not merged.
A later declaration is not prior evidence. Keep
published migration source and history unchanged; review the actual model and
ordered contract instead of weakening the guard to make an unknown set pass.

No helper CHECK, forced stored generation, default injection or application
backfill substitutes for the proof. Other generated-column, index and FK
capability/dependency gates remain independent. Native type, expression and
storage drift still reject even when a result is provably non-NULL. See
[MySQL generated columns](https://dev.mysql.com/doc/refman/8.4/en/create-table-generated-columns.html)
and [MariaDB](https://mariadb.com/docs/server/reference/sql-statements/data-definition/create/generated-columns)
(primary sources; retrieved 2026-10-20).

## JSON aliases and SQL defaults

MySQL stores `json` natively. Doka emits the same model store type on MariaDB
as `longtext COLLATE utf8mb4_bin` with an inline `JSON_VALID` check.
SafeMigrations compares those physical facets as one provider-owned JSON
contract. Strict table counts and unexpected-object inventory exclude only the
check whose column name and canonical `JSON_VALID(column)` expression match an
expected `json` column. An independent check remains owned drift and blocks a
strict table operation.

EF column operations expose SQL defaults through `DefaultValueSql`.
SafeMigrations parses that value with its bounded typed grammar when capturing
an EF-generated definition. `CURRENT_TIMESTAMP(6)` is therefore structurally
rendered and compared instead of being rejected as opaque. Expressions outside
the grammar remain `opaque_sql_expression`; they are not executed or accepted
heuristically. Parsing alone is not a non-null backfill proof. Required
SQL-default ADD eligibility, atomic native validation and matching exemptions
follow [the column contract](#explicit-backfill-metadata).

For a supported uncast structured ISO date-time string default on TIMESTAMP or
DATETIME, MySQL compares two independent catalog paths. With DEFAULT_GENERATED,
the expected `_utf8mb4` serialized expression display is matched byte-exactly,
without padding the expression's literal payload. Without that marker, only
exact original/plain temporal candidates and target-precision zero-padding
are eligible. MariaDB keeps its quoted native temporal display path. For
example, `2001-01-02 03:04:05` can match the plain `timestamp(6)` value
`2001-01-02 03:04:05.000000`, not a generated-expression default with an
independently padded or different charset-prefixed payload.

Comparison does not union arbitrary charset introducers, round or truncate
extra fractional digits, reinterpret a timezone or time-only value, or correct
an invalid calendar date. The authored expression, default kind and fingerprint
remain unchanged; display matching is not a new value-conversion or backfill
permission. Primary contracts:

- [MariaDB JSON data type](https://mariadb.com/docs/server/reference/data-types/string-data-types/json)
  (retrieved 2026-09-01);
- [EF Core 10 `ColumnOperation.DefaultValueSql`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.operations.columnoperation.defaultvaluesql?view=efcore-10.0)
  (retrieved 2026-09-01).

## Constraint and index semantic identity

Ensure operations compare semantic identity with exact-name precedence. When
the expected name exists, its complete modeled shape must match; an equivalent
alias cannot conceal drift under that name. When the expected name is absent,
any differently named active object with the same complete shape satisfies the
ensure as `Matching` and no DDL is emitted. Different columns, expressions,
actions, ordering, prefixes, methods, uniqueness, visibility, or other modeled
facets remain independent and do not suppress safe creation. Multiple semantic
aliases remain a deterministic no-op and are reconciled from unexpected-object
inventory through the same provider comparator.

Ordered preflight resolves a semantic no-op to one physical catalog name before
retaining it as prerequisite evidence. A later drop invalidates every semantic
alias bound to that physical object. If multiple physical objects match without
a resolved binding, the current ensure remains a deterministic no-op, but its
ambiguous identity is not used to authorize a later operation.

An accepted drop creates an ordered tombstone for the physical object. A later
ensure for the same definition, including a uniquely bound semantic alias,
therefore reports `Missing` and `Apply` when no matching physical object
survives, even though the immutable pre-batch catalog result was `Matching`.
A captured surviving matching identity instead retains `Matching` and `NoOp`.
If data or a required key/column changes between those operations, preflight
blocks before execution. This matters on both engines because their DDL can
commit independently of surrounding application transactions.

The same ordered existence facts apply when Core has only accepted column and
constraint prerequisites rather than a complete table image. Accepted standalone
FK, unique and check creations can be dropped later in the stream; accepted
removals remain tombstones for repeated drops and later ensures. A repeated
primary-key drop also retains the singleton's known absence instead of reusing
its immutable live match. Physical names and bound aliases remain distinct:
removing one matching object does not remove another surviving physical object.
Unknown owners, unbound identities and independent source or data blockers are
not promoted merely because a prior constraint operation was accepted.

MySQL/MariaDB column and FK member inventories compare those member names
case-insensitively. A case-only spelling change does not lose an accepted
column alteration or physical FK removal. This is deliberately narrower than
table/database identity: those owners retain their existing exact-name and
verified current-database qualification rules, without global case folding.
The atomic-add recovery path separately uses the server-mode owner lookup
described above; it does not change these general projection identities.

Physical constraint namespaces are checked only after local exact and semantic
matching. MySQL CHECK and foreign-key symbols must be free across the current
schema. MariaDB foreign-key symbols must be free across the database before
12.1; MariaDB 12.1 and later permit the same symbol on another table. An
occupied required namespace is `Different` and rejects before DDL. A locally
equivalent differently named object still wins first as `Matching`, so the
namespace guard never invites duplicate or destructive cleanup.

InnoDB may create an index automatically for a foreign key and may later drop
that index silently when another suitable index becomes available. An ensure
therefore compares its complete physical index definition without assigning
ownership from the index name or the foreign key that currently uses it. An
equivalent supporting index satisfies the ensure; a physical facet difference
remains independent. This follows the
[MySQL 8.4 foreign-key index contract](https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html)
(retrieved 2026-09-01).

MySQL checks match only when `TABLE_CONSTRAINTS.ENFORCED` is `YES`. MariaDB
check rows are joined by schema, table, and constraint name so table-scoped
names introduced in MariaDB 12.1 cannot cross-match another table. MySQL
invisible indexes and MariaDB ignored indexes do not satisfy a visible expected
index and do not block creation under a different name.

## Ordered constraint replacement

An exact accepted FK drop can precede a replacement with different referential
actions. For an existing populated table, preflight requires a separately
captured certificate for that exact replacement: compatible endpoint storage,
a complete unprefixed unique principal key, and an orphan-free row result.
The old FK's `Different` classification alone is not this certificate. Only
the accepted removal and value-preserving nullability transitions retain these
facts; row mutations or changes to their physical endpoints, columns or
principal candidate key invalidate them. Independent support and identity
conflicts remain blocking. If another matching physical FK survives the drop,
the ensure remains `Matching` with that actual alias identity rather than
claiming that a new FK must be created.

Runtime rechecks endpoint-table and column presence, orphan data, and every
affected local column's nullable state before an `ON DELETE SET NULL` or
`ON UPDATE SET NULL` FK can be created. The exact storage and unprefixed unique
candidate-key certificate above belongs to this bounded preflight replacement
path; it is not imposed globally on every runtime FK ensure. The engine still
enforces its physical FK formation rules, and this guard does not claim to
translate every physical type/key error into a SafeMigrations category. See the
[MySQL foreign-key contract](https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html)
and [MariaDB foreign-key contract](https://mariadb.com/docs/server/ha-and-performance/optimization-and-tuning/optimization-and-indexes/foreign-keys).

After an accepted primary-key drop, a current empty-table certificate and
accepted non-nullable target columns can prove a different replacement key's
row prerequisites. A successfully guarded required-column addition without a
default can establish that certificate on an empty table. Ordered writes or
unknown row state invalidate it, nullable key parts cannot use it, and the
provider still validates physical key feasibility.

An accepted independent sibling-column DROP can retain an unchanged ordinary
column's intrinsic source and NULL-free nullability-transition proof. This
certificate does not restore complete table, index or constraint knowledge.
Existing replacement-key members need their own current NULL and duplicate
checks: uniqueness of `(event_id, old_value)` does not establish uniqueness of
`(event_id, tag_id)`. The analyzer checks those replacement rows even while the
old PRIMARY still exists, so a known data conflict blocks before the destructive
prefix is applied. Removed members, changed value domains, unresolved native
dependencies, opaque operations and data mutations cannot borrow that evidence.

Adding a required column with an ordinary SQL default such as `UUID()` is a
separate native addition contract, not a structural proof of prospective key
uniqueness. The default's engine/session and target-column checks still apply.
When a replacement key depends on values not yet generated, preflight records
`RuntimeValidationRequired` and the actual prior accepted operation in
`DeferredOrigin`; it does not reject solely because those values do not exist
yet or claim that they have already been checked. Runtime and emitted scripts
must inspect the ordered live key, rows and dependencies before target DDL.
Existing duplicate IDs are not excused by a UUID default. Index identities
changed by DROP likewise require revalidation: removing one member can shrink
a composite index into a different semantic alias. No policy, row check or
physical-key limit is bypassed by deferral.

MariaDB documents [function defaults](https://mariadb.com/docs/server/server-usage/tables/create-table#default-column-option)
and the intended uniqueness and replication limits of
[`UUID()`](https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/miscellaneous-functions/uuid).
Those contracts are not an application data-repair policy or permission to
rewrite historical migrations.

Do not infer identical UUID-default DDL support on MySQL. With binary logging
and `ROW`/`MIXED` format, MySQL rejects an `ALTER TABLE` that evaluates a
non-deterministic expression default. The native engine can therefore refuse
the same addition independently of SafeMigrations projection. SafeMigrations
does not change replication settings to admit it. See MySQL's
[unsafe-statement contract](https://dev.mysql.com/doc/refman/8.4/en/replication-rbr-safe-unsafe.html).

## Scoped cleanup in Doka 10.4.0

`RenderStandardOperation` exposes provider-validated `Setup`, `Body`, and
`Cleanup` fragments. SafeMigrations embeds the exact body as UTF-8 hexadecimal
prepared SQL and carries provider setup and cleanup into its outer scope; it no
longer lexes or rewrites ordinary provider command bodies. The specialized
atomic-add path validates an exact single-column native ALTER before appending
its owned typed CHECK clause; it does not accept arbitrary caller SQL. The
scope uses one transaction-suppression value across the complete
provider-rendered command sequence and
rejects a sequence with mixed suppression requirements.

For column value generation, `AutoIncrement` is compared against `c.EXTRA`.
`None` and `ClientGuid` both require the absence of `auto_increment`; the
latter is a client-side EF value generator and is therefore not a database
column facet. SafeMigrations still retains the exact annotation in its
immutable definition, contract fingerprint, and provider operation replay.
Supported `Binary16`, `Char36` and MariaDB `Uuid` Guid metadata is accepted only
when it is consistent with a Guid column's complete store type. HiLo, malformed,
contradictory, spatial, invisible, and unknown column metadata remains
unsupported until its complete observable database contract is modeled.

Doka attempts cleanup after setup or body failure and after caller
cancellation. Asynchronous cleanup uses an independent cancellation token.
Stateful native cleanup requires the certified Doka SQL-mode shape with exactly
two setup fragments and one restore fragment. Unknown stateful cleanup is
refused instead of assuming idempotence; native commands without cleanup keep
their ordinary path.

The handler initializes operation-owned scope state before identity and
decision guards. After the decision assertion, immediately before native setup,
it captures the current mode and marks scope entry atomically. Cleanup restores
only an entered certified scope, then resets its owned bookkeeping. A rejection
before setup must not restore NULL or a stale saved mode from an earlier
operation: the conditional restore keeps the current mode when entry did not
occur. Failure or cancellation after entry retains the restore obligation;
an actual restore failure still follows Doka's unsafe-connection boundary.
These wrappers introduce no mode setting beyond the certified native provider
scope, and they do not move opaque setup ahead of identity or decision guards.
The certified snapshot and conditional restore are composed from shared native
SQL parts and bound to the complete expected statement before execution. A
changed provider statement cannot silently leave entry tracking unset through
a failed substring replacement; unsupported drift refuses generation. The
nullable staging target explicitly retains every immutable definition facet
and provider annotation rather than changing the authored contract.

SafeMigrations cleanup commands are idempotent: the prepared-statement cleanup
first replaces the named statement with an inert statement before deallocation,
certified provider cleanup conditionally restores entered scoped state, and
the final guard cleanup nulls session variables and drops the temporary table
with `IF EXISTS`.

If any cleanup command fails, Doka attempts the remaining cleanup commands,
closes the relational and physical connection, clears the matching
MySqlConnector pool generation, and reports the primary and cleanup failures.
The required engine matrix must verify failed-body cleanup, cancellation
cleanup, and physical-session replacement after an injected cleanup failure.

## Implicit commit and retry

Both engines can implicitly commit DDL. An application transaction cannot turn
a multi-DDL migration into an all-or-nothing unit. In particular:

- do not run migrations inside a business transaction that the caller expects
  to roll back;
- do not run `Down` as an incident-response shortcut;
- retain a tested backup or snapshot before the deployment;
- after failure, inspect the report and catalog, correct the classified cause,
  and rerun the same forward migration;
- EF writes the migration history row only after the migration completes, so a
  partial DDL state remains pending and is eligible for guarded retry.

Every successful generated path is tested for a second-run no-op. Integration
tests also prove recovery after connection disposal and after a standard EF
command precedes a failing safe command. The provider-executed cleanup guarantee
applies to runtime migration-command execution. A generated SQL script is plain
SQL; if an external script runner stops at the failing statement, operators must
discard that failed script connection before retry.

## Concurrency

Use exactly one active migrator per database. EF/Doka provider migration locks
serialize competing migrators against the same database, and the test suite
proves that separate databases can migrate in parallel. The operational rule is
still to designate one deployment writer and disable out-of-band DDL during the
window.

## Privileges

The migration principal needs:

- read access to the catalog views used by the provider;
- `SELECT` on the dependent and principal tables read by data preconditions;
- DDL privileges for the target objects in the migration;
- the required create/read/write privileges for the owned Core EF history
  table, plus separately reviewed privileges for ordinary data operations;
- the provider migration-lock capabilities;
- `CREATE TEMPORARY TABLES` where the guard plan requires temporary state.

It does not need:

- `CREATE ROUTINE` or `ALTER ROUTINE`;
- global administrative privileges;
- permanent helper schemas or tables;
- write access to unrelated legacy history tables.

The release matrix contains a least-privilege integration test. Operators must
derive the exact grant list from the operations in their own migration rather
than granting broad global roles.

## SQL modes and engine differences

The handler is tested with mode changes including `NO_BACKSLASH_ESCAPES` and
disabled MariaDB check enforcement. Catalog assertions do not depend on a
particular backslash interpretation, and temporarily disabled constraint
enforcement does not bypass SafeMigrations data preconditions.

SafeMigrations validates `AllowUserVariables=true` from the actual command
connection before its commands run, including caller-owned and already-open
connections. This runtime check remains defense in depth after Doka's options-
level ownership-aware validation. Validation errors never include connection
strings or credentials.

Doka requires `UseAffectedRows=false` independently of SafeMigrations because
EF update concurrency depends on matched-row semantics. Its low-level connector
transport is always `GuidFormat=Binary16`; Doka's model-level
`DefaultGuidFormat` and property configuration continue to control whether a
Guid property is stored as `binary(16)`, `char(36)` or MariaDB `uuid`. This
separation prevents connection ownership from changing the model's storage
contract.

### GUID storage and native UUID

The Doka dependency range is `[10.6.2,10.7.0)`. `Binary16` remains the default;
`Char36` is unchanged. Native `uuid` is an explicit Doka model choice through
`DefaultGuidFormat(MySqlGuidFormat.Uuid)` or a property's
`HasMySqlGuidFormat(MySqlGuidFormat.Uuid)`. It requires MariaDB 10.7 or later;
the SafeMigrations supported matrix remains narrower than this capability
threshold. Doka rejects UUID mapping on MySQL and older MariaDB profiles, even
with its unsupported-version override.

Hand-authored migration operations must specify `type: "uuid"` for native
storage. A `Doka:MySql:GuidFormat` annotation without a store type does not
replace Doka's ordinary context-default type inference. Model-based fluent
UUID configuration supplies the explicit native type during scaffolding.

Native UUID columns retain CLR `Guid`/`Guid?`, exact unsized `uuid` storage,
and no text collation, max-length or fixed-length facet. SafeMigrations uses
the public Doka mapping and typed metadata, not a duplicate converter or
internal capability enum. Doka sends canonical UUID text with `DbType.String`;
the connector's separate `GuidFormat=Binary16` invariant remains mandatory.
UUID contributes 16 bytes to row and index budgets and cannot use a key prefix.
Column, key, FK and model-managed-data checks keep the same ordered live/
projected evidence and runtime rechecks as the other supported scalar types.
Existing native-compatible UUID/Binary16 FK endpoints do not convert either
column's storage. Sparse mixed live/projected FK certificates still require
their existing exact-storage proof; native live compatibility alone does not
authorize a different projected endpoint domain.

`RepairIfSafe` does not authorize conversion among `binary(16)`, `char(36)` and
`uuid`, including an application-owned binary GUID converter. A format change
requires an explicitly staged destination column, application-defined backfill,
verification and replacement of affected keys/relationships. Do not reinterpret
binary GUID payloads as MariaDB UUID storage or rely on their byte ordering.

Upgrading Doka also changes implicit application-owned 16-byte binary mappings
to `varbinary(17)` under every GUID default. Review generated model differences
and migrations separately; SafeMigrations does not remove those differences,
rewrite prior source or silently approve storage conversions. These contracts
were checked against the [published 10.5.0 GUID migration guidance](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/27a8d3cce3ce2bc64f6efd1e9034b1cd66eb1ff0/docs/operations/migrations.md#guid-representation-changes)
and [MariaDB UUID reference](https://mariadb.com/docs/server/reference/data-types/string-data-types/uuid-data-type)
on 2026-10-15.

Unsupported features are rejected from Doka's canonical server feature
profile. Examples include independent relational schema namespaces,
cross-database operations, and filtered indexes on MySQL/MariaDB. The
separately qualified Binary16-default catalog boundary is version-specific:
MySQL, MariaDB 10.11, and MariaDB 11.4 fail closed, while MariaDB 11.8 and 12.3
are enabled by the complete live evidence matrix. No unqualified future engine
line is admitted implicitly.

## Primary documentation

- [MySQL implicit commit statements](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html)
- [MySQL prepared statement restrictions](https://dev.mysql.com/doc/refman/8.4/en/sql-prepared-statements.html)
- [MySQL ALTER TABLE](https://dev.mysql.com/doc/refman/8.4/en/alter-table.html)
- [MySQL INSERT ON DUPLICATE KEY UPDATE](https://dev.mysql.com/doc/refman/8.4/en/insert-on-duplicate.html)
- [MySQL foreign-key actions](https://dev.mysql.com/doc/refman/8.4/en/constraint-foreign-key.html)
- [MariaDB null-safe equal operator](https://mariadb.com/docs/server/reference/sql-structure/operators/comparison-operators/null-safe-equal)
- [MySQL JSON data type](https://dev.mysql.com/doc/refman/8.4/en/json.html)
- [MariaDB JSON data type](https://mariadb.com/docs/server/reference/data-types/string-data-types/json)
- [MariaDB JSON_EQUALS](https://mariadb.com/docs/server/reference/sql-functions/special-functions/json-functions/json_equals)
- [MariaDB INSERT ON DUPLICATE KEY UPDATE](https://mariadb.com/docs/server/reference/sql-statements/data-manipulation/inserting-loading-data/insert-on-duplicate-key-update)
- [MySQL InnoDB limits](https://dev.mysql.com/doc/refman/8.4/en/innodb-limits.html)
  (retrieved 2026-09-12)
- [MySQL CREATE TABLE](https://dev.mysql.com/doc/refman/8.4/en/create-table.html)
  (retrieved 2026-09-12)
- [MySQL `group_concat_max_len`](https://dev.mysql.com/doc/refman/8.4/en/server-system-variables.html#sysvar_group_concat_max_len)
  (retrieved 2026-09-12)
- [MySQL INFORMATION_SCHEMA.STATISTICS](https://dev.mysql.com/doc/refman/8.4/en/information-schema-statistics-table.html)
  (retrieved 2026-09-30)
- [MariaDB INFORMATION_SCHEMA.STATISTICS](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-statistics-table)
  (retrieved 2026-09-30)
- [MySQL CREATE INDEX](https://dev.mysql.com/doc/refman/8.4/en/create-index.html)
  (retrieved 2026-09-12)
- [MySQL invisible indexes](https://dev.mysql.com/doc/refman/8.4/en/invisible-indexes.html)
- [MySQL CHECK constraints](https://dev.mysql.com/doc/refman/8.4/en/create-table-check-constraints.html)
- [MySQL foreign keys](https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html)
- [MariaDB 12.1 changes](https://mariadb.com/docs/release-notes/community-server/12.1/changes-and-improvements-in-mariadb-12.1)
- [MySQL INFORMATION_SCHEMA.COLUMNS](https://dev.mysql.com/doc/refman/en/information-schema-columns-table.html)
- [MySQL INFORMATION_SCHEMA collation guidance](https://dev.mysql.com/doc/refman/8.4/en/charset-collation-information-schema.html)
  (retrieved 2026-10-13)
- [MySQL temporary table privileges](https://dev.mysql.com/doc/refman/8.4/en/create-temporary-table.html)
- [MariaDB SQL statements causing implicit commit](https://mariadb.com/docs/server/reference/sql-statements/transactions/sql-statements-that-cause-an-implicit-commit)
- [MariaDB ALTER TABLE](https://mariadb.com/docs/server/reference/sql-statements/data-definition/alter/alter-table)
- [MariaDB INFORMATION_SCHEMA.COLUMNS](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-columns-table)
- [MariaDB PREPARE statement](https://mariadb.com/docs/server/reference/sql-statements/prepared-statements/prepare-statement)
- [MariaDB InnoDB limitations](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-limitations)
- [MariaDB InnoDB row formats](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-row-formats/innodb-row-formats-overview)
- [MariaDB ignored indexes](https://mariadb.com/docs/server/ha-and-performance/optimization-and-tuning/optimization-and-indexes/ignored-indexes)
- [Doka 10.2.0 provider configuration](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/provider-configuration.md)
- [Doka ownership-aware connection decision](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/decisions/D-029-ownership-aware-connection-invariants.md)
- [Doka 10.3.0 migration-operation metadata](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/src/Doka.EntityFrameworkCore.MySql/Migrations/MySqlMigrationOperationMetadata.cs)
- [Doka.EntityFrameworkCore.MySql 10.3.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.3.0)
- [Doka 10.4.0 migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.4.0/docs/migration-operation-handlers.md)
- [Doka.EntityFrameworkCore.MySql 10.4.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.4.0)

Deployment sequencing and incident response are defined in
[Deployment and recovery](runbooks/deployment-and-recovery.md).
