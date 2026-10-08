# PostgreSQL behavior

Prepared stable 10.4.9 preserves explicit repair collations and exact ALTER
source authority. Session-active DDL event triggers and DML triggers, including
FK-cascade and provider-backfill effects, invalidate ordered row and structural
proofs before projection shortcuts. Supported unproven states retain their
mutation origin for fresh runtime validation; independent refusals still block.
Narrowing eligibility plans are retained only for the current transport
statement, and fully matching runtime columns skip unused repair/data scopes.
Public APIs, dependency ranges, migration source and report schemas are unchanged.

## Operational summary

The PostgreSQL adapter composes Npgsql's EF Core 10 provider. It classifies
SafeMigrations operations through parameterized catalog queries and renders
guarded operations through the configured Npgsql migrations SQL generator.
PostgreSQL 14 through 18 are qualified independently; the exact pinned matrix
in the successful release run is the release evidence.

## Model-managed data

Newly scaffolded `HasData` changes use source-frozen ensure, update, and delete
operations. Values are parameters during analysis and use `IS NOT DISTINCT
FROM` for null-safe equality. Native `json` and `jsonb` values cast both
operands to `jsonb` before that comparison because PostgreSQL provides ordinary
comparison operators for `jsonb` but not `json`. This gives both types the same
document semantics while keeping SQL `NULL`, JSON `null`, and JSON-like text
distinct. Ensure inserts only an absent primary key. Update and delete repeat
the captured source-state predicate and validate the target postcondition.
SafeMigrations does not use `ON CONFLICT DO UPDATE` or `MERGE` because their
arbiter and trigger semantics do not prove the same source-frozen primary-key
transition.

Incoming `NO ACTION`, `RESTRICT`, `CASCADE`, `SET NULL`, and `SET DEFAULT`
foreign keys are treated as observable dependent effects. A principal delete
is accepted only when every affected model-managed dependent row was removed
by an earlier accepted operation. One unmatched or concurrently inserted
dependent row blocks or fails the delete; SafeMigrations never accepts an
implicit cascade, nulling, or defaulting side effect as convergence.

PostgreSQL triggers can alter or recreate rows. The guarded command therefore
checks the target state after DML. A trigger-produced mismatch fails the
migration rather than returning a successful assessment. The normal EF
migration transaction owns rollback; SafeMigrations does not create a nested
transaction inside the generator.

Model-managed values remain present in model snapshots, generated migration
source, and generated SQL scripts. They are excluded from SafeMigrations
reports, telemetry, stable reason codes, and exception messages. Do not put
secrets or environment-specific values in `HasData`.

For a derived custom context with an independent migration lineage,
`ExcludeModelManagedDataForExcludedTables()` applies the same exact
source/target table-ownership contract as the MySQL/MariaDB adapter. It does not
remove inherited entities from the runtime model or reinterpret existing
migration source. See [model-managed-data ownership](model-managed-data-ownership.md).

## Canonical null-test index filters

