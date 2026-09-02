# Failure-code runbook

SafeMigrations report codes are stable, low-cardinality machine values. Object
names may appear in protected report fields but never as metric tags. Always
correlate a code with operation ordinal, kind, provider environment, model and
contract fingerprints, and the protected deployment record.

## Which code appears in a report

Provider analysis, the public decision planner, the run report, and database
errors are distinct contracts. `SafeMigrationAssessment.Code` is selected by
the runner as follows; do not treat all codes below as interchangeable:

| Assessment | Code source |
| --- | --- |
| Ordinary EF/provider operation | `provider_owned_not_analyzed` |
| Superseded postflight safe operation | `postcondition_superseded` |
| Blocked postflight | `postcondition_failed` |
| Blocked preflight with `Unsupported` state | The analyzer's specific unsupported reason, or `classified_unsupported` |
| Other blocked preflight | The rejecting planner decision code |
| Non-blocked safe operation | The analyzer/projection code, not the accepting planner decision code |

Use report status, observed state, action, and postcondition together. A code
prefix does not by itself establish whether deployment is permitted. The
[runner](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationRunner.cs)
owns this mapping; the [planner](../../src/Doka.EntityFrameworkCore.SafeMigrations/Planning/SafeMigrationDecisionPlanner.cs)
returns a separate `SafeMigrationDecision.Code`.

## Blocking decision codes

| Code | Meaning | Required response |
| --- | --- | --- |
| `unsupported` | The active engine cannot represent the operation or requested facet. | Stop. Remove the unsupported intent or change the reviewed support contract; do not emit provider-specific ad-hoc SQL as a bypass. |
| `data_blocked` | Existing rows violate a uniqueness, nullability, check, or foreign-key precondition. | Keep target DDL unapplied. Repair data through an audited, idempotent transformation, rerun preflight, then migrate. |
| `prerequisite_missing` | A required table or referenced column does not exist, so dependent state or data checks cannot be evaluated safely. | Add or converge the prerequisite first. Do not reinterpret the result as an empty table or a data violation. |
| `different_reject` | An ensure target exists with a different definition under `ThrowIfDifferent`. | Compare each expected/live facet. Correct drift or author an explicit safe transition. |
| `different_no_safe_repair` | `RepairIfSafe` was requested but no allowlisted repair passed. | Do not widen the allowlist for this instance. Author a reviewed migration/backfill or restore the expected definition. |
| `wrong_object_kind` | A drop target has a conflicting kind or ownership, such as an index belonging to another table. | Stop and identify ownership. Never drop it by name alone. |
| `rename_target_conflict` | Rename is rejected because of target occupancy, source kind, or source table ownership. | Inspect both identities; do not infer merge/equivalence or assume that a free target is sufficient. |
| `alter_target_missing` | An alter operation cannot find its target column. | Use an explicit ensure/add path if absence is valid, otherwise correct drift. |
| `alter_not_approved` | Live column differs and the policy is not `RepairIfSafe`, or the old definition/lossless transition is not proven. | Check policy and evidence; correct `oldDefinition` only if the catalog proves it, otherwise design a reviewed forward transition. |

`RejectUnsupported`, `RejectDifferent`, `RejectDataBlocked`, and
`RejectPrerequisiteMissing` are the corresponding `SafeMigrationAction`
values.

`unsupported` is the planner's generic rejection; a blocked preflight report
retains the analyzer's more specific reason instead. `postcondition_failed` is
a runner code for a false final condition in postflight. Keep writes fenced,
preserve catalog/history evidence, and use the recovery runbook if it occurs;
it does not prove that every earlier migration command completed.

## Runtime database error identity

Runtime guards preserve the same categories at the database boundary:

| MySQL/MariaDB constraint identity | PostgreSQL SQLSTATE/message | Meaning |
| --- | --- | --- |
| `doka_sm_different` | `P1001` / `doka_sm_different` | Definition mismatch or unapproved repair. |
| `doka_sm_unsupported` | `P1002` / `doka_sm_unsupported` | Active engine capability rejects the operation. |
| `doka_sm_data_blocked` | `P1003` / `doka_sm_data_blocked` | Existing data violates a precondition. |
| `doka_sm_prerequisite_missing` | `P1004` / `doka_sm_prerequisite_missing` | A required table or referenced column is absent; dependent expressions were not evaluated. |
| `doka_sm_postcondition` | `P1005` / `doka_sm_postcondition` | Target DDL ran but final catalog condition is false. |

