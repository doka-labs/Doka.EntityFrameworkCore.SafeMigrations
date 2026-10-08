# Implementation design

## Architectural objective

SafeMigrations turns an ordered EF Core migration into a deterministic
convergence contract. Provider-neutral code owns intent, policy, expected
definitions, planning, fingerprints, and reports. Provider packages own live
catalog interpretation and SQL generation. The database remains authoritative
for observed state; no provider tries to reconstruct history from names or
SQL text.

```text
MigrationBuilder extension
  -> sealed SafeMigrationOperation
     -> sealed typed SafeMigrationIntent
        + immutable Expected Definition
        + SafeMigrationPolicy
  -> provider live-state classifier
  -> pure SafeMigrationDecisionPlanner
  -> provider command plan or classified rejection
  -> read-only postcondition verification
```

Core has no compile-time dependency on MySQL, MariaDB, PostgreSQL, SQLite,
SQL Server, Doka's provider, Npgsql, or a provider-specific EF package.

Source ownership follows the hybrid vertical-slice contract in
[Vertical-slice architecture](vertical-slice-architecture.md). Public
namespaces and package boundaries remain stable; core and provider behavior is
co-located by `Schemas`, `Tables`, `Columns`, `Indexes`, and the four constraint
families. Shared lifecycle orchestration remains centralized.

## Package boundaries

### Core

`Doka.EntityFrameworkCore.SafeMigrations` owns:

- the exact `SafeMigrationOperation` envelope;
- the closed set of 20 intent kinds;
- immutable table, column, index, and constraint definitions;
- the closed typed SQL-expression tree and opaque-expression provenance;
- `SafeMigrationPolicy` and `SafeMigrationTableMode`;
- the total, I/O-free `SafeMigrationDecisionPlanner`;
- `ISafeMigrationRunner` and the report contract;
- model and ordered-operation SHA-256 fingerprints;
- unexpected-object inventory;
- reflection-free report JSON and its packaged JSON Schema;
- bounded diagnostic names and low-cardinality telemetry.

It does not generate provider SQL and does not register a relational provider.

### MySQL and MariaDB

`Doka.EntityFrameworkCore.SafeMigrations.MySql` registers exactly one
`IMySqlMigrationOperationHandler` for the exact `SafeMigrationOperation` type.
Doka's public SPI performs constant-time exact-type dispatch and remains owner
of the provider migrations generator. SafeMigrations does not derive from,
replace, reflect over, or copy Doka provider internals.

Registration declares the required user-variable capability through Doka
10.3.0. Doka normalizes only an omitted option in its provider-owned connection
string and validates caller-owned connections or data sources without
mutation. Its matched-row and Binary16 wire-transport invariants remain
provider-owned. SafeMigrations then validates the actual runtime connection
again before migration-history or catalog access.

The handler:

1. validates the actual context connection before migration-history or catalog access;
2. validates the exact envelope and active engine features;
3. renders provider-owned baseline DDL through
   `MySqlMigrationOperationContext.RenderStandardOperation`;
4. creates one typed catalog/runtime plan from the real Doka handler context;
5. records that plan through a scoped capture lease during read-only analysis;
6. creates session-local assertion commands for runtime execution;
7. evaluates data-reading state only after catalog prerequisites pass;
8. executes target DDL only after state and repair preconditions pass;
9. clears session variables and temporary state on every successful plan.

No permanent helper object or stored routine is created. Prepared statements
contain only DDL rendered from typed EF operations and the lazy state query
derived from the typed plan. The analyzer renders the same plan directly with
`DbParameter` values; it never parses generated migration commands or
duplicates Doka's engine-feature profile.

### PostgreSQL

`Doka.EntityFrameworkCore.SafeMigrations.PostgreSql` decorates Npgsql's public
migrations generator boundary. It intercepts only `SafeMigrationOperation` and
delegates every ordinary EF operation to the provider generator. Safe commands
use parameter-free migration SQL
because EF migration scripts have no runtime parameter channel; all identifiers
and literals are rendered by Npgsql/EF SQL helpers and type mappings.

The read-only PostgreSQL analyzer builds parameterized `pg_catalog` queries
directly. Guarded runtime execution uses PostgreSQL anonymous blocks and normal
EF transaction semantics.

### SQLite

`Doka.EntityFrameworkCore.SafeMigrations.Sqlite` composes the official EF Core
SQLite migrations generator. It delegates every ordinary operation unchanged
and wraps only `SafeMigrationOperation`. Runtime catalog classification
reads `sqlite_schema` and provider PRAGMAs from the active connection; `main`
and an omitted qualifier share one identity while attached databases reject.

SQLite structural operations that the engine cannot alter directly are
analyzed as one contiguous batch. Once every operation is accepted, the
official provider generates the corresponding rebuild sequence. SafeMigrations
disables foreign-key enforcement outside one local transaction, executes the
rebuild in that transaction, and verifies effective final postconditions before
commit. When enforcement was originally enabled, the affected relationship
closure must also pass `foreign_key_check`; a disabled original mode is
preserved without adding that validation contract. The original enforcement
mode is restored afterwards. A rebuild requires complete
model ownership and rejects unknown triggers, views, virtual tables, expression
or partial indexes, constraints, `STRICT`, and `WITHOUT ROWID`.

Safe operations cannot be rendered into a standalone SQLite script because the
engine has no procedural branch that can evaluate their catalog-dependent
decision. Script generation therefore rejects before returning partial output.
Runtime migration and Migration Bundles execute the guarded command objects.

### SQL Server

`Doka.EntityFrameworkCore.SafeMigrations.SqlServer` composes the official EF
Core SQL Server migrations generator. Ordinary EF operations remain provider
owned; only the exact SafeMigrations envelope is classified and guarded.
The adapter reads bounded `sys.*` catalog evidence and emits T-SQL checks
immediately around its target operation. SQL Server's metadata-visibility
rules make absent rows ambiguous without adequate catalog permissions, so
unprovable visibility fails closed. Primary and unique constraints share
backing-index identity, whereas defaults are separate schema objects.

Unqualified safe-operation names require the caller's default schema to be
`dbo`; callers with another default schema must qualify the schema explicitly.
This prevents a catalog/EF-baseline identity split. The first SQL Server
release requires independent Linux/x86-64 qualification of 2019, 2022, and
2025. Azure SQL services and
Synapse are outside this first release's declared support matrix. See
[SQL Server behavior](sqlserver-behavior.md).

## Fail-closed ownership

A safe operation is never encoded as an annotation on an ordinary EF
operation. Without the matching adapter, the provider cannot silently execute
the operation as normal DDL:

- Doka rejects an unowned `SafeMigrationOperation`;
- Npgsql rejects the unknown safe envelope when its adapter is absent;
  incompatible SafeMigrations generator registration also fails closed;
- SQLite rejects the unknown safe envelope when its adapter is absent;
  incompatible generator registration also fails closed;
- SQL Server rejects the unknown safe envelope when its adapter is absent;
  incompatible generator registration also fails closed;
