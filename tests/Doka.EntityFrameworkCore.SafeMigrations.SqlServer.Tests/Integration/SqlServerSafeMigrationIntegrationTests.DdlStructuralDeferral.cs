namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>DDL-trigger-created dependencies invalidate captured widening certificates before their runtime guard.</summary>
    /// <param name="dependency">The physical dependency introduced by DROP_INDEX.</param>
    /// <param name="disabled">Whether the trigger is disabled and emits no side effects.</param>
    [SqlServerLiveTheory]
    [InlineData("index", false)]
    [InlineData("check", false)]
    [InlineData("index", true)]
    [InlineData("check", true)]
    public async Task IntegerWidening_DroppedOldDependencyCannotCertifyTriggerCreatedReplacement(
        string dependency,
        bool disabled
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_structure_values(Value int NOT NULL); "
            + "INSERT dbo.ddl_structure_values VALUES(-2147483648),(2147483647); "
            + "CREATE INDEX IX_old ON dbo.ddl_structure_values(Value);");
        var mutation = dependency == "index"
            ? "CREATE INDEX IX_new ON dbo.ddl_structure_values(Value);"
            : "ALTER TABLE dbo.ddl_structure_values ADD CONSTRAINT CK_new CHECK(Value>=-2147483648);";

        // WHY: Dynamic trigger SQL is deliberately absent from static expression
        // dependencies. A captured index list cannot infer the resulting object.
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER create_ddl_dependency ON DATABASE FOR DROP_INDEX AS "
            + "IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]','nvarchar(128)')=N'IX_old' "
            + "EXEC sys.sp_executesql N'" + mutation + "';");
        if (disabled)
        {
            await ExecuteSqlAsync(connectionString, "DISABLE TRIGGER create_ddl_dependency ON DATABASE;");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_old", "ddl_structure_values");
        builder.AlterColumnIfDifferent("ddl_structure_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        SafeMigrationRunReport report;
        Exception? failure;
        int observedDependency;

        // Act
        report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-structural-freshness"));
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await ExecuteOperationsAsync(context, [builder.Operations[0]]);
            observedDependency = await context.Database.SqlQueryRaw<int>(dependency == "index"
                    ? "SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ddl_structure_values') "
                        + "AND name=N'IX_new'"
                    : "SELECT COUNT(*) AS [Value] FROM sys.check_constraints "
                        + "WHERE parent_object_id=OBJECT_ID(N'dbo.ddl_structure_values') AND name=N'CK_new'")
                .SingleAsync();
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, [builder.Operations[1]]));
            await transaction.RollbackAsync();
        }

        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.ddl_structure_values WHERE Value IN(-2147483648,2147483647);");
        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.ddl_structure_values') "
            + "AND name=N'Value' AND system_type_id=56;");

        // Assert
        Assert.Equal(disabled ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.RuntimeValidationRequired,
            report.Status);
        Assert.Equal(disabled ? 0 : 1, observedDependency);
        if (disabled)
        {
            Assert.Equal(SafeMigrationAction.Repair, report.Assessments[1].Action);
            Assert.Null(failure);
        }
        else
        {
            AssertDdlDeferredOrigin(report.Assessments[1], "projected_ddl_trigger_structure_unknown", 0,
                typeof(SafeMigrationOperation), migrationId: null);
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }

        Assert.Equal(2, preserved);
        Assert.Equal(1, source);
    }

    /// <summary>A trigger can remove unrelated matching metadata, which is recreated only by the fresh runtime guard.</summary>
    [SqlServerLiveFact]
    public async Task MatchingIndex_TriggerRemovedMetadataCannotRemainPreflightNoOp()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_metadata_source(Value int NOT NULL); "
            + "CREATE TABLE dbo.ddl_metadata_other(Value int NOT NULL); "
            + "CREATE INDEX IX_old ON dbo.ddl_metadata_source(Value); "
            + "CREATE INDEX IX_matching ON dbo.ddl_metadata_other(Value);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER remove_matching_ddl_metadata ON DATABASE FOR DROP_INDEX AS "
            + "IF EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]','nvarchar(128)')=N'IX_old' "
            + "EXEC sys.sp_executesql N'DROP INDEX IX_matching ON dbo.ddl_metadata_other;';");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_old", "ddl_metadata_source");
        builder.CreateIndexIfNotExists("IX_matching", "ddl_metadata_other", ["Value"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-matching-metadata"));
        await ExecuteOperationsAsync(context, builder.Operations);
        var remaining = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.ddl_metadata_other') "
            + "AND name=N'IX_matching';");
        var replay = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-matching-metadata-replay"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.RuntimeValidationRequired, report.Status);
        AssertDdlDeferredOrigin(report.Assessments[1], "projected_ddl_trigger_structure_unknown", 0,
            typeof(SafeMigrationOperation), migrationId: null);
        Assert.Equal(1, remaining);
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static assessment => Assert.Equal(SafeMigrationAction.NoOp, assessment.Action));
    }
}
