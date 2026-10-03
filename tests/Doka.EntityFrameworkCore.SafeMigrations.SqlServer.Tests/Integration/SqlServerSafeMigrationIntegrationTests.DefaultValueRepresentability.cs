namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Stops an unrepresentable immutable datetime default before column or table DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InvalidDatetimeDefault_IsInvariantUnsupportedWithoutMutation(
        bool inlineTable,
        bool structured
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1); "
            + "CREATE TABLE dbo.default_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_default_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.default_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(DateTime.MinValue, "datetime"))
            : SafeMigrationDefaultValue.Literal(DateTime.MinValue);

        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime",
            defaultValue: defaultValue);

        var operation = TemporalDefaultOperation(definition, inlineTable);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var originalColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.default_items', N'U');");

        var originalRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1;");

        var newTables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.new_default_items', N'U');");

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("default_value_unrepresentable", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.False(analysis.PostconditionSatisfied);
        Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(1, originalColumns);
        Assert.Equal(1, originalRows);
        Assert.Equal(0, newTables);
        Assert.Equal(0, ddlEvents);
    }

    /// <summary>
    /// Backfills valid datetime lower bounds and datetime2 minima, then applies and replays inline defaults.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("datetime", 1753, false, false)]
    [InlineData("datetime2", 1, false, false)]
    [InlineData("datetime", 1753, true, false)]
    [InlineData("datetime2", 1, true, false)]
    [InlineData("smalldatetime", 1900, false, false)]
    [InlineData("smalldatetime", 1900, true, false)]
    [InlineData("datetime", 1753, false, true)]
    [InlineData("datetime", 1753, true, true)]
    [InlineData("smalldatetime", 1900, false, true)]
    [InlineData("smalldatetime", 1900, true, true)]
    public async Task ValidTemporalLiteralDefault_AppliesBackfillsAndReplays(
        string storeType,
        int year,
        bool inlineTable,
        bool structured
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1);");
        await using var context = CreateContext(connectionString);
        var value = new DateTime(year, 1, 1);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value, storeType))
            : SafeMigrationDefaultValue.Literal(value);

        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: defaultValue);

        var operation = TemporalDefaultOperation(definition, inlineTable);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var table = inlineTable ? "new_default_items" : "default_items";
        var expected = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // Act
        var beforeAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteSqlAsync(connectionString, "INSERT dbo." + table + " (Id) VALUES (2);");
        var afterAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var matchingRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo." + table + " WHERE Created = CAST(N'" + expected + "' AS datetime2(7));");

        var defaults = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo."
            + table + "', N'U');");

        // Assert
        var before = Assert.Single(beforeAnalyses);
        var after = Assert.Single(afterAnalyses);
        Assert.Equal(SafeMigrationObservedState.Missing, before.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
        Assert.True(after.PostconditionSatisfied);
        Assert.Equal(inlineTable ? 1 : 2, matchingRows);
        Assert.Equal(1, defaults);
    }

    /// <summary>Preserves known current-time and COALESCE defaults under the shared scalar conversion guard.</summary>
    [SqlServerLiveTheory]
    [InlineData("CURRENT_TIMESTAMP", "datetime", false)]
    [InlineData("GETDATE()", "datetime2", true)]
    [InlineData("SYSUTCDATETIME()", "datetime", false)]
    [InlineData("COALESCE(SYSUTCDATETIME(), CAST(NULL AS datetime2))", "datetime2", true)]
    public async Task KnownTemporalExpressionDefault_AppliesBackfillsAndReplays(
        string sql,
        string storeType,
        bool inlineTable
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, storeType,
            defaultValue: SafeMigrationDefaultValue.Sql(sql));

        var operation = TemporalDefaultOperation(definition, inlineTable);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var table = inlineTable ? "new_default_items" : "default_items";

        // Act
        var beforeAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        if (inlineTable)
        {
            await ExecuteSqlAsync(connectionString, "INSERT dbo." + table + " (Id) VALUES (1);");
        }

        var afterAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        var matchingRows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo." + table + " WHERE Id = 1 AND Created IS NOT NULL;");

        var defaults = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(N'dbo."
            + table + "', N'U');");

        // Assert
        var before = Assert.Single(beforeAnalyses);
        var after = Assert.Single(afterAnalyses);
        Assert.Equal(SafeMigrationObservedState.Missing, before.ObservedState);
        Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
        Assert.True(after.PostconditionSatisfied);
        Assert.Equal(1, matchingRows);
        Assert.Equal(1, defaults);
    }

    /// <summary>Rejects a typed temporal function when its destination family cannot represent its result.</summary>
    [SqlServerLiveTheory]
    [InlineData("integer")]
    [InlineData("coalesce")]
    public async Task IncompatibleComputedDefault_IsUnsupportedBeforeBackfillOrDdl(
        string kind
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = kind == "integer"
            ? new ExpectedColumnDefinition("Created", typeof(int), false, "int",
                defaultValue: SafeMigrationDefaultValue.Sql("SYSUTCDATETIME()"))
            : new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime",
                defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function("COALESCE",
                    SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp),
                    SafeMigrationSql.Literal(DateTime.MinValue))));

        var operation = TemporalDefaultOperation(definition, inlineTable: false);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.default_items', N'U');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal(kind == "integer" ? "default_expression_unproven" : "default_value_unrepresentable",
            analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.IsType<NotSupportedException>(failure);
        Assert.Equal(1, columns);
        Assert.Equal(1, rows);
    }

    /// <summary>
    /// Retains valid ANSI exact fits while refusing UTF-8 byte overflow and code-page loss before any DDL.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", "\u00E9", 1, false, false)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", "\u00E9", 2, true, false)]
    [InlineData("Latin1_General_100_CI_AS", "\u6F22", 8, false, false)]
    [InlineData("Latin1_General_100_CI_AS", "A", 1, true, false)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", "\u00E9", 1, false, true)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", "\u00E9", 2, true, true)]
    public async Task AnsiLiteralDefault_ProvesEncodedBytesAndRoundtripBeforeMutation(
        string collation,
        string value,
        int maximumBytes,
        bool supported,
        bool inlineTable
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1); "
            + "CREATE TABLE dbo.default_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_default_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.default_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false,
            "varchar(" + maximumBytes.ToString(CultureInfo.InvariantCulture) + ")",
            collation: new SafeMigrationCollationIdentifier(collation),
            defaultValue: SafeMigrationDefaultValue.Literal(value));

        var operation = TemporalDefaultOperation(definition, inlineTable);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var table = inlineTable ? "new_default_items" : "default_items";

        // Act
        var beforeAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        if (supported && inlineTable)
        {
            await ExecuteSqlAsync(connectionString, "INSERT dbo." + table + " (Id) VALUES (1);");
        }

        var afterAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo." + table + "', N'U');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1;");
        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_ddl_events;");

        // Assert
        var before = Assert.Single(beforeAnalyses);
        var after = Assert.Single(afterAnalyses);
        Assert.Equal(1, rows);
        Assert.Equal(supported ? 2 : inlineTable ? 0 : 1, columns);
        if (supported)
        {
            Assert.Equal(SafeMigrationObservedState.Missing, before.ObservedState);
            Assert.Null(failure);
            Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
            Assert.True(ddlEvents > 0);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, before.ObservedState);
            Assert.Equal("default_value_unrepresentable", before.Code);
            Assert.True(before.IsInvariantUnsupported);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(SafeMigrationObservedState.Unsupported, after.ObservedState);
            Assert.Equal(0, ddlEvents);
        }
    }

    /// <summary>Checks the authored ANSI target before an alteration can replace an existing default.</summary>
    [SqlServerLiveFact]
    public async Task AlterAnsiDefault_RejectsEncodedOverflowWithoutChangingRowsOrDefault()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL, "
            + "Caption varchar(1) COLLATE Latin1_General_100_CI_AS_SC_UTF8 "
            + "NOT NULL DEFAULT ('A')); INSERT dbo.default_items (Id) VALUES (1); "
            + "CREATE TABLE dbo.default_ddl_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_default_ddl_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.default_ddl_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var oldDefinition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(1)",
            collation: new SafeMigrationCollationIdentifier("Latin1_General_100_CI_AS_SC_UTF8"),
            defaultValue: SafeMigrationDefaultValue.Literal("A"));

        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "varchar(1)",
            collation: oldDefinition.Collation, defaultValue: SafeMigrationDefaultValue.Literal("\u00E9"));

        var operation = new SafeMigrationOperation(new AlterColumnIntent("default_items", definition, oldDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1 AND Caption = 'A';");

        var defaults = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints "
            + "WHERE parent_object_id = OBJECT_ID(N'dbo.default_items', N'U');");

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_ddl_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
        Assert.Equal("default_value_unrepresentable", analysis.Code);
        Assert.True(analysis.IsInvariantUnsupported);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, rows);
        Assert.Equal(1, defaults);
        Assert.Equal(0, ddlEvents);
    }

    /// <summary>Projects a provider-proven required current-time column before a later index operation.</summary>
    [SqlServerLiveTheory]
    [InlineData("GETDATE()")]
    [InlineData("CURRENT_TIMESTAMP")]
    public async Task KnownDefaultColumn_OrderedRunnerProjectsTheLaterIndexAndVerifiesReplay(
        string sql
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL PRIMARY KEY); INSERT dbo.default_items VALUES (1);");
        await using var context = CreateContext(connectionString);
        var definition = new ExpectedColumnDefinition("Created", typeof(DateTime), false, "datetime2",
            defaultValue: SafeMigrationDefaultValue.Sql(sql));

        SafeMigrationOperation[] operations =
        [
            new(new EnsureColumnIntent("default_items", definition), SafeMigrationPolicy.ThrowIfDifferent),
            new(new EnsureIndexIntent(new ExpectedIndexDefinition("IX_default_Created", "default_items",
                [new ExpectedIndexKeyDefinition("Created")])), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var runner = context.GetService<ISafeMigrationRunner>();
        var options = new SafeMigrationRunOptions("sqlserver-default-projection");

        // Act
        var before = await runner.AnalyzeAsync(context, operations, options);
        await ExecuteOperationsAsync(context, operations);
        var after = await runner.VerifyAsync(context, operations, options);
        await ExecuteOperationsAsync(context, operations);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1 AND Created IS NOT NULL;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, before.Status);
        Assert.All(before.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, after.Status);
        Assert.All(after.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
        Assert.Equal(1, rows);
    }

    /// <summary>Rejects ANSI intermediate loss or UTF-8 overflow before a Unicode default can backfill rows.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, "Latin1_General_100_CI_AS_SC_UTF8", 1, "\u00E9", false)]
    [InlineData(true, "Latin1_General_100_CI_AS_SC_UTF8", 1, "\u00E9", false)]
    [InlineData(false, "Latin1_General_100_CI_AS_SC_UTF8", 2, "\u00E9", true)]
    [InlineData(true, "Latin1_General_100_CI_AS_SC_UTF8", 2, "\u00E9", true)]
    [InlineData(false, "Latin1_General_100_CI_AS_SC_UTF8", 1, "a", true)]
    [InlineData(false, "Latin1_General_100_CI_AS", 8, "\u6F22", false)]
    [InlineData(true, "Latin1_General_100_CI_AS", 8, "\u6F22", false)]
    public async Task AnsiIntermediateDefault_ProvesUtf8BytesBeforeUnicodeBackfill(
        bool explicitCast,
        string collation,
        int maximum,
        string value,
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT COLLATE " + collation + ";");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_items (Id int NOT NULL); INSERT dbo.default_items VALUES (1); "
            + "CREATE TABLE dbo.default_intermediate_events (Id int NOT NULL);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER doka_default_intermediate_audit ON DATABASE FOR DDL_TABLE_EVENTS "
            + "AS INSERT dbo.default_intermediate_events VALUES (1);");
        await using var context = CreateContext(connectionString);
        var intermediate = "varchar(" + maximum.ToString(CultureInfo.InvariantCulture) + ")";
        var expression = explicitCast ? SafeMigrationSql.Cast(SafeMigrationSql.Literal(value), intermediate)
            : SafeMigrationSql.Literal(value, intermediate);

        var definition = new ExpectedColumnDefinition("Caption", typeof(string), false, "nvarchar(10)",
            defaultValue: SafeMigrationDefaultValue.Sql(expression));

        var operation = TemporalDefaultOperation(definition, inlineTable: false);
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var analyses = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        if (supported)
        {
            await ExecuteOperationsAsync(context, [operation]);
        }

        var afterAnalyses = await analyzer.AnalyzeAsync(context, [operation]);
        var columns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.default_items', N'U');");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_items WHERE Id = 1;");
        var stored = supported ? await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.default_items WHERE CONVERT(varbinary(max), Caption) = "
            + "CONVERT(varbinary(max), N'" + value.Replace("'", "''", StringComparison.Ordinal) + "');") : 0;

        var events = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.default_intermediate_events;");

        // Assert
        var analysis = Assert.Single(analyses);
        var after = Assert.Single(afterAnalyses);
        Assert.Equal(1, rows);
        if (supported)
        {
            Assert.Equal(SafeMigrationObservedState.Missing, analysis.ObservedState);
            Assert.Null(failure);
            Assert.Equal(SafeMigrationObservedState.Matching, after.ObservedState);
            Assert.True(after.PostconditionSatisfied);
            Assert.Equal(2, columns);
            Assert.Equal(1, stored);
            Assert.True(events > 0);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, analysis.ObservedState);
            Assert.Equal("default_value_unrepresentable", analysis.Code);
            Assert.True(analysis.IsInvariantUnsupported);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(SafeMigrationObservedState.Unsupported, after.ObservedState);
            Assert.Equal(1, columns);
            Assert.Equal(0, events);
        }
    }

    private static SafeMigrationOperation TemporalDefaultOperation(
        ExpectedColumnDefinition definition,
        bool inlineTable
    )
    {
        SafeMigrationIntent intent = inlineTable
            ? new EnsureTableIntent(new ExpectedTableDefinition("new_default_items",
                [new ExpectedColumnDefinition("Id", typeof(int), false, "int"), definition]),
                SafeMigrationTableMode.StrictDefinition)
            : new EnsureColumnIntent("default_items", definition);

        return new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
    }
}
