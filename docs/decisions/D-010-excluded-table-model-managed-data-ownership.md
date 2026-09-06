---
id: D-010
status: implemented
date: 2026-09-05
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Model-managed-data ownership for excluded tables in independent migration lineages"
supersedes: []
superseded-by: []
amends: [D-005, D-008, D-009]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-010 -- Extend excluded-table ownership to model-managed data explicitly

## Context and Problem Statement

A derived `ExtendedApplicationDbContext` can retain the shared
`ApplicationDbContext` model while owning a separate migration project,
snapshot, assembly, and history table. EF excludes the inherited tables from
structural migrations, but can still calculate model-managed data operations
for their inherited `HasData` declarations. SafeMigrations then correctly
rejects an insert without exact inverse evidence.

The decision is how an extended migration lineage declares that another lineage
owns both the schema and model-managed data of its excluded tables without
requiring a second entity-configuration system or weakening inverse pairing.

## Decision Drivers

- Existing behavior must remain fail-closed by default.
- Runtime mappings and relationships between shared and extended entities must
  stay available.
- Ownership must use exact relational metadata, not naming or inheritance.
- Included instance-specific data must retain exact inverse pairing and
  operation order.
- Design-time and runtime pending-model behavior must agree.
- Filtering must remain linear and avoid copying model-managed row matrices.

## Considered Options

- Add an explicit option extending excluded-table ownership to data differences
- Introduce a separate marker interface for every shared `HasData` configuration
- Filter every excluded table implicitly
- Infer ownership from context inheritance, assemblies, or history names

## Decision Outcome

Chosen option: "Add an explicit option extending excluded-table ownership to
data differences", because EF's exact `IsExcludedFromMigrations` table metadata
plus an opt-in expresses the real ownership boundary without changing defaults.

`ExcludeModelManagedDataForExcludedTables()` flows through the immutable
SafeMigrations/provider options and into the model-differ decorator. Insert uses
target-table exclusion, delete uses source-table exclusion, and update requires
both sides to be excluded. Missing metadata and source/target disagreement fail
closed. `HasDifferences` observes the same filtered operation set as
`GetDifferences`.

The filter indexes source and target tables once and scans operations once. It
returns the original operation list when nothing is removed and otherwise
retains the original operation objects and order. Filtering occurs before
store-type enrichment and row pairing. Existing compiled migrations are not
reinterpreted.

### Consequences

- Good, because an extended context can keep the complete shared application
  model without taking ownership of shared seed migrations.
- Good, because the compatibility default and exact inverse contract remain
  unchanged.
- Good, because one option replaces per-entity ownership lists and parallel
  configuration interfaces.
- Bad, because the application must maintain exact table-exclusion metadata and
  identical design-time/runtime options.
- Bad, because enabled ownership filtering must materialize provider differences
  during `HasDifferences` so it can distinguish unowned data operations from
  owned differences without weakening the pending-model check.
- Bad, because an intentional ownership transition must be modeled explicitly
  rather than inferred.

### Confirmation

- Run Core and provider tests for default-off, inserts, updates, deletes,
  ambiguity, schema identity, retained order, and no-removal identity.
- Run real EF tooling with separate application/extended projects, snapshots,
  migrations, histories, application, rollback, replay, and SQL inspection.
- Qualify MySQL 8.4/9.7, MariaDB 10.11/11.4/11.8/12.3, and PostgreSQL 14-18.
- Run large mixed-operation tests and verify no model-managed row values enter
  diagnostics.

## Pros and Cons of the Options

### Add an explicit option extending excluded-table ownership to data differences

- Good, because the choice is local, typed, reviewable, and default-off.
- Bad, because every extended lineage must configure it consistently at design
  time and runtime.

### Introduce a separate marker interface for every shared `HasData` configuration

- Good, because data ownership would be visible at every configuration type.
- Bad, because it duplicates the existing `IEntityTypeConfiguration<TEntity>`
  workflow and relies on every developer selecting the right parallel marker.

### Filter every excluded table implicitly

- Good, because no new option is required.
- Bad, because existing consumers may exclude schema management while retaining
  intentional data ownership.

### Infer ownership from context inheritance, assemblies, or history names

- Good, because existing names could appear to require no configuration.
- Bad, because none proves relational table ownership and refactoring could
  silently change migration behavior.

## More Information

D-008 remains authoritative for SafeMigrations design-time composition. D-009
remains authoritative for source-frozen model-managed operations retained by a
lineage. D-005 remains authoritative for bounded evidence and non-disclosure.

### Re-evaluation Triggers

- EF Core changes excluded-table or model-differ behavior.
- A supported provider can no longer expose exact relational exclusion metadata.
- A real ownership transition requires a separately reviewed migration contract.

### Decision History

- 2026-09-05: Decision recorded with status proposed.
- 2026-09-05: Dominic Kalkbrenner selected explicit default-off ownership;
  status changed from proposed to accepted.
- 2026-09-05: Implemented Core/provider option propagation, exact filtering,
  real EF tooling qualification, tests, API documentation, and operator guide;
  status changed from accepted to implemented.

### Implementation References

- [Ownership filter](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationModelManagedDataOwnershipFilter.cs)
- [Model-differ composition](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationMigrationsModelDiffer.cs)
- [Consumer guide](../model-managed-data-ownership.md)
- [Real EF tooling qualification](../../eng/verify-ef-tooling.sh)

### Sources

- [EF Core excluded tables](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types#excluding-from-migrations) (primary source; retrieved 2026-09-05)
- [EF Core model-managed data](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding#model-managed-data) (primary source; retrieved 2026-09-05)
- [EF Core target and startup projects](https://learn.microsoft.com/en-us/ef/core/cli/dotnet#target-project-and-startup-project) (primary source; retrieved 2026-09-05)
- [EF Core 10.0.11 MigrationsModelDiffer](https://github.com/dotnet/efcore/blob/v10.0.11/src/EFCore.Relational/Migrations/Internal/MigrationsModelDiffer.cs) (primary source; retrieved 2026-09-05)
