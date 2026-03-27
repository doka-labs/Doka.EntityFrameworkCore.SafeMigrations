# Doka.EntityFrameworkCore.SafeMigrations

[![CI](https://github.com/kdominic89/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml/badge.svg)](https://github.com/kdominic89/Doka.EntityFrameworkCore.SafeMigrations/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations)
[![NuGet MariaDB](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.MariaDb.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.MariaDb)
[![NuGet PostgreSQL](https://img.shields.io/nuget/v/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.svg)](https://www.nuget.org/packages/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

`Doka.EntityFrameworkCore.SafeMigrations` extends EF Core migrations with safe, idempotent, provider-aware schema operations for MariaDB and PostgreSQL.

The main goal is to let a migration run safely against databases that may already contain some or all of the target schema, while still allowing explicit drift detection, preflight analysis, and tightly controlled additive repair for approved cases.

## What This Project Solves

This library is designed for scenarios such as:

- introducing a consolidated schema into an existing production database
- running an initial migration against a database that already has tables and data
- rerunning migrations safely without blindly recreating objects
- detecting incompatible drift instead of silently ignoring it
- filling in missing additive objects such as indexes or constraints when explicitly allowed

## Requirements

- .NET 9.0
- EF Core 9.x (`Microsoft.EntityFrameworkCore.Relational`)
- For MariaDB: Pomelo.EntityFrameworkCore.MySql 9.x and MariaDB 11.x
- For PostgreSQL: Npgsql.EntityFrameworkCore.PostgreSQL 9.x and PostgreSQL 13 or later

## Installation

Install the provider package for your database. The core package is a transitive dependency and does not need to be installed separately.

**MariaDB:**

```bash
dotnet add package Doka.EntityFrameworkCore.SafeMigrations.MariaDb
```

**PostgreSQL:**

```bash
dotnet add package Doka.EntityFrameworkCore.SafeMigrations.PostgreSql
```

## Current Provider Support

- MariaDB (via Pomelo)
- PostgreSQL (via Npgsql)

Both providers have unit coverage plus live integration coverage against real database containers.

## Core Capabilities

The public API includes safe wrappers for:

- create/drop table
- add/drop column
- create/drop index
- add/drop primary key
- add/drop unique constraint
- add/drop foreign key
- add/drop check constraint
- rename table/column/index
- ensure/drop schema
- alter column when different

The library supports three related execution styles:

1. **Basic idempotent execution** - existing objects are skipped safely.
2. **Strict checking** - existing objects are compared and rejected when they differ.
3. **Controlled execution options** - selected operations support preflight-only analysis and controlled additive repair for approved cases.

## Non-Goals

This project intentionally does **not** try to be:

- a general-purpose schema auto-healing engine
- a destructive migration repair tool
- a drop-and-recreate reconciliation framework
- a views/functions/triggers/sequences/materialized-views library
- a heuristic object-renaming detector

If the library cannot prove that a change is safe, it should reject the operation rather than guess.

## Registering The Provider Generator

### MariaDB

```csharp
using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

services.AddDbContext<AppDbContext>(options =>
{
    options.UseMySql(connectionString, serverVersion);
    options.UseMariaDbSafeMigrations();
});
```

Or at service-registration level:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations.MariaDb;

services.AddMariaDbSafeMigrations();
```

### PostgreSQL

```csharp
using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

services.AddDbContext<AppDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UsePostgreSqlSafeMigrations();
});
```

Or at service-registration level:

```csharp
using Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

services.AddPostgreSqlSafeMigrations();
```

## Basic Migration Examples

### Idempotent Create Table

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateTableIfNotExists(
        "users",
        table => new
        {
            id = table.Column<Guid>(nullable: false),
            email = table.Column<string>(type: "varchar(320)", nullable: false)
        },
        constraints: table =>
        {
            table.PrimaryKey("pk_users", x => x.id);
        });
}
```

### Strict Column Add

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.AddColumnIfNotExists<string>(
        name: "display_name",
        table: "users",
        nullable: true,
        strictMode: SafeMigrationStrictMode.ThrowIfDifferent);
}
```

### Controlled Repair / Preflight

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.CreateIndexIfNotExists(
        name: "ix_users_email",
        table: "users",
        columns: ["email"],
        unique: true,
        execution: new SafeMigrationExecutionOptions(
            SafeMigrationConflictMode.RepairIfPossible));

    migrationBuilder.AddForeignKeyIfNotExists(
        name: "fk_orders_users_user_id",
        table: "orders",
        columns: ["user_id"],
        principalTable: "users",
        principalColumns: ["id"],
        execution: new SafeMigrationExecutionOptions(
            SafeMigrationConflictMode.ThrowIfDifferent,
            PreflightOnly: true));
}
```

## Recommended Initial-Migration Workflow

One important target scenario for this library is a consolidated initial migration for an existing application database.

Recommended workflow:

1. Merge multiple historical `DbContext` models into one target model.
2. Generate a clean EF Core initial migration from that unified model.
3. Replace raw create/add operations with the safe migration APIs from this library.
4. Run the converted migration against the existing populated database.
5. Use strict checks, preflight, and controlled repair to close approved additive gaps.
6. Rerun safely until the database and the target model are aligned.

This workflow is why the project focuses so heavily on:

- rerunnable operations
- explicit mismatch handling
- provider-aware catalog comparison
- conservative additive repair only

## Project Layout

- `src/Doka.EntityFrameworkCore.SafeMigrations`
  Core abstractions, definitions, builder extensions, planners, and shared helpers.
- `src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb`
  MariaDB provider integration and SQL generation.
- `src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`
  PostgreSQL provider integration and SQL generation.
- `tests/Doka.EntityFrameworkCore.SafeMigrations.Tests`
  Shared unit tests.
- `tests/Doka.EntityFrameworkCore.SafeMigrations.MariaDb.Tests`
  MariaDB live integration tests (requires Docker).
- `tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests`
  PostgreSQL live integration tests (requires Docker).
- `samples/Doka.EntityFrameworkCore.SafeMigrations.Sample`
  Compileable sample project showing provider registration and safe-migration usage.

## Building And Testing

```bash
dotnet build Doka.EntityFrameworkCore.SafeMigrations.slnx
dotnet test Doka.EntityFrameworkCore.SafeMigrations.slnx
```

The integration test projects spin up Docker containers automatically. Docker must be running locally for those suites to execute.

## Known Limitations

### Default Value Comparison After Type Rename Or Removal

The library captures column default values at migration-authoring time using `SafeMigrationDefaultValueSerializer`. At comparison time, it attempts to deserialize the captured annotation back to a typed CLR value for provider-correct literal comparison.

If the CLR type used as a column default value is **renamed, moved to a different namespace, or removed from the project** after the migration annotation was written, `SafeMigrationDefaultValueSerializer.TryDeserialize` will silently fall back to the legacy literal string representation for comparison. This fallback comparison may produce false-positive strict-mode mismatches if the provider generates a slightly different literal for the same value than the legacy `ToString()`-based serializer did.

This is unlikely in practice but worth noting for projects that aggressively refactor their domain model types after migrations have been authored.

## Important MariaDB Operational Note

On MariaDB, some strict, repair, and preflight paths are implemented as multi-statement guarded flows, including temporary stored procedures and prepared statements where the server does not offer an equivalent single guarded DDL statement.

That means:

- MariaDB safe migrations are designed to be rerunnable and explicit
- MariaDB safe migrations should not be treated as fully atomic transaction units
- MariaDB preflight does not mutate target schema objects, but it may still issue temporary routine DDL internally
- MariaDB guarded operations must not be issued from within an open application transaction you intend to keep open or roll back

If you plan to use the library against an existing populated MariaDB database, read:

- [MariaDB Multi-Statement And Implicit DDL Commit Behavior](docs/mariadb-ddl-behavior.md)

## License

MIT - see [LICENSE](LICENSE).

## Further Reading

- [Implementation Design](docs/implementation-design.md)
- [MariaDB Multi-Statement And Implicit DDL Commit Behavior](docs/mariadb-ddl-behavior.md)
- [EF Core / Provider Upgrade Maintenance Risk](docs/efcore-provider-upgrade-risk.md)
- [Sample Project](samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md)
