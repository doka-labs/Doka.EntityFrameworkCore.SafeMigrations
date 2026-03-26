# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
This project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-03-25

Initial release.

### Added

**Core package (`Doka.EntityFrameworkCore.SafeMigrations`)**

- `MigrationBuilder` extensions for safe, idempotent schema operations: `CreateTableIfNotExists`, `DropTableIfExists`, `RenameTableIfExists`, `EnsureSchemaExists`, `DropSchemaIfExists`
- Column operations: `AddColumnIfNotExists`, `DropColumnIfExists`, `RenameColumnIfExists`, `AlterColumnIfDifferent`
- Index operations: `CreateIndexIfNotExists`, `DropIndexIfExists`, `RenameIndexIfExists`
- Constraint operations: `AddPrimaryKeyIfNotExists`, `DropPrimaryKeyIfExists`, `AddUniqueConstraintIfNotExists`, `DropUniqueConstraintIfExists`, `AddForeignKeyIfNotExists`, `DropForeignKeyIfExists`, `AddCheckConstraintIfNotExists`, `DropCheckConstraintIfExists`
- `SafeMigrationStrictMode` for explicit definition mismatch rejection (`ThrowIfDifferent`)
- `SafeMigrationExecutionOptions` with `SafeMigrationConflictMode` (`RepairIfPossible`, `ThrowIfDifferent`) and `PreflightOnly` for analysis-only runs
- Provider-neutral planning layer (`SafeMigrationDecisionPlanner`) for classifying operation outcomes
- Expected-definition records and serialization for stable catalog comparison
- Column repair safety gates (`SafeMigrationColumnRepairHelper`) — only nullable or default-bearing columns are auto-added

**MariaDB provider (`Doka.EntityFrameworkCore.SafeMigrations.MariaDb`)**

- `UseMariaDbSafeMigrations()` extension for `DbContextOptionsBuilder`
- `AddMariaDbSafeMigrations()` extension for `IServiceCollection`
- Native `IF EXISTS` / `IF NOT EXISTS` paths where MariaDB server syntax supports it
- Prepared-statement guard flows for rename and drop paths
- Temporary stored-procedure guard flows for strict, repair, and preflight paths
- `information_schema` catalog comparison for indexes, columns, constraints, and foreign keys
- Provider veto for filtered indexes (not supported by MariaDB)
- Live integration test coverage against real MariaDB 11.x containers

**PostgreSQL provider (`Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`)**

- `UsePostgreSqlSafeMigrations()` extension for `DbContextOptionsBuilder`
- `AddPostgreSqlSafeMigrations()` extension for `IServiceCollection`
- Native `IF EXISTS` / `IF NOT EXISTS` paths
- Guarded `DO` block flows for strict, repair, and preflight paths
- `pg_catalog` comparison for indexes, columns, constraints, and foreign keys
- Full filtered-index support
- Live integration test coverage against real PostgreSQL 13+ containers
