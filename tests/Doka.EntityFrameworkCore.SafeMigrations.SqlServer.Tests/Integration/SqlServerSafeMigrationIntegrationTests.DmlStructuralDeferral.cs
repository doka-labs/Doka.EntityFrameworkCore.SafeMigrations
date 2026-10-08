namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Typed DML can remove or redefine unrelated matching metadata through an enabled trigger.</summary>
    /// <param name="writeKind">The typed EF INSERT, UPDATE or DELETE operation.</param>
    /// <param name="triggerMode">Absent, enabled or disabled trigger presence.</param>
    /// <param name="different">Whether the trigger replaces the index rather than only removing it.</param>
    [SqlServerLiveTheory]
    [InlineData("insert", "enabled", false)]
    [InlineData("update", "enabled", false)]
    [InlineData("delete", "enabled", false)]
    [InlineData("insert", "enabled", true)]
    [InlineData("insert", "disabled", false)]
    [InlineData("insert", "absent", false)]
    public async Task TypedDml_TriggerInvalidatesUnrelatedMatchingIndex(
        string writeKind,
        string triggerMode,
        bool different
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.dml_source(Id int NOT NULL); "
            + "CREATE TABLE dbo.dml_other(Value int NOT NULL,OtherValue int NOT NULL); "
            + "CREATE INDEX IX_matching ON dbo.dml_other(Value); "
            + (writeKind == "insert" ? string.Empty : "INSERT dbo.dml_source VALUES(1); "));
        if (triggerMode != "absent")
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TRIGGER mutate_dml_metadata ON dbo.dml_source AFTER " + writeKind.ToUpperInvariant()
                + " AS EXEC sys.sp_executesql N'DROP INDEX IX_matching ON dbo.dml_other; "
                + (different ? "CREATE INDEX IX_matching ON dbo.dml_other(OtherValue);" : string.Empty) + "';");
            if (triggerMode == "disabled")
            {
                await ExecuteSqlAsync(connectionString, "DISABLE TRIGGER mutate_dml_metadata ON dbo.dml_source;");
            }
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(TypedDml(writeKind));
        builder.CreateIndexIfNotExists("IX_matching", "dml_other", ["Value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-dml-matching-metadata"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var beforeEnsure = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.dml_other') AND name=N'IX_matching';");
        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
        var correctKey = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes i JOIN sys.index_columns k ON k.object_id=i.object_id "
            + "AND k.index_id=i.index_id JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id "
            + "WHERE i.object_id=OBJECT_ID(N'dbo.dml_other') AND i.name=N'IX_matching' AND c.name=N'Value';");

        // Assert
        if (triggerMode == "enabled")
        {
            Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_dml_trigger_structure_unknown", 0,
                TypedDml(writeKind).GetType(), migrationId: null);
            Assert.Equal(different ? 1 : 0, beforeEnsure);
        }
        else
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
            Assert.Null(report.Assessments[1].DeferredOrigin);
            Assert.Equal(1, beforeEnsure);
        }

        if (different)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(0, correctKey);
        }
        else
        {
            Assert.Null(failure);
            Assert.Equal(1, correctKey);
        }
    }

    /// <summary>Global trigger presence covers a cascade child even when the typed write's own table has no trigger.</summary>
    /// <param name="disabled">Whether the cascade child's trigger emits no side effects.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedDelete_CascadeChildTriggerInvalidatesUnrelatedMatchingIndex(bool disabled)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.dml_parent(Id int NOT NULL PRIMARY KEY); "
            + "CREATE TABLE dbo.dml_child(Id int NOT NULL PRIMARY KEY,ParentId int NOT NULL "
            + "REFERENCES dbo.dml_parent(Id) ON DELETE CASCADE); "
            + "CREATE TABLE dbo.dml_other(Value int NOT NULL); CREATE INDEX IX_matching ON dbo.dml_other(Value); "
            + "INSERT dbo.dml_parent VALUES(1); INSERT dbo.dml_child VALUES(1,1);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER cascade_metadata_mutation ON dbo.dml_child AFTER DELETE AS "
            + "EXEC sys.sp_executesql N'DROP INDEX IX_matching ON dbo.dml_other;';");
        if (disabled)
        {
            await ExecuteSqlAsync(connectionString, "DISABLE TRIGGER cascade_metadata_mutation ON dbo.dml_child;");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.Operations.Add(new DeleteDataOperation { Table = "dml_parent", KeyColumns = ["Id"],
            KeyColumnTypes = ["int"], KeyValues = new object?[,] { { 1 } } });
        builder.CreateIndexIfNotExists("IX_matching", "dml_other", ["Value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-dml-cascade-metadata"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var removed = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.dml_other') AND name=N'IX_matching';");
        await ExecuteOperationsAsync(context, [builder.Operations[1]]);
        var children = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.dml_child;");

        // Assert
        Assert.Equal(disabled ? SafeMigrationReportStatus.ReadyWithProviderOperations : SafeMigrationReportStatus.RuntimeValidationRequired,
            report.Status);
        Assert.Equal(disabled ? 1 : 0, removed);
        Assert.Equal(0, children);
        if (disabled)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
        }
        else
        {
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_dml_trigger_structure_unknown", 0,
                typeof(DeleteDataOperation), migrationId: null);
        }
    }

    /// <summary>Supplies explicit store types so typed EF writes need no unrelated model entity.</summary>
    private static MigrationOperation TypedDml(string writeKind)
        => writeKind switch
        {
            "insert" => new InsertDataOperation { Table = "dml_source", Columns = ["Id"], ColumnTypes = ["int"],
                Values = new object?[,] { { 1 } } },
            "update" => new UpdateDataOperation { Table = "dml_source", KeyColumns = ["Id"], KeyColumnTypes = ["int"],
                KeyValues = new object?[,] { { 1 } }, Columns = ["Id"], ColumnTypes = ["int"],
                Values = new object?[,] { { 2 } } },
            "delete" => new DeleteDataOperation { Table = "dml_source", KeyColumns = ["Id"], KeyColumnTypes = ["int"],
                KeyValues = new object?[,] { { 1 } } },
            _ => throw new ArgumentOutOfRangeException(nameof(writeKind)),
        };

    /// <summary>Typed ALTER's EF-generated UPDATE can invoke a DML trigger before the structural DDL.</summary>
    /// <param name="disabled">Whether the trigger is disabled and the baseline only changes its authored column.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedAlter_DefaultBackfillTriggerInvalidatesUnrelatedMatchingIndex(bool disabled)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.backfill_source(Value int NULL); INSERT dbo.backfill_source VALUES(NULL); "
            + "CREATE TABLE dbo.dml_other(Value int NOT NULL); CREATE INDEX IX_matching ON dbo.dml_other(Value);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER backfill_metadata_mutation ON dbo.backfill_source AFTER UPDATE AS "
            + "EXEC sys.sp_executesql N'DROP INDEX IX_matching ON dbo.dml_other;';");
        if (disabled)
        {
            await ExecuteSqlAsync(connectionString, "DISABLE TRIGGER backfill_metadata_mutation ON dbo.backfill_source;");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumn<int>("Value", "backfill_source", type: "int", nullable: false, defaultValue: 0,
            oldClrType: typeof(int), oldType: "int", oldNullable: true);
        builder.CreateIndexIfNotExists("IX_matching", "dml_other", ["Value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-typed-backfill-metadata"));
        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var removed = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.dml_other') AND name=N'IX_matching';");
        await ExecuteOperationsAsync(context, [builder.Operations[1]]);
        var backfilled = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.backfill_source WHERE Value=0;");

        // Assert
        Assert.Equal(disabled ? 1 : 0, removed);
        Assert.Equal(1, backfilled);
        if (disabled)
        {
            Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[1].Action);
            Assert.Null(report.Assessments[1].DeferredOrigin);
        }
        else
        {
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_dml_trigger_structure_unknown", 0,
                typeof(AlterColumnOperation), migrationId: null);
        }
    }
}
