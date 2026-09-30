namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Proves constant conversion and predicate-side precedence before row reads or index DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData("int", "1", "[Flag] = N'abc'", false)]
    [InlineData("int", "1", "[Flag] = N'1'", true)]
    [InlineData("varchar(8)", "'a'", "[Flag] = 'a'", true)]
    [InlineData("varchar(8)", "'a'", "[Flag] = N'a'", false)]
    [InlineData("nvarchar(8)", "N'1'", "[Flag] = 1", false)]
    [InlineData("datetime", "'20000101'", "[Flag] = '0001-01-01T00:00:00'", false)]
    public async Task FilterConstantConversion_IsQualifiedWithoutMutationOnRejection(
        string type,
        string value,
        string filter,
        bool supported
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            $"CREATE TABLE dbo.filter_values (Code int NOT NULL, Flag {type} NULL); "
            + $"INSERT dbo.filter_values VALUES (1, {value}), (2, {value}); "
            + "CREATE TABLE dbo.filter_value_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_filter_value_ddl_audit ON DATABASE FOR DDL_INDEX_EVENTS "
            + "AS INSERT dbo.filter_value_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedIndexDefinition("IX_filter_values_Code", "filter_values",
            [new ExpectedIndexKeyDefinition("Code")], unique: true, filter: filter);

        var operation = new SafeMigrationOperation(new EnsureIndexIntent(definition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var originalId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_values', N'U');");
        var analyses = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var afterAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        var remainingId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.filter_values', N'U');");
        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_values;");
        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.filter_value_ddl_events;");
        var indexes = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.filter_values', N'U') "
            + "AND name = N'IX_filter_values_Code';");

        // Assert
        var analysis = Assert.Single(analyses);
        var after = Assert.Single(afterAnalyses);
        Assert.Equal(originalId, remainingId);
        Assert.Equal(2, rows);
        if (supported)
        {
            Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
            Assert.Null(failure);
            Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
            Assert.True(after.PostconditionSatisfied);
            Assert.Equal(1, indexes);
            Assert.True(events > 0);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
            Assert.Equal("index_filter_unproven", analysis.Code);
            Assert.True(analysis.IsInvariantUnsupported);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(SafeMigrationObservedState.Unsupported, after.ObservedState);
            Assert.Equal(0, indexes);
            Assert.Equal(0, events);
        }
    }

    /// <summary>Rejects an unavailable authored collation before scalar defaults or baseline binding.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDefaultCollation_IsInvariantUnsupportedBeforeBinding(
        bool inlineTable
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1); "
            + "CREATE TABLE dbo.default_collation_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_default_collation_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.default_collation_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(8)",
            collation: new SafeMigrationCollationIdentifier("Doka_missing_collation"),
            defaultValue: SafeMigrationDefaultValue.Literal("a"));

        var operation = TemporalDefaultOperation(definition, inlineTable);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.default_items', N'U');");

        var newTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'new_default_items';");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_items;");
        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_collation_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("column_collation_unproven", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, columns);
        Assert.Equal(0, newTables);
        Assert.Equal(1, rows);
        Assert.Equal(0, events);
    }
}
