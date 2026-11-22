namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the LifecycleMissingSnapshot PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlLifecycleMissingSnapshotIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    private const string SnapshotFreeCatalogCountSql =
        """
        SELECT COUNT(*) FROM information_schema.tables
        WHERE table_schema = current_schema()
        AND table_name IN ('snapshotless_items', '__EFMigrationsHistory');
        """;

    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlLifecycleMissingSnapshotIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Missing Snapshot Analyzes And Verifies The Design Time Model.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSnapshot_AnalyzesAndVerifiesTheDesignTimeModel(
        bool requireExpectedFingerprint
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateSnapshotFreeContext(connectionString);
        var assembly = context.GetService<IMigrationsAssembly>();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        var fingerprint = SafeMigrationModelFingerprint.Create(model, context.Database.ProviderName!);
        var options = new SafeMigrationRunOptions(
            "snapshot-free-instance",
            expectedModelFingerprint: requireExpectedFingerprint
                ? fingerprint
                : null);

        var builder = CreateSnapshotFreeOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
        var preflightConnectionState = context.Database.GetDbConnection()
            .State;

        var preflightCatalogCount = await ScalarIntAsync(
            connectionString,
            SnapshotFreeCatalogCountSql,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);

        // The SQL helper opens the raw connection, outside EF's connection-ownership counter.
        await context
            .Database
            .GetDbConnection()
            .CloseAsync();

        var postflight = await runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None);

        // Assert
        Assert.Equal(
            "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationMigrationsAssembly",
            assembly.GetType()
                .FullName);

        Assert.Null(assembly.ModelSnapshot);
        Assert.Null(assembly.ModelSnapshot);
        Assert.Empty(assembly.Migrations);
        Assert.Single(model.GetEntityTypes());

        var preflightAssessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportMode.Preflight, preflight.Mode);
        Assert.Equal(fingerprint, preflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.False(preflightAssessment.PostconditionSatisfied);
        Assert.Equal(ConnectionState.Closed, preflightConnectionState);

        Assert.Equal(0, preflightCatalogCount);

        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportMode.Postflight, postflight.Mode);
        Assert.Equal(fingerprint, postflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
        Assert.True(postflightAssessment.PostconditionSatisfied);
        Assert.Null(assembly.ModelSnapshot);
        Assert.Equal(
            ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);

        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                SnapshotFreeCatalogCountSql,
                cancellationToken: CancellationToken.None));
    }

    /// <summary>Missing Snapshot Rejects A Different Model Fingerprint Before Opening The Connection.</summary>
    [Theory]
    [InlineData(SafeMigrationReportMode.Preflight)]
    [InlineData(SafeMigrationReportMode.Postflight)]
    public async Task MissingSnapshot_RejectsADifferentModelFingerprintBeforeOpeningTheConnection(
        SafeMigrationReportMode mode
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateSnapshotFreeContext(connectionString);
        await using var differentModelContext = CreateContext(connectionString);
        var expected = SafeMigrationModelFingerprint.Create(
            differentModelContext.GetService<IDesignTimeModel>()
                .Model,
            differentModelContext.Database.ProviderName!);

        var actual = SafeMigrationModelFingerprint.Create(
            context.GetService<IDesignTimeModel>()
                .Model,
            context.Database.ProviderName!);

        var options = new SafeMigrationRunOptions("snapshot-free-instance", expectedModelFingerprint: expected);
        var builder = CreateSnapshotFreeOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();
        var connection = context.Database.GetDbConnection();
        var connectionTransitions = 0;

        // The final closed state alone would miss an unintended open-and-close cycle.
        connection.StateChange += (_,
            _) => connectionTransitions++;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            mode == SafeMigrationReportMode.Preflight
                ? runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None)
                : runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None));

        // Assert
        Assert.Null(
            context.GetService<IMigrationsAssembly>()
                .ModelSnapshot);

        Assert.NotEqual(expected, actual);
        Assert.Equal(ConnectionState.Closed, connection.State);

        var exception = Assert.IsType<SafeMigrationModelMismatchException>(failure);
        Assert.Equal(expected, exception.ExpectedFingerprint);
        Assert.Equal(actual, exception.ActualFingerprint);
        Assert.Equal(0, connectionTransitions);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(
            0,
            await ScalarIntAsync(
                connectionString,
                SnapshotFreeCatalogCountSql,
                cancellationToken: CancellationToken.None));
    }

    private static SnapshotFreeContext CreateSnapshotFreeContext(
        string connectionString
    )
    {
        var options = new DbContextOptionsBuilder<SnapshotFreeContext>()
            // WHY: Even the snapshot-free path must retain the oldest matrix engine's SQL dialect.
            .UseNpgsql(connectionString, provider => provider.SetPostgresVersion(14, 0))
            .UsePostgreSqlSafeMigrations()
            .Options;

        return new SnapshotFreeContext(options);
    }

    private static MigrationBuilder CreateSnapshotFreeOperations(
        DbContext context
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists(
            "snapshotless_items",
            table => new { id = table.Column<int>(type: "integer", nullable: false) },
            comment: "Snapshot-free target.");

        return builder;
    }
}
