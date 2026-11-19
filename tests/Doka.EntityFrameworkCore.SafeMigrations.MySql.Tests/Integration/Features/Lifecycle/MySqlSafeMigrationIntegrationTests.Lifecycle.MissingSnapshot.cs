namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the LifecycleMissingSnapshot migration contracts in an independent class collection.</summary>
public sealed class MySqlLifecycleMissingSnapshotIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    private const string SnapshotFreeCatalogCountSql =
        """
        SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_SCHEMA = DATABASE()
        AND TABLE_NAME IN ('snapshotless_items', '__EFMigrationsHistory');
        """;

    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlLifecycleMissingSnapshotIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Uses the initialized design-time model when no migration snapshot exists.</summary>
    /// <param name="requireExpectedFingerprint">Whether canonical model-fingerprint equality is required.</param>
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

        var assemblyType = assembly.GetType()
            .FullName;

        var firstSnapshot = assembly.ModelSnapshot;
        var secondSnapshot = assembly.ModelSnapshot;
        var migrationCount = assembly.Migrations.Count;
        var entityCount = model
            .GetEntityTypes()
            .Count();

        var builder = CreateSnapshotFreeOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);

        var connectionAfterPreflight = context.Database.GetDbConnection()
            .State;

        var objectsAfterPreflight = await ScalarIntAsync(
            connectionString,
            SnapshotFreeCatalogCountSql,
            cancellationToken: CancellationToken.None);

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None);
        var snapshotAfterPostflight = assembly.ModelSnapshot;
        var connectionAfterPostflight = context.Database.GetDbConnection()
            .State;

        var objectsAfterPostflight = await ScalarIntAsync(
            connectionString,
            SnapshotFreeCatalogCountSql,
            cancellationToken: CancellationToken.None);

        // Assert
        var preflightAssessment = Assert.Single(preflight.Assessments);
        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal("Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationMigrationsAssembly", assemblyType);
        Assert.Null(firstSnapshot);
        Assert.Null(secondSnapshot);
        Assert.Equal(0, migrationCount);
        Assert.Equal(1, entityCount);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportMode.Preflight, preflight.Mode);
        Assert.Equal(fingerprint, preflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.False(preflightAssessment.PostconditionSatisfied);
        Assert.Equal(ConnectionState.Closed, connectionAfterPreflight);
        Assert.Equal(0, objectsAfterPreflight);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportMode.Postflight, postflight.Mode);
        Assert.Equal(fingerprint, postflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
        Assert.True(postflightAssessment.PostconditionSatisfied);
        Assert.Null(snapshotAfterPostflight);
        Assert.Equal(ConnectionState.Closed, connectionAfterPostflight);
        Assert.Equal(1, objectsAfterPostflight);
    }

    /// <summary>Rejects a mismatched model fingerprint without opening a database connection.</summary>
    /// <param name="mode">The explicitly selected strict or legacy-convergence scaffolding mode.</param>
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

        var snapshotBeforeRun = context.GetService<IMigrationsAssembly>()
            .ModelSnapshot;

        var connectionBeforeRun = connection.State;

        // Act
        var failure = await Record.ExceptionAsync(() =>
            mode == SafeMigrationReportMode.Preflight
                ? runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None)
                : runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None));

        var remainingObjects = await ScalarIntAsync(
            connectionString,
            SnapshotFreeCatalogCountSql,
            cancellationToken: CancellationToken.None);

        // Assert
        var exception = Assert.IsType<SafeMigrationModelMismatchException>(failure);

        Assert.Null(snapshotBeforeRun);
        Assert.NotEqual(expected, actual);
        Assert.Equal(ConnectionState.Closed, connectionBeforeRun);
        Assert.Equal(expected, exception.ExpectedFingerprint);
        Assert.Equal(actual, exception.ActualFingerprint);
        Assert.Equal(0, connectionTransitions);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Equal(0, remainingObjects);
    }

    private SnapshotFreeContext CreateSnapshotFreeContext(
        string connectionString
    )
    {
        var options = new DbContextOptionsBuilder<SnapshotFreeContext>()
            .UseMySql(connectionString, Fixture.ServerVersion)
            .UseMySqlSafeMigrations()
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
            table => new { id = table.Column<int>(type: "int", nullable: false) },
            comment: "Snapshot-free target.");

        return builder;
    }
}

public abstract partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Provides a finalized design-time model without a migration snapshot.</summary>
    protected sealed class SnapshotFreeContext : DbContext
    {
        /// <summary>Initializes the isolated model and explicit provider dependencies.</summary>
        /// <param name="options">The isolated model and provider service options.</param>
        public SnapshotFreeContext(
            DbContextOptions<SnapshotFreeContext> options
        ) : base(options) { }

        /// <inheritdoc />
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<SnapshotFreeEntry>(entity =>
            {
                entity.HasNoKey();
                entity.ToTable("snapshotless_items", table => table.HasComment("Snapshot-free target."));
                entity
                    .Property(entry => entry.Id)
                    .HasColumnName("id")
                    .HasColumnType("int");
            });
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class SnapshotFreeEntry
    {
        public int Id { get; set; }
    }
}
