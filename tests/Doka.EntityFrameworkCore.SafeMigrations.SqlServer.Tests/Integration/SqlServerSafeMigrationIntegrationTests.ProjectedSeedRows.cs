namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Qualifies inline unique constraints even when managed metadata omits the physical candidate.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, null, null, SafeMigrationReportStatus.Blocked)]
    [InlineData(true, null, null, SafeMigrationReportStatus.Blocked)]
    [InlineData(false, "a", "A", SafeMigrationReportStatus.Blocked)]
    [InlineData(true, "a", "A", SafeMigrationReportStatus.Blocked)]
    [InlineData(false, null, "a", SafeMigrationReportStatus.Ready)]
    [InlineData(true, null, "a", SafeMigrationReportStatus.Ready)]
    public async Task ProjectedSeedRows_InlineUniqueKeyUsesServerEqualityWithoutLaterKey(
        bool split,
        string? first,
        string? second,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CI_AS;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("inline_seed_rows", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_inline_seed_rows", row => row.Id);
            table.UniqueConstraint("UQ_inline_seed_rows_Code", row => row.Code);
        });
        if (split)
        {
            builder.EnsureModelManagedDataFromModel("inline_seed_rows", ["Id"], ["int"],
                ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 1, first } });
            builder.EnsureModelManagedDataFromModel("inline_seed_rows", ["Id"], ["int"],
                ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 2, second } });
        }
        else
        {
            builder.EnsureModelManagedDataFromModel("inline_seed_rows", ["Id"], ["int"],
                ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 1, first }, { 2, second } });
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-prospective-inline-seed"));

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.inline_seed_rows');");

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(expected == SafeMigrationReportStatus.Blocked
            ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(0, tableCount);
    }

    /// <summary>
    /// Structured filtered unique keys omit NULL rows but still block colliding non-null rows before insert.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData(null, null, SafeMigrationReportStatus.Ready)]
    [InlineData("same", "same", SafeMigrationReportStatus.Blocked)]
    [InlineData("first", "second", SafeMigrationReportStatus.Ready)]
    public async Task ProjectedSeedRows_UsesTheAcceptedStructuredUniqueFilter(
        string? first,
        string? second,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("filtered_seed_rows", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
        }, constraints: table => table.PrimaryKey("PK_filtered_seed_rows", row => row.Id));
        var index = new ExpectedIndexDefinition("IX_filtered_seed_rows_Code", "filtered_seed_rows",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("Code")));

        builder.Operations.Add(
            new SafeMigrationOperation(new EnsureIndexIntent(index), SafeMigrationPolicy.ThrowIfDifferent));
        builder.EnsureModelManagedDataFromModel("filtered_seed_rows", ["Id"], ["int"],
            ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 1, first }, { 2, second } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-prospective-filtered-seed"));

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(expected == SafeMigrationReportStatus.Blocked
            ? SafeMigrationAction.RejectDataBlocked : SafeMigrationAction.Apply, report.Assessments[2].Action);
    }

    /// <summary>
    /// Invalid typed authored values never escape as a raw SQL conversion failure on an existing destination.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("datetime", 1, SafeMigrationReportStatus.Blocked)]
    [InlineData("datetime", 1753, SafeMigrationReportStatus.Ready)]
    [InlineData("smalldatetime", 1, SafeMigrationReportStatus.Blocked)]
    [InlineData("smalldatetime", 1900, SafeMigrationReportStatus.Ready)]
    [InlineData("datetime2(7)", 1, SafeMigrationReportStatus.Ready)]
    public async Task LiveManagedTemporalValue_IsQualifiedBeforeTypedStateProbe(
        string storeType,
        int year,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.managed_dates (Id int NOT NULL PRIMARY KEY, Value " + storeType + " NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel("managed_dates", ["Id"], ["int"],
            ["Id", "Value"], ["int", storeType], new object?[,] { { 1, new DateTime(year, 1, 1) } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-managed-date-value-boundary"));

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.managed_dates;");

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(expected == SafeMigrationReportStatus.Blocked
            ? SafeMigrationAction.RejectUnsupported : SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(0, rowCount);
    }
}
