namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Applies each externally provisioned tooling schema once without imposing test-method order.</summary>
public sealed class GeneratedInitialMigrationFixture
{
    private readonly Lazy<Task<GeneratedInitialMigrationObservation>> _strict = new(() => InitializeAsync(
        "SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING",
        "StrictScaffoldingProbe",
        static connectionString => new StrictSafeMigrationScaffoldingDbContext(
            connectionString,
            MySqlDesignTimeContextConfiguration.ServerVersion())));

    private readonly Lazy<Task<GeneratedInitialMigrationObservation>> _legacy = new(() => InitializeAsync(
        "SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING",
        "LegacyScaffoldingProbe",
        static connectionString => new LegacySafeMigrationScaffoldingDbContext(
            connectionString,
            MySqlDesignTimeContextConfiguration.ServerVersion())));

    /// <summary>Gets the once-only lifecycle observation, retaining setup failures for every dependent fact.</summary>
    /// <param name="legacy">Selects the independently provisioned legacy rather than strict schema.</param>
    /// <returns>The captured initial application, replay and verification evidence.</returns>
    internal Task<GeneratedInitialMigrationObservation> GetInitializedAsync(bool legacy)
    {
        // WHY: The first selected fact owns setup, not a particular method order. Lazy also
        // retains a fault instead of retrying a partially applied schema behind another fact.
        return (legacy ? _legacy : _strict).Value;
    }

    /// <summary>Captures the original lifecycle before independent discriminator probes add mutable rows.</summary>
    private static async Task<GeneratedInitialMigrationObservation> InitializeAsync(
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

        // WHY: Preserve rejection-before-execution even when a discriminator fact initializes the fixture.
        AssertReady(preflight);
        await ExecuteOperationsAsync(context, operations);
        var replay = await runner.AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}-replay"),
            CancellationToken.None);

        AssertReady(replay);
        await ExecuteOperationsAsync(context, operations);
        var postflight = await runner.VerifyAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}-postflight"),
            CancellationToken.None);

        var seedCount = await ScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `scaffolding_users` "
            + "WHERE `Id` = 1 AND `TenantId` = 7 AND `Email` = 'administrator@example.test';");

        return new GeneratedInitialMigrationObservation(
            preflight, replay, postflight, seedCount, () => createContext(connectionString));
    }

    /// <summary>Finds the real freshly scaffolded migration for the selected tooling profile.</summary>
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

    /// <summary>Reports every blocking operation instead of hiding setup failure behind a dependent query.</summary>
    internal static void AssertReady(SafeMigrationRunReport report)
    {
        if (report.Status == SafeMigrationReportStatus.Ready)
        {
            return;
        }

        var blockers = report.Assessments
            .Where(static assessment => assessment.Action is
                SafeMigrationAction.RejectDifferent
                or SafeMigrationAction.RejectUnsupported
                or SafeMigrationAction.RejectDataBlocked
                or SafeMigrationAction.RejectPrerequisiteMissing)
            .Select(static assessment =>
                $"{assessment.Ordinal}: {assessment.OperationKind} {assessment.ObjectName}; "
                + $"state={assessment.ObservedState}; action={assessment.Action}; "
                + $"analysis={assessment.AnalysisCode}; decision={assessment.DecisionCode}")
            .ToArray();

        Assert.Fail(
            "Generated initial migration preflight was blocked:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, blockers));
    }

    /// <summary>Runs the generated operation stream without replacing the provider's native commands.</summary>
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

    /// <summary>Reads the source-controlled seed without retaining a connection outside its setup context.</summary>
    private static async Task<int> ScalarIntAsync(DbContext context, string sql)
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

/// <summary>
/// Retains immutable lifecycle evidence and creates isolated contexts for later query/write contracts.
/// </summary>
/// <param name="Preflight">The initial analysis before schema application.</param>
/// <param name="Replay">The matching analysis after initial application.</param>
/// <param name="Postflight">The verification after an idempotent replay.</param>
/// <param name="SeedCount">The count of the expected source-controlled seed row.</param>
/// <param name="CreateContext">Creates a new context against the fixture-owned profile.</param>
internal sealed record GeneratedInitialMigrationObservation(
    SafeMigrationRunReport Preflight,
    SafeMigrationRunReport Replay,
    SafeMigrationRunReport Postflight,
    int SeedCount,
    Func<DbContext> CreateContext
);
