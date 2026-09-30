namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies captured SQL Server payloads remain representable before any mutation.
/// </summary>
public sealed class SqlServerModelManagedDataLiteralContracts : SqlServerIntegrationTestBase
{
    /// <summary>
    /// Creates the literal suite with an isolated SQL Server database per test.
    /// </summary>
    public SqlServerModelManagedDataLiteralContracts(SqlServerContainerFixture fixture) : base(fixture) { }

    /// <summary>
    /// Retains exact-boundary payload bytes while blocking longer string and binary payloads.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("char(5)")]
    [InlineData("varchar(5)")]
    [InlineData("nchar(5)")]
    [InlineData("nvarchar(5)")]
    [InlineData("binary(5)")]
    [InlineData("varbinary(5)")]
    public async Task BoundedPayload_AppliesMaximumAndRejectsOverlongWithoutMutation(string storeType)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.bounded_payloads (Id int NOT NULL CONSTRAINT PK_bounded_payloads PRIMARY KEY, "
            + "Payload " + storeType + " NOT NULL);");
        await using var context = CreateContext(connectionString);
        var binary = storeType.Contains("binary", StringComparison.Ordinal);
        object maximum = binary ? new byte[] { 1, 2, 3, 4, 5 } : "abcde";
        object overlong = binary ? new byte[] { 1, 2, 3, 4, 5, 6 } : "abcdef";
        var valid = new MigrationBuilder(context.Database.ProviderName!);
        valid.EnsureModelManagedDataFromModel(
            "bounded_payloads", ["Id"], ["int"], ["Id", "Payload"], ["int", storeType],
            new object?[,] { { 1, maximum } });
        var invalid = new MigrationBuilder(context.Database.ProviderName!);
        invalid.EnsureModelManagedDataFromModel(
            "bounded_payloads", ["Id"], ["int"], ["Id", "Payload"], ["int", storeType],
            new object?[,] { { 2, overlong } });

        // Act
        await ExecuteOperationsAsync(context, valid.Operations);
        await ExecuteOperationsAsync(context, valid.Operations);
        var validReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, valid.Operations, new SafeMigrationRunOptions("sqlserver-payload-boundary"));

        var invalidReport = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, invalid.Operations, new SafeMigrationRunOptions("sqlserver-payload-overlong"));

        var generationFailure = Record.Exception(() =>
            context.GetService<IMigrationsSqlGenerator>().Generate(invalid.Operations, context.Model));

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.bounded_payloads;");
        var actualBytes = await ScalarIntAsync(connectionString,
            "SELECT DATALENGTH(Payload) FROM dbo.bounded_payloads WHERE Id = 1;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(validReport.Assessments).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(invalidReport.Assessments).ObservedState);
        Assert.IsType<NotSupportedException>(generationFailure);
        Assert.Equal(1, rowCount);
        Assert.Equal(storeType.StartsWith('n') ? 10 : 5, actualBytes);
    }

    /// <summary>
    /// Proves ANSI encoding loss and UTF-8 byte capacity before any bounded character CAST.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", 3, true)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", 2, false)]
    [InlineData("Latin1_General_100_CI_AS", 5, false)]
    public async Task AnsiPayload_RequiresLosslessCodePageAndEncodedByteCapacity(
        string collation,
        int maximumBytes,
        bool supported
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var administrativeConnection = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        await ExecuteSqlAsync(administrativeConnection,
            "ALTER DATABASE [" + database.Replace("]", "]]", StringComparison.Ordinal) + "] COLLATE " + collation
            + ";");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ansi_payloads (Id int NOT NULL CONSTRAINT PK_ansi_payloads PRIMARY KEY, "
            + "Payload varchar(" + maximumBytes.ToString(CultureInfo.InvariantCulture) + ") NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "ansi_payloads", ["Id"], ["int"], ["Id", "Payload"],
            ["int", "varchar(" + maximumBytes.ToString(CultureInfo.InvariantCulture) + ")"],
            new object?[,] { { 1, "\u4E2D" } });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-ansi-value-loss"));

        var mutationFailure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.ansi_payloads;");
        var exact = supported
            ? await ScalarIntAsync(connectionString,
                "SELECT COUNT(*) FROM dbo.ansi_payloads WHERE CONVERT(nvarchar(max), Payload) = N'\u4E2D' "
                + "AND DATALENGTH(Payload) = 3;")
            : 0;

        // Assert
        if (supported)
        {
            Assert.Null(mutationFailure);
            Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(report.Assessments).ObservedState);
            Assert.Equal(1, rowCount);
            Assert.Equal(1, exact);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
            Assert.Equal(51002, Assert.IsType<SqlException>(mutationFailure).Number);
            Assert.Equal(0, rowCount);
        }
    }

    /// <summary>
    /// Uses canonical provider precision for lawful decimal and time values and replay.
    /// </summary>
    [SqlServerLiveFact]
    public async Task DecimalAndTimePrecision_AppliesCanonicalProviderContractAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.precision_payloads (Id int NOT NULL CONSTRAINT PK_precision_payloads PRIMARY KEY, "
            + "Amount decimal(5,2) NOT NULL, RecordedAt datetime2(3) NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureModelManagedDataFromModel(
            "precision_payloads", ["Id"], ["int"], ["Id", "Amount", "RecordedAt"],
            ["int", "decimal(5,2)", "datetime2(3)"],
            new object?[,] { { 1, 12.345m, new DateTime(2026, 9, 30, 12, 0, 0, 123).AddTicks(4567) } });

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-canonical-precision"));

        var exact = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.precision_payloads WHERE Amount = CAST(12.345 AS decimal(5,2));");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, exact);
    }
}
