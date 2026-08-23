namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class PostgreSqlServiceCompositionTests
{
    private const string CanonicalConfigurationTypeName =
        "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCanonicalContextConfiguration";

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