- multiple owners for the same exact operation type are rejected;
- scaffolding stops before publishing source for an operation SafeMigrations
  cannot model; and
- provider-owned ordinary operations continue through the base provider.

Integration tests prove that missing and conflicting registration writes
neither target DDL nor the EF history row.

## Expected definitions

Definitions snapshot enumerable input exactly once and expose read-only
collections. The column contract distinguishes:

- CLR type and explicit store type;
- nullability, Unicode, maximum length, fixed length, and row-version facets;
- precision and scale;
- structured collation identity and comment;
- no default, literal default including literal `null`, and SQL default;
- computed expression and stored/virtual form.

Indexes contain ordered key definitions with direction plus provider facets for
filter, included columns, operator classes, collations, null ordering,
null-distinctness, and MySQL prefix lengths. Constraints retain ordered columns,
principal identity, referential actions, and check SQL.

SQL-bearing facets use a typed expression tree whose identifier, literal,
operator, cast, collation, function, and current-value roles are explicit.
Providers render these nodes and compare only catalog representations whose
structural equivalence they can prove. Legacy raw SQL is opaque and returns a
stable `Unsupported` reason; it never authorizes `Matching`. Core renames only
typed identifier nodes. Opaque dependent SQL becomes unproven after a rename
and remains fail-closed.

Comparison reads structured catalog metadata. It does not globally lowercase
or strip whitespace from expressions because doing so can change quoted
literals. A null column collation means the provider-inferred effective
default and is compared exactly; it is never a wildcard.

`SafeMigrationCollationIdentifier` carries schema and name as separate ordinal
fields, so a dot inside either identifier is data rather than a parser
separator. PostgreSQL resolves the identity to one catalog OID. MySQL and
MariaDB support only an unqualified collation name and classify a qualified
identity as unsupported before target DDL.

MySQL 8.4 and 9.7 expose typed check and generated-column expressions through
`INFORMATION_SCHEMA` in canonical and parser-display forms. The latter escapes
identifier punctuation and can expose non-ASCII identifier bytes as Latin-1
code points. The MySQL adapter therefore adds that exact, token-bounded display
form only for expressions rendered from the closed typed tree. MariaDB uses the
canonical form. Opaque SQL never enters this compatibility path, and both
engine families retain negative value-, operator-, and identifier-drift tests.

## Table modes and convergence

EF Core's design-time service pipeline supplies the provider model differ and
C# migration generator. SafeMigrations decorates both public contracts. The C#
generator delegates provider rendering for table and index operations,
validates the expected generated call shape, and substitutes reviewed safe
calls. Standalone constraints are rendered from their complete validated
operation values; annotations without an immutable constraint contract reject.
Batch analysis projects those operations into a transition envelope used by
strict replay and postflight. The envelope allows initial, intermediate, and
terminal constraint definitions, requires only definitions that remain present
throughout the ordered stream, and still rejects every unknown shape. The
execution postcondition attached to the initial table operation remains its
baseline contract, because later ordered constraints do not exist when that
assertion runs.
Column drops form an explicit dependency boundary inside the same catalog.
Known keys, checks, foreign keys, and indexes must be removed by preceding
safe operations; structured expressions prove when checks, functional keys,
and filters are unrelated, while opaque expressions reject. This prevents a
provider's implicit constraint removal or composite-index narrowing from
creating an unmodeled terminal state.
The MySQL/MariaDB adapter decorates the provider SQL generator only to scope
this immutable transition catalog across Doka's per-operation handler calls;
the provider still renders every command. PostgreSQL receives the complete
operation list directly in its composed generator.
This preserves provider-owned rendering where it is representable without
forking EF Core's generator. An unexpected upstream output shape stops
scaffolding instead of producing ambiguous source.

Provider package `buildTransitive` assets add EF's
`DesignTimeServicesReferenceAttribute` to a consuming assembly that directly
references the EF Design package or the EF Tools package that supplies Design
transitively. A project with neither package is intentionally treated as
runtime-only and receives no design-service attribute or warning. Runtime
service-provider identity excludes scaffolding mode and legacy policy because
both change generated source only and do not alter runtime service registration.
The selected values are read from that context's options by the design-time
service provider and become literal calls and arguments in the generated
migration.

An MSBuild `ProjectReference` to the provider source does not import the NuGet
package's `buildTransitive` assets. Source-development generators therefore use
EF's explicit `DesignTimeServicesReferenceAttribute` in their startup assembly.
The referenced type remains provider-owned; the generator does not implement or
replace SafeMigrations design-time services. Package and source-reference split
fixtures qualify both compositions.

EF Core reads referenced design-time-service attributes from both the migration
target and startup assemblies. SafeMigrations does not infer which assembly owns
that reference at runtime. Instead, its runtime-registered model-differ decorator
prepends one internal guard to every non-empty model difference while scaffolding
is enabled. Correctly composed design-time services validate and remove the guard
before delegating to the provider generator. Without them, EF Core dispatches the
unknown operation to its ordinary generator and fails before source is saved.
Nested model-differ decoration preserves a single guard, and empty differences
remain empty. EF Core also calls the runtime differ for migration-history DDL.
The MySQL/MariaDB handler returns Doka's explicit commandless consumed result,
while the PostgreSQL adapter removes the marker before Npgsql delegation.
Neither path generates marker SQL or changes the history schema, and both
reject a misplaced marker.

The deferred generator selector preserves EF Core's legacy and case-insensitive
last-match rules. Disabled SafeMigrations scaffolding delegates non-C# provider
generators unchanged; enabled scaffolding accepts only C# because its bounded
rewriter understands only C# source. Source edits preserve the provider's LF or
CRLF convention and reject mixed line endings before returning generated code.

Core accepts zero or one provider-owned create-index projector through an
internal design-time interface. The MySQL/MariaDB package registers the single
projector and reads Doka 10.4.0's typed migration-operation metadata. It
removes the consumed provider annotation from a copied EF operation and emits
the ordered prefix values as an explicit SafeMigrations argument. Zero means a
complete key. Multiple projectors, unrecognized operation metadata, malformed
prefix counts, or negative values stop scaffolding. PostgreSQL registers no
projector and retains the ordinary generated index calls.

`Strict` rewrites schema ensure/drop; table create/drop/rename; column
add/alter/drop/rename; index create/drop/rename; standalone primary-key, unique,
check, and foreign-key adds and drops; and model-managed data produced from
`HasData`. Add and alter columns are captured through one generated callback so
the sealed definition retains every provider annotation. Constraint adds freeze
the existing `ThrowIfDifferent` contract; drops make only absence idempotent.
Operations with unrepresentable annotations, implicit foreign-key principal
columns, or opaque check SQL stop scaffolding. `LegacyConvergence` rewrites the
same forward operations but replaces `Down` with a deterministic exception:
adopted legacy objects have no provable destructive inverse. The inverse model
difference is still verified before that replacement because safe forward
updates and deletes require captured source values. The generator renders the
complete body into scratch output before publishing it. Raw SQL, raw data
operations, `AlterTable`, sequences, and every unknown shape therefore reject
without returning a partially safe migration. EF Core's internally generated
history setup is outside the scaffolded migration body. Existing
migration-authored SQL remains provider-owned at runtime.

