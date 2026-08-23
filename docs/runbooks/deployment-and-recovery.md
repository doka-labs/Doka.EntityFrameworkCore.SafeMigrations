# Deployment and recovery runbook

## Purpose

Use this runbook for each database instance. Instances may have different
legacy schemas and legacy history tables; they do not need a shared starting
snapshot. They must share the same reviewed Core migration assembly, canonical
Core model, ordered Core migration sequence, and Core history-table contract.

## Required evidence before the window

- immutable application and migration artifact digest;
- target migration ID and SafeMigrations contract fingerprint;
- pseudonymous instance ID mapped in the protected deployment inventory;
- successful restore drill for a recent backup or snapshot;
- verified free space and provider health;
- reviewed migration-principal grants;
- identified migration writer and disabled competing deployment writers;
- write-fence or maintenance-window plan for data-sensitive transitions;
- rollback decision owner and forward-fix owner;
- destination for preflight, migration, history, and postflight evidence.

Do not put a connection string, server/database name, credential, customer
name, or raw data value in the instance ID, log tags, metrics, or general
deployment output.

## Preflight

1. Connect with the same provider configuration and migration assembly that
   will execute the migration.
2. Resolve `ISafeMigrationRunner` from the context.
3. Call `AnalyzePendingMigrationsAsync` with the pseudonymous instance ID,
   intended target migration, and expected model fingerprint when the
   deployment manifest provides one.
4. Persist JSON through `SafeMigrationReportJson` and validate it against the
   packaged schema.
5. Confirm provider ID, engine family, exact server version, model fingerprint,
   contract fingerprint, and target migration.
6. Review every assessment and unexpected object.

PostgreSQL preflight and postflight normally create their own read-only
`RepeatableRead` transaction and hold one transaction-scoped advisory lock
through all catalog chunks. If deployment code supplies an existing
transaction, configure it as read-only with `RepeatableRead` or `Serializable`
before invoking the runner. SafeMigrations rejects weaker or read-write caller
transactions without disposing, committing, or rolling them back.

Gate interpretation:

| Status | Operator action |
|---|---|
| `NoOperations` | Confirm the intended migration is already present; do not assume success from status alone. |
| `Ready` | Continue if all external deployment gates are also satisfied. |
| `ReadyWithProviderOperations` | Review ordinary EF operations manually; SafeMigrations cannot read-only classify them. |
| `Blocked` | Stop before `Migrate`; use the failure-code runbook. |

Unexpected objects are inventory findings, not deletion instructions. Preserve
them unless a separate reviewed migration explicitly owns their removal.

## Time-of-check/time-of-use controls

Preflight is read-only and cannot reserve the catalog state. Before execution:

- enable the application write fence or enter the maintenance window;
- prevent out-of-band DDL;
- verify no other deployment writer is active;
- keep the same artifact, model, and ordered operation contract used by
  preflight;
- start migration promptly; rerun preflight if the window or artifact changes.

## Migration

1. Capture the pre-migration Core history rows.
2. Execute `Database.MigrateAsync`, `IMigrator.MigrateAsync`, the qualified EF
   CLI path, or the qualified Bundle. Do not wrap MySQL/MariaDB migration DDL in
   a caller-owned business transaction.
3. Keep the process alive until the provider migration lock is released.
4. Do not run `Down` automatically after a failure.
5. Capture the exact exception type, provider error code, SafeMigrations/Doka
   code, operation ordinal, and timestamp without logging SQL payloads or
   connection data.

## Postflight

For the exact explicit operation contract used by the deployment, call
`ISafeMigrationRunner.VerifyAsync` and retain the JSON report. Then verify:

- report status is not `Blocked`;
- every safe operation has `postconditionSatisfied: true`;
- model and contract fingerprints match preflight;
- exactly the expected Core migration history row was added;
- ordinary provider-owned operations have their own postconditions;
- application health and critical read/write smoke checks pass.

Only then release the write fence and mark the instance complete.

## Failure decision table

| Failure point | Database assumption | Response |
|---|---|---|
| Preflight blocked | Target DDL was not executed by SafeMigrations | Correct drift/data/unsupported intent; review a forward migration; rerun preflight. |
| MySQL/MariaDB runtime guard | Earlier DDL in the migration may be committed | Keep writer fenced, inspect catalog/history, correct the classified cause, rerun the same pending migration. |
| PostgreSQL transactional command | Current migration transaction normally rolled back | Confirm history and catalog; correct the cause; rerun. Account for explicitly transaction-suppressed provider operations. |
| Process lost after DDL | History may or may not contain the row | Read catalog and history; never infer success from process exit alone; rerun guarded pending migration or postflight applied migration. |
| Postflight failed with history present | Migration is recorded but target contract is not satisfied | Stop traffic, preserve evidence, issue a reviewed forward-fix migration or restore backup. Do not edit history as a shortcut. |
| Data corruption or unbounded destructive effect | State cannot be proven safe | Isolate instance and restore the tested backup/snapshot under incident control. |

## Partial MySQL/MariaDB retry

The connection must set `Allow User Variables=true`, corresponding to
`MySqlConnectionStringBuilder.AllowUserVariables`; SafeMigrations validates
this before its first guarded command. Doka rc.11 executes the handler cleanup
after success, failure, or cancellation with an independent cancellation
token. If cleanup itself fails, Doka closes the connection, clears its
MySqlConnector pool generation, and reports a non-retryable cleanup exception.
Dispose the failed `DbContext` before an operator-approved retry even when the
provider reports successful cleanup; the retry must start from a fresh unit of
work.

1. Leave the Core history row unchanged.
2. Keep writes fenced.
3. Re-run preflight against the still-pending migration.
4. Confirm earlier successful operations now classify `matching` and the failed
   operation has a stable actionable classification.
5. Correct only the underlying data, permission, capability, or reviewed
   migration defect.
6. Re-run the same migration artifact. Do not create a fake history row.
7. Run postflight and history checks.

The session-local guard has no permanent stored procedure to clean up. Runtime
tests prove same-session cleanup with pool reset disabled after a rejected
state, failed DDL, and cancellation. They also prove that an injected cleanup
failure evicts the physical session before another borrower can receive it.

## Multiple application instances

Run the sequence independently for every database. A green report for one
instance says nothing about another. Deployment orchestration may process
different databases in bounded parallel batches, but each database has one
writer and its own evidence bundle. Blocked instances remain fenced or on the
old application version according to the reviewed compatibility plan; they do
not cause SafeMigrations to relax the contract for other instances.

## Backup references

- [MySQL backup and recovery](https://dev.mysql.com/doc/refman/8.4/en/backup-and-recovery.html)
- [MariaDB backup and restore overview](https://mariadb.com/docs/server/server-usage/backing-up-and-restoring-databases)
- [PostgreSQL backup and restore](https://www.postgresql.org/docs/current/backup.html)
- [EF Core applying migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying)

See [Failure codes](failure-codes.md) for classified response details.
