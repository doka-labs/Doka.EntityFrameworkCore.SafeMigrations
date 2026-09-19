namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed class SqlitePendingTargetModelTests
{
    [Fact]
    public async Task PendingMigrations_AnalyzeEachRebuildAgainstItsOwnCumulativeTargetModel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE target_model_entities ("
                + "Id INTEGER NOT NULL, Code TEXT NOT NULL, "
                + "CONSTRAINT pk_target_model_entities PRIMARY KEY (Id));";

            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var context = new SqlitePendingTargetModelContext(connection);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzePendingMigrationsAsync(
                context,
                new SafeMigrationRunOptions("sqlite-pending-target-model"),
                CancellationToken.None);

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

    public SqlitePendingTargetModelContext(
        DbConnection connection
    ) => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(
            _connection,
            provider => provider.MigrationsAssembly(typeof(SqlitePendingTargetModelContext).Assembly.FullName));
        optionsBuilder.UseSqliteSafeMigrations<SqlitePendingTargetModelContext>();
    }

    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}

internal static class SqlitePendingTargetModel
{
    public static void Build(
        ModelBuilder modelBuilder,
        bool includeSecondCheck
    )
    {
        modelBuilder.Entity("SqlitePendingTargetModelEntity", entity =>
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
            entity.Property<int>("Id").ValueGeneratedNever();
            entity.Property<string>("Code").IsRequired();
            entity.HasKey("Id").HasName("pk_target_model_entities");
        });
    }
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
[Migration(MigrationIdentifier)]
internal sealed class SqliteFirstTargetModelMigration : Migration
{
    public const string MigrationIdentifier = "202609180010_FirstTargetModel";

    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddCheckConstraintIfNotExists(
        "ck_target_model_entities_code",
        "target_model_entities",
        "length(Code) > 0");

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropCheckConstraintIfExists(
        "ck_target_model_entities_code",
        "target_model_entities");

    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: false);
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
[Migration(MigrationIdentifier)]
internal sealed class SqliteSecondTargetModelMigration : Migration
{
    public const string MigrationIdentifier = "202609180020_SecondTargetModel";

    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddCheckConstraintIfNotExists(
        "ck_target_model_entities_id",
        "target_model_entities",
        "Id > 0");

    protected override void Down(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.DropCheckConstraintIfExists(
        "ck_target_model_entities_id",
        "target_model_entities");

    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}

[DbContext(typeof(SqlitePendingTargetModelContext))]
internal sealed class SqlitePendingTargetModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingTargetModel.Build(modelBuilder, includeSecondCheck: true);
}