The model-differ decorator first delegates to the active Doka or Npgsql differ,
then completes data-operation store types from public source and target
`IRelationalModel` metadata. It snapshots only candidate-key and incoming
foreign-key maps required by the changed rows. Provider-supplied and relational
store types must agree; missing, contradictory, or unrepresentable metadata
stops scaffolding.

When `ExcludeModelManagedDataForExcludedTables()` is explicitly enabled, the
decorator first applies an ownership filter to the provider-generated
differences. It indexes source and target tables by exact ordinal schema/name
identity and scans operations once. Insert uses target exclusion, delete uses
source exclusion, and update requires both sides to be excluded. Missing table
metadata or changing exclusion state fails closed. The original operation list
is returned when nothing is removed; otherwise retained operation instances and
their order are preserved without copying row matrices. `HasDifferences` uses
this same filtered set, so pending-model detection and scaffolding cannot
disagree. The [ownership guide](model-managed-data-ownership.md) owns the
consumer architecture.

Before rendering, the migration generator pairs forward and inverse data rows
by schema, table, ordered key columns, and canonical typed key values. An insert
pairs with its inverse delete to prove key identity, an update pairs with its
inverse update to capture old values, and a delete pairs with its inverse insert
to capture the complete removed row. Pairing is exactly one-to-one and rejects
missing, duplicate, ambiguous, contradictory, or unused inverse rows. Verified
rows retain EF order and are partitioned at 128 rows or 4,096 value cells,
whichever bound is reached first. No accepted scaffolding run falls back to raw
data operations.

When the immutable expected definition is captured from an EF column
operation, SafeMigrations parses SQL defaults through its bounded expression
grammar. A proven expression such as `CURRENT_TIMESTAMP(6)` receives typed
catalog equivalence; a fragment outside that grammar remains opaque and fails
closed. This preserves the reviewed EF operation while avoiding a blanket
allowlist of provider SQL strings.

The generated table call also freezes either `ThrowIfDifferent` or
`RepairIfSafe`. Repair-capable `EnsureColumnIntent` analysis separates mutable
nullability, default, and comment facets from provider-proven type transitions
and invariant collation, generation/identity, row-version, metadata, and
dependency facets. Provider-neutral Core permits no annotation-bearing inferred
repair. MySQL/MariaDB can authorize repair only after Doka's typed metadata
recognizes every annotation and proves the complete column shape.

Both adapters independently recognize ordinary `VARCHAR` widening and
data-verified narrowing. MySQL/MariaDB extends the same nonbinary string proof
to `VARCHAR` into a text family, widening between text families, and a
live-data-verified text-to-`VARCHAR(n)` transition. A target text type must
contain the complete declared source byte domain; ordinary dependent indexes
must already use a valid prefix. Widening needs catalog and dependency proof
but no row scan. Narrowing candidates are deduplicated and grouped by table
into bounded character-length probes. The result is one Boolean fact per
candidate and no row value. The execution guard repeats that proof before DDL;
provider strict conversion and the complete postcondition close the remaining
race. One overlength value becomes `DataBlocked`. MySQL/MariaDB additionally
recognize only the exact CLR-Boolean `BIT(1) -> TINYINT(1)` value-domain
expansion. PostgreSQL does not reuse those provider-specific proofs.
The Boolean proof accepts only the absent, null, false, and true literal
default forms understood by the catalog contract. It rejects expression
defaults and any foreign-key dependency because changing one side cannot prove
or atomically apply the required coupled type transition. Ordinary indexes,
primary keys, unique constraints, and checks remain provider-validated parts
of the complete replacement definition; SafeMigrations never drops or rewrites
them implicitly.

Tightening nullability performs its own catalog and data precondition and
classifies existing nulls as `DataBlocked`. MySQL/MariaDB delegates the complete
replacement definition to Doka's `AlterColumnOperation` renderer; PostgreSQL
delegates the complete applicable transition to Npgsql. Apply and repair SQL
are distinct guarded branches and share the same postcondition. Both adapters
first prove target-column existence from the catalog before compiling or
executing a data-reading probe. A missing target therefore remains `Missing` or
`DataBlocked` according to add safety and never fails with an engine-level
unknown-column error. Accepted length and Boolean repairs report
`TableRewritePossible`; losslessness is separate from availability. Explicit
safe nullability, default, or comment repairs report `Unknown` when the provider
cannot prove a narrower execution shape; `NotApplicable` is reserved for
assessments that do not plan repair DDL. Explicit
`AlterColumnIntent` repairs continue to execute their reviewed provider
baseline; only inferred `EnsureColumnIntent` repair needs a separately rendered
branch.

