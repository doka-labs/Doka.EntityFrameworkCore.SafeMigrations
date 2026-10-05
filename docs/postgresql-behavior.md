# PostgreSQL behavior

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