MySQL/MariaDB uses unique constraints on a session-local temporary assertion
table because `SIGNAL` cannot be used in its prepared-statement path.
PostgreSQL uses private application SQLSTATE values in the provider `DO` block.
PostgreSQL exposes the category through SQLSTATE. MySQL/MariaDB uses duplicate
key error 1062 for these assertions; that number alone does not identify a
SafeMigrations rejection. Match the invariant `doka_sm_*` constraint token for
the guarded command, not localized sentence fragments. Do not export the full
provider error message into public telemetry.

<a id="non-blocking-assessment-codes"></a>

## Analyzer and projection codes

These describe observed or projected state, including blocking states. Report
selection follows the mapping above. In particular, a blocked preflight
data/prerequisite result uses its planner rejection code.

| Code | Meaning |
| --- | --- |
| `classified_missing` | Live analyzer observed absence. |
| `classified_matching` | Live analyzer observed the target definition. |
| `classified_different` | Live analyzer observed drift; the policy determines whether this blocks. |
| `classified_unsupported` | Provider classified unsupported without a more specific static reason; remains the blocked preflight report code. |
| `classified_data_blocked` | Provider classified a data precondition failure; a blocked preflight report uses `data_blocked`. |
| `classified_prerequisite_missing` | Provider proved a required table or referenced column is absent without evaluating dependent SQL. |
| `projected_missing` | Preflight projection observes absence after applying earlier accepted operations virtually. |
| `projected_matching` | Preflight projection observes a match after earlier accepted operations virtually. |
| `projected_different` | Preflight projection observes a conflict between ordered operations. |
| `projected_data_state_unknown` | A typed EF data operation preserved structural facts but invalidated a projected or live pre-batch row-safety proof. The public blocked assessment uses `prerequisite_missing`; do not execute the dependent operation without a separately provable post-DML state. |
| `postcondition_superseded` | A later safe operation is the final writer for the same exact catalog resource. The earlier ordered assessment remains visible and has a satisfied effective postcondition; provider-owned operations can never produce this code. |
| `provider_owned_not_analyzed` | Ordinary EF/provider operation is present and is not classified as safe. A recognized deterministic table/column postcondition may be projected conditionally into a later safe prerequisite. Typed insert/update/delete-data operations retain those structural facts but invalidate data-safety proofs; the provider operation itself remains unanalyzed. |

When a report is `ReadyWithProviderOperations`, supply independent
postconditions for every `provider_owned_not_analyzed` operation before
deployment approval. A later `projected_missing` result proves only that its
safe prerequisite follows if the preceding provider operation succeeds; it
does not waive that independent evidence.
Typed EF data operations preserve only structural prerequisites for a later
non-unique index. A data-dependent unique index or additive constraint remains
blocked even when the live analyzer observed absence before the ordered data
operation. A later structural provider operation cannot clear that uncertainty.
If an unrecognized provider operation or raw SQL separates the prerequisite
from the safe operation, projection facts are discarded and the later operation
uses the live analyzer result.

An accepted exact-name index drop can project a following ordinary column
BTREE ensure to `projected_missing`. It cannot override
`projected_data_state_unknown`, a physical-key unsupported result,
`data_blocked`, `prerequisite_missing`, or unrelated exact-name index drift.
If replacement preflight blocks, do not execute the preceding
ordinary drop independently; correct the target definition or live data first.

## Accepting planner decision codes

These are the public planner's decision codes, not the normal code values in
non-blocked run assessments. The report exposes the corresponding `Action`
alongside the analyzer/projection code.

| Code | Meaning |
| --- | --- |
| `missing_apply` | Ensure target is absent and target DDL is planned. |
| `matching_noop` | Existing target matches; no DDL is required. |
| `existing_existence_noop` | Existing object is intentionally accepted under explicit existence-only semantics. |
| `different_repair` | A proven lossless repair and its preconditions passed. |
| `missing_noop` | Drop target is absent; no DDL is required. |
| `existing_drop` | Drop target exists with the expected kind and will be removed. |
| `source_missing_noop` | Rename source is absent; no rename is performed. Independently check the destination; rename postflight proves source absence only. |
| `source_exists_rename` | Rename source exists and target is free. |

## Stable unsupported reason codes

