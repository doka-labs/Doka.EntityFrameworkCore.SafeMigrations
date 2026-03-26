# Implementation Design

This document explains how the library is implemented today and why the main design choices were made. It is intentionally focused on the code paths that are easiest to misread when coming in fresh: the migration-builder extensions, the operation-factory layer, the provider SQL generators, the comparison/planning logic, and the controlled repair and preflight flow.

## 1. High-Level Architecture

The library is built around EF Core migration operations rather than around handwritten provider SQL.

The runtime flow is:

1. Public extension methods on `MigrationBuilder` create or decorate EF Core `MigrationOperation` instances.
2. The operation layer stores safe-migration intent either as:
   - annotations on standard EF Core operations, or
   - dedicated safe operation types for constraint families that need richer CLR properties.
3. Provider-specific `IMigrationsSqlGenerator` implementations inspect those operations and emit guarded SQL for MariaDB or PostgreSQL.
4. For controlled repair and preflight, shared planning helpers classify the operation before the provider emits either executable SQL or analysis-only SQL.

The key consequence of this design is that the library stays inside EF Core's migration pipeline. Consumers still author migrations in the normal EF Core style, and providers remain responsible for provider-specific SQL generation.

## 2. Why The Public API Uses `MigrationBuilder` Extensions

The public API lives in [SafeMigrationBuilderExtensions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Extensions/SafeMigrationBuilderExtensions.cs).

This layer exists for two reasons:

- It gives consumers migration methods that read like ordinary EF Core migration code.
- It lets the library attach safe-migration metadata at authoring time, before SQL generation starts.

For simple EF Core operations such as `CreateTableOperation`, `AddColumnOperation`, or `CreateIndexOperation`, the extension methods usually create a normal EF operation and then mark it with annotations such as:

- `IfExists`
- `IfNotExists`
- `StrictMode`
- `ExpectedDefinition`
- `ConflictMode`
- `PreflightOnly`

That keeps the public surface small and lets provider generators reuse EF Core's normal SQL emission where possible.

Some methods rely on `migrationBuilder.Operations[^1]` after calling a normal EF Core method such as `CreateTable`, `DropSchema`, `RenameTable`, `RenameColumn`, or `RenameIndex`. That is a deliberate implementation shortcut: EF Core currently appends the just-created operation last, so the library can decorate that operation without rebuilding it from scratch. The code documents that assumption inline so it is easy to revisit if EF Core changes that behavior.

## 3. Why Some Constraint Families Use Dedicated Safe Operations

The core factory logic lives in [SafeMigrationOperationFactory.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/SafeMigrationOperationFactory.cs).

Indexes and columns can stay close to standard EF Core operations because their comparison metadata fits naturally as annotations plus the normal EF Core properties.

Constraint families such as:

- primary keys
- unique constraints
- foreign keys
- check constraints

use dedicated safe operation types such as:

- [SafeAddPrimaryKeyOperation.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Operations/SafeAddPrimaryKeyOperation.cs)
- [SafeAddUniqueConstraintOperation.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Operations/SafeAddUniqueConstraintOperation.cs)
- [SafeAddForeignKeyOperation.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Operations/SafeAddForeignKeyOperation.cs)
- [SafeAddCheckConstraintOperation.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Operations/SafeAddCheckConstraintOperation.cs)

That split exists because those operations need richer expected-definition data and clearer provider access than the old annotation-only approach provided. Storing the comparison state as CLR properties makes the generator code easier to follow and avoids a lot of fragile annotation parsing for complex constraint definitions.

## 4. Expected Definitions And Why They Are Serialized

The expected-definition records live in [src/Doka.EntityFrameworkCore.SafeMigrations/Definitions](../src/Doka.EntityFrameworkCore.SafeMigrations/Definitions), and serialization is handled by [SafeMigrationDefinitionSerializer.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/SafeMigrationDefinitionSerializer.cs).

The library stores an expected definition for operations that need comparison against the live catalog. Examples include:

- table shape
- column shape
- index definition
- primary key definition
- unique constraint definition
- foreign key definition
- check constraint definition

These definitions are serialized onto normal EF Core operations because the generator only receives the operation model, not the original migration-builder call site. By persisting an explicit expected definition, the provider generator can decide whether the existing live object matches, differs, or is missing.

