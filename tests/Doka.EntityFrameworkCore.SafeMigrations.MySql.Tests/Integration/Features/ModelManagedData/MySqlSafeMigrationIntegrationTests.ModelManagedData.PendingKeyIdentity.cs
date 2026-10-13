namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Two pending migrations may transition the same typed composite key in their migration order.</summary>
    [Fact]
    public async Task ModelManagedPendingKeyIdentityPreservesMigrationBoundariesAndCompositeOrdinals()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE `managed_pending_keys` (`Id` int NOT NULL, `Code` int NOT NULL, "
            + "`Marker` int NOT NULL, PRIMARY KEY (`Id`, `Code`)) ENGINE=InnoDB;");

        await using var context = new ModelManagedPendingKeyDbContext(connectionString, Fixture.ServerVersion);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("model-managed-pending-key-identity"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        var completed = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("model-managed-pending-key-identity-completed"), CancellationToken.None);

        await context.Database.MigrateAsync(cancellationToken: CancellationToken.None);
        var rerun = await runner.AnalyzePendingMigrationsAsync(context,
            new SafeMigrationRunOptions("model-managed-pending-key-identity-rerun"), CancellationToken.None);

        var pending = await context.Database.GetPendingMigrationsAsync(CancellationToken.None);
        var applied = await context.Database.GetAppliedMigrationsAsync(CancellationToken.None);
        var historyRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `__ModelManagedPendingKeyHistory`;");

        var retainedRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM `managed_pending_keys` WHERE `Id` = 1 AND `Code` = 7 AND `Marker` = 1;");

        var totalRows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM `managed_pending_keys`;");

        // Assert
        Assert.Equal(2, preflight.Assessments.Count);
        Assert.Equal(SafeMigrationObservedState.Missing, preflight.Assessments[0].ObservedState);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        // WHY: The accepted guarded Ensure establishes this exact row, not
        // table-wide completeness. Canonical paired keys let the following
        // explicit transition use that row proof rather than stale live absence.
        Assert.Equal(SafeMigrationObservedState.TransitionReady, preflight.Assessments[1].ObservedState);
        Assert.Equal("projected_transition_ready", preflight.Assessments[1].AnalysisCode);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[1].Action);
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, completed.Status);
        Assert.Empty(completed.Assessments);
        Assert.Equal(SafeMigrationReportStatus.NoOperations, rerun.Status);
        Assert.Empty(rerun.Assessments);
        Assert.Empty(pending);
        var expectedApplied = new[]
        {
            "20261013000001_ModelManagedKeyEnsure",
            "20261013000002_ModelManagedKeyUpdate",
        };

        Assert.Equal(expectedApplied, applied);
        Assert.Equal(2, historyRows);
        Assert.Equal(1, retainedRows);
        Assert.Equal(1, totalRows);
        Assert.Empty(context.GetService<IDesignTimeModel>().Model.GetEntityTypes());
        var snapshot = Assert.IsType<ModelManagedPendingKeyModelSnapshot>(
            context.GetService<IMigrationsAssembly>().ModelSnapshot);

        Assert.Empty(snapshot.Model.GetEntityTypes());
    }
}

/// <summary>Provides an isolated real migration assembly and history for ordered typed-key transitions.</summary>
public sealed class ModelManagedPendingKeyDbContext : DbContext
{
    private readonly string _connectionString;
    private readonly MySqlServerVersion _serverVersion;

    /// <summary>Initializes the engine fixture without borrowing another test context's history.</summary>
    /// <param name="connectionString">The isolated test-owned native database.</param>
    /// <param name="serverVersion">The active MySQL or MariaDB server version.</param>
    public ModelManagedPendingKeyDbContext(string connectionString, MySqlServerVersion serverVersion)
    {
        _connectionString = connectionString;
        _serverVersion = serverVersion;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseMySql(_connectionString, _serverVersion,
            provider => provider.MigrationsAssembly(typeof(ModelManagedPendingKeyDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__ModelManagedPendingKeyHistory"));

        optionsBuilder.UseMySqlSafeMigrations<ModelManagedPendingKeyDbContext>();
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // WHY: The physical owner is an existing brownfield table and each
        // operation freezes all column types explicitly. This data-only
        // fixture has an empty runtime model, snapshot and migration models;
        // it does not approximate provider conventions with a hand-built map.
        ArgumentNullException.ThrowIfNull(modelBuilder);
    }
}

/// <summary>Explicitly captures the unchanged empty model after the two typed-data migrations.</summary>
[DbContext(typeof(ModelManagedPendingKeyDbContext))]
public sealed class ModelManagedPendingKeyModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
        => ArgumentNullException.ThrowIfNull(modelBuilder);
}

/// <summary>Introduces the original row using the physical Id/Code key order.</summary>
[DbContext(typeof(ModelManagedPendingKeyDbContext))]
[Migration("20261013000001_ModelManagedKeyEnsure")]
public sealed class ModelManagedPendingKeyEnsureMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.EnsureModelManagedDataFromModel("managed_pending_keys", ["Id", "Code"], ["int", "int"],
            ["Id", "Code", "Marker"], ["int", "int", "int"], new object?[,] { { 1, 7, 0 } });

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => ArgumentNullException.ThrowIfNull(modelBuilder);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("The anonymous typed-key fixture is forward-only.");
}

/// <summary>Transitions the same row using Code/Id order with correspondingly reordered typed key values.</summary>
[DbContext(typeof(ModelManagedPendingKeyDbContext))]
[Migration("20261013000002_ModelManagedKeyUpdate")]
public sealed class ModelManagedPendingKeyUpdateMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.UpdateModelManagedDataFromModel("managed_pending_keys", ["Code", "Id"], ["int", "int"],
            new object?[,] { { 7, 1 } }, ["Marker"], ["int"],
            new object?[,] { { 0 } }, new object?[,] { { 1 } });

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => ArgumentNullException.ThrowIfNull(modelBuilder);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException("The anonymous typed-key fixture is forward-only.");
}
