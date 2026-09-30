namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Empty projected destinations still require provider-valid filtered-index constant conversions.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("1", SafeMigrationReportStatus.Ready, SafeMigrationAction.Apply)]
    [InlineData("abc", SafeMigrationReportStatus.Blocked, SafeMigrationAction.RejectUnsupported)]
    public async Task ProjectedEmptyTableFilter_QualifiesConstantBeforeAnyMutation(
        string value,
        SafeMigrationReportStatus expected,
        SafeMigrationAction action
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("projected_filter_values", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<int>(type: "int", nullable: true),
        }, constraints: table => table.PrimaryKey("PK_projected_filter_values", row => row.Id));
        var definition = new ExpectedIndexDefinition("IX_projected_filter_values_Code", "projected_filter_values",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Code"),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(value)));

        builder.Operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-projected-filter-value"));

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.projected_filter_values');");

        // Assert
        Assert.Equal(expected, report.Status);
        Assert.Equal(action, report.Assessments[1].Action);
        Assert.Equal(0, tableCount);
    }

    /// <summary>
    /// Filtered-index DDL cannot use a predicate that converts the character column into a numeric type.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ProjectedCharacterFilter_RejectsColumnSideConversion()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("projected_filter_precedence", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
        }, constraints: table => table.PrimaryKey("PK_projected_filter_precedence", row => row.Id));
        var definition = new ExpectedIndexDefinition(
            "IX_projected_filter_precedence_Code", "projected_filter_precedence",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Code"),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal(1)));

        builder.Operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-projected-filter-precedence"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("index_filter_value_unproven", report.Assessments[1].AnalysisCode);
    }

    /// <summary>The accepted constant-side conversion remains valid during actual filtered-index creation.</summary>
    [SqlServerLiveFact]
    public async Task ProjectedNumericFilter_ValidConstantCreatesThePhysicalIndex()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("projected_filter_runtime", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<int>(type: "int", nullable: true),
        }, constraints: table => table.PrimaryKey("PK_projected_filter_runtime", row => row.Id));
        var definition = new ExpectedIndexDefinition("IX_projected_filter_runtime_Code", "projected_filter_runtime",
            [new ExpectedIndexKeyDefinition("Code")], unique: true,
            structuredFilter: new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Code"),
                SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("1")));

        builder.Operations.Add(new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        var indexCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.projected_filter_runtime') "
            + "AND name = N'IX_projected_filter_runtime_Code';");

        // Assert
        Assert.Equal(1, indexCount);
    }
}
