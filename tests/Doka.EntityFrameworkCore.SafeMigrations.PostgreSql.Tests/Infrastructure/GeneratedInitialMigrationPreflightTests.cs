namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed class GeneratedInitialMigrationPreflightTests
{
    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING")]
    public Task StrictGeneratedInitialMigrationConvergesThroughPreflightAndReplay() => VerifyAsync(
        "SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING",
        "StrictScaffoldingProbe",
        static connectionString => new StrictSafeMigrationScaffoldingDbContext(connectionString));

    [GeneratedToolingFact("SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING")]
    public Task LegacyGeneratedInitialMigrationConvergesThroughPreflightAndReplay() => VerifyAsync(
        "SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING",
        "LegacyScaffoldingProbe",
        static connectionString => new LegacySafeMigrationScaffoldingDbContext(connectionString));

    private static async Task VerifyAsync(
        string connectionVariable,
        string migrationName,
        Func<string, DbContext> createContext
    )
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionVariable)
            ?? throw new InvalidOperationException($"{connectionVariable} is required by the tooling gate.");

        await using var context = createContext(connectionString);
        var operations = GeneratedOperations(context, migrationName);
        var runner = context.GetService<ISafeMigrationRunner>();

        var preflight = await runner.AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}"),
            CancellationToken.None);

        var dataAssessment = Assert.Single(
            preflight.Assessments,
            static assessment => assessment.OperationKind == SafeMigrationOperationKind.EnsureModelManagedData);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationObservedState.Missing, dataAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, dataAssessment.Action);
        Assert.Equal("projected_missing", dataAssessment.Code);

        await ExecuteOperationsAsync(context, operations);
        await ExecuteOperationsAsync(context, operations);

        var postflight = await runner.VerifyAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}-postflight"),
            CancellationToken.None);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, await ScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM scaffolding_users "
            + "WHERE \"Id\" = 1 AND \"TenantId\" = 7 AND \"Email\" = 'administrator@example.test';"));
    }

    private static IReadOnlyList<MigrationOperation> GeneratedOperations(
        DbContext context,
        string migrationName
    )
    {
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();

        var migration = Assert.Single(
            migrationsAssembly.Migrations,
            entry => StringComparer.Ordinal.Equals(
                migrationsAssembly.FindMigrationId(migrationName),
                entry.Key));

        return migrationsAssembly
            .CreateMigration(migration.Value, context.Database.ProviderName!)
            .UpOperations;
    }

    private static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);
        var connection = context.GetService<IRelationalConnection>();

        foreach (var command in commands)
        {
            _ = await command.ExecuteNonQueryAsync(connection, cancellationToken: CancellationToken.None);
        }
    }

    private static async Task<int> ScalarIntAsync(
        DbContext context,
        string sql
    )
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(CancellationToken.None);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(CancellationToken.None),
            CultureInfo.InvariantCulture);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class GeneratedToolingFactAttribute : FactAttribute
{
    public GeneratedToolingFactAttribute(
        string connectionVariable
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionVariable);

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionVariable)))
        {
            Skip = "The generated-migration preflight is executed by eng/verify-ef-tooling.sh.";
        }
    }
}
