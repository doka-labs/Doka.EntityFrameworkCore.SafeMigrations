namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects excluded-table seed ownership, transition boundaries, and stable retained order.</summary>
public sealed class MySqlModelManagedDataOwnershipTests
{
    /// <summary>
    /// Filters only model-managed data owned by excluded tables, retaining unrelated provider operations.
    /// </summary>
    [Fact]
    public void EnabledOwnership_FiltersOnlyExcludedModelManagedData()
    {
        // Arrange
        using var context = new EnabledMixedOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        // Act
        var operations = differ.GetDifferences(source: null, model);

        var hasDifferences = differ.HasDifferences(source: null, model);

        // Assert
        Assert.DoesNotContain(operations, operation => Table(operation) == "core_roles");
        Assert.Contains(operations, operation => Table(operation) == "custom_profiles");
        Assert.True(hasDifferences);
    }

    /// <summary>
    /// Preserves provider-generated core seed differences when excluded-table ownership filtering is disabled.
    /// </summary>
    [Fact]
    public void DisabledOwnership_PreservesProviderGeneratedCoreDataDifference()
    {
        // Arrange
        using var context = new DisabledMixedOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        // Act
        var operations = differ.GetDifferences(source: null, model);

        // Assert
        Assert.Contains(
            operations,
            operation => operation is InsertDataOperation { Table: "core_roles" });

        Assert.Contains(
            operations,
            operation => operation is InsertDataOperation { Table: "custom_profiles" });
    }

    /// <summary>Treats an externally owned model-only seed difference as an empty managed migration.</summary>
    [Fact]
    public void EnabledOwnership_MakesExternalOnlyModelDifferenceEmpty()
    {
        // Arrange
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        var differ = context.GetService<IMigrationsModelDiffer>();

        // Act
        var operations = differ.GetDifferences(source: null, model);

        var hasDifferences = differ.HasDifferences(source: null, model);

        // Assert
        Assert.Empty(operations);
        Assert.False(hasDifferences);
    }

    /// <summary>Preserves the original list and operation instances when ownership filtering removes nothing.</summary>
    [Fact]
    public void OwnershipFilter_PreservesOriginalListWhenNothingIsRemoved()
    {
        // Arrange
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

        // Act
        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        // Assert
        Assert.Same(operations, filtered);
        Assert.Same(operations[0], filtered[0]);
        Assert.Same(operations[1], filtered[1]);
    }

    /// <summary>Rejects contradictory source and target ownership without disclosing managed row values.</summary>
    [Fact]
    public void OwnershipFilter_RejectsContradictoryUpdateOwnershipWithoutValues()
    {
        // Arrange
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

        // Act
        var capturedException = Record.Exception(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                RelationalModel(sourceContext),
                RelationalModel(targetContext)));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Contains("model_managed_data_ownership_transition", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("42", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects contradictory insert or delete ownership without disclosing managed row values.</summary>
    /// <param name="insert">Whether the seed transition inserts rather than removes the selected row.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnershipFilter_RejectsContradictoryInsertAndDeleteOwnershipWithoutValues(
        bool insert
    )
    {
        // Arrange
        using var excludedContext = new EnabledExternalOnlyOwnershipContext();
        using var includedContext = new IncludedCoreOwnershipContext();
        MigrationOperation operation = insert
            ? new InsertDataOperation
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

        // Act
        var capturedException = Record.Exception(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                RelationalModel(excludedContext),
                RelationalModel(includedContext)));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(capturedException);
        Assert.Contains("model_managed_data_ownership_transition", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("42", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes every externally owned seed shape while retaining the relative order of surviving operations.
    /// </summary>
    [Fact]
    public void OwnershipFilter_SuppressesEveryExcludedDataShapeAndPreservesRetainedOrder()
    {
        // Arrange
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

        // Act
        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            model,
            model);

        // Assert
        Assert.Collection(
            filtered,
            operation => Assert.Same(operations[1], operation),
            operation => Assert.Same(operations[3], operation),
            operation => Assert.Same(operations[4], operation),
            operation => Assert.Same(operations[6], operation));
    }

    /// <summary>
    /// Rejects the specific missing source or target instead of accidentally borrowing another model.
    /// </summary>
    /// <param name="operationKind">The seed transition whose relevant model is unavailable.</param>
    /// <param name="missingSource">Whether the unresolved model supplies the old rather than new row contract.</param>
    /// <param name="expectedReason">The transition role that the failure must identify.</param>
    [Theory]
    [InlineData("insert", false, "insert target")]
    [InlineData("delete", true, "delete source")]
    [InlineData("update", true, "update source")]
    [InlineData("update", false, "update target")]
    public void OwnershipFilter_FailsClosedForEveryUnresolvedRelevantModel(
        string operationKind,
        bool missingSource,
        string expectedReason
    )
    {
        // Arrange
        using var context = new EnabledExternalOnlyOwnershipContext();
        var model = RelationalModel(context);
        var operation = operationKind switch
        {
            "insert" => (MigrationOperation)Insert("missing_table", 1),
            "delete" => Delete("missing_table", 2),
            "update" => Update("core_roles", 3),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKind)),
        };

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelManagedDataOwnershipFilter.Filter(
                [operation],
                source: missingSource
                    ? null
                    : model,
                target: missingSource
                    ? model
                    : null));

