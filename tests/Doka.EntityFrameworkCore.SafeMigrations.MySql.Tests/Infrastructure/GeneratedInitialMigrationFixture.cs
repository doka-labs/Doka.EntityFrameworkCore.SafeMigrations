namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Applies each externally provisioned tooling schema once without imposing test-method order.</summary>
public sealed class GeneratedInitialMigrationFixture
{
    private readonly Lazy<Task<GeneratedInitialMigrationObservation>> _legacy;
    private readonly Lazy<Task<GeneratedInitialMigrationObservation>> _strict;

    /// <summary>Creates shared initializers whose lifetime is independent of each observing test.</summary>
    public GeneratedInitialMigrationFixture()
    {
        // WHY: Canceling one observer cannot abandon schema mutation or let another fact
        // start a second initializer over a partially applied migration.
        _strict = new Lazy<Task<GeneratedInitialMigrationObservation>>(() => InitializeAsync(
            "SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING",
            "StrictScaffoldingProbe",
            static connectionString => new StrictSafeMigrationScaffoldingDbContext(
                connectionString,
                MySqlDesignTimeContextConfiguration.ServerVersion()),
            CancellationToken.None));

        _legacy = new Lazy<Task<GeneratedInitialMigrationObservation>>(() => InitializeAsync(
            "SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING",
            "LegacyScaffoldingProbe",
            static connectionString => new LegacySafeMigrationScaffoldingDbContext(
                connectionString,
                MySqlDesignTimeContextConfiguration.ServerVersion()),
            CancellationToken.None));
    }

    /// <summary>Supplies a resource-free initializer for ownership and observer-cancellation counterprobes.</summary>
    /// <param name="initialize">The once-only profile initializer receiving an independent fixture-owned token.</param>
    internal GeneratedInitialMigrationFixture(
        Func<bool, CancellationToken, Task<GeneratedInitialMigrationObservation>> initialize
    )
    {
        ArgumentNullException.ThrowIfNull(initialize);

        _strict = new Lazy<Task<GeneratedInitialMigrationObservation>>(() => initialize(false, CancellationToken.None));
        _legacy = new Lazy<Task<GeneratedInitialMigrationObservation>>(() => initialize(true, CancellationToken.None));
    }

    /// <summary>Gets the once-only lifecycle observation, retaining setup failures for every dependent fact.</summary>
    /// <param name="legacy">Selects the independently provisioned legacy rather than strict schema.</param>
    /// <param name="cancellationToken">Cancels this observer without abandoning the shared initializer.</param>
    /// <returns>The captured initial application, replay and verification evidence.</returns>
    internal Task<GeneratedInitialMigrationObservation> GetInitializedAsync(
        bool legacy,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        // WHY: The first selected fact owns setup, not a particular method order. Lazy also
        // retains a fault instead of retrying a partially applied schema behind another fact.
        var initialization = (legacy
            ? _legacy
            : _strict).Value;

        // WHY: Only this observer stops waiting; the retained owner must finish its schema
        // mutation independently so neither a peer nor cleanup can race a second initializer.
        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    /// <summary>Captures the original lifecycle before independent discriminator probes add mutable rows.</summary>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<GeneratedInitialMigrationObservation> InitializeAsync(
        string connectionVariable,
        string migrationName,
        Func<string, DbContext> createContext,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connectionString = Environment.GetEnvironmentVariable(connectionVariable)
            ?? throw new InvalidOperationException($"{connectionVariable} is required by the tooling gate.");

        await using var context = createContext(connectionString);
        var operations = GeneratedOperations(context, migrationName);
        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}"),
            cancellationToken);

        // WHY: Preserve rejection-before-execution even when a discriminator fact initializes the fixture.
        AssertReady(preflight);
        await ExecuteOperationsAsync(context, operations, cancellationToken: cancellationToken);
        var replay = await runner.AnalyzeAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}-replay"),
            cancellationToken);

        AssertReady(replay);
        await ExecuteOperationsAsync(context, operations, cancellationToken: cancellationToken);
        var postflight = await runner.VerifyAsync(
            context,
            operations,
            new SafeMigrationRunOptions($"generated-initial-{migrationName}-postflight"),
            cancellationToken);

        var seedCount = await ScalarIntAsync(
            context,
            "SELECT COUNT(*) FROM `scaffolding_users` "
            + "WHERE `Id` = 1 AND `TenantId` = 7 AND `Email` = 'administrator@example.test';",
            cancellationToken: cancellationToken);

        return new GeneratedInitialMigrationObservation(
            preflight,
            replay,
            postflight,
            seedCount,
            () => createContext(connectionString));
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
    internal static void AssertReady(
        SafeMigrationRunReport report
    )
    {
        if (report.Status == SafeMigrationReportStatus.Ready)
        {
            return;
        }

        var blockers = report
            .Assessments
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
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task ExecuteOperationsAsync(
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var generator = context.GetService<IMigrationsSqlGenerator>();
        var commands = generator.Generate(operations, context.Model);
        var connection = context.GetService<IRelationalConnection>();

        foreach (var command in commands)
        {
            _ = await command.ExecuteNonQueryAsync(connection, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Reads the source-controlled seed without retaining a connection outside its setup context.</summary>
    /// <param name="cancellationToken">The caller's operation cancellation.</param>
    private static async Task<int> ScalarIntAsync(
        DbContext context,
        string sql,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
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