An unsupported preflight assessment retains the provider's bounded reason
instead of collapsing every case into one message. The current built-in
adapters emit these static reasons. A listed provider is the producer of that
code, not a claim that the feature is absent from every version of that engine.

| Code | Provider | Meaning |
| --- | --- | --- |
| `opaque_sql_expression` | Both | Raw/provider-fragment SQL has no provable typed catalog equivalence. |
| `opaque_expression_rename_projection` | Both | An earlier rename affected an opaque facet that cannot be safely rewritten. |
| `column_type_mapping` | Both | The expected column has no supported relational type mapping. |
| `index_prefix_length` | Both | Prefix-length keys are not supported by the selected provider/capability. |
| `index_prefix_required_for_key_limit` | MySQL/MariaDB | A missing ordinary BTREE index exceeds the live InnoDB key limit, or a declared prefix is invalid for the key column. SafeMigrations does not invent a semantics-changing prefix. |
| `index_key_length_unverifiable` | MySQL/MariaDB | A missing expression, non-BTREE, text/blob, unknown-type, or otherwise unbounded index shape has no provable physical key width. |
| `index_replacement_data_blocked` | Both | An accepted exact-name index drop is followed by a unique replacement whose live key values contain duplicates. Preflight preserves this evidence and blocks before executing the drop. |
| `schema_operations` | MySQL/MariaDB | PostgreSQL-style schema ensure/drop is not a supported namespace operation. |
| `schema_qualified_object` | MySQL/MariaDB | An object expectation supplies a PostgreSQL-style schema namespace. |
| `schema_qualified_collation` | MySQL/MariaDB | A column collation supplies a schema-qualified identity. |
| `literal_default_catalog_representation` | MySQL/MariaDB | The literal default cannot be represented and compared reliably through that catalog/profile. |
| `generated_column` | MySQL/MariaDB | The active profile lacks the requested stored/virtual generated-column capability. |
| `expression_default` | MySQL/MariaDB | The active profile lacks expression-default capability. |
| `check_constraint` | MySQL/MariaDB | The active profile lacks the required check-constraint capability. |
| `filtered_index` | MySQL/MariaDB | The active profile does not support a filtered index. |
| `functional_index` | MySQL/MariaDB | The active profile does not support expression index keys. |
| `index_null_order` | MySQL/MariaDB | Explicit index-key null ordering is not supported by the adapter. |
| `index_sort_order` | MySQL/MariaDB | Explicit ordering is requested for a HASH index. |
| `descending_index` | MySQL/MariaDB | The active profile lacks descending-index capability. |
| `included_columns` | MySQL/MariaDB | Included/non-key index columns are not supported by the adapter. |
| `nulls_not_distinct` | MySQL/MariaDB | A nulls-not-distinct index contract is not supported by the adapter. |
| `index_key_collation` | MySQL/MariaDB | An explicit per-key index collation is not supported by the adapter. |
| `operator_class` | MySQL/MariaDB | PostgreSQL-style index operator classes are not supported. |
| `virtual_generated_column` | PostgreSQL | The adapter rejects an explicitly virtual computed column. |

PostgreSQL also evaluates capabilities dynamically. A nulls-not-distinct index
before PostgreSQL 15, or ordering on an access method without `can_order`,
produces `classified_unsupported` rather than a new static reason above.

The provider catalog builders and their feature slices own the reasons:
[MySQL/MariaDB](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/SqlGeneration/MySqlSafeMigrationCatalogSqlBuilder.cs)
and [PostgreSQL](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationCatalogSqlBuilder.cs).
For an unknown reason, stop automated rollout, record the actual package/engine
versions, and investigate a documentation gap, version mismatch, or defect.
Do not assume that an undocumented string alone proves a new runtime contract.

## Diagnose index and constraint definition drift

The assessment code remains low-cardinality and therefore never embeds live
object names, widths, or SQL fragments. The protected assessment still carries
the expected table/index/constraint identity. Retrieve live candidates only in
the controlled deployment session and retain the result with the deployment
record; do not copy it into metric labels. A differently named object with the
same complete semantic definition is `Matching`; never drop or rename it merely
to align naming. An object with the expected name and a different definition is
authoritative drift and cannot be hidden by a second matching alias. A
`Different` result can also mean that a provider-required physical namespace is
occupied: PostgreSQL index, index-rename target, or primary/unique backing-index
relation names; MySQL CHECK or foreign-key symbols; or MariaDB foreign-key
symbols before 12.1. Inspect the owning object; do not drop it automatically
merely to free the name.

