# EF Core / Provider Upgrade Maintenance Risk

This note documents one of the most important maintenance boundaries in the project:

- the MariaDB and PostgreSQL providers replace EF Core's migrations SQL generator
- both provider generators subclass provider-specific migrations generator base classes
- both implementations intentionally suppress `EF1001`

That combination is powerful, but it also means provider upgrades deserve deliberate review.

## Where The Risk Lives

The main touchpoints are:

- [MariaDbSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/SqlGeneration/MariaDbSafeMigrationsSqlGenerator.cs)
- [PostgreSqlSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationsSqlGenerator.cs)
- [MariaDbSafeMigrationOptionsBuilderExtensions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/Extensions/MariaDbSafeMigrationOptionsBuilderExtensions.cs)
- [PostgreSqlSafeMigrationOptionsBuilderExtensions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Extensions/PostgreSqlSafeMigrationOptionsBuilderExtensions.cs)
- [MariaDbServiceCollectionExtensions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/Extensions/MariaDbServiceCollectionExtensions.cs)
- [PostgreSqlServiceCollectionExtensions.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Extensions/PostgreSqlServiceCollectionExtensions.cs)

The provider SQL generators inherit from:

- `MySqlMigrationsSqlGenerator`
- `NpgsqlMigrationsSqlGenerator`

Those base classes are close enough to provider internals that the compiler emits `EF1001`, which is why both generator files explicitly suppress that warning.

## Why `EF1001` Matters Here

`EF1001` is the warning EF Core uses to signal that code is relying on internal APIs that may change without normal compatibility guarantees.

In this project, the warning is not suppressed casually. It is suppressed because the library intentionally integrates at the migrations SQL-generator layer, which is the only practical place to:

- inspect provider-specific `MigrationOperation`s
- reuse provider DDL generation
- wrap provider DDL in guarded existence / strict / repair / preflight behavior

There is no equally capable high-level hook that would give the same control with less risk.

So the right reading is:

- this is an intentional architecture decision
- it is acceptable
- it carries explicit maintenance cost

## What Can Break During Upgrades

When upgrading EF Core, Pomelo, or Npgsql, the following areas are at risk:

### Constructor Signatures

The provider base classes can change constructor dependencies or required service types.

That would break:

- [MariaDbSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.MariaDb/SqlGeneration/MariaDbSafeMigrationsSqlGenerator.cs)
- [PostgreSqlSafeMigrationsSqlGenerator.cs](../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationsSqlGenerator.cs)

### Overridable Method Shape

Protected `Generate(...)` overloads, helper methods, or base behavior can change.

That matters because this library overrides provider generation for:

- create/add/drop operations
- rename operations
- alter-column behavior
- safe constraint families

Even when code still compiles, behavior can drift if the provider changes how it expects those methods to cooperate with the base generator.

### Provider SQL Helper Behavior

Changes in quoting, type rendering, constraint rendering, or DDL statement splitting can affect:

- generated SQL shape
- single-statement assumptions
- alter-column handling
- strict-mode comparison behavior when provider-generated SQL changes indirectly influence expectations

### Command Batching Or Statement-Termination Behavior

This project builds some statements manually and relies on `MigrationCommandListBuilder` plus provider helpers. Changes in batching or statement generation could affect:

- command boundaries
- whether certain helper-generated SQL stays single-statement
- preflight and guarded multi-statement flows

### Service Replacement Wiring

The registration layer uses `ReplaceService<IMigrationsSqlGenerator, ...>()` and service-collection replacement for the active provider generator.

If provider wiring changes, the library can appear to register successfully while silently no longer intercepting migrations the way it expects.

## Symptoms To Watch For After An Upgrade

After an EF Core or provider version change, watch for:

- compile errors in the provider generator constructors
- compile errors in overridden `Generate(...)` methods
- failing SQL-shape unit tests
- failing MariaDB or PostgreSQL integration tests
- generated SQL that falls back to the provider default instead of the safe generator
- missing strict-mode mismatches
- preflight unexpectedly executing DDL
- repaired operations changing from additive to destructive behavior

The live integration suites are especially important here, because many upgrade problems are behavioral rather than purely compile-time.

## Required Upgrade Procedure

When upgrading EF Core, Pomelo, or Npgsql, the minimum safe procedure is:

1. Update one provider stack at a time when possible.
2. Build the solution and inspect the two provider generator files first.
3. Review constructor changes and overridden method signatures in the new provider version.
4. Run the full solution test suite:
   - `dotnet test Doka.EntityFrameworkCore.SafeMigrations.slnx`
5. Review SQL-shape changes in the shared unit tests.
6. Pay special attention to the live MariaDB and PostgreSQL integration suites.
7. Re-read the generated SQL for:
   - strict-mode guarded operations
   - preflight paths
   - repair paths
   - alter-column paths
   - rename/drop guarded paths
8. Only keep `#pragma warning disable EF1001` in place if the upgraded provider surface still justifies the same architecture.

## Why The Integration Tests Matter So Much

This project already has strong live provider coverage, and that is the main defense against upgrade regressions.

The reason is simple:

- generator subclassing can keep compiling while behavior changes underneath
- provider catalog semantics can shift
- provider DDL generation can change shape without obvious compile-time signals

So for this project, a green unit test run is not enough on its own after provider upgrades. The live MariaDB and PostgreSQL suites are part of the real maintenance contract.

## What Maintainers Should Not Do

During upgrades, do not:

- assume `EF1001` is harmless and ignore the changed surface
- remove provider-specific test coverage to make upgrades easier
- widen supported behavior without provider-specific verification
- keep suppressed warnings without checking whether the integration point itself has changed

## Bottom Line

The project intentionally uses a low-level but appropriate integration point:

- it gives the control needed for safe migrations
- it is worth the maintenance cost
- it requires disciplined review whenever EF Core or the underlying providers change

That is the real contract behind the `EF1001` suppression.