The code intentionally keeps expected definitions normalized and explicit rather than reconstructing them from generated SQL. Comparing structured metadata is much more stable than trying to reverse-engineer intent from provider SQL text.

## 5. Legacy Strict Mode Versus The Extended Execution Pipeline

The library currently supports two related but intentionally distinct models.

### 5.1 Legacy strict mode

Legacy strict mode is represented by [SafeMigrationStrictMode.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Abstractions/SafeMigrationStrictMode.cs).

Its role is narrow:

- `None`: normal idempotent behavior
- `ThrowIfDifferent`: reject an existing conflicting definition

This mode is preserved because it is simple, stable, and already embedded in the original safe-operation API.

### 5.2 Extended execution options

The newer controlled execution model is represented by:

- [SafeMigrationConflictMode.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Abstractions/SafeMigrationConflictMode.cs)
- [SafeMigrationExecutionOptions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Abstractions/SafeMigrationExecutionOptions.cs)
- [SafeMigrationExecutionAnnotationHelper.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/SafeMigrationExecutionAnnotationHelper.cs)

This exists because a single strict-mode enum was no longer expressive enough once the library gained:

- preflight-only analysis
- safe additive repair
- provider veto rules

The execution-options model therefore adds:

- `ConflictMode`
- `PreflightOnly`

The compatibility helper maps execution options back onto the legacy strict-mode semantics where needed, so the generators can preserve existing behavior while gradually supporting the broader execution pipeline.

## 6. How Planning Works

The shared planner lives in [SafeMigrationDecisionPlanner.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/SafeMigrationDecisionPlanner.cs), supported by the internal decision types in the same project.

The planner answers a small but important question:

Given the requested execution mode and the comparison result, should the operation:

- do nothing
- create the missing object
- repair by adding a missing object
- reject the operation

The shared planner only handles provider-neutral rules. For example:

- a missing object can be created
- a matching object becomes a no-op
- a different object is rejected under `ThrowIfDifferent`
- a different object is only repairable when the operation family explicitly allows safe additive repair

Provider-aware planners refine that result for provider-specific edge cases:

- [MariaDbSafeMigrationPlanner.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/Planning/MariaDbSafeMigrationPlanner.cs)
- [PostgreSqlSafeMigrationPlanner.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Planning/PostgreSqlSafeMigrationPlanner.cs)

This split is intentional. Provider differences such as filtered-index support should not leak into the shared planner, but they also should not be buried as ad hoc special cases all over the SQL generators.

## 7. Why Preflight Is Implemented In The SQL Generators

Preflight is exposed through `SafeMigrationExecutionOptions.PreflightOnly`, but the actual analysis SQL is emitted by the provider generators.

That might seem surprising at first, but it is a deliberate choice:

- the generator already knows how to ask the live provider catalog whether an object exists or matches
- provider metadata queries are strongly provider-specific
- preflight must stay behaviorally aligned with real execution

Because of that, the same generator that would execute a guarded create or repair path can instead emit analysis-only SQL when `PreflightOnly` is set.

The design goal is consistency:

- preflight should classify the object the same way execution would
- preflight should never emit DDL
- provider vetoes should apply equally in preflight and execution modes

## 8. Why The Library Uses Provider-Specific SQL Generators

The core provider implementations are:

- [MariaDbSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/SqlGeneration/MariaDbSafeMigrationsSqlGenerator.cs)
- [PostgreSqlSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationsSqlGenerator.cs)

They subclass the provider migrations generators because this library needs to intercept EF Core migration operations at SQL-generation time, not after SQL has already been flattened into raw text.

This gives the library three benefits:

- reuse of the provider's normal SQL generation for the underlying operation
- direct access to the provider's existing quoting/type/DDL behavior
- a single place to wrap operations in idempotent or strict/repair guards

The tradeoff is maintenance risk when EF Core or the provider changes internal APIs. That risk is already tracked separately in the checklist because this code intentionally extends a low-level part of the EF Core stack.

## 9. How Guarded SQL Is Structured

The exact SQL differs per provider, but the pattern is consistent.

### 9.1 MariaDB

MariaDB relies on:

- native `IF EXISTS` / `IF NOT EXISTS` where the server supports it
- `information_schema` lookups where native syntax is missing or not expressive enough
- guarded blocks for strict mismatch signaling and preflight behavior

