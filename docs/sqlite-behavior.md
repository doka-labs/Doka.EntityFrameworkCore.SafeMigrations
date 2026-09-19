# SQLite behavior

## Supported deployment shape

`Doka.EntityFrameworkCore.SafeMigrations.Sqlite` composes the bundle-neutral
official EF Core SQLite provider core. Configure `UseSqlite(...)` first and then
`UseSqliteSafeMigrations()`. The application must reference and initialize its
chosen native SQLite bundle. The adapter requires SQLite 3.46.1 or later and
supports the primary `main` database.
An explicit `main` qualifier and an omitted qualifier identify the same object;
attached databases and cross-database operations fail closed with
`database_qualifier_mismatch`.

SQLite stores schema definitions as SQL text and supports only a limited set of
direct `ALTER TABLE` operations. SafeMigrations therefore uses the official EF
Core SQLite generator for ordinary provider operations and for model-owned table
rebuilds. Catalog classification reads `sqlite_schema`, `PRAGMA table_xinfo`,
`index_list`, `index_xinfo`, and `foreign_key_list` through parameterized
commands. Identifier, column order, collation, key direction, filter, generated
column, default, referential action, and constraint-expression facets remain
part of the comparison contract. A rebuild also preserves physical constraint
names. Per-constraint conflict clauses, deferred foreign keys, and explicit
foreign-key `MATCH` options are not represented by the EF relational target
model and therefore reject before DDL instead of being normalized.

## Transactional execution

Safe operations execute through EF runtime migration or a Migration Bundle.
A contiguous structural segment is analyzed in order before its first target
command. Operations accepted as `Apply` or `Repair` are passed together to the
official SQLite generator so related rebuild steps stay one provider-owned
unit. SafeMigrations executes ordinary structural units inside the caller
transaction or a local transaction that it commits only after every
postcondition succeeds.

For table rebuilds, EF emits foreign-key mode commands outside its transaction.
SafeMigrations therefore owns a transaction-suppressed boundary, records the
current mode, disables foreign-key enforcement before beginning its local
transaction, executes the official provider rewrite, and requires every final
postcondition to pass before commit. If foreign-key enforcement was originally
enabled, preflight rejects retained violations and the runtime validates the
affected relationship closure with `foreign_key_check` before commit. If it was
disabled, SafeMigrations preserves that mode and does not introduce a stronger
validation contract. The original mode is restored afterwards. This prevents
`DROP TABLE` from applying `CASCADE`, `SET NULL`, `SET DEFAULT`, `RESTRICT`, or
self-referential actions to data being preserved by the rebuild.

The rebuild transaction is atomic. EF writes the migration-history row after
the operation commands complete, outside this local transaction. A process
failure between those steps can therefore leave the rebuilt schema without its
history row. Safe operations reclassify the live state on replay, but operators
must inspect the live schema and history before retrying provider-owned work.

SQLite permits only one concurrent writer. EF Core also uses its SQLite
migration lock table. Preflight uses a deferred read transaction: it does not
reserve the writer slot immediately, but a long read can still delay a writer's
commit in rollback-journal mode. Run analysis and migration in a controlled
deployment window, configure an appropriate busy timeout, and consider WAL
mode only when it matches the application's durability and operational
contract. Microsoft.Data.Sqlite implements asynchronous ADO.NET calls
synchronously; a cancellation token is checked before provider work but cannot
interrupt native work already running.

## Rebuild ownership

A rebuild is accepted only when the target EF model and ordered operation stream
prove ownership of the complete table shape. SafeMigrations rejects before DDL
when it finds any of these unmodeled artifacts:

- columns, primary keys, unique constraints, checks, or foreign keys;
- expression, partial, or otherwise unmodeled indexes;
- physical constraint-name or key-collation drift and unmodeled SQLite
  constraint options;
- triggers or views that reference the table;
- virtual tables, `STRICT` tables, or `WITHOUT ROWID` tables; or
- target artifacts that the terminal model does not describe.

The boundary is intentionally conservative. SQLite's documented rebuild
procedure requires recreating every affected index, trigger, and view. Guessing
at an artifact outside the EF model could lose behavior while producing a
syntactically valid table.

