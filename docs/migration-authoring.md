# Migration Authoring Paths

SafeMigrations supports two generated paths and one explicit hand-authored
path. They share the same runtime contracts, but they serve different database
histories and produce visibly different C# source. Review these shapes before
selecting a scaffolding mode so the generated migration is not surprising.

## Choose the path

| Path | Use when | Source written by | Table call | Rollback |
| --- | --- | --- | --- | --- |
| Generated strict migration | The table is new or every existing copy must already match the complete model | `dotnet ef migrations add` | `CreateTableIfNotExists` | `DropTableIfExists` |
| Generated legacy convergence | Installations may contain an absent, empty, or partial copy of the same table | `dotnet ef migrations add` | `ConvergeTableFromModel` | Entire `Down` body throws before DDL |
| Hand-authored convergence | The reviewed expected contract or policy cannot be inferred completely from the EF model | Migration author | `ExpectedTableDefinition` plus `ConvergeTable` | Migration author must reject unsafe rollback |

`Strict` and `LegacyConvergence` are scaffolding modes. The hand-authored path
does not add a third mode; it uses the public builder API directly inside a
migration.

The generated examples below show the representative MySQL/MariaDB shape for
one model. PostgreSQL generates the same SafeMigrations method calls with its
own store types and Npgsql provider annotations. Names, store types, defaults,
constraints, and annotations always come from the consuming application's EF
model.

Every generated migration explicitly imports
`Doka.EntityFrameworkCore.SafeMigrations`. The source therefore resolves its
SafeMigrations extension methods and policy types without relying on project-
level global usings. Existing migration files remain source-frozen and are not
retrofitted; add the explicit import during review if an older file lacks it.

## Generated strict migration

Strict scaffolding is the default:

```csharp
options.UseMySqlSafeMigrations();
// or: options.UsePostgreSqlSafeMigrations();
```

Create the migration normally:

```bash
dotnet ef migrations add AddUsers
```

For an integer identity, tenant identifier, required email, unique email index,
and composite tenant/email index, the generated migration has this shape:

```csharp
using Doka.EntityFrameworkCore.MySql;
using Doka.EntityFrameworkCore.SafeMigrations;
using Microsoft.EntityFrameworkCore.Migrations;

public partial class AddUsers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTableIfNotExists(
            name: "users",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
                TenantId = table.Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                Email = table.Column<string>(
                        type: "varchar(320)",
                        maxLength: 320,
                        nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", value => value.Id);
            });

        migrationBuilder.CreateIndexIfNotExistsFromModel(
            name: "IX_users_Email",
            table: "users",
            column: "Email",
            unique: true);

        migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
            name: "IX_users_TenantId_Email",
            table: "users",
            columns: ["TenantId", "Email"],
            prefixLengths: [0, 191]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTableIfExists(
            name: "users");
    }
}
```

`CreateTableIfNotExists` treats the table as one complete owned definition. An
absent table is created. An existing matching table is a no-op. An existing
table with a missing or incompatible owned child is rejected rather than
silently accepted as equivalent.

The generated index calls are evaluated independently under the same
missing/matching/different contract. `DropTableIfExists` makes only absence
idempotent; it is still destructive when the table exists.

When EF scaffolds an index replacement as `DropIndex` followed by
`CreateIndex`, SafeMigrations writes `DropIndexIfExists` followed by the
appropriate safe create helper. Preflight preserves this operation order: an
accepted exact-name drop makes the replacement target missing, but it does not
override a data-blocked unique index, an unsupported physical key, a missing
prerequisite, or an exact-name definition mismatch that is unrelated to the
accepted drop.

The composite example assumes that Doka attached `.HasPrefixLength(0, 191)` to
the EF model. SafeMigrations reads that metadata through Doka's typed public
contract and writes it as an explicit argument. Zero preserves the complete
`TenantId` key; 191 limits `Email` using MySQL's character-prefix semantics.
The consumed provider annotation is not copied onto the outer safe operation.
Without prefix metadata, the ordinary
`CreateCompositeIndexIfNotExistsFromModel` call remains unchanged.

## Generated check constraints

EF Core supplies check constraints to a migration generator as provider SQL.
SafeMigrations accepts generated SQL only when it can translate the complete
expression into the provider-neutral `SafeMigrationSql` tree. The bounded
grammar covers quoted or unquoted identifiers, typed string/numeric/Boolean/
null literals, parentheses, unary arithmetic and `NOT`, arithmetic and
comparison operators, `AND`/`OR`, `IS NULL`, `BETWEEN`, `IN`, function calls,
`CAST`, `COLLATE`, and current date/time values.

