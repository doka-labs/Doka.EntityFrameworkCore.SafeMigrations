namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Text row classifiers reject missing SELECT before binding, including a matching replay.</summary>
    /// <param name="matching">Whether the physical column already has the complete target contract.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextAlter_MissingSelectPermissionIsAnInvariantRefusal(
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var length = matching ? 20 : 80;
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.text_read_values(Caption nvarchar("
            + length.ToString(CultureInfo.InvariantCulture) + ") NULL); "
            + "INSERT dbo.text_read_values VALUES(N'short'); "
            + "CREATE USER text_alter_writer WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO text_alter_writer; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO text_alter_writer; "
            + "GRANT ALTER,UPDATE ON OBJECT::dbo.text_read_values TO text_alter_writer; "
            + "DENY SELECT ON OBJECT::dbo.text_read_values TO text_alter_writer;");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'text_alter_writer';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("text_read_values",
            new ExpectedColumnDefinition("Caption", typeof(string), true,
                "nvarchar(" + length.ToString(CultureInfo.InvariantCulture) + ")", maxLength: length),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AlterColumnIfDifferent("text_read_values",
            new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(20)", maxLength: 20),
            new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)", maxLength: 80),
            SafeMigrationPolicy.RepairIfSafe);

        SafeMigrationRunReport report;
        Exception? failure;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-text-alter-read-permission"));

            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var width = await ScalarIntAsync(connectionString,
            "SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.text_read_values') "
            + "AND name=N'Caption';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.text_read_values WHERE Caption=N'short';");

        // Assert
        Assert.Equal(length * 2, width);
        Assert.Equal(1, rows);
        Assert.Equal(2, report.Assessments.Count);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[1].ObservedState);
        Assert.Equal("column_alter_read_permission", report.Assessments[1].AnalysisCode);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
    }

    /// <summary>Rewriting text needs UPDATE permission; a matching replay does not acquire that grant.</summary>
    /// <param name="matching">Whether the physical column already has the complete target contract.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextAlter_MissingUpdatePermissionRejectsOnlyMutation(
        bool matching
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var length = matching ? 20 : 80;
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.text_permission_values(Caption nvarchar("
            + length.ToString(CultureInfo.InvariantCulture) + ") NULL); "
            + "INSERT dbo.text_permission_values VALUES(N'short'); "
            + "CREATE USER text_alter_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO text_alter_reader; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO text_alter_reader; "
            + "GRANT ALTER,SELECT ON OBJECT::dbo.text_permission_values TO text_alter_reader;");

        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'text_alter_reader';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn("text_permission_values",
            new ExpectedColumnDefinition("Caption", typeof(string), true,
                "nvarchar(" + length.ToString(CultureInfo.InvariantCulture) + ")", maxLength: length),
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AlterColumnIfDifferent("text_permission_values",
            new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(20)", maxLength: 20),
            new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)", maxLength: 80),
            SafeMigrationPolicy.RepairIfSafe);

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);
        var sql = string.Join("\n", commands.Select(static command => command.CommandText));
        SafeMigrationRunReport report;
        Exception? failure;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-text-alter-write-permission"));

            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var width = await ScalarIntAsync(connectionString,
            "SELECT max_length FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.text_permission_values') "
            + "AND name=N'Caption';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.text_permission_values WHERE Caption=N'short';");

        // Assert
        Assert.DoesNotContain("UPDATE [", sql, StringComparison.Ordinal);
        Assert.Equal(length * 2, width);
        Assert.Equal(1, rows);
        Assert.Equal(2, report.Assessments.Count);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        var assessment = report.Assessments[1];
        if (matching)
        {
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(SafeMigrationObservedState.Unsupported, assessment.ObservedState);
            Assert.Equal("column_alter_write_permission", assessment.AnalysisCode);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }
    }
}
