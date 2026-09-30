# SQL Server behavior

The SQL Server adapter is a separate, unreleased package. The established
10.4.5 release contains only Core, MySQL/MariaDB, PostgreSQL, and SQLite.
Do not install the SQL Server package by assuming that it shares the 10.4.5
version. A later release must qualify and publish all five packages together.

## Supported engine boundary

The declared first-release matrix is SQL Server 2019, 2022, and 2025 on
the official EF Core 10 SQL Server provider. Each major has a separate,
digest-pinned Linux/x86-64 live test cell. Azure SQL Database, Azure SQL
Managed Instance, and Synapse are not qualified by those cells and are not
part of the declared support contract.

[Microsoft supports SQL Server Linux containers only on x86-64 hosts](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver17).
The Apple Silicon development host can run source and static tests, but an
emulated local container is not release evidence. The GitHub-hosted
`ubuntu-24.04` matrix is the intended live qualification environment.

## Catalog and safety boundary

SafeMigrations classifies the actual SQL Server catalog state before an
operation is accepted. Missing catalog metadata is not necessarily evidence
that an object is missing: SQL Server filters metadata by principal
permissions. The adapter must reject an unprovable state instead of treating
an invisible object as absent. See [Microsoft's metadata visibility contract](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration?view=sql-server-ver17).

SQL Server stores primary and unique constraints through backing indexes,
while default constraints are separate schema objects. Matching therefore
has to inspect physical identity, ordered key columns, index facets, trust
and enabled state, and affected dependencies; names alone are insufficient.
For the relevant catalog surfaces, see [key constraints](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-key-constraints-transact-sql?view=sql-server-ver17),
[indexes and index columns](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-index-columns-transact-sql?view=sql-server-ver17),
[foreign keys](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-foreign-keys-transact-sql?view=sql-server-ver17),
and [default constraints](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-default-constraints-transact-sql?view=sql-server-ver17).
The ordinary-table guard rejects graph node and edge tables as well as other
unsupported physical engines. Graph edge constraints are not ordinary foreign
keys: their delete rules can reject a node deletion or cascade into uncaptured
edge rows. See [Microsoft's graph edge-constraint contract](https://learn.microsoft.com/en-us/sql/relational-databases/tables/graph-edge-constraints?view=sql-server-ver17).
Scaffolded indexes with included non-key columns use the public
`CreateIndexWithIncludesIfNotExistsFromModel` or
`CreateCompositeIndexWithIncludesIfNotExistsFromModel` Core operation. Included
columns remain ordered in the immutable target definition; the adapter does
not silently drop them while classifying an existing index.

An unqualified table name is not automatically synonymous with `dbo`:
SQL Server first considers the caller's default schema, then `dbo`.
SafeMigrations accepts unqualified safe operations only when the caller's
default schema is `dbo`; otherwise it rejects them before mutation and
requires an explicit schema. This keeps the EF baseline and catalog contract
on the same physical object. See [Microsoft's schema-resolution rules](https://learn.microsoft.com/en-us/sql/relational-databases/security/authentication-access/ownership-and-user-schema-separation?view=sql-server-ver17).
Object-name comparisons also follow database collation rather than an
unconditional case-insensitive .NET comparer.

Authored identity columns must be non-NULL and default-free, with at most one
`IDENTITY` column per table. Supported types are `tinyint`, `smallint`, `int`,
`bigint`, `decimal(p,0)`, and `numeric(p,0)`. The captured annotation parser
accepts signed-64-bit seed and increment values; both must fit the destination
type, and the increment must be nonzero. Unsupported physical definitions and
multiple inline identities fail before DDL. See Microsoft's
[IDENTITY](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17).

An occupied live or accepted projected identity slot is
`PrerequisiteMissing`; an unproven ordered removal is not a free-slot proof.
Exact matching includes ordinary, not `NOT FOR REPLICATION`, identity
semantics. Seed and increment metadata use non-throwing `decimal(38,0)`
comparison rather than narrowing to `bigint`, so an existing wider decimal
seed can be classified as `Different` without an Int64 overflow.

Ordinary authored tables are limited to 1,024 columns and a proven fixed
resident layout of at most 8,060 bytes, including packed bits, the row header,
and null bitmap. Exceeding either limit is `Unsupported` with
`table_column_limit` or `table_fixed_row_limit`. Schema admission does not
indiscriminately sum declared variable payloads or variable offsets: eligible
values can use row overflow, and a maximum-row warning is not itself a CREATE
failure. This does not prove that every future row will fit. See Microsoft's
[capacity limits](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server)
and [row storage](https://learn.microsoft.com/en-us/sql/relational-databases/pages-and-extents-architecture-guide).

Missing-column admission also checks live and accepted ordered allocation
before row probes, policy, or DDL. Count, fixed-row, and unproven-layout
failures remain `Unsupported` with `column_limit`,
`column_fixed_row_limit`, or `column_layout_unproven`. Matching an existing
column does not allocate another slot. `DropColumn` does not prove that fixed
storage was reclaimed; ordered state retains that allocation, and preexisting
column-id gaps remain unproven. A proven table drop and fresh recreation can
reset the allocation lineage.

Materializing existing rows needs a separate conservative capacity bound,
including variable offsets, overflow roots, and resident variable clustered-key
widths. Microsoft specifically excludes `varchar` clustered-key data from
row overflow in its
[row storage](https://learn.microsoft.com/en-us/sql/relational-databases/pages-and-extents-architecture-guide).
An unproven populated-row bound is `DataBlocked` with
`column_row_layout_unproven`; a `TOP (1)` empty-table witness can discharge
it. Nullable variable additions with no default or a literal NULL default do
not require that backfill proof. Earlier data changes invalidate the row witness.
Row-bearing column plans require `SELECT`, even when a CASE arm would skip
the row probe for an existing column; without it they fail as `Unsupported`
before data binding. Column plans without row binding do not require that
`SELECT` proof.

Check and default constraints receive a physical provenance stamp after their
DDL. The normal EF migrator executes the DDL and stamp transactionally. A
directly executed, non-transactional SQL script can fail between those two
steps and leave an unstamped object. Replay then classifies that object as
`Different` and stops for manual recovery; matching its name or expression
alone is not sufficient proof that SafeMigrations owns it. Preserve the
catalog state and migration history before repairing such a partial run.

Model-managed inserts with explicit identity values temporarily enable
`IDENTITY_INSERT`. When the EF migrator encounters an error, cancellation, or
timeout, its command wrapper attempts cleanup on the same connection and
current transaction, independently of the cancelled operation's token. Failed
cleanup or cleanup-command disposal permanently quarantines the scoped
provider before attempting connection closure, with disposal as the fallback.
The original exception is retained with a sanitized recovery status; neither
closure nor disposal is assumed to succeed.

Every command returned by the enabled SafeMigrations generator checks that
quarantine before execution, including cached and ordinary-only commands.
Further generation and catalog analysis in that scope also reject reuse.
Discard the context and unrecovered connection; a new context over the same
uncertain session is not recovery. This scoped boundary does not intercept
arbitrary EF queries or separately executed SQL scripts. Script clients must
restore the session themselves or discard it after an attention or timeout.
[SQL Server `TRY/CATCH` does not handle client attentions](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/try-catch-transact-sql).

Asynchronous recovery uses one 30-second deadline for the returned cleanup
and command-disposal tasks, then 15 seconds for the returned close task and,
when needed, another 15 seconds for disposal. These waits cannot preempt a
synchronous provider override that blocks before returning its task, so they
are not an unconditional wall-clock bound. See the documented
[CloseAsync](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbconnection.closeasync?view=net-10.0#remarks)
and [DisposeAsync](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbconnection.disposeasync?view=net-10.0#remarks)
defaults.

Catalog analysis also owns a unique temporary identifier scope. Failed cleanup
preserves the original error with a sanitized recovery status and quarantines
the analyzer; it attempts connection closure or disposal without explicitly
rolling back a caller-owned transaction. Recreate that context and connection
before retrying, including when the caller originally owned the transaction.

String and binary model-managed values must fit their captured bounded store
types without truncation. Non-ASCII ANSI strings additionally require a
lossless code-page roundtrip and encoded-byte capacity proof against the
physical column collation. Decimal and temporal values use the captured
provider precision; successful replay compares their canonical stored values.

Column defaults must fit the supported scalar family, destination range and
length, and nullability contract; an out-of-range temporal or numeric literal
is `Unsupported` before DDL. SQL defaults must parse into supported typed
constants, single literal casts, provable `COALESCE` branches, or compatible
current-temporal values/functions. Known builtins such as `GETDATE` are not
general authorization for arbitrary functions or cross-family conversion.
Accepted defaults also receive provider conversion, byte-capacity, and ANSI
encoding guards before prerequisites, policy evaluation, and mutation.
Scalar conversions use non-throwing casts; large intrinsic same-family values
use `CONVERT` with independent capacity and encoding guards because
[`TRY_CAST` has large-input limits](https://learn.microsoft.com/en-us/sql/t-sql/functions/try-cast-transact-sql?view=sql-server-ver17#remarks).
An unproven expression or failed destination conversion remains
`Unsupported`; successful conversion can still use SQL Server's documented
rounding semantics rather than preserve every authored digit. See the
[precision/scale conversion rules](https://learn.microsoft.com/en-us/sql/t-sql/data-types/decimal-and-numeric-transact-sql?view=sql-server-ver17).

Required omitted seed columns need the shared provider-qualified non-NULL
default proof. Default presence alone, opaque or null-producing expressions,
and computed values without a row-bound proof do not establish that contract.

## Ordered key prerequisites

SQL Server uniqueness compares the complete key tuple, including `NULL`:
duplicate `(1, NULL)` tuples conflict, while `(1, NULL)` and `(2, NULL)` do
not. A newly added nullable column is therefore not a general uniqueness
proof. See [Microsoft's unique-index contract](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/create-unique-indexes?view=sql-server-ver17).

For an existing table, the adapter captures whether there are zero, one, or
at least two original rows using `TOP (2)`, not a whole-table count. Requested
column metadata is read in bounded groups of at most 512 values. Subject to
the other physical prerequisites, a unique key using a newly added nullable
column without a default is provable on zero or one original row. With at
least two original rows, an unfiltered single-column key is `DataBlocked`;
a composite key remains `PrerequisiteMissing` with
`projected_key_data_state_unknown`. A structured filter that excludes the
new `NULL` values, such as `IS NOT NULL`, can establish the filtered-index
proof instead. Earlier unproven data changes invalidate the original row
proof; an accepted table recreation uses the new table's ordered state.

Filtered indexes use a dedicated restricted grammar for structured predicates
and parsed text: `AND`-connected column-to-constant comparisons, `IS NULL` or
`IS NOT NULL`, and non-negated `IN` lists of non-NULL constants. General `OR`,
column-to-column comparisons, ordinary comparisons to `NULL`, and opaque
filters remain `Unsupported`. A general SQL renderer accepting an expression
does not authorize filtered-index DDL. See Microsoft's
[filtered-index syntax](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-index-transact-sql?view=sql-server-ver17).

Every predicate column also needs a supported physical scalar type; computed,
user-defined, CLR, spatial, and `hierarchyid` columns do not qualify. The
adapter proves constant-side conversion rather than accepting a conversion
of the column side. Missing projected predicate columns remain
`PrerequisiteMissing`; an invalid or unproven authored conversion is
`Unsupported` with `index_filter_value_unproven`. Catalog metadata and
constant guards run before duplicate-row checks; projected conversion proof
also precedes empty-table or added-NULL uniqueness promotion. Having no rows
does not waive the grammar, physical-column, or conversion prerequisites.
See Microsoft's [filtered-index limitations](https://learn.microsoft.com/en-us/sql/relational-databases/indexes/create-filtered-indexes?view=sql-server-ver17#limitations).

An unchanged table newly created earlier in the same analysis can also use an
authored-seed proof for a later primary key, unique constraint, or unique
index. This requires the exact accepted model-managed insert lineage,
matching captured destination types, and, for unfiltered candidates, seed
metadata declaring the same ordered candidate key. A supported structured
filter can establish the filtered candidate proof instead; its identifiers
must bind to the captured typed columns. SQL Server evaluates typed, collated
constant rows and checks both the seed identity and candidate key with
`GROUP BY`, so `NULL`, case, and precision-conversion collisions use provider
equality, not CLR equality. Identical rows from repeated Ensure operations
are counted once; their hashes only deduplicate exact authored rows and do
not prove uniqueness. See [SQL Server's grouping rules](https://learn.microsoft.com/en-us/sql/t-sql/queries/select-group-by-transact-sql?view=sql-server-ver17#remarks)
and [precision/scale conversion rules](https://learn.microsoft.com/en-us/sql/t-sql/data-types/decimal-and-numeric-transact-sql?view=sql-server-ver17).

Before accepting each insert in that newly created table lineage, the adapter
also checks the prospective rows against the seed identity and inline or
already accepted unique contracts, even without a later key operation.
An inline unique constraint therefore still uses SQL Server's `NULL`, case,
and precision equality; an authored CLR key declaration alone is not proof.
A proven conflict is `DataBlocked` with `projected_seed_row_collision`;
an unproven row contract is `PrerequisiteMissing` with
`projected_seed_row_data_unproven`.

Within one analysis, repeated requests for the same ordered candidate and
represented filter at the same immutable seed prefix reuse the provider
result, including collision and unproven outcomes. A changed prefix,
candidate, filter, or table lineage requires fresh proof. Each lineage shares
one immutable seed-reference buffer, with proofs retaining only prefix
counts. Retained seed references therefore grow linearly with the seed
batches; this is not a linear-runtime guarantee or a cross-analysis cache.

Ordinary provider operations or raw SQL discard that lineage, as do affected
model-managed updates/deletes and table/column mutations. Recreation starts
a new lineage; a refused insert cannot contribute to a later proof. The
constant-row query does not read destination rows and must fit the shared
4 MiB UTF-8 payload limit. Unsupported types, conversions, collations, or
oversized payloads remain `PrerequisiteMissing`; a proven collision is
`DataBlocked`. Key-width, primary-key nullability, physical-table, and unknown
structure guards still apply before this proof can be accepted.

## Foreign-key action prerequisites

Foreign keys have a separate physical limit of 32 columns and 900 bytes;
the 1,700-byte allowance for a nonclustered unique index does not increase
that limit. The adapter requires a proven bounded declared width on both
key sides, not merely short values in the current rows. Unbounded or
unproven storage is rejected. The same arity and width proof applies to
inline foreign keys, including self-references and an accepted newly created
parent; ordered prerequisite restoration cannot waive it. Immutable authored
arity/width failures are `Unsupported`; an oversized live column shape remains
`PrerequisiteMissing` unless earlier accepted operations establish compatible
storage. See [Microsoft's capacity specifications](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server?view=sql-server-ver17).

Single-path `CASCADE`, `SET NULL`, and `SET DEFAULT` actions are supported
when storage, candidate-key, and orphan-data prerequisites are proven.
The adapter checks both `ON DELETE` and `ON UPDATE` paths and rejects cycles
or repeated paths to a table, including paths involving `SET NULL` or
`SET DEFAULT`. Accepted schema, table, and foreign-key changes update this
ordered dependency state; opaque SQL makes later dependency proof unknown.
This is a conservative proof boundary, not a claim that every topology
accepted by the engine has been qualified. See [SQL Server error 1785](https://learn.microsoft.com/en-us/sql/relational-databases/errors-events/mssqlserver-1785-database-engine-error?view=sql-server-ver17).

On the dependent table, `ON DELETE CASCADE` conflicts with an
`INSTEAD OF DELETE` trigger. `ON UPDATE CASCADE` and `SET NULL` or
`SET DEFAULT` on either parent action conflict with an `INSTEAD OF UPDATE`
trigger: deleting the parent can still update the dependent row.
See [Microsoft's cascading-action and trigger rules](https://learn.microsoft.com/en-us/sql/relational-databases/tables/primary-and-foreign-key-constraints?view=sql-server-ver17#cascading-referential-integrity).

These actions are rejected when `rowversion` (`timestamp`) participates in
the foreign key or referenced key, not merely because an unrelated column
uses that type. See [the foreign-key storage restrictions](https://learn.microsoft.com/en-us/sql/t-sql/statements/create-table-transact-sql?view=sql-server-ver17#foreign-key-constraints)
and [the type synonym](https://learn.microsoft.com/en-us/sql/t-sql/data-types/rowversion-transact-sql?view=sql-server-ver17).
`SET NULL` requires nullable dependent columns. `SET DEFAULT` requires an
explicit default on each required dependent column; a nullable column without
one has an implicit `NULL` default. Default availability does not waive the
other foreign-key prerequisites. See [Microsoft's constraint action rules](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-table-table-constraint-transact-sql?view=sql-server-ver17).

## Tooling and deployment evidence

The SQL Server package's design-time build assets register SafeMigrations
for EF CLI scaffolding. The package-only consumer verifies that both direct
`Microsoft.EntityFrameworkCore.Design` and EF Tools references produce safe
migration source; without design assets, scaffolding must fail closed.

The release gate scaffolds Strict and LegacyConvergence migrations separately,
generates normal and idempotent SQL scripts for both, applies each migration
twice, builds a migration bundle for each context, applies each bundle twice
to another database, and checks both model-managed rows and migration history.
The three engine cells, package-content checks, locked restore, merged
coverage, allocation budgets, SBOM, and public NuGet readback are all
blocking. A successful build or a skipped ARM64 live test is not a substitute
for this evidence.
