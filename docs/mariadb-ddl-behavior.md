# MariaDB Multi-Statement And Implicit DDL Commit Behavior

This note explains an important operational property of the MariaDB provider in `Doka.EntityFrameworkCore.SafeMigrations`.

The short version is:

- some MariaDB safe-migration paths are single-statement DDL
- others require multi-statement control flow
- MariaDB DDL is not fully transactional in the way many consumers expect

Because of that, MariaDB users should treat strict checks, preflight mode, and controlled repair as **safe and rerunnable**, but not necessarily **fully atomic**.

## Why This Exists

MariaDB does not provide native single-statement syntax for every guard pattern this library needs.

For example, the library sometimes needs to express logic like:

- create the object if it is missing
- do nothing if the existing definition matches
- raise an explicit error if the existing definition differs

For those cases, the MariaDB provider in [MariaDbSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/SqlGeneration/MariaDbSafeMigrationsSqlGenerator.cs) uses multi-statement helper flows instead of pretending that one native statement can do everything safely.

## The Three Main MariaDB Execution Shapes

### 1. Native Single-Statement DDL

Some operations can use direct MariaDB syntax such as:

- `CREATE TABLE IF NOT EXISTS`
- `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`
- `CREATE INDEX IF NOT EXISTS`
- `DROP TABLE IF EXISTS`
- `DROP SCHEMA IF EXISTS`
- `CREATE SCHEMA IF NOT EXISTS`
- `DROP COLUMN IF EXISTS`
- `DROP INDEX IF EXISTS`
- `DROP FOREIGN KEY IF EXISTS`

These are the simplest paths operationally.

### 2. Prepared-Statement Guard Flows

Some guarded operations are emitted as multi-statement sequences using:

- `SET @safe_migrations_sql = ...`
- `PREPARE ...`
- `EXECUTE ...`
- `DEALLOCATE PREPARE ...`

This is currently used for operations such as some guarded rename and drop paths where MariaDB needs an existence check before executing provider SQL safely.

### 3. Temporary Stored-Procedure Guard Flows

The strictest MariaDB paths use a temporary stored procedure sequence:

- `DROP PROCEDURE IF EXISTS safe_migrations_guard`
- `CREATE PROCEDURE safe_migrations_guard() ...`
- `CALL safe_migrations_guard()`
- `DROP PROCEDURE IF EXISTS safe_migrations_guard`

This shape is currently used for paths that need branching and explicit mismatch signaling, including:

- strict create/add checks
- some controlled repair paths
- MariaDB preflight paths
- guarded alter-column decision blocks

## Operational Consequences

### MariaDB DDL Can Implicitly Commit

MariaDB DDL should not be treated as fully rollback-safe.

That means:

- do not assume an outer EF Core migration transaction makes these MariaDB paths atomic
- do not assume every intermediate helper statement can be rolled back cleanly
- treat MariaDB migration execution as rerunnable and guarded, but not as an all-or-nothing transaction boundary

This matters most for:

- initial synchronization against an existing production schema
- strict mode
- preflight mode
- controlled repair mode

### Preflight Is Non-Mutating For Target Objects, But Not Literally DDL-Free On MariaDB

The library's MariaDB preflight mode does **not** create or alter the target schema objects it is analyzing.

However, the current implementation may still issue temporary routine DDL internally in order to evaluate the guarded logic safely.

In other words, on MariaDB:

- preflight is "no target-schema change"
- preflight is **not** "zero DDL statements executed"

That distinction is important for operators and release tooling.

### Run Migrations Outside Open Application Transactions

Because MariaDB DDL statements implicitly commit any open transaction, guarded safe-migration operations must not be issued from within an open application transaction that you intend to keep open or roll back.

If an outer application transaction is open when a guarded MariaDB migration operation executes:

- the DDL within the guarded flow will implicitly commit the outer transaction
- any subsequent rollback of the outer transaction will not undo the committed schema changes

Consumers should run MariaDB migrations independently, outside of open application transactions. EF Core's standard `IMigrator.Migrate` / `MigrateAsync` entry points run each migration in its own execution block and do not themselves wrap all migrations in a single outer transaction, which is the expected usage pattern.

This constraint applies specifically to the multi-statement guarded paths (stored-procedure flows and prepared-statement guard flows). Simple native-DDL operations such as `CREATE TABLE IF NOT EXISTS` are also DDL and carry the same MariaDB implicit-commit behavior, even when no guard wrapper is used.

### A Failure Can Leave The Temporary Guard Procedure Behind

The temporary procedure flow intentionally starts with `DROP PROCEDURE IF EXISTS` so reruns can recover cleanly.

However, if the `CALL safe_migrations_guard()` step fails because of a mismatch or another runtime error, the final `DROP PROCEDURE IF EXISTS` statement may not run.

Practical consequence:

- a failed MariaDB guarded operation can temporarily leave `safe_migrations_guard` behind
- the next guarded run starts by dropping it again
- if needed, operators can also remove it manually

### Concurrent MariaDB Migration Runs Are A Bad Idea

The temporary procedure name is intentionally stable so reruns can recover predictably, but that also means concurrent MariaDB migration sessions against the same database are not a supported operating mode.

Do not run multiple safe-migration processes concurrently against the same target schema on MariaDB.

### The Migration Principal Needs Routine Privileges For Some Paths

Because strict, repair, and preflight flows may create and drop temporary procedures, the migration user may need the corresponding routine privileges for those paths.

If the migration principal cannot create or drop procedures, those guarded MariaDB paths can fail even though simpler single-statement safe operations would succeed.

## What Consumers Should Do

For MariaDB deployments, the recommended operational posture is:

1. Run migrations serially, not concurrently.
2. Test strict/repair/preflight migrations against a disposable copy or staging clone first.
3. Do not rely on full transactional rollback semantics for MariaDB DDL.
4. Expect rerunnability and explicit rejection behavior, not atomicity.
5. Use a migration principal that has the privileges required by the chosen safe-migration paths.
6. Keep large initial synchronization migrations reasonably segmented when possible.

## What This Does Not Mean

This note does **not** mean the MariaDB provider is unsafe.

It means the MariaDB provider is honest about MariaDB's operational model:

- it uses guarded SQL to make reruns and mismatch handling explicit
- it prefers a clear error over hidden drift
- it preserves data-loss avoidance
- it does not promise stronger transactional guarantees than MariaDB can actually provide

That is a deliberate part of the library design.