        // Assert
        Assert.Contains(expectedReason,
            Assert.IsType<InvalidOperationException>(exception)
                .Message,
            StringComparison.Ordinal);
    }

    /// <summary>Filters excluded core seed transitions while retaining application-owned custom transitions.</summary>
    [Fact]
    public void EnabledOwnership_FiltersCoreDataTransitionsAndRetainsCustomTransitions()
    {
        // Arrange
        using var sourceContext = new SourceTransitionOwnershipContext();
        using var targetContext = new TargetTransitionOwnershipContext();
        var source = RelationalModel(sourceContext);
        var target = RelationalModel(targetContext);
        var differ = targetContext.GetService<IMigrationsModelDiffer>();

        // Act
        var operations = differ.GetDifferences(source, target);

        var hasDifferences = differ.HasDifferences(source, target);

        // Assert
        Assert.DoesNotContain(operations, operation => IsDataOperation(operation, "core_roles"));
        Assert.Contains(operations, operation => operation is InsertDataOperation { Table: "custom_profiles" });
        Assert.Contains(operations, operation => operation is UpdateDataOperation { Table: "custom_profiles" });
        Assert.Contains(operations, operation => operation is DeleteDataOperation { Table: "custom_profiles" });
        Assert.True(hasDifferences);
    }

    /// <summary>Reports no pending migration when only externally owned seed data changed.</summary>
    [Fact]
    public void EnabledOwnership_ReportsNoPendingDifferenceForCoreDataTransitionsOnly()
    {
        // Arrange
        using var sourceContext = new SourceExternalTransitionOwnershipContext();
        using var targetContext = new TargetExternalTransitionOwnershipContext();
        var source = RelationalModel(sourceContext);
        var target = RelationalModel(targetContext);
        var differ = targetContext.GetService<IMigrationsModelDiffer>();

        // Act
        var operations = differ.GetDifferences(source, target);

        var hasDifferences = differ.HasDifferences(source, target);

        // Assert
        Assert.Empty(operations);
        Assert.False(hasDifferences);
    }

    /// <summary>
    /// Leaves previously compiled migration SQL unchanged when the current ownership option is enabled.
    /// </summary>
    [Fact]
    public void EnabledOwnership_DoesNotReinterpretAnExistingCompiledMigration()
    {
        // Arrange
        const string connectionString = "Server=127.0.0.1;Port=1;Database=test;User ID=test;Password=test";

        var serverVersion = MySqlServerVersion.MySql(new Version(8, 4, 11));
        using var defaultContext = new SafeMigrationDbContext(connectionString, serverVersion);
        using var ownershipContext = new SafeMigrationDbContext(
            connectionString,
            serverVersion,
            excludeModelManagedDataForExcludedTables: true);

        // Act
        var defaultSql = GenerateCompiledMigrationSql(defaultContext);
        var ownershipSql = GenerateCompiledMigrationSql(ownershipContext);

        // Assert
        Assert.NotEmpty(defaultSql);
        Assert.Equal(
            defaultSql.Select(MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation),
            ownershipSql.Select(MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation));
    }

    /// <summary>Preserves reference identity and order while filtering a hundred thousand mixed operations.</summary>
    [Fact]
    public void OwnershipFilter_HandlesOneHundredThousandMixedOperationsInOrder()
    {
        // Arrange
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

        // Act
        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        // Assert
        Assert.Equal(50_000, filtered.Count);
        for (var ordinal = 0; ordinal < filtered.Count; ordinal++)
        {
            Assert.Same(operations[ordinal * 2 + 1], filtered[ordinal]);
        }
    }

    /// <summary>
    /// Returns an empty result when every large-workload seed operation belongs to an external owner.
    /// </summary>
    [Fact]
    public void OwnershipFilter_RemovesOneHundredThousandExternallyOwnedOperations()
    {
        // Arrange
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

        // Act
        var filtered = SafeMigrationModelManagedDataOwnershipFilter.Filter(
            operations,
            source: null,
            model);

        // Assert
        Assert.Empty(filtered);
    }

    private static IRelationalModel RelationalModel(
        DbContext context
    ) => context
        .GetService<IDesignTimeModel>()
        .Model
        .GetRelationalModel();

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

            // WHY: Retain this explicit materialization/copy path and its collection lowering.
            // ReSharper disable once UseCollectionExpression
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
                entity
                    .Property(role => role.Name)
                    .HasMaxLength(64)
                    .IsRequired();

                entity.HasData(
                    new CoreRole
                    {
                        Id = 1,
                        Name = "administrator",
                    });
            });

            if (IncludeCustomTable)
            {
                modelBuilder.Entity<CustomProfile>(entity =>
                {
                    entity.ToTable("custom_profiles");
                    entity.HasKey(profile => profile.Id);
                    entity
                        .Property(profile => profile.Name)
                        .HasMaxLength(64)
                        .IsRequired();

                    entity.HasData(
                        new CustomProfile
                        {
                            Id = 1,
                            Name = "default",
                        });
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

    private sealed class TransitionCoreRoleConfiguration : IEntityTypeConfiguration<CoreRole>
    {
        private readonly OwnershipSeedState _state;

        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="state">The source or target seed state configured for this model.</param>
        public TransitionCoreRoleConfiguration(
            OwnershipSeedState state
        )
        {
            _state = state;
        }

        public void Configure(
            EntityTypeBuilder<CoreRole> builder
        )
        {
            builder.ToTable("core_roles", table => table.ExcludeFromMigrations());
            builder.HasKey(role => role.Id);
            builder
                .Property(role => role.Name)
                .HasMaxLength(64)
                .IsRequired();

            builder.HasData(
                _state == OwnershipSeedState.Source
                    ?
                    [
                        new CoreRole
                        {
                            Id = 1,
                            Name = "administrator",
                        },
                        new CoreRole
                        {
                            Id = 2,
                            Name = "member",
                        },
                    ]
                    :
                    [
                        new CoreRole
                        {
                            Id = 1,
                            Name = "owner",
                        },
                        new CoreRole
                        {
                            Id = 3,
                            Name = "auditor",
                        },
                    ]);
        }
    }

    private sealed class TransitionCustomProfileConfiguration : IEntityTypeConfiguration<CustomProfile>
    {
        private readonly OwnershipSeedState _state;

        /// <summary>Initializes the explicit dependencies of this isolated test helper.</summary>
        /// <param name="state">The source or target seed state configured for this model.</param>
        public TransitionCustomProfileConfiguration(
            OwnershipSeedState state
        )
        {
            _state = state;
        }

        public void Configure(
            EntityTypeBuilder<CustomProfile> builder
        )
        {
            builder.ToTable("custom_profiles");
            builder.HasKey(profile => profile.Id);
            builder
                .Property(profile => profile.Name)
                .HasMaxLength(64)
                .IsRequired();

            builder.HasData(
                _state == OwnershipSeedState.Source
                    ?
                    [
                        new CustomProfile
                        {
                            Id = 10,
                            Name = "default",
                        },
                        new CustomProfile
                        {
                            Id = 11,
                            Name = "legacy",
                        },
                    ]
                    :
                    [
                        new CustomProfile
                        {
                            Id = 10,
                            Name = "premium",
                        },
                        new CustomProfile
                        {
                            Id = 12,
                            Name = "audit",
                        },
                    ]);
        }
    }

    private enum OwnershipSeedState
    {
        Source,
        Target,
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class CoreRole
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class CustomProfile
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }
}
