# Failure-code runbook

SafeMigrations report codes are stable, low-cardinality machine values. Object
names may appear in protected report fields but never as metric tags. Always
correlate a code with operation ordinal, kind, provider environment, model and
contract fingerprints, and the protected deployment record.

## Blocking decision codes

| Code | Meaning | Required response |
|---|---|---|
| `unsupported` | The active engine cannot represent the operation or requested facet. | Stop. Remove the unsupported intent or change the reviewed support contract; do not emit provider-specific ad-hoc SQL as a bypass. |
| `data_blocked` | Existing rows violate a uniqueness, nullability, check, or foreign-key precondition. | Keep target DDL unapplied. Repair data through an audited, idempotent transformation, rerun preflight, then migrate. |
| `prerequisite_missing` | A required table does not exist, so dependent state or data checks cannot be evaluated safely. | Add or converge the prerequisite first. Do not reinterpret the result as an empty table or a data violation. |
| `different_reject` | An ensure target exists with a different definition under `ThrowIfDifferent`. | Compare each expected/live facet. Correct drift or author an explicit safe transition. |
| `different_no_safe_repair` | `RepairIfSafe` was requested but no allowlisted repair passed. | Do not widen the allowlist for this instance. Author a reviewed migration/backfill or restore the expected definition. |
| `wrong_object_kind` | A drop target name denotes a conflicting object kind. | Stop and identify ownership. Never drop it by name alone. |
| `rename_target_conflict` | Rename source exists and the intended target name is already occupied. | Resolve semantic ownership explicitly; do not infer merge/equivalence. |
| `alter_target_missing` | An alter operation cannot find its target column. | Use an explicit ensure/add path if absence is valid, otherwise correct drift. |
| `alter_not_approved` | Live column differs but does not exactly match the declared old definition or the transition is outside the lossless allowlist. | Correct `oldDefinition` only if catalog evidence proves it; otherwise design a forward data/schema transition. |
| `postcondition_failed` | Runtime completed or postflight ran, but the expected final catalog condition is false. | Stop traffic, preserve catalog/history evidence, and use forward fix or backup restore. |

`RejectUnsupported`, `RejectDifferent`, `RejectDataBlocked`, and
`RejectPrerequisiteMissing` are the corresponding `SafeMigrationAction`
values.

## Runtime database error identity

Runtime guards preserve the same categories at the database boundary:

| MySQL/MariaDB constraint identity | PostgreSQL SQLSTATE/message | Meaning |
|---|---|---|
| `doka_sm_different` | `P1001` / `doka_sm_different` | Definition mismatch or unapproved repair. |
| `doka_sm_unsupported` | `P1002` / `doka_sm_unsupported` | Active engine capability rejects the operation. |
| `doka_sm_data_blocked` | `P1003` / `doka_sm_data_blocked` | Existing data violates a precondition. |
| `doka_sm_prerequisite_missing` | `P1004` / `doka_sm_prerequisite_missing` | A required table is absent; dependent expressions were not evaluated. |
| `doka_sm_postcondition` | `P1005` / `doka_sm_postcondition` | Target DDL ran but final catalog condition is false. |

MySQL/MariaDB uses unique constraints on a session-local temporary assertion
table because `SIGNAL` cannot be used in its prepared-statement path.
PostgreSQL uses private application SQLSTATE values in the provider `DO` block.
Handle the stable identity/category; do not parse a localized provider message.

## Non-blocking assessment codes

| Code | Meaning |
|---|---|
| `classified_missing` | Live analyzer observed absence. |
| `classified_matching` | Live analyzer observed the target definition. |
| `classified_different` | Live analyzer observed drift; the policy determines whether this blocks. |
| `classified_unsupported` | Provider classified unsupported; normally paired with `unsupported` when blocked. |
| `classified_data_blocked` | Provider classified a data precondition failure; normally paired with `data_blocked`. |
| `classified_prerequisite_missing` | Provider proved a required table is absent without evaluating dependent table SQL. |
| `projected_missing` | Preflight projection observes absence after applying earlier accepted operations virtually. |
| `projected_matching` | Preflight projection observes a match after earlier accepted operations virtually. |
| `projected_different` | Preflight projection observes a conflict between ordered operations. |
| `missing_apply` | Ensure target is absent and target DDL is planned. |
| `matching_noop` | Existing target matches; no DDL is required. |
| `existing_existence_noop` | Existing object is intentionally accepted under explicit existence-only semantics. |
| `different_repair` | A proven lossless repair and its preconditions passed. |
| `missing_noop` | Drop target or rename source is absent; operation is idempotently complete. |
| `existing_drop` | Drop target exists with the expected kind and will be removed. |
| `source_missing_noop` | Rename source is absent and no target conflict requires action. |
| `source_exists_rename` | Rename source exists and target is free. |
| `provider_owned_not_analyzed` | Ordinary EF/provider operation is present; SafeMigrations cannot classify it read-only. |

When a report is `ReadyWithProviderOperations`, supply independent
postconditions for every `provider_owned_not_analyzed` operation before
deployment approval.

## Stable unsupported reason codes

An unsupported assessment retains the provider's bounded reason instead of
collapsing every case into one message. Important expression reasons are:

| Code | Meaning |
|---|---|
| `opaque_sql_expression` | Raw SQL has no typed structure from which catalog equivalence can be proven. |
| `opaque_expression_rename_projection` | An earlier identifier rename affected an opaque SQL facet that Core cannot rewrite safely. |
| `index_sort_order` | The active access method or engine cannot represent the requested explicit order. |
| `index_null_order` | The active engine cannot represent the requested explicit null order. |

Treat an unknown unsupported reason as a contract change and stop rollout.

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

`safe_migrations.run.failure.count` uses only these bounded tags:

| Code | Source | Response |
|---|---|---|
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
