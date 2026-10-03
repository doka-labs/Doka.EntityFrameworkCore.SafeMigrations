namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Applies the 900-byte boundary and rejects 901 bytes before orphan reads or ADD CONSTRAINT.</summary>
    [SqlServerLiveTheory]
    [InlineData(900, true)]
    [InlineData(901, false)]
    public async Task StandaloneForeignKey_DeclaredByteBoundaryIsFailClosed(
        int bytes,
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var storeType = "char(" + bytes.ToString(CultureInfo.InvariantCulture) + ")";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.width_parent (Value " + storeType + " NOT NULL, "
            + "CONSTRAINT UQ_width_parent UNIQUE NONCLUSTERED (Value)); "
            + "CREATE TABLE dbo.width_child (Value " + storeType + " NULL); "
            + "CREATE TABLE dbo.width_ddl_events (Id int NOT NULL);");
        await CreateForeignKeyWidthAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_width", "width_child", ["Value"], "width_parent", ["Value"])), SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();
        var originalId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.width_child', N'U');");

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.width_child', N'U');");

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.width_ddl_events;");
        var remainingId = await ScalarIntAsync(connectionString, "SELECT OBJECT_ID(N'dbo.width_child', N'U');");
        var matching = supported ? await analyzer.AnalyzeAsync(context, [operation]) : null;

        if (supported)
        {
            await ExecuteOperationsAsync(context, [operation]);
        }

        var replayDdlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.width_ddl_events;");

        // Assert
        Assert.Equal(originalId, remainingId);
        if (!supported)
        {
            Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, Assert.Single(before).ObservedState);
            Assert.Equal(51004, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(0, foreignKeys);
            Assert.Equal(0, ddlEvents);

            return;
        }

        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Null(failure);
        Assert.Equal(1, foreignKeys);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<SafeMigrationProviderAnalysis>>(matching)).ObservedState);
        Assert.Equal(ddlEvents, replayDdlEvents);
    }

    /// <summary>Allows 32 FK columns and rejects an immutable 33-column contract before any DDL.</summary>
    [SqlServerLiveTheory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task StandaloneForeignKey_ColumnBoundaryIsFailClosed(
        int count,
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var names = ForeignKeyWidthColumnNames(count);
        var definitions = string.Join(", ", names.Select(static name => "[" + name + "] int NOT NULL"));
        var key = supported ? ", CONSTRAINT UQ_width_parent UNIQUE NONCLUSTERED ("
            + string.Join(", ", names.Select(static name => "[" + name + "]")) + ")" : string.Empty;

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.width_parent (" + definitions + key + "); "
            + "CREATE TABLE dbo.width_child (" + definitions + "); "
            + "CREATE TABLE dbo.width_ddl_events (Id int NOT NULL);");
        await CreateForeignKeyWidthAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "FK_arity", "width_child", names, "width_parent", names)), SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var foreignKeys = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.width_child', N'U');");

        var matching = supported ? await analyzer.AnalyzeAsync(context, [operation]) : null;

        if (supported)
        {
            await ExecuteOperationsAsync(context, [operation]);
        }

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.width_ddl_events;");

        // Assert
        if (!supported)
        {
            var unsupported = Assert.Single(before);

            Assert.Equal(SafeMigrationObservedState.Unsupported, unsupported.ObservedState);
            Assert.Equal("foreign_key_unproven_width", unsupported.Code);
            Assert.True(unsupported.IsInvariantUnsupported);
            Assert.IsType<NotSupportedException>(failure);
            Assert.Equal(0, foreignKeys);
            Assert.Equal(0, ddlEvents);

            return;
        }

        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Null(failure);
        Assert.Equal(1, foreignKeys);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<SafeMigrationProviderAnalysis>>(matching)).ObservedState);
    }

    /// <summary>Checks authored byte limits for a new inline child and a new self-referencing table.</summary>
    [SqlServerLiveTheory]
    [InlineData(900, false, true)]
    [InlineData(901, false, false)]
    [InlineData(900, true, true)]
    [InlineData(901, true, false)]
    public async Task InlineForeignKey_AuthoredByteBoundaryIsFailClosed(
        int bytes,
        bool selfReference,
        bool supported
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var storeType = "char(" + bytes.ToString(CultureInfo.InvariantCulture) + ")";
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.width_parent (Value " + storeType + " NOT NULL, "
            + "CONSTRAINT UQ_width_parent UNIQUE NONCLUSTERED (Value)); "
            + "CREATE TABLE dbo.width_ddl_events (Id int NOT NULL);");
        await CreateForeignKeyWidthAuditAsync(connectionString);
        await using var context = CreateContext(connectionString);
        var foreignKey = new ExpectedForeignKeyDefinition("FK_inline_width", "width_child", ["Value"],
            selfReference ? "width_child" : "width_parent", ["Value"]);

        var table = new ExpectedTableDefinition("width_child",
            [new ExpectedColumnDefinition("Value", typeof(string), false, storeType)],
            uniqueConstraints: selfReference
                ? [new ExpectedUniqueConstraintDefinition("UQ_child_value", "width_child", ["Value"])]
                : [],
            foreignKeys: [foreignKey]);

        var operation = new SafeMigrationOperation(
            new EnsureTableIntent(table, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);

        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        // Act
        var before = await analyzer.AnalyzeAsync(context, [operation]);
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [operation]));
        var tables = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'width_child';");

        var matching = supported ? await analyzer.AnalyzeAsync(context, [operation]) : null;

        if (supported)
        {
            await ExecuteOperationsAsync(context, [operation]);
        }

        var ddlEvents = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.width_ddl_events;");

        // Assert
        if (!supported)
        {
            var unsupported = Assert.Single(before);

            Assert.Equal(SafeMigrationObservedState.Unsupported, unsupported.ObservedState);
            Assert.Equal("foreign_key_unproven_width", unsupported.Code);
            Assert.True(unsupported.IsInvariantUnsupported);
            Assert.IsType<NotSupportedException>(failure);
            Assert.Equal(0, tables);
            Assert.Equal(0, ddlEvents);

            return;
        }

        Assert.Equal(SafeMigrationObservedState.Missing, Assert.Single(before).ObservedState);
        Assert.Null(failure);
        Assert.Equal(1, tables);
        Assert.Equal(SafeMigrationObservedState.Matching, Assert.Single(Assert.IsAssignableFrom<
            IReadOnlyList<SafeMigrationProviderAnalysis>>(matching)).ObservedState);
    }

    private static string[] ForeignKeyWidthColumnNames(int count) => Enumerable.Range(0, count)
        .Select(static ordinal => "K" + ordinal.ToString(CultureInfo.InvariantCulture)).ToArray();

    private static Task CreateForeignKeyWidthAuditAsync(string connectionString)
        => ExecuteSqlAsync(connectionString, "CREATE TRIGGER doka_width_ddl_audit ON DATABASE "
            + "FOR DDL_TABLE_EVENTS, DDL_INDEX_EVENTS AS INSERT dbo.width_ddl_events VALUES (1);");
}