The `storeType` supplied to `SafeMigrationSql.Literal` or
`SafeMigrationSql.Cast` is a provider store type, not raw SQL. The
MySQL/MariaDB renderer maps column aliases into their common CAST grammar:
signed integer aliases become `SIGNED`, unsigned integer aliases become
`UNSIGNED`, `char`/`varchar`/text aliases become `CHAR`, and supported binary,
decimal, floating-point, date, datetime, and time shapes receive a canonical
target. Precision, scale, and fractional-second bounds are validated. Types
whose semantics cannot be represented identically on both engines fail closed
with `structured_cast_type`; arbitrary type clauses are never copied into SQL.
This follows the official [MySQL CAST grammar](https://dev.mysql.com/doc/refman/8.4/en/cast-functions.html)
and [MariaDB CAST grammar](https://mariadb.com/docs/server/reference/sql-functions/string-functions/cast).

A typed null remains typed: MySQL/MariaDB render `CAST(NULL AS <type>)`, while
PostgreSQL renders `NULL::<type>`. Use an untyped `SafeMigrationSql.Literal(null)`
only when provider type inference is intentional.

PostgreSQL validates each structured literal or cast store type through
Npgsql's relational type mapping. SafeMigrations then emits documented built-in
aliases in their catalog-canonical form. Aliases such as `int4` therefore
round-trip the catalog's `integer` spelling. Unknown types and values with
appended SQL grammar are `Unsupported` with `structured_cast_type` before DDL.
PostgreSQL `float` without a precision maps to `double precision`;
`float(1)` through `float(24)` map to `real`, while `float(25)` through
`float(53)` map to `double precision`. Other precisions fail closed before DDL,
and array forms retain the same scalar semantics. This preserves PostgreSQL's
native type system without treating a caller-supplied type name as unchecked
SQL, and follows PostgreSQL's official
[type-cast syntax](https://www.postgresql.org/docs/current/sql-expressions.html#SQL-SYNTAX-TYPE-CASTS).
The precision boundaries follow the official
[floating-point type definition](https://www.postgresql.org/docs/current/datatype-numeric.html#DATATYPE-FLOAT).

MariaDB's generated-column grammar does not expose a `NOT NULL` facet and its
catalog reports generated columns as nullable. A non-nullable computed
definition is therefore `Unsupported` with `generated_column_nullability`
before any target DDL. MySQL supports and verifies that facet. Keep a shared
MySQL/MariaDB generated-column contract nullable unless separate definitions
are intentional.

Comments, parameters, multiple statements, subqueries, opaque provider
operators, provider-dependent backslash escapes, malformed input, excessive
nesting, oversized lists, and expressions larger than the documented parser
bound fail during scaffolding. Delimited identifiers and strings use only SQL
delimiter doubling. The exception identifies the check constraint and stable
parse-failure category. It never emits a migration that would later fail solely
because the generated check remained opaque.

For SQL outside the bounded grammar, express the reviewed semantics explicitly:

```csharp
var check = ExpectedCheckConstraintDefinition.FromExpression(
    "ck_orders_amount",
    "orders",
    SafeMigrationSql.Binary(
        SafeMigrationSql.Identifier("amount"),
        SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
        SafeMigrationSql.Literal(0)));

migrationBuilder.EnsureCheckConstraint(
    check,
    SafeMigrationPolicy.ThrowIfDifferent);
```

EF Core's `HasCheckConstraint` contract intentionally accepts provider SQL;
therefore SafeMigrations, rather than EF, owns this stricter structural
translation boundary. See the official
[EF Core check-constraint documentation](https://learn.microsoft.com/en-us/ef/core/modeling/indexes#check-constraints),
[MySQL string-literal contract](https://dev.mysql.com/doc/refman/8.4/en/string-literals.html),
and [PostgreSQL lexical contract](https://www.postgresql.org/docs/current/sql-syntax-lexical.html).

The same bounded parser captures SQL defaults exposed by EF through
`ColumnOperation.DefaultValueSql`. A generated value such as
`CURRENT_TIMESTAMP(6)` is stored as a structured expression and compared
semantically with the live catalog. SQL outside the grammar remains opaque and
fails closed with `opaque_sql_expression`; SafeMigrations never executes an
unknown expression merely to infer equivalence.

## Model-managed data from `HasData`

EF Core calls `HasData` model-managed data. Keep its declaration in
`OnModelCreating` and create migrations normally:

```csharp
modelBuilder.Entity<Role>().HasData(
    new Role
    {
        Id = 1,
        Name = "Administrator",
    });
```

```bash
dotnet ef migrations add AddAdministratorRole
```

With SafeMigrations scaffolding enabled, the generated `Up` method does not
contain raw `InsertData`, `UpdateData`, or `DeleteData` calls. A new row has
this representative source-frozen shape:

```csharp
migrationBuilder.EnsureModelManagedDataFromModel(
    table: "Roles",
    keyColumns: ["Id"],
    keyColumnTypes: ["int"],
    columns: ["Id", "Name"],
    columnTypes: ["int", "varchar(128)"],
    values: new object[,]
    {
        { 1, "Administrator" },
    });
```

Changing the same row in the model produces a compare-and-swap update. The
old values come from the inverse model difference; they are not read from a
developer database while the migration is scaffolded:

```csharp
migrationBuilder.UpdateModelManagedDataFromModel(
    table: "Roles",
    keyColumns: ["Id"],
    keyColumnTypes: ["int"],
    keyValues: new object[,]
    {
        { 1 },
    },
    columns: ["Name"],
    columnTypes: ["varchar(128)"],
    oldValues: new object[,]
    {
        { "Administrator" },
    },
    newValues: new object[,]
    {
        { "System administrator" },
    });
```

Removing the row produces a delete containing its complete captured source
state and source-model incoming dependencies:

```csharp
migrationBuilder.DeleteModelManagedDataFromModel(
    table: "Roles",
    keyColumns: ["Id"],
    keyColumnTypes: ["int"],
    keyValues: new object[,]
    {
        { 1 },
    },
    columns: ["Id", "Name"],
    columnTypes: ["int", "varchar(128)"],
    oldValues: new object[,]
    {
        { 1, "Administrator" },
    },
    foreignKeys:
    [
        new ExpectedModelManagedDataForeignKeyDefinition(
            table: "UserRoles",
            columns: ["RoleId"],
            principalColumns: ["Id"]),
    ]);
```

The generated calls have fixed fail-closed semantics in both scaffolding
modes:

- ensure inserts only an absent primary-key row; an equal row is a no-op and a
  different row is rejected;
- update changes only a row whose key and captured old managed values still
  match; an already-target row is a no-op;
- delete removes only a row whose complete captured source values still match
  and whose incoming dependencies would not be changed implicitly;
- every applied transition validates its target state before completing;
- multiple rows are partitioned deterministically into at most 128 rows and
  4,096 value cells per generated operation.

An initial migration may create a table and populate its `HasData` rows in the
same ordered operation stream. Preflight treats a preceding accepted table
creation as proof that the complete projected table is empty, so the generated
ensure is `Missing`/`Apply`. That proof is intentionally narrow: existing
tables remain catalog-backed, and ordinary data operations, opaque SQL,
incomplete columns, or a partially known row invalidate the inference.

The providers use null-safe comparisons for captured values. MySQL and MariaDB
use `<=>`; PostgreSQL uses `IS NOT DISTINCT FROM`. SafeMigrations deliberately
does not use `INSERT IGNORE`, `ON DUPLICATE KEY UPDATE`, `ON CONFLICT DO
UPDATE`, or a generic merge operation. Those shortcuts can select a different
unique conflict or change trigger behavior without proving the model-managed
primary-key contract.

The values are source-controlled in the EF model, snapshot, generated
migration, and generated SQL script. Do not place secrets, per-environment
values, mutable operational data, temporary test data, or large datasets in
`HasData`. Use EF Core `UseSeeding`/`UseAsyncSeeding` or an application-owned
bootstrap workflow for those cases. See the official
[EF Core model-managed-data guidance](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding).

SafeMigrations does not reinterpret existing migration files. If an unapplied
migration still contains raw model-managed data calls, remove it and scaffold
it again after upgrading. Never replace an already applied migration; express
the correction as a new forward migration. A hand-authored raw data operation
remains `provider_owned_not_analyzed` because SafeMigrations cannot prove its
model origin or reconstruct missing old values.

## Generated legacy convergence

Select legacy convergence only while scaffolding a reviewed baseline for
heterogeneous existing installations:

```csharp
options.UseMySqlSafeMigrations(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(
            SafeMigrationPolicy.RepairIfSafe);
});
```

PostgreSQL uses the same option:

```csharp
options.UsePostgreSqlSafeMigrations(safeMigrations =>
{
    safeMigrations
        .UseScaffoldingMode(
            SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(
            SafeMigrationPolicy.RepairIfSafe);
});
```

Then scaffold the baseline:

```bash
dotnet ef migrations add CoreLegacyConvergence
```

For the same EF model, the generated migration has this shape:

```csharp
using System;
using Doka.EntityFrameworkCore.MySql;
using Doka.EntityFrameworkCore.SafeMigrations;
using Microsoft.EntityFrameworkCore.Migrations;

public partial class CoreLegacyConvergence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.ConvergeTableFromModel(
            name: "users",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.AutoIncrement),
                TenantId = table.Column<int>(type: "int", nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
                Email = table.Column<string>(
                        type: "varchar(320)",
                        maxLength: 320,
                        nullable: false)
                    .Annotation(
                        "Doka:MySql:ValueGenerationStrategy",
                        MySqlValueGenerationStrategy.None),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_users", value => value.Id);
            },
            policy: SafeMigrationPolicy.RepairIfSafe);

        migrationBuilder.CreateIndexIfNotExistsFromModel(
            name: "IX_users_Email",
            table: "users",
            column: "Email",
            unique: true);

        migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel(
            name: "IX_users_TenantId_Email",
            table: "users",
            columns: ["TenantId", "Email"],
            prefixLengths: [0, 191]);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "A legacy-convergence migration cannot be rolled back safely "
            + "because SafeMigrations cannot prove which database objects "
            + "predated the migration.");
    }
}
```

`ConvergeTableFromModel` does not stop after noticing that `users` exists. It
captures EF's complete typed table operation and converts it to an immutable
expected definition. SafeMigrations then emits an existence-only table
container followed by separate column and constraint contracts. The generated
index calls remain separate operations.

Consequently, an absent table is created, a partial table receives only
proven-safe missing children, and an incompatible existing child blocks before
its target DDL. Unknown extra objects are preserved and reported. An unsafe
`NOT NULL` addition without a usable default remains blocked rather than being
forced onto existing rows.

The policy is a literal part of the generated migration. Without
`UseLegacyConvergencePolicy`, the generated argument is
`SafeMigrationPolicy.ThrowIfDifferent`. With the explicit `RepairIfSafe`
configuration above, ordinary existing columns can converge only nullability,
default, and comment drift. The live catalog must already prove identical store
type, collation, generated/identity state, and row-version state. Doka's typed
metadata contract must recognize every MySQL/MariaDB annotation and prove it
consistent with the column shape. Existing `NULL` rows make a `NOT NULL` repair
`DataBlocked`. Invariant, malformed, contradictory, or unsupported drift
rejects before target DDL. MySQL and MariaDB use the Doka provider's complete
`MODIFY COLUMN` definition; PostgreSQL uses its provider-generated
`SET`/`DROP DEFAULT`, `SET`/`DROP NOT NULL`, and comment statements.

Ordered preflight projects deterministic structural postconditions of preceding
ordinary EF table and column operations into later safe prerequisites. For
example, an ordinary `AddColumnOperation` followed by a safe index can produce
`projected_missing` rather than the catalog's historical
`prerequisite_missing`. The ordinary operation remains
`provider_owned_not_analyzed`, the overall result remains
`ReadyWithProviderOperations`, and deployment approval still requires an
independent review and postcondition for that operation. Projection describes
the state only if the earlier provider operation succeeds; it does not convert
ordinary DDL into a SafeMigrations operation.
Raw hand-authored or previously compiled typed EF insert, update, and
delete-data operations preserve structural table/column prerequisites for a
later non-unique index, but invalidate every earlier projected or live
pre-batch data-safety proof. Newly scaffolded HasData operations instead use
the source-frozen model-managed contract above. A later unique index or additive
data-validating constraint after raw data therefore remains blocked rather than
assuming that unanalyzed values are safe. Later structural DDL does not
re-establish row-level certainty. An unrecognized provider operation or raw SQL
still invalidates all in-memory projection facts; represent the required state
explicitly or reorder the safe operation after a separately reviewed boundary.

For unique indexes on an existing table, projection applies a stricter data
safety proof. A newly added key column must be nullable, non-computed, and have
no non-null default, while the index must retain default null-distinct
semantics. Unknown columns, non-null defaults, computed values, and
`NULLS NOT DISTINCT` remain `prerequisite_missing`. Runtime guards and
postflight remain authoritative for the actual database state.

The generated `Down` body applies to the entire migration. It throws before any
destructive DDL because the migration cannot prove which table, column,
constraint, or index existed before the baseline.

After the intended legacy baseline sequence has been scaffolded and reviewed,
return the registration to the no-argument strict default. Changing the option
affects only migrations scaffolded afterwards; it never reinterprets existing
migration source.

## Hand-authored expected definition

Before automatic scaffolding existed, convergence migrations constructed an
`ExpectedTableDefinition` explicitly and passed it to `ConvergeTable`. That API
remains supported for a reviewed contract that cannot be inferred faithfully
from the current EF model.

```csharp
using System;
using Doka.EntityFrameworkCore.SafeMigrations;
using Microsoft.EntityFrameworkCore.Migrations;

public partial class HandAuthoredLegacyConvergence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var users = new ExpectedTableDefinition(
            "users",
            [
                new ExpectedColumnDefinition("id", typeof(Guid), isNullable: false),
                new ExpectedColumnDefinition("tenant_id", typeof(Guid), isNullable: false),
                new ExpectedColumnDefinition(
                    "email",
                    typeof(string),
                    isNullable: false,
                    maxLength: 320),
            ],
            primaryKey: new ExpectedPrimaryKeyDefinition(
                "pk_users",
                "users",
                ["id"]));

        var indexes = new ExpectedIndexDefinition[]
        {
            new(
                "ix_users_email",
                "users",
                [new ExpectedIndexKeyDefinition(column: "email")],
                unique: true),
            new(
                "ix_users_tenant_id_email",
                "users",
                [
                    new ExpectedIndexKeyDefinition(column: "tenant_id"),
                    new ExpectedIndexKeyDefinition(column: "email"),
                ]),
        };

        migrationBuilder.ConvergeTable(users, indexes);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "A hand-authored legacy-convergence migration cannot be "
            + "rolled back safely.");
    }
}
```

The author owns the completeness and correctness of every manually supplied
facet, name, constraint, index, and policy. Provider-owned identity annotations
are captured automatically by the generated model path but are not public
hand-authored definition inputs. Use `ConvergeTableFromModel` when those facets
must come from provider scaffolding; do not manually translate generated EF
table source into expected-definition constructors.

Doka 10.3.0 may attach `ClientGuid` to a scaffolded application-converted Guid
key. SafeMigrations preserves it but compares the column as non-
`AUTO_INCREMENT`, so both strict and legacy-convergence preflight can apply a
missing relationship graph and recognize its idempotent replay. HiLo and
unmodeled provider column facets remain unsupported before target DDL.

## MySQL and MariaDB index limits

Before applying a missing BTREE index, SafeMigrations evaluates its physical
key width against the live InnoDB row format and page size. Character prefixes
are counted in characters and converted through the column character set;
binary prefixes are counted in bytes. A prefix is rejected when it exceeds the
declared column width or targets a scalar key. Full `TEXT`/`BLOB`, expression
keys, unknown store families, and non-BTREE missing-index shapes reject when
their physical width cannot be proven.

`index_prefix_required_for_key_limit` means the modeled ordinary key is wider
than the live InnoDB limit. Add the intended `.HasPrefixLength(...)` to the EF
model, scaffold a new migration, and review the resulting explicit
`prefixLengths` argument. `index_key_length_unverifiable` means the shape has no
bounded proof in the adapter; author a reviewed provider-specific transition
instead of relying on a later server error. SafeMigrations never invents a
prefix because doing so can change uniqueness and query semantics.

`ConvergeTable` and `ConvergeTableFromModel` reach the same object-granular
convergence implementation. Their difference is how the immutable expected
definition is obtained:

| Entry point | Expected definition source | Intended author |
| --- | --- | --- |
| `ConvergeTableFromModel` | Complete EF `CreateTableOperation`, including supported provider annotations | SafeMigrations scaffolder |
| `ConvergeTable` | Explicit `ExpectedTableDefinition` and optional index definitions | Migration author |

## Review before deployment

For every generated or hand-authored migration:

1. Review the complete generated C# source and verify that the selected path is
   visible in the method names.
2. Verify names, schemas, store types, nullability, defaults, constraints,
   indexes, provider annotations, and the complete `Down` body.
3. Run SafeMigrations preflight against every independently deployed instance;
   one matching instance is not evidence for another instance.
4. Resolve every `Different`, `Unsupported`, or blocked assessment before
   allowing target DDL.
5. Preserve the reviewed migration source. A later scaffolding-mode change does
   not and must not mutate its contract.

See [deployment and recovery](runbooks/deployment-and-recovery.md) for the
instance-by-instance operational sequence and [API reference](api-reference.md)
for the complete builder surface.