Explicit alterations reuse the provider transition kernel but add exact old
definition matching before physical eligibility or data probes. Eligibility is
operation-specific; a physical column/length row fact may be shared only after
each operation passes its own source contract. Core retains source identity,
accepted intermediate column definitions, and row-proof freshness. It does not
relax the provider-neutral same-type helper into a general conversion rule.
MySQL/MariaDB capture candidate evidence against an existing table's original
physical identity for later alterations and indexes after a rename. A candidate
is consumed only after Core accepts that rename; an occupied target, intervening
unproven mutation, or incompatible source cannot create an alias proof.
Provider-specific validation checks cumulative declared-row, InnoDB in-page row,
and key limits against the accepted intermediate shape. Physical keys survive
certified repairs; an unproven mutation cannot silently erase their budget.
New-table emptiness replaces a row scan only,
never a storage or dependency proof. Backfill-capable repairs and applied
model-managed data operations invalidate cross-table row evidence when their
effects cannot be confined to one table. An insert into a fully projected new
plain table preserves unrelated proofs: there can be no pre-existing table
trigger, and inserts do not execute update/delete cascades. The current table
definition must contain no SQL default, computed expression, check constraint,
functional or filtered index, explicit index method or operator class, or
unknown physical structure. SQL expressions are not assumed pure: PostgreSQL
[defaults execute when inserted](https://www.postgresql.org/docs/18/ddl-default.html)
and [functions can modify data](https://www.postgresql.org/docs/18/xfunc-volatility.html).
The check walks only the target table's columns and indexes without copying its
definition or scanning unrelated tables. It is re-evaluated after structural
changes. Existing tables, raw provider DML, updates and deletes retain global
invalidation. The written table still loses its generic empty-row proof; that
local marker follows a rename and resets only for a new physical lifetime.
Exact model-managed row tracking is separate from the lifetime of a captured
live-data proof.
After an accepted lossless expansion, a later alteration may use the original
declared value domain only when provider evidence certifies the entire intervening
column history and that domain fits the final target. A stale live `Matching`
result alone is not a conversion proof. Recreated tables use creation defaults
rather than the removed table's physical environment. A database-default change
invalidates inherited charset evidence for subsequently created tables; it does
not alter the charset already inherited by an earlier table or invalidate an
independently resolved explicit column collation.

SQLite's existing validated rebuild path can retain an exact same-shape column
repair after unrelated safe column drops. This requires a provider certificate,
an untouched source column, drop-only table history, and current row evidence.
Other structural changes, opaque operations, recreated tables, and stale proofs
cannot use that exception; it does not make the immutable snapshot sequence-aware.

Missing MySQL/MariaDB BTREE indexes have an additional physical-achievability
guard. The catalog plan calculates conservative maximum key bytes from the
live InnoDB row format and page size, the referenced column metadata, and the
explicit key prefixes. Unknown-width or non-BTREE creation shapes reject
before baseline DDL. Existing provider-supported indexes remain comparable,
because comparison does not imply that SafeMigrations can reproduce an
unmodeled creation shape.

Constraint and index ensure operations use semantic identity with exact-name
precedence. If the expected name exists, its complete definition must match;
another equivalent alias cannot hide drift under that name. If the expected
name is absent, any differently named active object with the complete modeled
shape satisfies the ensure as `Matching`. Different-name/different-shape
objects remain independent and do not suppress safe creation. The comparison
covers ordered keys, principal identity, referential actions, expressions,
index method, uniqueness, ordering, prefixes, filters, included columns, null
semantics, and supported visibility facets. Multiple equivalent aliases are a
deterministic no-op. Drop and rename retain exact physical-name identity.

Semantic identity and physical achievability are separate checks. A
differently shaped primary key conflicts with PostgreSQL's one-primary-key
table invariant. PostgreSQL indexes, index rename targets, and primary or
unique constraint backing indexes require a free name in the schema relation
namespace. MySQL CHECK and foreign-key symbols are schema-wide. MariaDB
foreign-key symbols are database-wide before 12.1 and table-scoped from 12.1.
These collisions classify `Different` after local semantic-alias matching and
before data probes, so a valid legacy alias remains a no-op while impossible
DDL fails with the normal SafeMigrations guard.

InnoDB can create a supporting index for a foreign key and later replace it
with another suitable index. SafeMigrations therefore compares the complete
physical index shape rather than inferring ownership from its name or from the
foreign key that currently uses it. An equivalent supporting index satisfies
an ensure; a different key order, prefix, method, uniqueness, sort direction,
or visibility remains a distinct object.

MySQL checks must be enforced; MariaDB check catalog rows are correlated by
table as well as constraint name. MySQL invisible and MariaDB ignored indexes
are not equivalent to the visible indexes emitted by ordinary EF operations.
For MariaDB, Doka's `json` operation is compared against its complete physical
alias: `longtext`, `utf8mb4_bin`, and the inline column `JSON_VALID` check. That
exact provider-generated check is excluded from the separately owned table
check count and unexpected-object inventory; additional user checks remain
visible.
PostgreSQL indexes must be valid, ready, live, and independently owned for an
exact match. A healthy partitioned parent index remains comparable, while an
attached child or constraint-owned backing index is rejected for independent
ensure, drop, and rename operations.

PostgreSQL constraint equality also includes every catalog facet that changes
the behavior expressible by an ordinary EF migration operation. Primary and
unique constraints must be validated, immediate, non-deferrable,
non-temporal, and enforced. Unique constraints retain default `NULLS DISTINCT`
semantics. Foreign keys additionally require `MATCH SIMPLE` and an all-column
`SET NULL` or `SET DEFAULT` action. Checks must be validated, enforced, and
inheritable. Inherited and partition-derived constraints cannot satisfy a
local ensure or drop. Catalog fields added after PostgreSQL 14 are read through
a JSON projection of the catalog row, so older supported majors remain query
compatible while newer unmodeled semantics reject fail-closed.

| EF operation | `Strict` source | `LegacyConvergence` source |
| --- | --- | --- |
| `CreateTable` | `CreateTableIfNotExists` | `ConvergeTableFromModel` |
| Single-column `CreateIndex` | `CreateIndexIfNotExistsFromModel`, or prefix-aware MySQL/MariaDB counterpart | Same |
| Multi-column `CreateIndex` | `CreateCompositeIndexIfNotExistsFromModel`, or prefix-aware MySQL/MariaDB counterpart | Same |
| `DropIndex` | `DropIndexIfExists` | Same |
| Standalone PK, unique, check, or FK add | Corresponding `*IfNotExists` method with `ThrowIfDifferent` | Same |
| Standalone PK, unique, check, or FK drop | Corresponding `*IfExists` method | Same |
| Generated rollback of `CreateTable` | `DropTableIfExists` | Entire `Down` body rejects before DDL |

The `*FromModel` methods are stable public targets for generated migration
source. They capture EF's provider-rendered operation into immutable expected
definitions; they are not a second runtime discovery layer.

The typed table callback creates EF operations in memory and immediately
converts them to immutable expected definitions. Provider column annotations
are snapshotted, fingerprinted, restored to baseline DDL, and compared through
the provider catalog. Unsupported annotation value types fail during capture;
unmodeled operation annotations classify unsupported before DDL.

`StrictDefinition` compares the complete owned table shape: ordered columns,
primary key, unique constraints, checks, and foreign keys. Unexpected owned
members reject the strict operation. MySQL and MariaDB expose a unique index in
both `STATISTICS` and `TABLE_CONSTRAINTS`; full-batch analysis therefore admits
only unique-index names present in the same expected operation catalog. Normal
EF runtime generation receives operations one at a time, so it derives the
same bounded name set from EF's target relational model and caches that
projection for the scoped handler. Generation without either evidence source
remains fail-closed. This normalization does not admit an unrelated unique key.
The behavior follows the
official [MySQL `TABLE_CONSTRAINTS` contract](https://dev.mysql.com/doc/refman/8.4/en/information-schema-table-constraints-table.html)
and [MariaDB `TABLE_CONSTRAINTS` contract](https://mariadb.com/docs/server/reference/system-tables/information-schema/information-schema-tables/information-schema-table_constraints-table).
MariaDB documents `JSON` as an alias for `LONGTEXT COLLATE utf8mb4_bin` with an
automatic `JSON_VALID` check; SafeMigrations binds that exception only to an
expected `json` column and the exact provider expression
([MariaDB JSON data type](https://mariadb.com/docs/server/reference/data-types/string-data-types/json),
retrieved 2026-09-01).

`ConvergenceContainer` checks only that the target name denotes a table. It is
used by `ConvergeTable`, which immediately emits granular operations for every
required child object using the supplied policy (`ThrowIfDifferent` by
default). Choosing `ExistenceOnly` explicitly relaxes child-definition checks;
an existing table never skips emitting the child operations. This prevents a
copied empty table from hiding missing columns while preserving unknown extra
objects.

## State and policy

Each provider must classify exactly one state:

| State | Meaning |
| --- | --- |
| `Missing` | The operation target or source does not exist. |
| `Matching` | The relevant live definition satisfies the expected contract. |
| `Different` | The target name exists but the definition or rename target conflicts. |
| `Unsupported` | The active engine cannot represent the requested feature. |
| `DataBlocked` | Existing rows violate a required transition precondition. |
| `PrerequisiteMissing` | A required table or referenced column does not exist, so dependent state cannot be evaluated safely. |
| `TransitionReady` | A captured model-managed source row is present and a guarded compare-and-swap transition may be attempted. |

The pure planner maps operation kind, state, policy, and repair capability to
one action. It is total over all defined enum combinations and performs no
allocation-backed discovery, SQL generation, service lookup, or I/O.

Repair is an allowlist, not a general reconciliation algorithm. Missing
nullable/default/computed columns and additive indexes or constraints can be
safe after data preconditions. Alter-column repair requires the live column to
match the declared old definition and permits only the implementation's
lossless metadata/default transition. Type narrowing, collation changes,
renames, primary-key reconstruction, and violated constraints reject.
Model-managed data never uses repair policy. Ensure, update, and delete have a
fixed `ThrowIfDifferent` contract. A transition-ready update/delete is an
explicit source-frozen migration step, not permission to overwrite arbitrary
live drift.

## Model-managed data convergence

The `ModelManagedData` slice owns three provider-neutral intents. Ensure freezes
the complete target rows and inserts only absent primary keys. Update freezes
keys, old managed values, and target values. Delete freezes keys, complete old
rows, and incoming source-model dependency maps. Values retain canonical type
identity in fingerprints but are excluded from assessment text, telemetry, and
exception messages.

Provider classification uses typed parameters and null-safe equality: `<=>` for
ordinary MySQL/MariaDB values and `IS NOT DISTINCT FROM` on PostgreSQL. Native
MySQL JSON casts the expected value to JSON before `<=>`; MariaDB JSON combines
explicit SQL-NULL branches with `JSON_EQUALS` because its JSON type is LONGTEXT;
PostgreSQL casts `json` and `jsonb` operands to `jsonb` before comparison. This
preserves document equality without treating JSON-like text as JSON or relying
on the equality operator absent from PostgreSQL `json`. The classifier
distinguishes absent, target-matching, source-matching, drifted, unique/check
blocked, dependent, and missing-prerequisite rows without interpolating values
into catalog SQL. MySQL and MariaDB additionally require a transactional table
engine.

Execution deliberately avoids generic upsert syntax. Ensure uses conditional
plain inserts. Update and delete repeat the captured source predicate in their
target DML, then verify the target state in the same provider-owned migration
command scope. A concurrent source change therefore cannot become an overwrite;
it causes a failed postcondition. Delete also proves that incoming rows will not
be cascaded, nulled, defaulted, or otherwise changed implicitly. EF's normal
migration transaction owns rollback where the provider supports it; the
handler does not create a nested transaction.

Ordered preflight projection retains only model-managed identities touched by
the migration. Accepted ensures record targets, updates replace source with
target candidate-key identities, and deletes record absence. An unconfined write
invalidates all earlier exact rows and candidate-key evidence before recording
only its own guarded keys and target columns. Original dependency counts cannot
prove a child/parent delete handoff after triggers may have changed either table.
Later seed operations without fresh proof therefore use `ValidateAtRuntime`
and `RuntimeValidationRequired`, attributed to the preceding invalidation.
Their existing runtime guards decide against the actual ordered state; an
untouched conflict still blocks. Deferred writes also invalidate earlier proofs
without claiming postconditions. Provider DML and data-changing repairs share
this boundary. This accounts for [cross-table and cascading trigger effects](https://www.postgresql.org/docs/18/trigger-definition.html)
without parsing or assuming the behavior of user trigger code.

An accepted table creation additionally proves that its complete projected
relation starts empty. A following model-managed ensure may therefore classify
an otherwise catalog-invisible key as missing when every referenced column is
known and no intervening provider DML or opaque operation could have populated
the table, including through a trigger. This inference is never used for an
existing table or for a key whose earlier projected row is only partially
known. A confined insert into a fully known, newly created plain table retains
unrelated empty-table proofs. Pure table renames move owned row and unique-key
evidence. Discarding populated-table row knowledge revokes completeness rather
than incorrectly inferring missing rows; genuinely empty new tables remain known.

## Preflight and postflight

Preflight is a separate API. `ISafeMigrationRunner`:

- invokes provider context validation before pending-history, model, environment,
  lock, catalog access, or connection opening;
- resolves pending migrations through EF services without writing history;
- validates that a derived runtime context has the explicitly configured
  canonical migration model;
- reads provider/engine/server identity;
- runs ordered safe-operation classification in bounded parameterized chunks;
- reports ordinary EF and provider operations as not analyzed, projects only
  bounded postconditions, invalidates uncertain dependent proofs, and requires
  independent artifact and postcondition review;
- inventories unexpected additive objects without deleting them;
- emits model and operation-contract fingerprints.

Provider dependency analyzers can qualify captured facts before neutral
missing-owner, sequence-aware and matching shortcuts. This invocation-local
hook is necessary for SQL Server DDL triggers, PostgreSQL event triggers and
user DML triggers which can execute unrelated DDL:
the initiating command can change unrelated rows and physical dependencies,
including recreating a dropped owner. Active or unprovable trigger risk after
executable DDL or DML produces origin-bearing runtime validation, not an accepted
postcondition. Identity, invariant unsupported contracts, authored-SQL boundaries
and an earlier deferred structural origin retain precedence. Providers without
this risk return no override and keep the existing projection path.

The model fingerprint is a versioned, provider-bound SHA-256 envelope over an
ordinally sorted relational metadata stream. It covers tables, columns, keys,
foreign keys, indexes, checks, sequences, views, queries, functions, stored
procedures, and classified migration annotations. Values are length-prefixed
and streamed directly into the hash. The contract does not depend on EF Core's
debug-string format; an unknown migration annotation value fails closed.
Ordinary scalar columns retain the property-mapping path that preserves the
established `v1` digest and allocation profile. A relational column with no
property mapping, including an EF Core `ToJson` container, is serialized
through the public `IColumn` facet and converted-default contract. The branch
avoids indexing an empty `PropertyMappings` collection without imposing the
aggregating `IColumn` facet getters on every scalar column in a large model.

An accepted exact-name index drop creates a table/schema/name-scoped
projection tombstone. A later ordinary column BTREE ensure can therefore be
classified as projected missing even though the batch analyzer observed the
pre-drop catalog. The projection never overwrites an unsupported key shape,
duplicate-data block, missing prerequisite, or differently named semantic
conflict. Opaque provider operations clear the tombstone because their effects
cannot be bounded.

Facet-isolation tests mutate every serialized relational family independently.
Golden digests run in separate provider test processes and again under the
committed EF/Npgsql dependency graph. The exact model-differ plus fingerprint path
used by `SafeMigrationRunner` has provider-specific duration/allocation budgets.

Postflight re-runs the live classifier without preflight projection. It walks
the ordered safe contract backwards and treats the final writer for one exact
schema, table, column, index, primary-key, or named-constraint resource as
authoritative. Earlier safe writers remain visible with
`postcondition_superseded` and a satisfied effective postcondition. This makes
drop/recreate and successive-definition streams verifiable against their final
catalog without requiring an impossible transient state. Provider-owned
operations never participate in the reduction. Renames still prove source
absence only, so complete destination verification requires an explicit ensure
or a separately reviewed final-state contract. Bind the selected contract and
its fingerprint to the same artifact, model, and target migration as execution.
The [postflight runbook](runbooks/deployment-and-recovery.md#postflight) owns
these checks. Reports are immutable and can be streamed through a caller-owned
`Utf8JsonWriter` without reflection or an intermediate DTO graph.

Report schema version 2 separates provider `AnalysisCode` from planner
`DecisionCode` while retaining the aggregate compatibility `Code`. Each
assessment can carry at most 16 typed facet differences with bounded printable
ASCII metadata and one closed `OperationalImpact` value. Provider SQL renders
only catalog-derived safe metadata; model-managed values are represented by the
category `model_managed_row_content`, never by keys or row contents. Detailed
evidence remains out of metrics. `SafeMigrationPreflightException` attaches the
immutable blocked report and renders one bounded deterministic conflict summary
for hosts that prefer an exception boundary.

Explicit report selection writes a separate report-view schema, currently
version 2. The serializer first counts selected entries and then streams them
directly from the immutable source report, so it can emit accurate
source/included totals and size its bounded initial buffer without allocating
a filtered array or DTO graph. `Complete`, `NonMatching`, and phase-specific
`BlockingOnly` selection share the canonical assessment writer and therefore
retain the same bounded field representation. A blocked report with no selected
blocker fails closed; future status/action contracts cannot become invisible
through an old filter.

Report schema version 3 and report-view schema version 2 add the distinct
`RuntimeValidationRequired` preflight outcome. After raw SQL makes the ordered
projection opaque, later safe assessments carry `ValidateAtRuntime` and the
first SQL operation's migration ID, stream ordinal, and CLR type. The runner
does not substitute the stale batch catalog result, project a hypothetical
safe effect, or treat this outcome as read-only `Ready`. Independently proven
conflicts remain blocked. The existing guarded runtime operation performs its
own live catalog and data check after preceding operations; on nontransactional
DDL engines, a later rejection may follow already committed work.

The same report contract covers later model-managed operations whose row state
was invalidated by an unconfined write or lost structural evidence. These use
`projected_model_managed_data_state_unknown` as their analysis code and identify
the preceding invalidation in `DeferredOrigin`. No new report enum or schema
version is needed; invariant unsupported contracts and known missing owners or
required columns retain their blocking classifications.

SQL Server uses that existing deferral contract when accepted executable safe
or typed provider DDL invalidates later row or structural certificates through a visible
enabled DDL trigger or insufficient metadata visibility to prove absence.
The provider identifies the invalidating operation; Core records its migration
ID, ordinal, and CLR type rather than attributing it to raw SQL. The two cases
retain distinct analysis codes and do not create a new schema, action, or
permission grant. Fresh runtime validation occurs immediately before the
affected operation, with independent invariant blockers retained. Initial
non-executing no-ops do not invalidate evidence, but metadata matching captured
before trigger-risk DDL does not prove its survival. PostgreSQL session-active
event triggers use the same preceding-operation provenance and freshness gate.

Enabled user DML triggers also invalidate physical metadata after executable safe
or typed DML. Both providers retain only a global risk flag, so FK cascades and
nested writes cannot bypass freshness through an owner-only lookup. PostgreSQL
shares its two permission-first trigger metadata reads; SQL Server adds a scalar
to its existing environment read. Empty typed writes, initial safe no-ops and
unsupported operation contracts retain their prior behavior. Origin-bearing
runtime validation records the actual DML and infers no physical postcondition.

A provider-specific deferred structural operation establishes no accepted
physical postcondition. Core keeps later supported safe operations in a
conservative runtime-validation boundary, using
`projected_provider_postcondition_unknown` and the first deferred structural
operation as their origin. This prevents a deferred key from becoming an
invented candidate key or a false missing prerequisite for a following foreign
key. Deferred model-managed writes do not create this structural boundary;
all live `Unsupported` results, including permission and capability refusals,
and identifier mismatches retain precedence.

Operation-contract fingerprints include safe intent, expected definitions,
policy, annotations, and ordering. An ordinary EF operation contributes
only its CLR type marker and still requires the immutable deployment artifact
digest. Source-frozen model-managed operations contribute bounded row evidence
and the ordered projection described above. Raw `InsertDataOperation`,
`UpdateDataOperation`, `DeleteDataOperation`, SQL, `AlterTable`, sequences, and
unknown operation types contribute only their CLR type marker and execute
through the provider unchanged. A preflight may first perform read-only
provider analysis, but it cannot approve such an operation.

Each optimizer-visible statement contains at most 32 operations. At most eight
statements travel in one ADO.NET batch, bounded by 16,000 parameters and 4 MiB
of UTF-8 SQL plus parameter payload across the batch. MySQL/MariaDB also use
half the live `max_allowed_packet` as an upper bound. MySQL/MariaDB and
PostgreSQL capture runtime plans in 512-operation windows. The complete migration-level unique-index
catalog is retained across those windows. Repeated typed values are interned
within a statement, global ordinals span every statement, batch, and capture
window, and results are published only after all work succeeds. Every raw
catalog command and batch receives the configured EF command timeout.
Connections use native `DbBatch` only when `CanCreateBatch` is true. A
compatible wrapper that does not forward provider batching executes the same
bounded statements through sequential `DbCommand` instances. The fallback
does not concatenate provider SQL and preserves statement order, parameters,
timeouts, cancellation, and all-or-nothing report publication.

The shared bounded work selector submits unresolved original ordinals even
when locally classified operations lie between them. MySQL/MariaDB and
PostgreSQL retain those completed slots instead of fragmenting each classifier
statement at the gap. Readers require exactly the submitted ordinal sequence;
omitted, duplicate or unsubmitted rows fail before report publication. This
does not coalesce templates or change migration-order projection. SQLite keeps
its existing snapshot/rebuild transport because it has no equivalent remote
classifier stream.

MySQL/MariaDB and PostgreSQL omit prerequisite transport only for the exact
builder-certified constant `TRUE`; every nonconstant predicate still executes
before dependent data binding. Independent diagnostic, narrowing-eligibility
and already-qualified row-probe statements use the same bounded Core transport.
Each statement owns one result set with exact ordinal and completion checks;
aggregate parameter and UTF-8 bounds include all statements. Phase barriers
are not merged. Npgsql batches reduce network roundtrips; MySqlConnector
documents that benefit for MariaDB, but not necessarily for MySQL. See
[Npgsql batching](https://www.npgsql.org/doc/basic-usage.html#batching) and
[MySqlConnector batching](https://mysqlconnector.net/api/mysqlconnector/mysqlbatchtype/).

PostgreSQL constructs operation-aware prerequisites directly without building
and discarding a complete classifier. A payload spill restores the exact
retained parameter objects, names, ordering and type mappings in the provider
collection before the next statement is built; named lookup and enumeration
must describe the same retained prefix.

SQLite inventory passes its existing immutable catalog snapshot to semantic
alias analysis within that invocation. Alias windows no longer recapture the
complete schema. Separate analysis/inventory invocations and runtime structural
mutations still obtain fresh snapshots; row proofs are not cached by this path.

SQL Server captures at most 512 plans and groups metadata-only classifiers
separately from delayed-binding classifiers inside each capture. These are
read-only queries, with preambles confined to their own dynamic invocation;
they do not depend on another classifier's execution. Grouping prevents a
binding-mode change from fragmenting every statement. Each result retains its
original ordinal and captured plan; projection still runs in migration order.
Delayed classifiers bind that ordinal as an explicit `int` parameter to a
complete isolated `sp_executesql` classifier. Rejection and successful branches
return one nine-column result set directly instead of inserting into a shared
table variable with `INSERT ... EXEC`. Stable local source markers keep the
heavy template independent of source values and original ordinals. Physical,
layout, collation, default, filter, and prerequisite guards remain before
nested delayed row binding. See Microsoft's
[dynamic batch scope and plan reuse](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-executesql-transact-sql?view=sql-server-ver17).

SQL Server captures table-name occupancy through a bounded parameterized
`sys.schemas`/`sys.objects` query before building full table classifiers.
Only proven absence omits the complete structure matcher; schema, physical,
default, collation and inline-FK support checks remain. An object appearing
after the probe is classified as different rather than accepted by name.
There is no cross-capture or cross-analysis absence cache.

Managed-data analysis binds source values independently of destination types:
Unicode and binary sources use maximum-width parameters, temporal sources
retain seven fractional digits, and decimal sources retain their actual scale.
Target `TRY_CAST`, ANSI roundtrip and capacity guards still run before typed
row relations bind. Stable local parameters live inside `sp_executesql`;
transport parameters feed the whole guard and its nested classifier explicitly.
The SQL Server transport limit is 2,000 parameters, below the documented
[2,100-parameter limit](https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server?view=sql-server-ver17),
with source payload included in the existing 4 MiB bound. At exactly 2,000
source parameters, the dispatcher passes its generated integer ordinal as a
literal instead of adding another transport parameter. The inner classifier
still receives a typed ordinal parameter; existing source capacity is retained.
Runtime mutation generation retains its literal contract.

This transport optimization does not establish a database-wide immutable
snapshot or an external-write fence. SQL Server's analysis scope and deployment
exclusion requirements remain unchanged; live runtime guards stay authoritative.

PostgreSQL holds one read-only `RepeatableRead`
snapshot and transaction-scoped analysis advisory lock across analysis. This
analysis lock is not an application write fence. A caller-owned
transaction is accepted only when it is read-only and uses `RepeatableRead` or
`Serializable`; weaker or read-write transactions fail before catalog access
and remain caller-owned. MySQL/MariaDB uses the provider migration lock;
out-of-band DDL remains prohibited during the window.

Preflight cannot eliminate time-of-check/time-of-use drift. Deployment must
prevent out-of-band DDL and data writes that invalidate checked constraints.
Runtime guards and postflight remain authoritative.

The preflight projection tracks accepted safe operations and recognized
postconditions of ordinary provider operations in operation order. Table and
column transitions update compact presence and unique-index safety facts;
these are not complete projected table definitions. For a unique index on an
existing table, a live `PrerequisiteMissing` result becomes projected `Missing`
only when every referenced column is known and a newly added key column is
nullable, non-computed, has no non-null default, and uses default null-distinct
semantics. Other unique transitions remain blocked. Typed EF data operations
reach this projection only after conversion to source-frozen model-managed safe
operations. An opaque provider effect invalidates projection evidence. After
raw SQL, later safe operations with an unprovable state report runtime
validation rather than a proven missing prerequisite. Immutable unsupported
contracts, earlier proven conflicts, and other unproven provider effects remain
blocked. The ordinary provider operation itself remains reported as not analyzed.

Existing convergence tables retain a compact accepted-constraint catalog
separately from complete table definitions. Exact names detect definition
drift; semantic aliases remain matching without destructive replacement.
Primary-key and check creation require an empty-table proof when their live
analysis could not inspect projected columns. Unique constraints and foreign
keys can alternatively rely on a newly added nullable, non-computed column that
preserves `NULL` for existing rows. A projected foreign key also requires an
accepted primary or unique principal key in the exact declared order and equal
physical storage facets for every dependent/principal column pair. Provider
data operations invalidate the row proof in O(1) through the existing monotonic
mutation version. Semantic hash indexes keep lookup proportional to definition
arity and retain collision-safe equality checks. Each accepted no-op alias is
bound to its uniquely resolved physical catalog object. A drop or rename
invalidates all aliases bound to that identity, while distinct physical objects
with equal semantics remain independent. Ambiguous or unresolved identities are
not retained as prerequisite evidence.

Provider analyzers intentionally materialize one bounded result set against the
initial catalog. The ordered projection therefore owns all later temporal
state. Accepted drops create physical-identity tombstones which override stale
live `Matching`, `Different`, or `Missing` observations. A subsequent ensure
can become projected `Missing` only when the removed definition or provider-
resolved physical identity matches and the required column, candidate-key, and
row-safety evidence still holds. Unknown identity or an intervening mutation
returns the corresponding structure- or data-unknown prerequisite result.
Re-creating an equivalent object clears its own tombstone without clearing
unrelated removals. Dictionary and semantic-hash lookups keep each transition
amortized O(1) apart from the definition's bounded column arity.

An accepted index definition is not immutable evidence. A following index drop
removes its exact physical name, while a column drop or opaque structural
mutation invalidates every affected alias. For MySQL and MariaDB, a changed
index column also invalidates the live physical-width conclusion. The adapter
then combines exact projected definitions with bounded live shapes for
unchanged composite parts and re-evaluates the key against the target table's
InnoDB row format and server page size. Prefix units retain character semantics
for character keys and byte semantics for binary keys. Unknown store families,
unsupported engines, overflow, and incomplete evidence remain unsupported.
The provider analyzers and both runtime SQL generators independently validate
the ordered index-transition stream. A known index must be dropped explicitly
before one of its key, included, expression, or filter columns is dropped;
opaque expressions keep the dependency unknown and fail closed. This rule also
applies when the migration has no `EnsureTable` operation.

MySQL and MariaDB expose a unique constraint through the same physical unique-
index identity. The projection mirrors each representable BTREE unique key in
both semantic views and invalidates both when either operation family drops the
physical name. Prefixes, expressions, provider options, and `PRIMARY` are not
cross-kind aliases. Every missing or replacement ordinary index, primary key,
and unique constraint is physically qualified before execution, including the
16-part ceiling. Primary and unique constraint identity uses one bounded
ordinal predicate per key part; classification never depends on a potentially
truncated `GROUP_CONCAT` value.

Provider-specific exemptions are internal and proof-based. Doka 10.3.x maps
`AlterDatabaseOperation` only to the database character-set default, which does
not mutate existing table, column, index, constraint, or row state. The
MySQL/MariaDB adapter can therefore certify that exact bounded shape during
scaffolding and retain existing table-scoped facts during preflight. Core does
not grant that exemption by operation type: Npgsql 10.0.3 also uses
`AlterDatabaseOperation` for extensions, enums, ranges, and other database
artifacts. PostgreSQL and unknown-provider scaffolding therefore reject that
new source; existing operations remain provider-owned at runtime.

`AnalyzePendingMigrationsAsync` calls provider validation before EF's
`IHistoryRepository.GetAppliedMigrationsAsync` path. This ordering is required
because that EF service queries the history table; see the official
[`IHistoryRepository` API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.migrations.ihistoryrepository?view=efcore-10.0).

## EF history and context ownership

SafeMigrations uses normal EF migration execution and history. A successful
migration receives one history row only after all of its commands complete. A
failed MySQL/MariaDB migration may have committed earlier DDL because the
server performs implicit DDL commits; retry converges from that partial state.

All application instances share one canonical Core model, migration assembly,
ordered migration sequence, and Core history table. SafeMigrations replaces
the scoped `IMigrationsAssembly`; the generic registration names the canonical
context explicitly and affects runtime migration discovery, `IMigrator`,
`dotnet ef`, scripts, and bundles. A derived runtime context is accepted only
when its type is assignable to that canonical context and EF's relational
model differ reports no difference from the canonical `ModelSnapshot`, when
one is present. Without a snapshot, analysis still fingerprints the runtime
model but cannot compare it to the canonical snapshot. An independently
established `ExpectedModelFingerprint` can bind a snapshot-free analysis;
keep the canonical snapshot for normal EF migration deployments.
Schema-bearing instance extensions use a separate context and history.

PostgreSQL composes a custom provider migrations generator only through the
explicit typed registration overload. The adapter delegates ordinary
operations and SafeMigrations baselines through that selected generator; it
does not silently replace an application customization with the Npgsql
default.

## Concurrency and recovery

Provider migration locks serialize multiple migrators for the same database.
Different databases can proceed independently. SafeMigrations adds no process
global lock or mutable static cache.

Every multi-command provider plan is idempotent at command boundaries. Tests
cover failure after earlier standard DDL, same-session recovery after a guard
failure, cancellation during blocked DDL, cleanup failure with pool eviction,
and repeat execution. Doka 10.4.0 executes every handler-authored guard as one
bounded scope with ordered setup, one body containing the guarded provider
sequence, and reverse-order cleanup. Cleanup runs after success, failure, or
cancellation with an independent cancellation token. A cleanup failure closes
the connection and evicts its physical session from the pool. Recovery remains
forward fix or restore from a tested backup; a heterogeneous convergence
baseline has no destructive `Down`.

## Performance and memory

The runtime path has:

- one exact Doka registry lookup/dispatch and one guarded command scope per
  MySQL/MariaDB safe operation, reusing the scoped handler instance;
- stable grouping of short owned same-operation setup SQL, bounded by 256
  UTF-8 bytes and the largest previous single setup/body payload, retaining
  large strings by reference and preserving original scope limits, opaque
  provider setup, body and independent cleanup boundaries;
- direct final-buffer emission of owned prepared assignments with their
  immediate control suffixes, avoiding both separate transport and large
  completed-string copies; fused controls retain their original logical
  fragment count and do not include opaque provider setup, body or cleanup;
- at most one immutable decision-SQL cache entry per scoped MySQL/MariaDB
  handler, keyed only by operation kind, policy and structural repair
  capability; no operation, model, live state or row-safety evidence is cached;
- no reflection, JSON intent serialization, type-name deserialization, or
  service-provider lookup per operation;
- input/model-, command-, assessment-, and catalog-inventory-dependent
  allocations; individual request bounds are not a whole-run memory cap;
- no database I/O during SQL generation;
- bounded parameterized classification chunks plus a scoped unexpected-object
  inventory per preflight or postflight;
- caller-owned report serialization support;
- allocation-bounded report-view selection without filtered collections;
- bounded telemetry tags without object names or connection data.

MySQL/MariaDB setup grouping reduces command exchanges without combining
operations, reusing catalog evidence, or changing script text. Command timeout
granularity follows the grouped setup invocation; caller cancellation and
independent cleanup remain authoritative. Runtime regression workloads exercise
actual `Database.MigrateAsync` without explicit preflight against empty,
matching, partial and safely widened catalogs, then verify schema, rows,
migration history and history-only replay. They persist aggregate command,
payload, duration and generation-allocation evidence without SQL text or
connection information. Local workload timings are not production speedup
claims. See [MySQL/MariaDB runtime boundaries](mysql-mariadb-ddl-behavior.md#session-local-guard-shape).

The repository measures construction, planning, all provider generators, and
report serialization at 1, 100, and 1000 operations, plus blocker-view
selection across 50,000 assessments, against allocation comparison limits and
coarse wall-clock ceilings in schema-versioned Core,
MySQL/MariaDB, PostgreSQL, SQLite, and SQL Server sets in `eng/performance-budgets.json`; missing,
duplicate, unknown, and orphaned measurements fail the run. The broad duration
ceilings provide regression evidence rather than a cross-machine pass guarantee.
All five CI benchmark sets use `--report-only`: numeric overruns retain false
JSON verdicts without blocking qualification. Configuration, execution and
output failures remain fatal; strict manual comparison remains available by
omitting that flag. The repository separately measures
the canonical snapshot initialization, `IMigrationsModelDiffer` comparison,
and model fingerprint path used by the runner.

Provider integration tests measure 20 complete pooled runner calls against 100
expected tables in clean and noisy catalogs. The noisy catalog adds 1,000
foreign tables with columns and indexes. Each matrix cell persists p50/p95,
assessment counts, and unexpected-object counts; noisy p95 must remain within
`2 * clean p95 + 250 ms`, and foreign child objects must not escape the
server-side expected-table scope.

The construction budgets are informational regression and allocation evidence;
the live measurements are same-runner relative SLO evidence rather than an
absolute cross-machine throughput claim.

## Verification surfaces

Release qualification covers:

- direct generator tests and real catalogs;
- `Database.MigrateAsync` and `IMigrator.MigrateAsync`;
- migration history success and failure paths;
- normal, idempotent, and no-transaction scripts;
- `dotnet ef database update` and Migration Bundle;
- external internal-service-provider registration;
- package-only consumers with no ProjectReference for direct Design,
  Tools-only, runtime-only, and intentionally excluded SafeMigrations build-asset
  layouts;
- deterministic pairwise legacy states;
- every supported PostgreSQL major, every qualified Doka engine profile, and
  locked dependency graph;
- conservatively merged product line and branch coverage floors;
- byte-identical packages, SBOM, provenance, and NuGet readback.

Primary boundaries are based on the public contracts documented by
[EF Core migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/),
[EF Core design-time tools architecture](https://learn.microsoft.com/en-us/ef/core/miscellaneous/internals/tools),
[Doka.EntityFrameworkCore.MySql](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql),
and [Npgsql EF Core](https://www.npgsql.org/efcore/), retrieved 2026-08-27,
plus the database catalog and DDL documentation linked from the operational
guides.
