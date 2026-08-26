namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    private const string SnapshotFreeCatalogCountSql =
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() "
        + "AND TABLE_NAME IN ('snapshotless_items', '__EFMigrationsHistory');";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSnapshot_AnalyzesAndVerifiesTheDesignTimeModel(
        bool requireExpectedFingerprint
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateSnapshotFreeContext(connectionString);
        var assembly = context.GetService<IMigrationsAssembly>();
        var model = context.GetService<IDesignTimeModel>()
            .Model;
        var fingerprint = SafeMigrationModelFingerprint.Create(model, context.Database.ProviderName!);
        var options = new SafeMigrationRunOptions(
            "snapshot-free-instance",
            expectedModelFingerprint: requireExpectedFingerprint ? fingerprint : null);

        Assert.Equal(
            "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationMigrationsAssembly",
            assembly.GetType()
                .FullName);
        Assert.Null(assembly.ModelSnapshot);
        Assert.Null(assembly.ModelSnapshot);
        Assert.Empty(assembly.Migrations);
        Assert.Single(model.GetEntityTypes());

        var builder = CreateSnapshotFreeOperations(context);
        var runner = context.GetService<ISafeMigrationRunner>();
        var preflight = await runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None);
        var preflightAssessment = Assert.Single(preflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportMode.Preflight, preflight.Mode);
        Assert.Equal(fingerprint, preflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Missing, preflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflightAssessment.Action);
        Assert.False(preflightAssessment.PostconditionSatisfied);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);
        Assert.Equal(0, await ScalarIntAsync(connectionString, SnapshotFreeCatalogCountSql));

        await ExecuteOperationsAsync(context, builder.Operations, CancellationToken.None);
        var postflight = await runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None);
        var postflightAssessment = Assert.Single(postflight.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(SafeMigrationReportMode.Postflight, postflight.Mode);
        Assert.Equal(fingerprint, postflight.ModelFingerprint);
        Assert.Equal(SafeMigrationObservedState.Matching, postflightAssessment.ObservedState);
        Assert.Equal(SafeMigrationAction.NoOp, postflightAssessment.Action);
        Assert.True(postflightAssessment.PostconditionSatisfied);
        Assert.Null(assembly.ModelSnapshot);
        Assert.Equal(
            System.Data.ConnectionState.Closed,
            context.Database.GetDbConnection()
                .State);
        Assert.Equal(1, await ScalarIntAsync(connectionString, SnapshotFreeCatalogCountSql));
    }

    [Theory]
    [InlineData(SafeMigrationReportMode.Preflight)]
    [InlineData(SafeMigrationReportMode.Postflight)]
    public async Task MissingSnapshot_RejectsADifferentModelFingerprintBeforeOpeningTheConnection(
        SafeMigrationReportMode mode
    )
    {
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
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
        connection.StateChange += (
            _,
            _
        ) => connectionTransitions++;

        Assert.Null(
            context.GetService<IMigrationsAssembly>()
                .ModelSnapshot);
        Assert.NotEqual(expected, actual);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);

        var exception = await Assert.ThrowsAsync<SafeMigrationModelMismatchException>(() =>
            mode == SafeMigrationReportMode.Preflight
                ? runner.AnalyzeAsync(context, builder.Operations, options, CancellationToken.None)
                : runner.VerifyAsync(context, builder.Operations, options, CancellationToken.None));

        Assert.Equal(expected, exception.ExpectedFingerprint);
        Assert.Equal(actual, exception.ActualFingerprint);
        Assert.Equal(0, connectionTransitions);
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.Equal(0, await ScalarIntAsync(connectionString, SnapshotFreeCatalogCountSql));
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

    private sealed class SnapshotFreeContext : DbContext
    {
        public SnapshotFreeContext(
            DbContextOptions<SnapshotFreeContext> options
        ) : base(options) { }

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

    private sealed class SnapshotFreeEntry
    {
        public int Id { get; set; }
    }
}