For `index_prefix_required_for_key_limit` or
`index_key_length_unverifiable`, inspect the live MySQL/MariaDB table and key
columns:

```sql
SELECT
    t.ENGINE,
    t.ROW_FORMAT,
    @@innodb_page_size AS innodb_page_size,
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.CHARACTER_OCTET_LENGTH,
    c.CHARACTER_SET_NAME,
    c.NUMERIC_PRECISION,
    c.NUMERIC_SCALE,
    c.DATETIME_PRECISION
FROM INFORMATION_SCHEMA.TABLES AS t
JOIN INFORMATION_SCHEMA.COLUMNS AS c
    ON c.TABLE_SCHEMA = t.TABLE_SCHEMA
    AND c.TABLE_NAME = t.TABLE_NAME
WHERE t.TABLE_SCHEMA = DATABASE()
    AND t.TABLE_NAME = '<expected_table>'
    AND c.COLUMN_NAME IN ('<key_column_1>', '<key_column_2>')
ORDER BY c.ORDINAL_POSITION;
```

Compare those values with the generated `prefixLengths` argument and the
reviewed EF model. A zero entry means the complete key. Do not copy a prefix
from another installation: character set, row format, page size, and intended
uniqueness semantics are part of the decision.

If an expected index is reported `Different` while an equivalent differently
named index exists, inspect the object with the exact expected name first. Its
definition takes precedence. An equivalent alias, including an InnoDB foreign-
key support index, satisfies the ensure only when the expected name itself is
absent. Never rename or drop a candidate solely because it has the same keys.

To diagnose MySQL/MariaDB foreign-key definition drift, list each physical
candidate independently:

```sql
SELECT
    rc.CONSTRAINT_NAME,
    rc.UPDATE_RULE,
    rc.DELETE_RULE,
    kcu.ORDINAL_POSITION,
    kcu.COLUMN_NAME,
    kcu.REFERENCED_TABLE_SCHEMA,
    kcu.REFERENCED_TABLE_NAME,
    kcu.REFERENCED_COLUMN_NAME
FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS AS rc
JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE AS kcu
    ON kcu.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA
    AND kcu.TABLE_NAME = rc.TABLE_NAME
    AND kcu.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
WHERE rc.CONSTRAINT_SCHEMA = DATABASE()
    AND rc.TABLE_NAME = '<expected_table>'
ORDER BY rc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION;
```

For unique/check/index identity or facet drift, inspect the corresponding
catalog without copying live expressions into logs. MySQL exposes check
enforcement and index visibility; MariaDB exposes the table identity of a check
and whether an index is ignored:

```sql
-- MySQL
SELECT tc.TABLE_NAME, tc.CONSTRAINT_NAME, tc.CONSTRAINT_TYPE, tc.ENFORCED
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS AS tc
WHERE tc.CONSTRAINT_SCHEMA = DATABASE()
    AND tc.TABLE_NAME = '<expected_table>';

SELECT s.INDEX_NAME, s.SEQ_IN_INDEX, s.COLUMN_NAME, s.NON_UNIQUE,
    s.INDEX_TYPE, s.SUB_PART, s.COLLATION, s.IS_VISIBLE
FROM INFORMATION_SCHEMA.STATISTICS AS s
WHERE s.TABLE_SCHEMA = DATABASE()
    AND s.TABLE_NAME = '<expected_table>'
ORDER BY s.INDEX_NAME, s.SEQ_IN_INDEX;
```

```sql
-- MariaDB
SELECT cc.TABLE_NAME, cc.CONSTRAINT_NAME, cc.CHECK_CLAUSE
FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS AS cc
WHERE cc.CONSTRAINT_SCHEMA = DATABASE()
    AND cc.TABLE_NAME = '<expected_table>';

SELECT s.INDEX_NAME, s.SEQ_IN_INDEX, s.COLUMN_NAME, s.NON_UNIQUE,
    s.INDEX_TYPE, s.SUB_PART, s.COLLATION, s.IGNORED
FROM INFORMATION_SCHEMA.STATISTICS AS s
WHERE s.TABLE_SCHEMA = DATABASE()
    AND s.TABLE_NAME = '<expected_table>'
ORDER BY s.INDEX_NAME, s.SEQ_IN_INDEX;
```