Dropping a table with any unresolved external incoming foreign key is also
rejected, regardless of the current enforcement mode. SQLite performs an
implicit delete before `DROP TABLE` when enforcement is enabled, so accepting
the operation could otherwise cascade, null, default, or reject rows in another
table. The dependency check projects earlier table and foreign-key creates,
renames, replacements, and drops. A self-reference disappears with its table;
an external dependent foreign key or its table must be removed earlier in the
same ordered structural segment.

Table renames reject while `PRAGMA legacy_alter_table` is enabled. In that mode,
SQLite can leave trigger and view references on the old name, which cannot be
represented by a successful rename postcondition alone.

Virtual generated columns can be added directly when every expression and
facet is supported. SQLite cannot add a STORED generated column through
`ALTER TABLE ADD COLUMN`; that operation fails before DDL with
`stored_generated_column_add`. A model-owned full rebuild may retain a STORED
generated column already represented by the terminal model.

A check constraint that references a column newly added in the same structural
segment remains fail-closed on a populated table. The catalog cannot evaluate
the check against a column that has not been materialized yet, and the provider
operation does not carry a general proof of every resulting row value. Split
the column materialization and the check into ordered migrations, then analyze
the check against the resulting live data.

## Script boundary

SQLite has no procedural language that can express SafeMigrations' live catalog
branches inside a standalone SQL script. If a migration contains a safe
operation, normal or idempotent script generation throws before returning a
partial script. Use `dotnet ef database update`, `IMigrator`, or a qualified
Migration Bundle. Provider-only migrations retain the ordinary EF Core script
behavior.

## Model-managed data

Insert, update, and delete rows use bound ADO.NET parameters and SQLite null-safe
`IS` comparison. Commands are chunked below SQLite's parameter limit. The
runtime repeats source, target, uniqueness, and dependency proofs in the same
transaction as the mutation. Replaying the identical operation is a no-op;
drift and collisions remain fail-closed.

## Operational recovery

Do not retry a blocked operation by editing `sqlite_schema` or disabling
SafeMigrations. Inspect the report code and either correct the model, remove an
unintended migration operation, or author an explicit reviewed forward
migration. If a process dies while EF owns the migration lock, follow EF's
documented abandoned-lock recovery for `__EFMigrationsLock`. Preserve a tested
backup before any production schema migration. If the live schema matches a
rebuild but its migration-history row is absent, do not insert history manually;
first compare the complete migration contract, then use a reviewed replay or
forward recovery procedure.

## Primary sources

- [EF Core SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations),
  retrieved 2026-09-18.
- [EF Core SQLite provider](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/),
  retrieved 2026-09-18.
- [Microsoft.Data.Sqlite custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions),
  retrieved 2026-09-18.
- [EF Core applying migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying),
  retrieved 2026-09-18.
- [SQLite ALTER TABLE](https://www.sqlite.org/lang_altertable.html), retrieved
  2026-09-18.
- [SQLite generated columns](https://www.sqlite.org/gencol.html), retrieved
  2026-09-18.
- [SQLite transactions](https://www.sqlite.org/lang_transaction.html),
  retrieved 2026-09-18.
- [SQLite foreign keys](https://www.sqlite.org/foreignkeys.html), retrieved
  2026-09-18.
- [SQLite DROP TABLE](https://www.sqlite.org/lang_droptable.html), retrieved
  2026-09-18.
- [SQLite PRAGMA statements](https://www.sqlite.org/pragma.html), retrieved
  2026-09-18.
- [SQLite CREATE INDEX](https://www.sqlite.org/lang_createindex.html), retrieved
  2026-09-18.
- [SQLite identifier comparison](https://www.sqlite.org/c3ref/stricmp.html),
  retrieved 2026-09-18.
- [SQLite comments](https://www.sqlite.org/lang_comment.html), retrieved
  2026-09-19.
- [SQLite tokenizer requirements](https://www.sqlite.org/draft/tokenreq.html),
  retrieved 2026-09-19.
