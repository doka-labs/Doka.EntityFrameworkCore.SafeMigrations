namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlModelManagedDataOwnershipTests
{
    [Fact]
    public void EnabledOwnership_FiltersOnlyExcludedModelManagedData()
    {
        using var context = new EnabledMixedOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source: null, model);

        Assert.DoesNotContain(operations, operation => Table(operation) == "core_roles");
        Assert.Contains(operations, operation => Table(operation) == "custom_profiles");
        Assert.True(differ.HasDifferences(source: null, model));
    }

    [Fact]
    public void DisabledOwnership_PreservesProviderGeneratedCoreDataDifference()
    {
        using var context = new DisabledMixedOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source: null, model);

        Assert.Contains(
            operations,
            operation => operation is InsertDataOperation { Table: "core_roles", Schema: "shared" });
        Assert.Contains(
            operations,
            operation => operation is InsertDataOperation { Table: "custom_profiles", Schema: "instance" });
    }

    [Fact]
    public void OwnershipFilter_UsesExactSchemaIdentityAndFailsClosed()
    {
        using var context = new EnabledExternalOnlyOwnershipContext();
        var operation = new InsertDataOperation
        {
            Table = "core_roles",
            Schema = "instance",
            Columns = ["Id", "Name"],
            Values = new object[,] { { 1, "sensitive-value" } },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                source: null,
                RelationalModel(context)));

        Assert.Contains("insert target", exception.Message, StringComparison.Ordinal);
        Assert.Contains("instance.core_roles", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipFilter_HandlesOneHundredThousandSameNamedCrossSchemaOperationsInOrder()
    {
        using var context = new ExactSchemaOwnershipContext();
        var model = RelationalModel(context);
        var values = new object[,] { { 1, "shared" } };
        var operations = new MigrationOperation[100_000];
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            operations[ordinal] = new InsertDataOperation
            {
                Table = "roles",
                Schema = ordinal % 2 == 0 ? "shared" : "instance",
                Columns = ["Id", "Name"],
                Values = values,
            };
        }

        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        Assert.Equal(50_000, filtered.Count);
        for (var ordinal = 0; ordinal < filtered.Count; ordinal++)
        {
            Assert.Same(operations[(ordinal * 2) + 1], filtered[ordinal]);
        }
    }

    private static IRelationalModel RelationalModel(
        DbContext context
    ) => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    private static string? Table(
        MigrationOperation operation
    ) => operation switch
    {
        CreateTableOperation table => table.Name,
        InsertDataOperation data => data.Table,
        UpdateDataOperation data => data.Table,
        DeleteDataOperation data => data.Table,
        _ => null,
    };

    private abstract class OwnershipContext : DbContext
    {
        protected abstract bool IncludeCustomTable { get; }

        protected abstract bool EnableExcludedDataOwnership { get; }

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=test;Username=test;Password=test");

            optionsBuilder.UsePostgreSqlSafeMigrations(options =>
            {
                if (EnableExcludedDataOwnership)
                {
                    options.ExcludeModelManagedDataForExcludedTables();
                }
            });
        }

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<CoreRole>(entity =>
            {
                entity.ToTable(
                    "core_roles",
                    "shared",
                    table => table.ExcludeFromMigrations());

                entity.HasKey(role => role.Id);
                entity.Property(role => role.Name).HasMaxLength(64).IsRequired();
                entity.HasData(new CoreRole { Id = 1, Name = "administrator" });
            });

            if (IncludeCustomTable)
            {
                modelBuilder.Entity<CustomProfile>(entity =>
                {
                    entity.ToTable("custom_profiles", "instance");
                    entity.HasKey(profile => profile.Id);
                    entity.Property(profile => profile.Name).HasMaxLength(64).IsRequired();
                    entity.HasData(new CustomProfile { Id = 1, Name = "default" });
                });
            }
        }
    }

    private sealed class EnabledMixedOwnershipContext : OwnershipContext
    {
        protected override bool IncludeCustomTable => true;

        protected override bool EnableExcludedDataOwnership => true;
    }

    private sealed class DisabledMixedOwnershipContext : OwnershipContext
    {
        protected override bool IncludeCustomTable => true;

        protected override bool EnableExcludedDataOwnership => false;
    }

    private sealed class EnabledExternalOnlyOwnershipContext : OwnershipContext
    {
        protected override bool IncludeCustomTable => false;

        protected override bool EnableExcludedDataOwnership => true;
    }

    private sealed class ExactSchemaOwnershipContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=test;Username=test;Password=test");

            optionsBuilder.UsePostgreSqlSafeMigrations(options =>
                options.ExcludeModelManagedDataForExcludedTables());
        }

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<CoreRole>(entity =>
            {
                entity.ToTable(
                    "roles",
                    "shared",
                    table => table.ExcludeFromMigrations());

                entity.HasKey(role => role.Id);
                entity.Property(role => role.Name).HasMaxLength(64).IsRequired();
            });

            modelBuilder.Entity<CustomRole>(entity =>
            {
                entity.ToTable("roles", "instance");
                entity.HasKey(role => role.Id);
                entity.Property(role => role.Name).HasMaxLength(64).IsRequired();
            });
        }
    }

    private sealed class CoreRole
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class CustomProfile
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class CustomRole
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