For PostgreSQL constraint identity or facet conflicts, query the catalog in the
expected schema:

```sql
SELECT
    c.conname,
    c.contype,
    c.convalidated,
    c.condeferrable,
    c.condeferred,
    c.conislocal,
    c.coninhcount,
    c.conparentid,
    pg_get_constraintdef(c.oid, true) AS definition
FROM pg_catalog.pg_constraint AS c
JOIN pg_catalog.pg_class AS t ON t.oid = c.conrelid
JOIN pg_catalog.pg_namespace AS n ON n.oid = t.relnamespace
WHERE n.nspname = '<expected_schema>'
    AND t.relname = '<expected_table>'
    AND c.contype IN ('p', 'u', 'c', 'f')
ORDER BY c.contype, c.conname;
```

For PostgreSQL index drift, retain health and ownership evidence together:

```sql
SELECT
    idx.relname,
    idx.relkind,
    i.indisvalid,
    i.indisready,
    i.indislive,
    parent.oid IS NOT NULL AS attached_to_parent,
    owner.conname AS owning_constraint,
    pg_get_indexdef(i.indexrelid) AS definition
FROM pg_catalog.pg_index AS i
JOIN pg_catalog.pg_class AS idx ON idx.oid = i.indexrelid
JOIN pg_catalog.pg_class AS tbl ON tbl.oid = i.indrelid
JOIN pg_catalog.pg_namespace AS n ON n.oid = idx.relnamespace
LEFT JOIN pg_catalog.pg_inherits AS parent ON parent.inhrelid = i.indexrelid
LEFT JOIN pg_catalog.pg_constraint AS owner
    ON owner.conindid = i.indexrelid
    AND owner.conrelid = i.indrelid
    AND owner.contype IN ('p', 'u', 'x')
WHERE n.nspname = '<expected_schema>'
    AND tbl.relname = '<expected_table>'
ORDER BY idx.relname;
```

Resolve definition drift explicitly. If the exact expected name is wrong,
repair or replace that object only after its dependencies and data integrity
have been independently verified. If only the physical name differs and every
modeled facet matches, retain the object: the ensure is already satisfied and
must remain a non-destructive no-op.

## Unexpected-object inventory codes

The following codes report additive live objects outside the supplied
canonical owned definitions:

- `unexpected_table`;
- `unexpected_column`;
- `unexpected_index`;
- `unexpected_primary_key`;
- `unexpected_unique_constraint`;
- `unexpected_check_constraint`;
- `unexpected_foreign_key`.

They are informational inventory and never authorize deletion, rename, or
semantic equivalence. Escalate only when an unexpected object conflicts with
the intended migration or violates organizational schema ownership.

## Telemetry failure codes

`safe_migrations.run.failure.count` uses the bounded failure-code vocabulary
below. See [observability](observability.md#measurement-boundary) for which
runner regions emit it; not every preflight rejection is a failure metric.

<a id="model_contract_mismatch"></a>
<a id="provider_command_failed"></a>
<a id="input_contract_invalid"></a>
<a id="runtime_contract_invalid"></a>
<a id="unexpected_failure"></a>

| Code | Source | Response |
| --- | --- | --- |
| `model_contract_mismatch` | Derived runtime model differs from canonical migration snapshot/fingerprint. | Stop before catalog analysis; move instance-specific mappings to a separate context or restore the canonical model. |
| `provider_command_failed` | Provider catalog or command returned a `DbException`. | Inspect protected provider error code, permissions, availability, lock state, and exact server version. |
| `input_contract_invalid` | A definition, option, enum, or identifier violated construction rules. | Correct migration source; do not retry unchanged. |
| `runtime_contract_invalid` | Adapter registration, provider identity, generated classifier boundary, or runtime invariant is invalid. | Stop; verify package graph and exactly one matching adapter registration. |
| `unexpected_failure` | Exception is outside the classified families. | Treat as a defect/incident, preserve evidence, and do not auto-retry until bounded. |

## Stop-the-line conditions

Always stop automated rollout for:

- any `Blocked` report;
- model or contract fingerprint mismatch;
- missing or conflicting provider adapter;
- postcondition failure;
- an existing NuGet package that differs from qualified release bytes;
- unknown provider state or failure code;
- evidence of destructive or unbounded data change.

Continue only after the underlying cause is fixed, preflight is repeated for
the same instance, and the reviewed deployment contract remains unchanged.
