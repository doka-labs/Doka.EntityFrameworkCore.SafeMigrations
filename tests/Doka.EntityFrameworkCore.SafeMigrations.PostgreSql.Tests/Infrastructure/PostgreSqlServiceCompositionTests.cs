namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Verifies PostgreSQL Service Composition contracts in independently owned test state.</summary>
public sealed class PostgreSqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

    /// <summary>Scaffolding Configuration Defaults To Strict And Accepts Legacy Convergence.</summary>
    [Fact]
    public void ScaffoldingConfigurationDefaultsToStrictAndAcceptsLegacyConvergence()
    {
        // Arrange
        var strict = new DbContextOptionsBuilder();

        // Act
        strict.UsePostgreSqlSafeMigrations();

        var legacy = new DbContextOptionsBuilder();
        legacy.UsePostgreSqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var strictInfo = strict.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.Info;
        var legacyInfo = legacy.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.Info;

        // Assert
        Assert.Equal(
            SafeMigrationScaffoldingMode.Strict,
            strict.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.ScaffoldingMode);

        Assert.Equal(
            SafeMigrationPolicy.ThrowIfDifferent,
            strict.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.LegacyConvergencePolicy);

        Assert.Equal(
            SafeMigrationScaffoldingMode.LegacyConvergence,
            legacy.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.ScaffoldingMode);

        Assert.Equal(strictInfo.GetServiceProviderHashCode(), legacyInfo.GetServiceProviderHashCode());
        Assert.True(strictInfo.ShouldUseSameServiceProvider(legacyInfo));
    }

    /// <summary>Excluded Table Data Ownership Is Persisted And Separates Service Providers.</summary>
    [Fact]
    public void ExcludedTableDataOwnership_IsPersistedAndSeparatesServiceProviders()
    {
        // Arrange
        var defaults = new DbContextOptionsBuilder();

        // Act
        defaults.UsePostgreSqlSafeMigrations();

        var ownership = new DbContextOptionsBuilder();
        ownership.UsePostgreSqlSafeMigrations(options =>
            options.ExcludeModelManagedDataForExcludedTables());

        var defaultExtension = defaults.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;
        var ownershipExtension = ownership.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;

        // Assert
        Assert.False(defaultExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.True(ownershipExtension.ExcludeModelManagedDataForExcludedTablesEnabled);
        Assert.False(defaultExtension.Info.ShouldUseSameServiceProvider(ownershipExtension.Info));
        Assert.NotEqual(
            defaultExtension.Info.GetServiceProviderHashCode(),
            ownershipExtension.Info.GetServiceProviderHashCode());
    }

    /// <summary>Repair Policy Without Legacy Mode Is Rejected Before Options Mutation.</summary>
    [Fact]
    public void RepairPolicyWithoutLegacyModeIsRejectedBeforeOptionsMutation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var failure = Record.Exception(() =>
            options.UsePostgreSqlSafeMigrations(configuration =>
                configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)));

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("requires LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Null(options.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>());
    }

    /// <summary>Null Scaffolding Configuration Is Rejected Before Options Mutation.</summary>
    [Fact]
    public void NullScaffoldingConfigurationIsRejectedBeforeOptionsMutation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();

        // Act
        var failure = Record.Exception(() =>
            options.UsePostgreSqlSafeMigrations(configure: null!));

        // Assert
        Assert.IsType<ArgumentNullException>(failure);
        Assert.Null(options.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>());
    }

    /// <summary>Configured Overload Families Persist Legacy Mode And Composition.</summary>
    /// <param name="family">The independently discovered canonical/custom/typed overload family.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConfiguredOverloadFamiliesPersistLegacyModeAndComposition(
        int family
    )
    {
        // Arrange
        var untyped = new DbContextOptionsBuilder();
        var typed = new DbContextOptionsBuilder<SafeMigrationDbContext>();

        // Act
        switch (family)
        {
            case 0:
                untyped.UsePostgreSqlSafeMigrations<SafeMigrationDbContext>(ConfigureLegacy);
                break;
            case 1:
                untyped.UsePostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>(
                    ConfigureLegacy);

                break;
            case 2:
                typed.UsePostgreSqlSafeMigrations(ConfigureLegacy);
                break;
            case 3:
                typed.UsePostgreSqlSafeMigrations<SafeMigrationDbContext, RecordingNpgsqlMigrationsSqlGenerator,
                    SafeMigrationDbContext>(ConfigureLegacy);

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(family));
        }

        var extension = (family < 2
                ? untyped.Options
                : typed.Options)
            .FindExtension<PostgreSqlSafeMigrationsOptionsExtension>();

        // Assert
        Assert.NotNull(extension);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, extension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, extension.LegacyConvergencePolicy);
        Assert.Equal(family == 2
                ? null
                : typeof(SafeMigrationDbContext),
            extension.CanonicalContextType);

        if (family is 1 or 3)
        {
            Assert.Equal(typeof(RecordingNpgsqlMigrationsSqlGenerator), extension.BaselineGeneratorType);
        }
    }

    /// <summary>Registration Without Npgsql Fails With Provider Specific Message.</summary>
    [Fact]
    public void RegistrationWithoutNpgsqlFailsWithProviderSpecificMessage()
    {
        // Arrange
        var options = new DbContextOptionsBuilder();
        options.UsePostgreSqlSafeMigrations();

        // Act
        var failure = Record.Exception(() =>
        {
            using var context = new DbContext(options.Options);
        });

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("require the Npgsql EF Core provider", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Registration Order And Repeated Calls Remain Idempotent.</summary>
    [Fact]
    public void RegistrationOrderAndRepeatedCallsRemainIdempotent()
    {
        // Arrange
        var before = new DbContextOptionsBuilder<SafeMigrationDbContext>();

        // Act
        ((DbContextOptionsBuilder)before).UsePostgreSqlSafeMigrations();
        before.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)before).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(before.Options);

        // Assert
        Assert.IsType<PostgreSqlSafeMigrationsSqlGenerator>(context.GetService<IMigrationsSqlGenerator>());
        Assert.Equal(
            "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationMigrationsAssembly",
            context
                .GetService<IMigrationsAssembly>()
                .GetType()
                .FullName);
    }

    /// <summary>Runtime Sql Generator Consumes Leading Design Time Services Guard.</summary>
    [Fact]
    public void RuntimeSqlGenerator_ConsumesLeadingDesignTimeServicesGuard()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var commands = generator.Generate(
            [new SafeMigrationDesignTimeServicesRequiredOperation()],
            context.Model);

        // Assert
        Assert.Empty(commands);
    }

    /// <summary>Runtime Sql Generator Rejects Misplaced Design Time Services Guard.</summary>
    [Fact]
    public void RuntimeSqlGenerator_RejectsMisplacedDesignTimeServicesGuard()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureSchemaExists("application");

        MigrationOperation[] operations =
        [
            .. builder.Operations,
            new SafeMigrationDesignTimeServicesRequiredOperation(),
        ];

        // Act
        var exception = Record.Exception(() =>
            generator.Generate(operations, context.Model));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("must be the first migration operation", invalidOperation.Message, StringComparison.Ordinal);
    }

    /// <summary>Runtime Sql Generator Passes Ef History Bootstrap Sql To The Provider Without A Model.</summary>
    [Fact]
    public void RuntimeSqlGenerator_PassesEfHistoryBootstrapSqlToTheProviderWithoutAModel()
    {
        // Arrange
        using var context = CreateRuntimeContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var historySql = context
            .GetService<IHistoryRepository>()
            .GetCreateIfNotExistsScript();

        // Act
        var commands = generator.Generate([new SqlOperation { Sql = historySql }], model: null);
        var command = commands.Single();

        // Assert
        Assert.Single(commands);
        Assert.Contains("CREATE TABLE IF NOT EXISTS", historySql, StringComparison.Ordinal);
        Assert.DoesNotContain("SafeMigrations", historySql, StringComparison.Ordinal);
        Assert.Equal(historySql.Trim(), command.CommandText.Trim());
    }

    /// <summary>Directly Constructed Generator With Built In Baseline Recognizes Exact History Bootstrap.</summary>
    [Fact]
    public void DirectlyConstructedGenerator_WithBuiltInBaselineRecognizesExactHistoryBootstrap()
    {
        // Arrange
        using var context = CreateRuntimeContext();
        var generator = new PostgreSqlSafeMigrationsSqlGenerator(
            context.GetService<IPostgreSqlSafeMigrationsBaselineGenerator>(),
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var historySql = context
            .GetService<IHistoryRepository>()
            .GetCreateIfNotExistsScript();

        // Act
        var commands = generator.Generate([new SqlOperation { Sql = historySql }], model: null);

        // Assert
        var command = Assert.Single(commands);

        Assert.Equal(historySql.Trim(), command.CommandText.Trim());
    }

    /// <summary>Runtime Sql Generator Passes Arbitrary Model Less Sql To The Provider.</summary>
    [Fact]
    public void RuntimeSqlGenerator_PassesArbitraryModelLessSqlToTheProvider()
    {
        // Arrange
        using var context = CreateRuntimeContext();
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var commands = generator.Generate(
            [new SqlOperation { Sql = "SELECT 1;" }],
            model: null);

        // Assert
        var command = Assert.Single(commands);

        Assert.Equal("SELECT 1;", command.CommandText.Trim());
    }

    /// <summary>Runtime Sql Generator Rejects Known Index Before Column Drop Without Ensure Table.</summary>
    [Fact]
    public void RuntimeSqlGenerator_RejectsKnownIndexBeforeColumnDropWithoutEnsureTable()
    {
        // Arrange
        using var context = CreateRuntimeContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_items_legacy", "items", ["legacy"]);
        builder.DropColumnIfExists("legacy", "items");

        // Act
        var exception = Record.Exception(() => context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Drop the index explicitly", invalidOperation.Message, StringComparison.Ordinal);
    }

    /// <summary>Runtime Sql Generator Accepts Explicit Index Drop Before Column Drop Without Ensure Table.</summary>
    [Fact]
    public void RuntimeSqlGenerator_AcceptsExplicitIndexDropBeforeColumnDropWithoutEnsureTable()
    {
        // Arrange
        using var context = CreateRuntimeContext();
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_items_legacy", "items", ["legacy"]);
        builder.DropIndexIfExists("ix_items_legacy", "items");
        builder.DropColumnIfExists("legacy", "items");

        // Act
        var commands = context
            .GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(3, commands.Count);
    }

    /// <summary>Custom Baseline Generator Renders Raw Sql Before A Safe Operation.</summary>
    [Fact]
    public void CustomBaselineGeneratorRendersRawSqlBeforeASafeOperation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        options
            .UsePostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();

        using var context = new SafeMigrationDbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.Sql("SELECT 1;");
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition("value", typeof(int), isNullable: true, storeType: "integer"),
            SafeMigrationPolicy.ThrowIfDifferent);

        RecordingNpgsqlMigrationsSqlGenerator.Clear();

        // Act
        var commands = generator.Generate(migrationBuilder.Operations, context.Model);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.Equal("SELECT 1;",
            commands[0]
                .CommandText
                .Trim());

        Assert.StartsWith("DO $doka_", commands[1].CommandText, StringComparison.Ordinal);
        Assert.NotEmpty(RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes);
    }

    /// <summary>Repairable Ensure Column Guards Distinct Provider Apply And Repair Operations.</summary>
    [Fact]
    public void RepairableEnsureColumn_GuardsDistinctProviderApplyAndRepairOperations()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        options
            .UsePostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();

        using var context = new SafeMigrationDbContext(options.Options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: false,
                storeType: "character varying(40)",
                maxLength: 40,
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            SafeMigrationPolicy.RepairIfSafe);

        RecordingNpgsqlMigrationsSqlGenerator.Clear();

        // Act
        var commands = generator.Generate(migrationBuilder.Operations, context.Model);

        // Assert
        var command = Assert.Single(commands);

        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(AddColumnOperation));

        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(AlterColumnOperation));

        Assert.Contains("IF doka_action OPERATOR(pg_catalog.=) 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("-- custom baseline: AddColumnOperation", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("-- custom baseline: AlterColumnOperation", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Repairable Varchar Ensure Column Reuses One Classifier Across Locked Evaluation.</summary>
    [Fact]
    public void RepairableVarcharEnsureColumn_ReusesOneClassifierAcrossLockedEvaluation()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(options.Options);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(string),
                isNullable: true,
                storeType: "character varying(40)",
                maxLength: 40),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var commands =
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate(migrationBuilder.Operations, context.Model);

        // Assert
        var command = Assert.Single(commands);

        const string loop = "FOR doka_evaluation_pass IN 1..2 LOOP";
        const string classifier = "doka_transition_eligible := COALESCE((";
        const string action = "doka_action := CASE WHEN doka_state OPERATOR(pg_catalog.=)";
        const string exit = "EXIT WHEN doka_action OPERATOR(pg_catalog.<>) 'repair' "
            + "OR doka_evaluation_pass OPERATOR(pg_catalog.=) 2;";

        const string tableLock = " IN ACCESS EXCLUSIVE MODE;";
        const string mutationBranch = "IF doka_action OPERATOR(pg_catalog.=) 'reject_different' THEN";

        var loopIndex = command.CommandText.IndexOf(loop, StringComparison.Ordinal);
        var classifierIndex = command.CommandText.IndexOf(classifier, StringComparison.Ordinal);
        var actionIndex = command.CommandText.IndexOf(action, StringComparison.Ordinal);
        var exitIndex = command.CommandText.IndexOf(exit, StringComparison.Ordinal);
        var lockIndex = command.CommandText.IndexOf(tableLock, StringComparison.Ordinal);
        var mutationBranchIndex = command.CommandText.IndexOf(mutationBranch, StringComparison.Ordinal);

        Assert.True(loopIndex >= 0);
        Assert.True(classifierIndex > loopIndex);
        Assert.True(actionIndex > classifierIndex);
        Assert.True(exitIndex > actionIndex);
        Assert.True(lockIndex > exitIndex);
        Assert.True(mutationBranchIndex > lockIndex);
        Assert.Equal(loopIndex, command.CommandText.LastIndexOf(loop, StringComparison.Ordinal));
        Assert.Equal(classifierIndex, command.CommandText.LastIndexOf(classifier, StringComparison.Ordinal));
        Assert.DoesNotContain("__DOKA_SM_", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Ensure Column Without Data Proof Does Not Emit Evaluation Loop Or Table Lock.</summary>
    [Fact]
    public void EnsureColumnWithoutDataProof_DoesNotEmitEvaluationLoopOrTableLock()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(options.Options);
        var migrationBuilder = new MigrationBuilder(context.Database.ProviderName!);
        migrationBuilder.EnsureColumn(
            "items",
            new ExpectedColumnDefinition(
                "value",
                typeof(int),
                isNullable: true,
                storeType: "integer"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands =
            context
                .GetService<IMigrationsSqlGenerator>()
                .Generate(migrationBuilder.Operations, context.Model);

        // Assert
        var command = Assert.Single(commands);

        Assert.DoesNotContain("FOR doka_evaluation_pass", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCESS EXCLUSIVE", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("__DOKA_SM_", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Incompatible Canonical Context Fails Before Migration Discovery.</summary>
    [Fact]
    public void IncompatibleCanonicalContextFailsBeforeMigrationDiscovery()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        options.UsePostgreSqlSafeMigrations<UnrelatedContext>();

        using var context = new SafeMigrationDbContext(options.Options);

        // Act
        var failure = Record.Exception(() => context.GetService<IMigrationsAssembly>());

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("is not assignable from runtime context", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Repeated Equivalent Direct Registration Is Idempotent.</summary>
    [Fact]
    public void RepeatedEquivalentDirectRegistrationIsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();
        services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();

        // Assert
        Assert.Single(CanonicalConfigurations(services));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsAssembly));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsSqlGenerator));
    }

    /// <summary>Conflicting Direct Registration Fails Before Changing The Service Collection.</summary>
    [Fact]
    public void ConflictingDirectRegistrationFailsBeforeChangingTheServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddPostgreSqlSafeMigrations();
        var originalDescriptors = services.ToArray();

        // Act
        var failure = Record.Exception(() =>
            services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>());

        // Assert
        var exception = Assert.IsType<InvalidOperationException>(failure);
        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalDescriptors, services);
        Assert.Single(CanonicalConfigurations(services));
    }

    private static IEnumerable<ServiceDescriptor> CanonicalConfigurations(
        IServiceCollection services
    ) => services.Where(static descriptor => descriptor.ServiceType.FullName == CanonicalConfigurationTypeName);

    private static SafeMigrationDbContext CreateRuntimeContext()
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations();

        return new SafeMigrationDbContext(options.Options);
    }

    private static void ConfigureLegacy(
        SafeMigrationOptionsBuilder options
    ) => options
        .UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence)
        .UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe);

    private sealed class UnrelatedContext : DbContext;

    private sealed class RecordingNpgsqlMigrationsSqlGenerator : IMigrationsSqlGenerator
    {
        private static readonly List<Type> s_operationTypes = [];
        private readonly MigrationsSqlGeneratorDependencies _dependencies;

        public RecordingNpgsqlMigrationsSqlGenerator(
            MigrationsSqlGeneratorDependencies dependencies
        )
        {
            _dependencies = dependencies;
        }

        public static IReadOnlyList<Type> ObservedOperationTypes => s_operationTypes;

        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
        )
        {
            s_operationTypes.AddRange(operations.Select(static operation => operation.GetType()));

            return operations
                .Select(operation =>
                {
                    var sql = operation is SqlOperation sqlOperation
                        ? sqlOperation.Sql
                        : $"-- custom baseline: {operation.GetType().Name}";

                    var command = _dependencies
                        .CommandBuilderFactory
                        .Create()
                        .Append(sql)
                        .Build();

                    return new MigrationCommand(
                        command,
                        _dependencies.CurrentContext.Context,
                        _dependencies.Logger,
                        operation is SqlOperation { SuppressTransaction: true });
                })
                .ToArray();
        }

        public static void Clear() => s_operationTypes.Clear();
    }
}
