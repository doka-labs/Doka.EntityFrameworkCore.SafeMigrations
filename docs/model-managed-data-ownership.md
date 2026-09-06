# Model-managed-data ownership across application contexts

## When this option applies

Use this contract when one database has independent migration lineages:

- a shared `ApplicationDbContext` owns the shared schema and shared `HasData`
  changes; and
- an instance-specific `ExtendedApplicationDbContext` derives from
  `ApplicationDbContext`, retains its model for queries and relationships, but
  owns only the instance-specific schema and model-managed data.

Each lineage needs its own concrete context, migration project and snapshot,
migration assembly, design-time factory, and migrations-history table. Sharing
a connection string does not combine ownership.

Do not enable the option merely to bypass an inverse-pairing error. Every table
excluded by the extended context must have another identified migration owner.

## Model boundary

Build the shared model first, mark every inherited application table as excluded
from the extended migration lineage, and then add instance-specific mappings.
Exclusion keeps the entity available to the runtime model; it prevents the
extended migration line from owning that table's DDL.

```csharp
public sealed class ExtendedApplicationDbContext : ApplicationDbContext
{
    public ExtendedApplicationDbContext(
        DbContextOptions<ExtendedApplicationDbContext> options
    ) : base(options)
    {
    }

    protected override void ConfigureApplicationModel(
        ModelBuilder modelBuilder
    )
    {
        ExcludeApplicationModelFromMigrations(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ExtendedApplicationDbContext).Assembly);
    }
}
```

The exact exclusion helper belongs to the application's shared/extended context
architecture. It must cover every shared table mapping, including mapping
fragments, before instance-specific entities are added. SafeMigrations consumes
EF's relational `IsExcludedFromMigrations` metadata; it does not infer ownership
from the CLR base type, assembly name, table prefix, connection-string key, or
history-table name.

## SafeMigrations configuration

Enable the ownership extension on the extended context at both design time and
runtime:

```csharp
var optionsBuilder = new DbContextOptionsBuilder<ExtendedApplicationDbContext>();

optionsBuilder.UseMySql(
    connectionString,
    serverVersion,
    mySql =>
    {
        mySql.MigrationsHistoryTable(
            "__EFMigrationsHistory_ExtendedApplicationDbContext");
    });

optionsBuilder.UseMySqlSafeMigrations(safeMigrations =>
{
    safeMigrations.ExcludeModelManagedDataForExcludedTables();
});
```

For PostgreSQL, use the same callback on
`UsePostgreSqlSafeMigrations(...)`. The typed options builder already identifies
`ExtendedApplicationDbContext` as the canonical migration context. Do not select
`ApplicationDbContext` in a generic SafeMigrations overload for the extended
lineage.

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
reinterpret an existing migration file. Snapshots retain inherited application
entity metadata and model-managed values so runtime mapping and relationships
between shared and instance-specific entities remain complete.

## Project and command layout

A minimal layout is:

```text
src/ApplicationDbContext/
|-- ApplicationDbContext.cs
`-- Migrations/

src/ExtendedApplicationDbContext/
|-- ExtendedApplicationDbContext.cs
`-- Migrations/

eng/MigrationGenerator/
|-- ApplicationDbContextDesignFactory.cs
`-- ExtendedApplicationDbContextDesignFactory.cs
```

Create the extended migration in the project that owns the extended context:

```bash
dotnet ef migrations add "$migration_name" \
  --context ExtendedApplicationDbContext \
  --project ../../src/ExtendedApplicationDbContext \
  --startup-project .
```

`--project` selects the migration and snapshot destination.
`--startup-project` supplies the executable design-time environment. The
history-table name changes database bookkeeping, not generated file placement.

## Required review

Before accepting an extended migration, verify all of the following:

1. The application migration contains shared schema and shared model-managed
   changes.
2. The extended migration contains no shared schema or shared model-managed
   change.
3. The extended migration contains every intended instance-specific schema and
   model-managed change.
4. The extended snapshot retains inherited application mappings and exclusion
   markers.
5. The extended SQL does not create, alter, seed, or remove a shared table.
6. Application and extended migrations update only their own history tables.
7. Applying application and then extended migrations succeeds; rolling back the
   extended migration does not change shared schema, data, or history.
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
