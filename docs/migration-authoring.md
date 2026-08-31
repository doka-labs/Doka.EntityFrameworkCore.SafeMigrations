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

        migrationBuilder.CreateCompositeIndexIfNotExistsFromModel(
            name: "IX_users_TenantId_Email",
            table: "users",
            columns: ["TenantId", "Email"]);
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

## Generated check constraints

EF Core supplies check constraints to a migration generator as provider SQL.
SafeMigrations accepts generated SQL only when it can translate the complete
expression into the provider-neutral `SafeMigrationSql` tree. The bounded
grammar covers quoted or unquoted identifiers, typed string/numeric/Boolean/
null literals, parentheses, unary arithmetic and `NOT`, arithmetic and
comparison operators, `AND`/`OR`, `IS NULL`, `BETWEEN`, `IN`, function calls,
`CAST`, `COLLATE`, and current date/time values.

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

        migrationBuilder.CreateCompositeIndexIfNotExistsFromModel(
            name: "IX_users_TenantId_Email",
            table: "users",
            columns: ["TenantId", "Email"]);
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
type, collation, generated/identity state, row-version state, and provider
annotations. Existing `NULL` rows make a `NOT NULL` repair `DataBlocked`.
Invariant or unsupported drift rejects before target DDL. MySQL and MariaDB use
the Doka provider's complete `MODIFY COLUMN` definition; PostgreSQL uses its
provider-generated `SET`/`DROP DEFAULT`, `SET`/`DROP NOT NULL`, and comment
statements.

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
An unrecognized provider operation or raw SQL between that prerequisite and a
later safe operation invalidates the in-memory projection facts; represent the
required state explicitly or reorder the safe operation after a separately
reviewed boundary.

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

Doka 10.2.0 may attach `ClientGuid` to a scaffolded application-converted Guid
key. SafeMigrations preserves it but compares the column as non-
`AUTO_INCREMENT`, so both strict and legacy-convergence preflight can apply a
missing relationship graph and recognize its idempotent replay. HiLo and
unmodeled provider column facets remain unsupported before target DDL.

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