MariaDB also needs more care around features that are unavailable or inconsistent at the server level. A good example is filtered indexes: the code preserves the ordinary non-strict path where possible, but the planning/generator layers veto unsupported repair or strict-comparison cases rather than pretending they are safe.

### 9.2 PostgreSQL

PostgreSQL relies heavily on:

- native `IF EXISTS` / `IF NOT EXISTS`
- `pg_catalog` queries for detailed comparison
- guarded `DO` blocks when the operation needs branching logic

This provider can support some richer comparison paths more naturally than MariaDB because PostgreSQL's catalog model is stronger and because its anonymous block support makes analysis and rejection flows easier to express.

## 10. Controlled Repair: What It Means Here

The repair flow is intentionally narrow.

The library does not attempt broad schema healing, object renaming inference, drop-and-recreate repair, or data-loss operations. Instead, controlled repair is currently limited to additive cases that can be justified as safe, such as:

- creating a missing index
- creating a missing unique constraint
- creating a missing foreign key
- creating a missing check constraint
- adding a missing column only in explicitly safe additive cases

This constraint exists for a reason: once a library starts changing existing objects automatically, the risk of destructive or ambiguous behavior rises quickly. The code therefore treats "repair" as "finish the missing additive part safely," not "rewrite an existing schema into shape by any means necessary."

## 11. Why Column Repair Has Stronger Safety Gates

Column repair is more dangerous than the other supported repair families, so it uses explicit safety checks in [SafeMigrationColumnRepairHelper.cs](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/SafeMigrationColumnRepairHelper.cs).

A missing column is only auto-added when it is considered safely additive. In practice that means cases such as:

- nullable columns
- columns with a default or computed expression that can populate existing rows safely

The code rejects unsafe column repair cases rather than guessing. That keeps the library aligned with its "safe migration" promise and avoids hidden data backfill or nullability failures on existing databases.

## 12. Why Matching Is Provider-Metadata Based Instead Of SQL-Text Based

The library compares structured database metadata, not generated DDL text.

That design avoids several common problems:

- provider SQL text often varies in formatting without changing meaning
- providers can normalize definitions differently than EF Core authored them
- reconstructing intent from SQL strings is brittle for constraints and indexes

Instead, the code compares concrete catalog facts such as:

- column type/nullability/default-related facts
- index uniqueness, columns, ordering, and filters where supported
- constraint names and participating columns
- foreign-key principal mapping and referential actions

This makes the strict and repair decisions more stable across reruns and across provider upgrades.

## 13. How The Initial-Migration Workflow Fits In

One important use case for this library is a consolidated initial migration for an already existing database.

The intended flow is:

1. Merge multiple application contexts into a single target model.
2. Generate a clean EF Core initial migration from that unified model.
3. Convert the migration to safe operations such as:
   - `CreateTableIfNotExists`
   - `AddColumnIfNotExists`
   - `CreateIndexIfNotExists`
   - safe constraint-add operations
4. Run that migration against an existing populated database.
5. Use strict checks, preflight, and controlled repair to synchronize missing additive pieces without dropping data.

The implementation choices in this library are heavily influenced by that scenario. The code is designed to be safe to rerun, explicit about mismatches, and conservative about what it will repair automatically.

## 14. How To Extend The Library Safely

When adding a new operation family, the safest pattern is:

1. Add or extend the public `MigrationBuilder` API.
2. Decide whether the operation can stay as a normal EF Core operation with annotations or needs a dedicated safe operation type.
3. Define an expected-definition shape if live comparison is required.
4. Add or extend shared planning only for provider-neutral rules.
5. Add provider planner rules for provider-specific vetoes or capabilities.
6. Update both provider SQL generators.
7. Add unit tests for:
   - operation creation
   - planner decisions
   - SQL shape
8. Add live MariaDB and PostgreSQL integration coverage.

The most important rule is to reject uncertain cases rather than widen the definition of "safe" silently.

## 15. Known Maintenance Boundaries

The code intentionally accepts a few boundaries that future maintainers should remember:

- It depends on EF Core migration internals deeply enough that provider upgrades deserve careful review.
- Some public extension methods intentionally rely on EF Core appending the created operation last.
- Preflight and execution are kept close together on purpose so that they cannot drift semantically.
- Provider support is intentionally asymmetric where the underlying databases differ. A provider veto is preferred over faking support.

That conservatism is part of the design, not an accident.