PostgreSQL index definitions captured from EF retain their authored raw
`Filter` and contract fingerprint. The adapter recognizes only an exact
provider-delimited, single physical column followed by ` IS NULL` or
` IS NOT NULL`, with optional outer PostgreSQL ASCII SQL whitespace. Quoted
case-sensitive names, escaped quotes, and names containing spaces preserve
their physical identity. PostgreSQL's
[identifier rules](https://www.postgresql.org/docs/18/sql-syntax-lexical.html)
remain the boundary; unquoted mixed-case names are not reinterpreted.

This bounded recognition supplies structured catalog comparison and the exact
predicate-column prerequisite to both ordered preflight projection and runtime
validation. An earlier accepted table or column operation may supply that
column. A missing predicate column remains
`PrerequisiteMissing / RejectPrerequisiteMissing`, including after an accepted
table operation. Execution rejects `P1004 / doka_sm_prerequisite_missing`
before attempting the index DDL. Structured filters retain their existing
dependency and comparison path.

Other raw expressions remain `Unsupported / opaque_sql_expression`: qualified
identifiers, alternate quoting, comments, functions, arithmetic, additional
statements, noncanonical parentheses or keyword spelling are not admitted.
Unicode whitespace is not accepted as PostgreSQL SQL whitespace. Recognition
does not rewrite existing migration source or turn opaque SQL into a general
expression contract.

Canonical predicates participate in ordinary partial-index matching,
semantic-alias resolution, reruns and postflight verification. Partial unique
index data checks consider only rows selected by the authored predicate;
duplicates outside it are not conflicts. PostgreSQL documents that a
[partial-index predicate](https://www.postgresql.org/docs/18/indexes-partial.html)
can use table columns other than its index keys, so those predicate columns
are independent prerequisites rather than implied by the key definition.

Index keys with explicit collations retain their catalog name and schema
checks independently of bundled plain-key comparisons. A column key without
an explicit collation must use that physical column's default collation,
including keys with an explicit operator class. Matching compares
[`pg_index.indcollation`](https://www.postgresql.org/docs/18/catalog-pg-index.html)
with [`pg_attribute.attcollation`](https://www.postgresql.org/docs/18/catalog-pg-attribute.html),
not the absence of `COLLATE` in deparsed column SQL. Exact-name drift remains
`Different`; a differently collated index is not a matching semantic alias.
Expression keys compare the expression independently of key-level collation
and operator-class decorations. Without an explicit key collation, PostgreSQL
derives the expected collation through a scalar `LIMIT 0` subquery and
[`COLLATION FOR`](https://www.postgresql.org/docs/18/functions-info.html).
This reads no table rows and performs no row-wise expression evaluation;
PostgreSQL's ordinary planner constant-folding rules still apply.
Noncollatable expression keys retain the catalog's zero-collation identity.
Top-level structured `COLLATE` nodes follow PostgreSQL's stored operand shape;
nested collations remain part of the expression and use server-resolved names.

## Automatic lossless column repair

With explicit `RepairIfSafe`, the PostgreSQL adapter independently qualifies
ordinary `character varying(n)` widening and live-data-verified narrowing,
including an unbounded `character varying` source and a bounded target. The
declared maximum comes from `information_schema.columns`; its documented null
value means no maximum was declared and therefore requires the narrowing data
proof. The character family, collation, generated/identity state,
default/comment facets, and dependent objects must be understood and
compatible. PostgreSQL preserves ordinary foreign-key dependencies across the
independently qualified length change. Other type-family or semantic
conversions remain fail-closed.

Explicit `AlterColumnIfDifferent` and generated
`AlterColumnIfDifferentFromModel` calls reuse this `VARCHAR` transition contract
under `RepairIfSafe`, with an additional exact old-definition check. They do
not create missing columns or extend the allowlist to `text -> varchar`.
Operation-specific source eligibility is checked separately even when several
operations share a character-length scan. Existing null values still block
nullability tightening; a declared default does not authorize backfilling
existing PostgreSQL rows through this path.

Qualified column collations are preserved by both the initial ALTER and an
accepted repair. The repair emits the same schema-qualified `COLLATE` clause
before verifying the full target contract; otherwise PostgreSQL can reset the
column to its default collation during `ALTER TYPE`. See PostgreSQL's
[ALTER TABLE contract](https://www.postgresql.org/docs/18/sql-altertable.html).
Resolving an explicit collation compares its OID once and treats an absent OID
as a failed match, never as equality through SQL NULL.

Widening preserves the existing value domain and requires no row-value scan.
Narrowing groups and deduplicates candidates per table and uses a bounded
`char_length` existence proof. It returns only whether any value exceeds the
target character count; it never returns the value or key. A successful proof
may scan the complete table. SafeMigrations repeats the proof at execution, and
the provider conversion plus final full-definition postcondition remains the
race boundary. One overlength value is
`DataBlocked / varchar_narrowing_value_too_long`. Cancellation, timeout, an
incomplete result, or concurrent violating data cannot become approval or
truncation.

Accepted length repair reports `TableRewritePossible`. The classification
means the transition is lossless, not that it is metadata-only, nonblocking, or
zero-downtime. PostgreSQL acquires the lock required by its rendered `ALTER
TABLE`; operators must review table size and the maintenance window. PostgreSQL
has no SafeMigrations equivalent of the MySQL/MariaDB `BIT(1) -> TINYINT(1)`
Boolean repair.

## Analysis consistency

### DDL event-trigger freshness

Event triggers can modify unrelated rows and schema objects before a DDL command
returns. Preflight captures their session activation once per analysis invocation:
`O` for origin/local, `R` for replica, `A` for every role, and `D` disabled.
The optional `event_triggers` setting is honored when exposed by the server.
A separate catalog-SELECT permission check precedes the protected trigger query;
inaccessible metadata is uncertainty, not a trigger-absence certificate.
This costs at most two bounded metadata commands per nonempty analysis, not a
per-operation query, and retains only risk and ordinal state.

After executable safe or typed DDL with active or unprovable trigger risk,
later captured row and structural classifications become `ValidateAtRuntime`.
The analysis code is `projected_event_trigger_state_unknown` or
`projected_event_trigger_visibility_unknown`, with the actual DDL `DeferredOrigin`.
The preflight status is `RuntimeValidationRequired` unless an independent blocker
remains. Initial no-ops, disabled/inactive triggers and provably non-executing
typed renames retain normal projection. After risk-bearing DDL, even a previously
matching index or removed table may have changed; neutral shortcuts cannot prove
otherwise. Unsupported contracts remain blocking. Runtime still reads fresh
catalog and rows before its own mutation and may reject the actual ordered state.
The report format, history and transaction contract are unchanged.
See [event-trigger behavior](https://www.postgresql.org/docs/current/event-trigger-definition.html)
and [activation modes](https://www.postgresql.org/docs/current/catalog-pg-event-trigger.html).

User DML triggers can also execute unrelated DDL. The same two bounded metadata
reads capture a global session-active presence flag from `pg_trigger`; no trigger
body or per-owner dependency graph is retained. A direct owner filter would miss
triggers reached through FK cascades, partition routing or recursive writes.
Internal FK triggers alone do not establish this arbitrary-DDL risk.

After executable safe or typed DML with active or unprovable user-trigger risk,
later physical classifications use `projected_dml_trigger_structure_unknown` or
`projected_dml_trigger_visibility_unknown`, with the actual DML `DeferredOrigin`.
Later model-managed row contracts retain `projected_model_managed_data_state_unknown`.
This includes provider-emitted NULL-backfill UPDATEs inside column ALTER/repair
baselines: a statement trigger can fire even when no row needs changing.
Empty typed writes and initial safe no-ops do not invalidate evidence. Unsupported
contracts, fresh runtime classification and the existing transaction boundary
remain authoritative. See the [trigger catalog](https://www.postgresql.org/docs/current/catalog-pg-trigger.html).

### Bounded catalog and row probes

Narrowing eligibility builds only the current candidate's required predicates
inside the bounded statement builder. It no longer retains a full runtime-plan
array for the entire candidate set, and immutable candidate records keep only
their source/target identity, operation and ordinal rather than discarded
narrowing SQL. Result/report memory remains proportional to the operation
count; SQL construction and database checks still occur. This changes lifetime
and retained managed memory, not source authority, query shape or data-proof scope.

Catalog analysis omits only builder-certified constant `TRUE` prerequisites.
Nonconstant prerequisites still complete before row-dependent queries bind.
Independent diagnostic, narrowing-eligibility and qualified row-probe statements
use bounded native `DbBatch` transport, with a sequential fallback for wrappers
that do not expose batching. SQL evidence and scans remain unchanged; only
transport is grouped. Timeout, cancellation, original ordinals, exact result
counts and aggregate payload/parameter bounds remain enforced.
Discarding an oversized candidate preserves the exact retained parameter
objects and their named bindings, types, ordering and payload accounting.
Independent schema and child-object inventory reads use the same transport,
retaining complete 512-value metadata statements and eight-statement batches.
Each result is validated against its owning statement before a complete
inventory is returned.

A NULL row proof is skipped only when fresh metadata proves the complete
queried relation is physically NOT NULL. PostgreSQL 18's `attnotnull` flag can
describe an invalid constraint, so the adapter also checks the matching
constraint's validation and enforcement. PostgreSQL 14-17 retain their catalog
contract without referencing a field absent from those versions. A parent-only
proof does not suppress scans of inherited rows. See the PostgreSQL 18
[attribute catalog](https://www.postgresql.org/docs/18/catalog-pg-attribute.html)
and [constraint catalog](https://www.postgresql.org/docs/18/catalog-pg-constraint.html).

Eligible nullable or unvalidated columns retain a fresh NULL scan. Runtime
classification and repair consume one operation-local result per evaluation;
an accepted repair locks the relation and repeats the evidence before mutation.
The physical repair-eligibility predicate is likewise materialized once in
each evaluation and shared by the state, NULL gate and repair decision. It is
recomputed during the locked second evaluation, not cached across operations.
Matching columns do not acquire that repair lock. A dirty unvalidated constraint
is `DataBlocked`, not `Matching`; a clean one can be validated by the provider's
`SET NOT NULL` repair. These are structural eligibility checks, not retained
row proofs or a bound on rows examined.

Proof-bearing column runtime guards first establish the complete target and
NOT NULL relation contract using fresh metadata. An exact matching replay
skips unused repair and row-probe evaluation. Parent-only `NO INHERIT` metadata
does not discharge descendant NULL checks. Explicit ALTER additionally retains
the exact old-definition authority: a superficially matching parent cannot
authorize an otherwise unproven transition over descendants. Each locked
recheck recomputes these operation-local results, and the independent
postcondition still runs after DDL. These changes reduce repeated catalog work,
not client roundtrips or the necessary scans for a genuine narrowing.

Guard rendering appends action cases, state-guard branches and canonical
baseline commands directly into the final operation-owned buffer. Original
command text still determines a collision-free dollar tag. This removes
intermediate SQL copies without changing decision order, terminators,
indentation, locking or the independent execution postcondition.

When no transaction is supplied, analysis creates a read-only
`RepeatableRead` transaction and holds a transaction-scoped advisory analysis
lock through all catalog chunks. A caller-owned transaction is accepted only
when it is read-only and uses `RepeatableRead` or `Serializable`. The lock
coordinates SafeMigrations analysis; it does not fence application writers or
replace the deployment write window.

## Failure and retry

PostgreSQL migration DDL and model-managed DML normally participate in EF's
migration transaction. A failed compare-and-swap or postcondition therefore
rolls back the current transactional migration path and leaves the history row
unapplied. Transaction-suppressed provider operations or externally executed
no-transaction scripts have different boundaries and require catalog/history
inspection before retry. Follow the
[deployment and recovery runbook](runbooks/deployment-and-recovery.md).

## Primary documentation

- [PostgreSQL comparison functions](https://www.postgresql.org/docs/current/functions-comparison.html)
- [PostgreSQL JSON functions and operators](https://www.postgresql.org/docs/18/functions-json.html)
- [PostgreSQL foreign-key actions](https://www.postgresql.org/docs/current/ddl-constraints.html)
- [PostgreSQL trigger behavior](https://www.postgresql.org/docs/current/trigger-definition.html)
- [PostgreSQL transaction isolation](https://www.postgresql.org/docs/current/transaction-iso.html)
- [PostgreSQL INSERT](https://www.postgresql.org/docs/current/sql-insert.html)
- [PostgreSQL character types](https://www.postgresql.org/docs/18/datatype-character.html)
- [PostgreSQL information-schema columns](https://www.postgresql.org/docs/18/infoschema-columns.html)
- [PostgreSQL ALTER TABLE](https://www.postgresql.org/docs/18/sql-altertable.html)
