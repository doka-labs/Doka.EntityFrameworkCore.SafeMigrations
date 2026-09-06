namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed class MySqlModelManagedDataOwnershipTests
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
            operation => operation is InsertDataOperation { Table: "core_roles" });
        Assert.Contains(
            operations,
            operation => operation is InsertDataOperation { Table: "custom_profiles" });
    }

    [Fact]
    public void EnabledOwnership_MakesExternalOnlyModelDifferenceEmpty()
    {
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source: null, model);

        Assert.Empty(operations);
        Assert.False(differ.HasDifferences(source: null, model));
    }

    [Fact]
    public void OwnershipFilter_PreservesOriginalListWhenNothingIsRemoved()
    {
        using var context = new IncludedCoreOwnershipContext();
        var model = RelationalModel(context);
        IReadOnlyList<MigrationOperation> operations =
        [
            new InsertDataOperation
            {
                Table = "core_roles",
                Columns = ["Id", "Name"],
                Values = new object[,] { { 1, "administrator" } },
            },
            new SqlOperation { Sql = "SELECT 1;" },
        ];

        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        Assert.Same(operations, filtered);
        Assert.Same(operations[0], filtered[0]);
        Assert.Same(operations[1], filtered[1]);
    }

    [Fact]
    public void OwnershipFilter_RejectsContradictoryUpdateOwnershipWithoutValues()
    {
        using var sourceContext = new EnabledExternalOnlyOwnershipContext();
        using var targetContext = new IncludedCoreOwnershipContext();
        var operation = new UpdateDataOperation
        {
            Table = "core_roles",
            KeyColumns = ["Id"],
            KeyValues = new object[,] { { 42 } },
            Columns = ["Name"],
            Values = new object[,] { { "sensitive-value" } },
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                RelationalModel(sourceContext),
                RelationalModel(targetContext)));

        Assert.Contains("model_managed_data_ownership_transition", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("42", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnershipFilter_RejectsContradictoryInsertAndDeleteOwnershipWithoutValues(
        bool insert
    )
    {
        using var excludedContext = new EnabledExternalOnlyOwnershipContext();
        using var includedContext = new IncludedCoreOwnershipContext();
        var operation = insert
            ? (MigrationOperation)new InsertDataOperation
            {
                Table = "core_roles",
                Columns = ["Id", "Name"],
                Values = new object[,] { { 42, "sensitive-value" } },
            }
            : new DeleteDataOperation
            {
                Table = "core_roles",
                KeyColumns = ["Id"],
                KeyValues = new object[,] { { 42 } },
            };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                RelationalModel(excludedContext),
                RelationalModel(includedContext)));

        Assert.Contains("model_managed_data_ownership_transition", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("42", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipFilter_SuppressesEveryExcludedDataShapeAndPreservesRetainedOrder()
    {
        using var context = new EnabledMixedOwnershipContext();
        var model = RelationalModel(context);
        IReadOnlyList<MigrationOperation> operations =
        [
            Insert("core_roles", 1),
            Insert("custom_profiles", 2),
            Update("core_roles", 3),
            new SqlOperation { Sql = "SELECT 4;" },
            Update("custom_profiles", 5),
            Delete("core_roles", 6),
            Delete("custom_profiles", 7),
        ];

        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            model,
            model);

        Assert.Collection(
            filtered,
            operation => Assert.Same(operations[1], operation),
            operation => Assert.Same(operations[3], operation),
            operation => Assert.Same(operations[4], operation),
            operation => Assert.Same(operations[6], operation));
    }

    [Fact]
    public void OwnershipFilter_FailsClosedForEveryUnresolvedRelevantModel()
    {
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        MigrationOperation[] operations =
        [
            Insert("missing_table", 1),
            Delete("missing_table", 2),
            Update("core_roles", 3),
        ];

        var insertException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operations[0]],
                source: null,
                model));

        var deleteException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operations[1]],
                model,
                target: null));

        var updateSourceException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operations[2]],
                source: null,
                model));

        var updateTargetException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operations[2]],
                model,
                target: null));

        Assert.Contains("insert target", insertException.Message, StringComparison.Ordinal);
        Assert.Contains("delete source", deleteException.Message, StringComparison.Ordinal);
        Assert.Contains("update source", updateSourceException.Message, StringComparison.Ordinal);
        Assert.Contains("update target", updateTargetException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledOwnership_FiltersCoreDataTransitionsAndRetainsCustomTransitions()
    {
        using var sourceContext = new SourceTransitionOwnershipContext();
        using var targetContext = new TargetTransitionOwnershipContext();
        var source = RelationalModel(sourceContext);
        var target = RelationalModel(targetContext);
        var differ = targetContext.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source, target);

        Assert.DoesNotContain(operations, operation => IsDataOperation(operation, "core_roles"));
        Assert.Contains(operations, operation => operation is InsertDataOperation { Table: "custom_profiles" });
        Assert.Contains(operations, operation => operation is UpdateDataOperation { Table: "custom_profiles" });
        Assert.Contains(operations, operation => operation is DeleteDataOperation { Table: "custom_profiles" });
        Assert.True(differ.HasDifferences(source, target));
    }

    [Fact]
    public void EnabledOwnership_ReportsNoPendingDifferenceForCoreDataTransitionsOnly()
    {
        using var sourceContext = new SourceExternalTransitionOwnershipContext();
        using var targetContext = new TargetExternalTransitionOwnershipContext();
        var source = RelationalModel(sourceContext);
        var target = RelationalModel(targetContext);
        var differ = targetContext.GetService<IMigrationsModelDiffer>();

        var operations = differ.GetDifferences(source, target);

        Assert.Empty(operations);
        Assert.False(differ.HasDifferences(source, target));
    }

    [Fact]
    public void EnabledOwnership_DoesNotReinterpretAnExistingCompiledMigration()
    {
        const string connectionString = "Server=127.0.0.1;Port=1;Database=test;User ID=test;Password=test";

        var serverVersion = MySqlServerVersion.MySql(new Version(8, 4, 11));
        using var defaultContext = new SafeMigrationDbContext(connectionString, serverVersion);
        using var ownershipContext = new SafeMigrationDbContext(
            connectionString,
            serverVersion,
            excludeModelManagedDataForExcludedTables: true);

        var defaultSql = GenerateCompiledMigrationSql(defaultContext);
        var ownershipSql = GenerateCompiledMigrationSql(ownershipContext);

        Assert.NotEmpty(defaultSql);
        Assert.Equal(defaultSql, ownershipSql);
    }

    [Fact]
    public void OwnershipFilter_HandlesOneHundredThousandMixedOperationsInOrder()
    {
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        var operations = new MigrationOperation[100_000];
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            operations[ordinal] = ordinal % 2 == 0
                ? new InsertDataOperation
                {
                    Table = "core_roles",
                    Columns = ["Id", "Name"],
                    Values = new object[,] { { ordinal, "external" } },
                }
                : new SqlOperation { Sql = $"SELECT {ordinal};" };
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

    [Fact]
    public void OwnershipFilter_RemovesOneHundredThousandExternallyOwnedOperations()
    {
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        var values = new object[,] { { 1, "external" } };
        var operations = new MigrationOperation[100_000];
        for (var ordinal = 0; ordinal < operations.Length; ordinal++)
        {
            operations[ordinal] = new InsertDataOperation
            {
                Table = "core_roles",
                Columns = ["Id", "Name"],
                Values = values,
            };
        }

        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        Assert.Empty(filtered);
    }

    private static IRelationalModel RelationalModel(
        DbContext context
    ) => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    private static string[] GenerateCompiledMigrationSql(
        DbContext context
    )
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var migrationType = migrationsAssembly.Migrations[CoreConvergenceMigration.MigrationIdentifier];
        var migration = migrationsAssembly.CreateMigration(migrationType, context.Database.ProviderName!);

        return context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(migration.UpOperations, context.Model)
            .Select(static command => command.CommandText)
            .ToArray();
    }

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

    private static bool IsDataOperation(
        MigrationOperation operation,
        string table
    ) => operation switch
    {
        InsertDataOperation insert => StringComparer.Ordinal.Equals(insert.Table, table),
        UpdateDataOperation update => StringComparer.Ordinal.Equals(update.Table, table),
        DeleteDataOperation delete => StringComparer.Ordinal.Equals(delete.Table, table),
        _ => false,
    };

    private static InsertDataOperation Insert(
        string table,
        int id
    ) => new()
    {
        Table = table,
        Columns = ["Id", "Name"],
        Values = new object[,] { { id, "value" } },
    };

    private static UpdateDataOperation Update(
        string table,
        int id
    ) => new()
    {
        Table = table,
        KeyColumns = ["Id"],
        KeyValues = new object[,] { { id } },
        Columns = ["Name"],
        Values = new object[,] { { "value" } },
    };

    private static DeleteDataOperation Delete(
        string table,
        int id
    ) => new()
    {
        Table = table,
        KeyColumns = ["Id"],
        KeyValues = new object[,] { { id } },
    };

    private abstract class OwnershipContext : DbContext
    {
        protected abstract bool ExcludeCoreTable { get; }

        protected abstract bool IncludeCustomTable { get; }

        protected abstract bool EnableExcludedDataOwnership { get; }

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseMySql(
                "Server=127.0.0.1;Port=1;Database=test;User ID=test;Password=test",
                MySqlServerVersion.MySql(new Version(8, 4, 11)));

            optionsBuilder.UseMySqlSafeMigrations(options =>
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
                if (ExcludeCoreTable)
                {
                    entity.ToTable("core_roles", table => table.ExcludeFromMigrations());
                }
                else
                {
                    entity.ToTable("core_roles");
                }

                entity.HasKey(role => role.Id);
                entity.Property(role => role.Name).HasMaxLength(64).IsRequired();
                entity.HasData(new CoreRole { Id = 1, Name = "administrator" });
            });

            if (IncludeCustomTable)
            {
                modelBuilder.Entity<CustomProfile>(entity =>
                {
                    entity.ToTable("custom_profiles");
                    entity.HasKey(profile => profile.Id);
                    entity.Property(profile => profile.Name).HasMaxLength(64).IsRequired();
                    entity.HasData(new CustomProfile { Id = 1, Name = "default" });
                });
            }
        }
    }

    private sealed class EnabledMixedOwnershipContext : OwnershipContext
    {
        protected override bool ExcludeCoreTable => true;

        protected override bool IncludeCustomTable => true;

        protected override bool EnableExcludedDataOwnership => true;
    }

    private sealed class DisabledMixedOwnershipContext : OwnershipContext
    {
        protected override bool ExcludeCoreTable => true;

        protected override bool IncludeCustomTable => true;

        protected override bool EnableExcludedDataOwnership => false;
    }

    private sealed class EnabledExternalOnlyOwnershipContext : OwnershipContext
    {
        protected override bool ExcludeCoreTable => true;

        protected override bool IncludeCustomTable => false;

        protected override bool EnableExcludedDataOwnership => true;
    }

    private sealed class IncludedCoreOwnershipContext : OwnershipContext
    {
        protected override bool ExcludeCoreTable => false;

        protected override bool IncludeCustomTable => false;

        protected override bool EnableExcludedDataOwnership => true;
    }

    private abstract class TransitionOwnershipContext : DbContext
    {
        protected abstract bool IncludeCustomTable { get; }

        protected abstract OwnershipSeedState SeedState { get; }

        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        )
        {
            optionsBuilder.UseMySql(
                "Server=127.0.0.1;Port=1;Database=test;User ID=test;Password=test",
                MySqlServerVersion.MySql(new Version(8, 4, 11)));

            optionsBuilder.UseMySqlSafeMigrations(options =>
                options.ExcludeModelManagedDataForExcludedTables());
        }

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.ApplyConfiguration(new TransitionCoreRoleConfiguration(SeedState));

            if (IncludeCustomTable)
            {
                modelBuilder.ApplyConfiguration(new TransitionCustomProfileConfiguration(SeedState));
            }
        }
    }

    private sealed class SourceTransitionOwnershipContext : TransitionOwnershipContext
    {
        protected override bool IncludeCustomTable => true;

        protected override OwnershipSeedState SeedState => OwnershipSeedState.Source;
    }

    private sealed class TargetTransitionOwnershipContext : TransitionOwnershipContext
    {
        protected override bool IncludeCustomTable => true;

        protected override OwnershipSeedState SeedState => OwnershipSeedState.Target;
    }

    private sealed class SourceExternalTransitionOwnershipContext : TransitionOwnershipContext
    {
        protected override bool IncludeCustomTable => false;

        protected override OwnershipSeedState SeedState => OwnershipSeedState.Source;
    }

    private sealed class TargetExternalTransitionOwnershipContext : TransitionOwnershipContext
    {
        protected override bool IncludeCustomTable => false;

        protected override OwnershipSeedState SeedState => OwnershipSeedState.Target;
    }

    private sealed class TransitionCoreRoleConfiguration(
        OwnershipSeedState state
    ) : IEntityTypeConfiguration<CoreRole>
    {
        public void Configure(
            EntityTypeBuilder<CoreRole> builder
        )
        {
            builder.ToTable("core_roles", table => table.ExcludeFromMigrations());
            builder.HasKey(role => role.Id);
            builder.Property(role => role.Name).HasMaxLength(64).IsRequired();

            builder.HasData(state == OwnershipSeedState.Source
                ?
                [
                    new CoreRole { Id = 1, Name = "administrator" },
                    new CoreRole { Id = 2, Name = "member" },
                ]
                :
                [
                    new CoreRole { Id = 1, Name = "owner" },
                    new CoreRole { Id = 3, Name = "auditor" },
                ]);
        }
    }

    private sealed class TransitionCustomProfileConfiguration(
        OwnershipSeedState state
    ) : IEntityTypeConfiguration<CustomProfile>
    {
        public void Configure(
            EntityTypeBuilder<CustomProfile> builder
        )
        {
            builder.ToTable("custom_profiles");
            builder.HasKey(profile => profile.Id);
            builder.Property(profile => profile.Name).HasMaxLength(64).IsRequired();

            builder.HasData(state == OwnershipSeedState.Source
                ?
                [
                    new CustomProfile { Id = 10, Name = "default" },
                    new CustomProfile { Id = 11, Name = "legacy" },
                ]
                :
                [
                    new CustomProfile { Id = 10, Name = "premium" },
                    new CustomProfile { Id = 12, Name = "audit" },
                ]);
        }
    }

    private enum OwnershipSeedState
    {
        Source,
        Target,
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
}
