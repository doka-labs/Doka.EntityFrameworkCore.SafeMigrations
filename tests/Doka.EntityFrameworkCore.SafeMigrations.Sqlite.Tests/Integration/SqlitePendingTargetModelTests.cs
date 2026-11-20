namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Provides isolated SQLite coverage for pending target model.
/// </summary>
public sealed class SqlitePendingTargetModelTests
{
    /// <summary>
    /// Pending migrations analyze each rebuild against its own cumulative target model.
    /// </summary>
    [Fact]
    public async Task PendingMigrations_AnalyzeEachRebuildAgainstItsOwnCumulativeTargetModel()
    {
        // Arrange
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
            CREATE TABLE target_model_entities (Id INTEGER NOT NULL, Code TEXT NOT NULL,
            CONSTRAINT pk_target_model_entities PRIMARY KEY (Id));
            """;

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // WHY: Canonical-context options form a distinct EF service-cache key. This fixture owns that
        // provider explicitly so parallel sample/contract tests cannot exhaust the process-global cache.
        await using var internalServices = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSqliteSafeMigrations<SqliteMigrationsSqlGenerator, SqlitePendingTargetModelContext>()
            .BuildServiceProvider();

        await using var context = new SqlitePendingTargetModelContext(connection, internalServices);

        // Act
        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(
                context,
                new SafeMigrationRunOptions("sqlite-pending-target-model"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Collection(
            report.Assessments,
            assessment =>
            {
                Assert.Equal("ck_target_model_entities_code", assessment.ObjectName);
                Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            },
            assessment =>
            {
                Assert.Equal("ck_target_model_entities_id", assessment.ObjectName);
                Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            });
    }
}

internal sealed class SqlitePendingTargetModelContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _internalServices;

    /// <summary>
    /// Initializes the fixture with explicitly supplied dependencies and ownership.
    /// </summary>
    /// <param name="connection">The caller-owned native SQLite connection.</param>
    /// <param name="internalServices">The internal services defining this isolated fixture case.</param>
    public SqlitePendingTargetModelContext(
        DbConnection connection,
        IServiceProvider internalServices
    )
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _internalServices = internalServices ?? throw new ArgumentNullException(nameof(internalServices));
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(
            _connection,
            provider => provider.MigrationsAssembly(typeof(SqlitePendingTargetModelContext).Assembly.FullName));

        optionsBuilder.UseSqliteSafeMigrations<SqlitePendingTargetModelContext>();
        optionsBuilder.UseInternalServiceProvider(_internalServices);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}

internal static class SqlitePendingTargetModel
{
    /// <summary>
    /// Builds the exact first or second migration target model used by ordered preflight.
    /// </summary>
    /// <param name="modelBuilder">The builder receiving this fixture's model definition.</param>
    /// <param name="includeSecondCheck">The include second check defining this isolated fixture case.</param>
    public static void Build(
        ModelBuilder modelBuilder,
        bool includeSecondCheck
    )
    {
        modelBuilder.Entity(
            "SqlitePendingTargetModelEntity",
            entity =>
            {
                entity.ToTable(
                    "target_model_entities",
                    table =>
                    {
                        table.HasCheckConstraint("ck_target_model_entities_code", "length(Code) > 0");
                        if (includeSecondCheck)
                        {
                            table.HasCheckConstraint("ck_target_model_entities_id", "Id > 0");
                        }
                    });

                entity
                    .Property<int>("Id")
                    .ValueGeneratedNever();

                entity
                    .Property<string>("Code")
                    .IsRequired();

                entity
                    .HasKey("Id")
                    .HasName("pk_target_model_entities");
            });
    }
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
[Migration(MigrationIdentifier)]
// WHY: EF migration discovery constructs this fixture from its attributed migration identity.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqliteFirstTargetModelMigration : Migration
{
    /// <summary>
    /// Identifies this fixture migration for EF discovery and migration-history assertions.
    /// </summary>
    public const string MigrationIdentifier = "20260918001000_FirstTargetModel";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddCheckConstraintIfNotExists(
        "ck_target_model_entities_code",
        "target_model_entities",
        "length(Code) > 0");

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropCheckConstraintIfExists(
        "ck_target_model_entities_code",
        "target_model_entities");

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: false);
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
[Migration(MigrationIdentifier)]
// WHY: EF migration discovery constructs this fixture from its attributed migration identity.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqliteSecondTargetModelMigration : Migration
{
    /// <summary>
    /// Identifies this fixture migration for EF discovery and migration-history assertions.
    /// </summary>
    public const string MigrationIdentifier = "20260918002000_SecondTargetModel";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddCheckConstraintIfNotExists(
        "ck_target_model_entities_id",
        "target_model_entities",
        "Id > 0");

    /// <inheritdoc />
    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropCheckConstraintIfExists(
        "ck_target_model_entities_id",
        "target_model_entities");

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
// WHY: EF discovers this target-model snapshot through its DbContext attribute.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqlitePendingTargetModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}
