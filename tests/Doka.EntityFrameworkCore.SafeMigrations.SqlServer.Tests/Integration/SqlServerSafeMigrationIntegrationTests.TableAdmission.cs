namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>A maximum-row warning remains valid schema admission with NULL or empty variable rows.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VariableColumnAtFixedBoundary_CreatesAndAcceptsEmptyValues(
        bool nullable
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedTableDefinition("layout_items",
            [new ExpectedColumnDefinition("First", typeof(string), false, "char(8000)"),
                new ExpectedColumnDefinition("Second", typeof(string), false, "char(53)"),
                new ExpectedColumnDefinition("Variable", typeof(string), nullable, "varchar(1)")]);

        var operation = new SafeMigrationOperation(new EnsureTableIntent(definition,
            SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteSqlAsync(connectionString,
            "INSERT dbo.layout_items VALUES ('a','b'," + (nullable ? "NULL" : "''") + ");");
        var after = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_items;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(after).ObservedState);
        Assert.Equal(1, rows);
    }

    /// <summary>Unsupported column counts and fixed-row layouts stop before any table DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData("int", 1025, 0, "table_column_limit")]
    [InlineData("char", 2, 5000, "table_fixed_row_limit")]
    [InlineData("char", 2, 4054, "table_fixed_row_limit")]
    public async Task InvalidOrdinaryTableAdmission_IsInvariantUnsupportedBeforeDdl(
        string type,
        int count,
        int width,
        string code
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE TABLE dbo.layout_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER layout_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.layout_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var columns = Enumerable.Range(0, count).Select(index => new ExpectedColumnDefinition(
            "C" + index.ToString(CultureInfo.InvariantCulture), type == "int" ? typeof(int) : typeof(string), false,
            type == "int" ? "int" : "char(" + (width == 4054 && index == 0 ? 4000 : width)
                .ToString(CultureInfo.InvariantCulture) + ")")).ToArray();

        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "layout_items", columns), SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.layout_items');");

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal(code, analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(0, tables);
        Assert.Equal(0, events);
    }

    /// <summary>Admits ordinary limits and large variable columns without suppressing row overflow.</summary>
    [SqlServerLiveTheory]
    [InlineData("int", 1024, 0)]
    [InlineData("char", 2, 4053)]
    [InlineData("varchar", 2, 5000)]
    public async Task ValidOrdinaryTableAdmission_AppliesAndReplays(
        string type,
        int count,
        int width
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var columns = Enumerable.Range(0, count).Select(index => new ExpectedColumnDefinition(
            "C" + index.ToString(CultureInfo.InvariantCulture), type == "int" ? typeof(int) : typeof(string), false,
            type == "int" ? "int" : type + "(" + (width == 4053 && index == 0 ? 4000 : width)
                .ToString(CultureInfo.InvariantCulture) + ")")).ToArray();

        var operation = new SafeMigrationOperation(new EnsureTableIntent(new ExpectedTableDefinition(
            "layout_items", columns), SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        if (type != "int")
        {
            await ExecuteSqlAsync(connectionString,
                "INSERT dbo.layout_items VALUES (REPLICATE('a',4000),REPLICATE('b',"
                + width.ToString(CultureInfo.InvariantCulture) + "));");
        }

        var after = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var physicalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.layout_items');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.layout_items;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(after).ObservedState);
        Assert.True(Assert.Single(after).PostconditionSatisfied);
        Assert.Equal(count, physicalColumns);
        Assert.Equal(type == "int" ? 0 : 1, rows);
    }
}
