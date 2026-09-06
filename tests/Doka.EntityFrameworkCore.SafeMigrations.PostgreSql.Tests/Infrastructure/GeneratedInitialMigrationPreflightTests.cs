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

        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO scaffolding_work_items (\"Id\", \"Caption\", \"Discriminator\") "
            + "VALUES (1, 'Known', 0), (2, 'External', 1), (3, 'Future', 2);",
            CancellationToken.None);

        Assert.Equal(
            2,
            await context.Set<SafeMigrationScaffoldingWorkItem>().CountAsync(CancellationToken.None));
        Assert.Equal(
            1,
            await context.Set<SafeMigrationScaffoldingTask>().CountAsync(CancellationToken.None));
        Assert.Equal(
            1,
            await context.Set<SafeMigrationScaffoldingExternalWorkItem>().CountAsync(CancellationToken.None));

        _ = await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO scaffolding_attributed_work_items "
            + "(\"Id\", \"Caption\", \"AttributedDiscriminator\") VALUES "
            + "(11, 'Json', 'json-named'), "
            + "(12, 'Contract', 'contract-named'), "
            + "(13, 'Fallback', 'Fallback'), "
            + "(14, 'Future', 'future-value');",
            CancellationToken.None);

        context.AddRange(
            new SafeMigrationJsonNamedWorkItem
            {
                Id = 15,
                Caption = "Converted JSON name",
            },
            new SafeMigrationContractNamedWorkItem
            {
                Id = 16,
                Caption = "Converted contract name",
            },
            new SafeMigrationFallbackNamedWorkItem
            {
                Id = 17,
                Caption = "Converted fallback name",
            });
        _ = await context.SaveChangesAsync(CancellationToken.None);

        Assert.Equal(
            6,
            await context.Set<SafeMigrationAttributedWorkItem>().CountAsync(CancellationToken.None));
        Assert.Equal(
            2,
            await context.Set<SafeMigrationJsonNamedWorkItem>().CountAsync(CancellationToken.None));
        Assert.Equal(
            2,
            await context.Set<SafeMigrationContractNamedWorkItem>().CountAsync(CancellationToken.None));
        Assert.Equal(
            2,
            await context.Set<SafeMigrationFallbackNamedWorkItem>().CountAsync(CancellationToken.None));
        Assert.Equal(3, await ScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM scaffolding_attributed_work_items "
            + "WHERE (\"Id\" = 15 AND \"AttributedDiscriminator\" = 'json-named') "
            + "OR (\"Id\" = 16 AND \"AttributedDiscriminator\" = 'contract-named') "
            + "OR (\"Id\" = 17 AND \"AttributedDiscriminator\" = 'Fallback');"));
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
