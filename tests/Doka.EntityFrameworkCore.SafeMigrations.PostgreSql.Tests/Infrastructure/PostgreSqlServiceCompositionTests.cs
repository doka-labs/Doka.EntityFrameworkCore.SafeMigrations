namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

    [Fact]
    public void ScaffoldingConfigurationDefaultsToStrictAndAcceptsLegacyConvergence()
    {
        var strict = new DbContextOptionsBuilder();
        strict.UsePostgreSqlSafeMigrations();

        var legacy = new DbContextOptionsBuilder();
        legacy.UsePostgreSqlSafeMigrations(options =>
            options.UseScaffoldingMode(SafeMigrationScaffoldingMode.LegacyConvergence));

        var strictInfo = strict.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.Info;
        var legacyInfo = legacy.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!.Info;

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

    [Fact]
    public void RepairPolicyWithoutLegacyModeIsRejectedBeforeOptionsMutation()
    {
        var options = new DbContextOptionsBuilder();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            options.UsePostgreSqlSafeMigrations(configuration =>
                configuration.UseLegacyConvergencePolicy(SafeMigrationPolicy.RepairIfSafe)));

        Assert.Contains("requires LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Null(options.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>());
    }

    [Fact]
    public void NullScaffoldingConfigurationIsRejectedBeforeOptionsMutation()
    {
        var options = new DbContextOptionsBuilder();

        Assert.Throws<ArgumentNullException>(() =>
            options.UsePostgreSqlSafeMigrations(configure: null!));
        Assert.Null(options.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>());
    }

    [Fact]
    public void ConfiguredOverloadFamiliesPersistLegacyModeAndComposition()
    {
        var canonical = new DbContextOptionsBuilder();
        canonical.UsePostgreSqlSafeMigrations<SafeMigrationDbContext>(ConfigureLegacy);

        var custom = new DbContextOptionsBuilder();
        custom.UsePostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>(
            ConfigureLegacy);

        var typed = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        typed.UsePostgreSqlSafeMigrations(ConfigureLegacy);

        var typedCustom = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        typedCustom.UsePostgreSqlSafeMigrations<SafeMigrationDbContext, RecordingNpgsqlMigrationsSqlGenerator,
            SafeMigrationDbContext>(ConfigureLegacy);

        var canonicalExtension = canonical.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;
        var customExtension = custom.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;
        var typedExtension = typed.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;
        var typedCustomExtension = typedCustom.Options.FindExtension<PostgreSqlSafeMigrationsOptionsExtension>()!;

        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, canonicalExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, canonicalExtension.LegacyConvergencePolicy);
        Assert.Equal(typeof(SafeMigrationDbContext), canonicalExtension.CanonicalContextType);
        Assert.Equal(typeof(RecordingNpgsqlMigrationsSqlGenerator), customExtension.BaselineGeneratorType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, customExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedExtension.ScaffoldingMode);
        Assert.Equal(typeof(RecordingNpgsqlMigrationsSqlGenerator), typedCustomExtension.BaselineGeneratorType);
        Assert.Equal(typeof(SafeMigrationDbContext), typedCustomExtension.CanonicalContextType);
        Assert.Equal(SafeMigrationScaffoldingMode.LegacyConvergence, typedCustomExtension.ScaffoldingMode);
        Assert.Equal(SafeMigrationPolicy.RepairIfSafe, typedCustomExtension.LegacyConvergencePolicy);
    }

    [Fact]
    public void RegistrationWithoutNpgsqlFailsWithProviderSpecificMessage()
    {
        var options = new DbContextOptionsBuilder();
        options.UsePostgreSqlSafeMigrations();

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var context = new DbContext(options.Options);
        });

        Assert.Contains("require the Npgsql EF Core provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationOrderAndRepeatedCallsRemainIdempotent()
    {
        var before = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        ((DbContextOptionsBuilder)before).UsePostgreSqlSafeMigrations();
        before.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)before).UsePostgreSqlSafeMigrations();

        using var context = new SafeMigrationDbContext(before.Options);

        Assert.IsType<PostgreSqlSafeMigrationsSqlGenerator>(context.GetService<IMigrationsSqlGenerator>());
        Assert.Equal(
            "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationMigrationsAssembly",
            context
                .GetService<IMigrationsAssembly>()
                .GetType()
                .FullName);
    }

    [Fact]
    public void CustomBaselineGeneratorReceivesOrdinaryAndSafeMigrationBaselines()
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options)
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
        var commands = generator.Generate(migrationBuilder.Operations, context.Model);

        Assert.NotEmpty(commands);
        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(SqlOperation));
        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(AddColumnOperation));
        Assert.Contains(commands, command => command.CommandText.StartsWith("DO $doka_", StringComparison.Ordinal));
    }

    [Fact]
    public void RepairableEnsureColumn_GuardsDistinctProviderApplyAndRepairOperations()
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options)
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

        var command = Assert.Single(generator.Generate(migrationBuilder.Operations, context.Model));

        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(AddColumnOperation));
        Assert.Contains(
            RecordingNpgsqlMigrationsSqlGenerator.ObservedOperationTypes,
            type => type == typeof(AlterColumnOperation));
        Assert.Contains("IF doka_action = 'apply'", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("-- custom baseline: AddColumnOperation", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("-- custom baseline: AlterColumnOperation", command.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompatibleCanonicalContextFailsBeforeMigrationDiscovery()
    {
        var options = new DbContextOptionsBuilder<SafeMigrationDbContext>();
        options.UseNpgsql("Host=localhost;Database=composition;Username=test;Password=test");
        ((DbContextOptionsBuilder)options).UsePostgreSqlSafeMigrations<UnrelatedContext>();

        using var context = new SafeMigrationDbContext(options.Options);

        var exception = Assert.Throws<InvalidOperationException>(() => context.GetService<IMigrationsAssembly>());

        Assert.Contains("is not assignable from runtime context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedEquivalentDirectRegistrationIsIdempotent()
    {
        var services = new ServiceCollection();

        services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();
        services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>();

        Assert.Single(CanonicalConfigurations(services));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsAssembly));
        Assert.Single(services, static descriptor => descriptor.ServiceType == typeof(IMigrationsSqlGenerator));
    }

    [Fact]
    public void ConflictingDirectRegistrationFailsBeforeChangingTheServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddPostgreSqlSafeMigrations();
        var originalDescriptors = services.ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddPostgreSqlSafeMigrations<RecordingNpgsqlMigrationsSqlGenerator, SafeMigrationDbContext>());

        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalDescriptors, services);
        Assert.Single(CanonicalConfigurations(services));
    }

    private static IEnumerable<ServiceDescriptor> CanonicalConfigurations(
        IServiceCollection services
    ) => services.Where(static descriptor => descriptor.ServiceType.FullName == CanonicalConfigurationTypeName);

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

        public static void Clear() => s_operationTypes.Clear();

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
    }
}
