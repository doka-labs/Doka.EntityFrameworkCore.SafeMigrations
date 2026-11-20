namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>
/// Verifies culture-independent pending migration selection through the public runner and native SQLite.
/// </summary>
public sealed class SqlitePendingMigrationOrderingTests
{
    /// <summary>
    /// Uses ordinal migration identity for default targets, explicit boundaries and assessment order.
    /// </summary>
    /// <param name="cultureName">The invocation-local culture; empty selects invariant culture.</param>
    /// <param name="explicitTarget">Whether discovery stops at the first ordinal migration.</param>
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("tr-TR", false)]
    [InlineData("tr-TR", true)]
    public async Task PendingMigrationsRetainOrdinalTargetsAndOperationOrder(
        string cultureName,
        bool explicitTarget
    )
    {
        // Arrange
        var originalCulture = CultureInfo.CurrentCulture;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(CancellationToken.None);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE ordering_records (Id INTEGER NOT NULL PRIMARY KEY);";
            _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using var services = new ServiceCollection()
            .AddEntityFrameworkSqlite()
            .AddSqliteSafeMigrations<SqliteMigrationsSqlGenerator, SqlitePendingOrderingContext>()
            .BuildServiceProvider();

        await using var context = new SqlitePendingOrderingContext(connection, services);
        var options = new SafeMigrationRunOptions(
            "sqlite-ordinal-migrations",
            explicitTarget
                ? SqliteOrdinalFirstMigration.Identifier
                : null);

        string[] expectedMigrationIds = explicitTarget
            ? [SqliteOrdinalFirstMigration.Identifier]
            : [SqliteOrdinalFirstMigration.Identifier, SqliteOrdinalLastMigration.Identifier];

        string[] expectedObjects = explicitTarget
            ? ["Z"]
            : ["Z", "a"];

        SafeMigrationRunReport report;

        try
        {
            // WHY: CurrentCulture flows with this async execution context; never mutate process-wide defaults.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            // Act
            report = await context
                .GetService<ISafeMigrationRunner>()
                .AnalyzePendingMigrationsAsync(context, options, CancellationToken.None);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(
            explicitTarget
                ? SqliteOrdinalFirstMigration.Identifier
                : SqliteOrdinalLastMigration.Identifier,
            report.TargetMigrationId);

        Assert.Equal(expectedMigrationIds, report.Assessments.Select(static assessment => assessment.MigrationId));
        Assert.Equal(expectedObjects, report.Assessments.Select(static assessment => assessment.ObjectName));

        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
    }
}

internal sealed class SqlitePendingOrderingContext : DbContext
{
    private readonly DbConnection _connection;
    private readonly IServiceProvider _services;

    /// <summary>Creates an isolated provider scope over the caller-owned in-memory connection.</summary>
    /// <param name="connection">The case-local native connection.</param>
    /// <param name="services">The case-local provider services.</param>
    public SqlitePendingOrderingContext(
        DbConnection connection,
        IServiceProvider services
    )
    {
        _connection = connection;
        _services = services;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(
        DbContextOptionsBuilder optionsBuilder
    )
    {
        optionsBuilder.UseSqlite(
            _connection,
            provider => provider.MigrationsAssembly(typeof(SqlitePendingOrderingContext).Assembly.FullName));

        optionsBuilder.UseSqliteSafeMigrations<SqlitePendingOrderingContext>();
        optionsBuilder.UseInternalServiceProvider(_services);
    }

    /// <inheritdoc />
    protected override void OnModelCreating(
        ModelBuilder modelBuilder
    ) => BuildTarget(modelBuilder, includeLast: true);

    /// <summary>Builds the exact cumulative target without allowing culture to affect model identity.</summary>
    /// <param name="modelBuilder">The fixture model receiving the ordered columns.</param>
    /// <param name="includeLast">Whether the last ordinal migration has been applied.</param>
    internal static void BuildTarget(
        ModelBuilder modelBuilder,
        bool includeLast
    ) => modelBuilder.Entity("OrderingRecord",
        entity =>
        {
            entity.ToTable("ordering_records");
            entity
                .Property<int>("Id")
                .ValueGeneratedNever();

            entity.HasKey("Id");
            entity.Property<int?>("Z");
            if (includeLast)
            {
                entity.Property<int?>("a");
            }
        });
}

[DbContext(typeof(SqlitePendingOrderingContext))]
[Migration(Identifier)]
// WHY: EF migration discovery constructs this fixture from its attributed migration identity.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqliteOrdinalFirstMigration : Migration
{
    /// <summary>The first ordinal identity; its timestamp deliberately equals the other fixture's timestamp.</summary>
    public const string Identifier = "20261119000000_Z";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddColumnIfNotExists<int>("Z", "ordering_records", "INTEGER", nullable: true);

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingOrderingContext.BuildTarget(modelBuilder, includeLast: false);
}

[DbContext(typeof(SqlitePendingOrderingContext))]
[Migration(Identifier)]
// WHY: EF migration discovery constructs this fixture from its attributed migration identity.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqliteOrdinalLastMigration : Migration
{
    /// <summary>
    /// The last ordinal identity; culture-sensitive ordering incorrectly moves it before uppercase Z.
    /// </summary>
    public const string Identifier = "20261119000000_a";

    /// <inheritdoc />
    protected override void Up(
        MigrationBuilder migrationBuilder
    ) => migrationBuilder.AddColumnIfNotExists<int>("a", "ordering_records", "INTEGER", nullable: true);

    /// <inheritdoc />
    protected override void BuildTargetModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingOrderingContext.BuildTarget(modelBuilder, includeLast: true);
}

[DbContext(typeof(SqlitePendingOrderingContext))]
// WHY: EF discovers this target-model snapshot through its DbContext attribute.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed class SqlitePendingOrderingSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(
        ModelBuilder modelBuilder
    ) => SqlitePendingOrderingContext.BuildTarget(modelBuilder, includeLast: true);
}
