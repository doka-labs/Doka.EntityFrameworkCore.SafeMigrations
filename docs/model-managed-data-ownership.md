# Model-managed-data ownership across Core and custom contexts

## When this option applies

Use this contract when one database has independent migration lineages:

- a shared `CoreDbContext` owns the Core schema and Core `HasData` changes; and
- an instance-specific context derives from `CoreDbContext`, retains its model
  for queries and relationships, but owns only the instance-specific schema and
  model-managed data.

Each lineage needs its own concrete context, migration project and snapshot,
migration assembly, design-time factory, and migrations-history table. Sharing
a connection string does not combine ownership.

Do not enable the option merely to bypass an inverse-pairing error. Every table
excluded by the custom context must have another identified migration owner.

## Model boundary

Build the shared model first, mark every inherited Core table as excluded from
the custom migration lineage, and then add custom mappings. Exclusion keeps the
entity available to the runtime model; it prevents the custom migration line
from owning that table's DDL.

```csharp
public sealed class KraftanlagenDbContext : CoreDbContext
{
    public KraftanlagenDbContext(
        DbContextOptions<KraftanlagenDbContext> options
    ) : base(options)
    {
    }

    protected override void ConfigureApplicationModel(
        ModelBuilder modelBuilder
    )
    {
        ExcludeCoreModelFromMigrations(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(KraftanlagenDbContext).Assembly);
    }
}
```

The exact exclusion helper belongs to the application's Core/custom context
architecture. It must cover every Core table mapping, including mapping
fragments, before custom-only entities are added. SafeMigrations consumes EF's
relational `IsExcludedFromMigrations` metadata; it does not infer ownership from
the CLR base type, assembly name, table prefix, connection-string key, or
history-table name.

## SafeMigrations configuration

Enable the ownership extension on the custom context at both design time and
runtime:

```csharp
var optionsBuilder = new DbContextOptionsBuilder<KraftanlagenDbContext>();

optionsBuilder.UseMySql(
    connectionString,
    serverVersion,
    mySql =>
    {
        mySql.MigrationsHistoryTable(
            "__EFMigrationsHistory_KraftanlagenDbContext");
    });

optionsBuilder.UseMySqlSafeMigrations(safeMigrations =>
{
    safeMigrations.ExcludeModelManagedDataForExcludedTables();
});
```

For PostgreSQL, use the same callback on
`UsePostgreSqlSafeMigrations(...)`. The typed options builder already identifies
`KraftanlagenDbContext` as the canonical migration context. Do not select
`CoreDbContext` in a generic SafeMigrations overload for the custom lineage.

The option is disabled by default. When enabled, the decorated
`IMigrationsModelDiffer` removes only provider-generated
`InsertDataOperation`, `UpdateDataOperation`, and `DeleteDataOperation`
instances for exact excluded relational tables:

| Operation | Ownership evidence |
| --- | --- |
| Insert | Target table is excluded |
| Delete | Source table is excluded |
| Update | Source and target tables are both excluded |

An update whose exclusion state changes between source and target is an
ownership transition and fails closed. Missing table metadata also fails
closed. Included-table operations retain their order and continue through
store-type completion and exact inverse pairing.

The option affects newly calculated differences only. It does not edit or
reinterpret an existing migration file. Snapshots retain inherited Core entity
metadata and model-managed values so runtime mapping and custom-to-Core
relationships remain complete.

## Project and command layout

A minimal layout is:

```text
src/CoreDbContext/
|-- CoreDbContext.cs
`-- Migrations/

src/KraftanlagenDbContext/
|-- KraftanlagenDbContext.cs
`-- Migrations/

eng/MigrationGenerator/
|-- CoreDbContextDesignFactory.cs
`-- KraftanlagenDbContextDesignFactory.cs
```

Create the custom migration in the project that owns the custom context:

```bash
dotnet ef migrations add "$migration_name" \
  --context KraftanlagenDbContext \
  --project ../../src/KraftanlagenDbContext \
  --startup-project .
```

`--project` selects the migration and snapshot destination.
`--startup-project` supplies the executable design-time environment. The
history-table name changes database bookkeeping, not generated file placement.

## Required review

Before accepting a custom migration, verify all of the following:

1. The Core migration contains Core schema and Core model-managed changes.
2. The custom migration contains no Core schema or Core model-managed change.
3. The custom migration contains every intended custom schema and
   model-managed change.
4. The custom snapshot retains inherited Core mappings and exclusion markers.
5. The custom SQL does not create, alter, seed, or remove a Core table.
6. Core and custom migrations update only their own history tables.
7. Applying Core and then custom succeeds; rolling back custom does not change
   Core schema, data, or history.
8. Pending-model detection uses the same ownership configuration as
   scaffolding.

The repository's real EF tooling qualification implements these checks for
MySQL, MariaDB, and PostgreSQL. Provider qualification still has to pass on the
supported server matrix before release.

## Primary sources

Retrieved 2026-09-05:

- [EF Core target and startup projects](https://learn.microsoft.com/en-us/ef/core/cli/dotnet#target-project-and-startup-project)
- [EF Core design-time context creation](https://learn.microsoft.com/en-us/ef/core/cli/dbcontext-creation)
- [EF Core separate migrations projects](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/projects)
- [EF Core custom migrations history](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/history-table)
- [EF Core excluded tables](https://learn.microsoft.com/en-us/ef/core/modeling/entity-types#excluding-from-migrations)
- [EF Core model-managed data](https://learn.microsoft.com/en-us/ef/core/modeling/data-seeding#model-managed-data)
