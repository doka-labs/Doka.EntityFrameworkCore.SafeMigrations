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
