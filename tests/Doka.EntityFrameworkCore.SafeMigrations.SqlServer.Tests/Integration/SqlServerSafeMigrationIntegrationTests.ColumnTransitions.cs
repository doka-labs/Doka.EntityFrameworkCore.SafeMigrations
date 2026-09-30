namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Narrows a text column and removes nullability when every stored value fits.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AlterColumn_FittingRowsPermitNarrowingAndNotNullReplay()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.fitting_text (Id int NOT NULL, Caption nvarchar(80) NULL); "
            + "INSERT INTO dbo.fitting_text (Id, Caption) VALUES (1, N'short');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<string>(
                "Caption",
                "fitting_text",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80,
                oldNullable: true),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var matchingColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.fitting_text', N'U') "
            + "AND name = N'Caption' AND max_length = 40 AND is_nullable = 0;");

        var preservedRowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.fitting_text WHERE Id = 1 AND Caption = N'short';");

        // Assert
        Assert.Equal(1, matchingColumnCount);
        Assert.Equal(1, preservedRowCount);
    }

    /// <summary>
    /// Expands a varchar column without changing existing row content.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AlterColumn_VarcharExpansionAppliesAndReplays()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.expand_text (Id int NOT NULL, Caption varchar(10) NOT NULL); "
            + "INSERT INTO dbo.expand_text (Id, Caption) VALUES (1, 'short');");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<string>(
                "Caption",
                "expand_text",
                type: "varchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(10)",
                oldMaxLength: 10),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var matchingColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.expand_text', N'U') "
            + "AND name = N'Caption' AND max_length = 200 AND is_nullable = 0;");

        var preservedRowCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.expand_text WHERE Id = 1 AND Caption = 'short';");

        // Assert
        Assert.Equal(1, matchingColumnCount);
        Assert.Equal(1, preservedRowCount);
    }

    /// <summary>
    /// Rejects a nullable-to-required transition while any row still contains null.
    /// </summary>
    [SqlServerLiveFact]
    public async Task AlterColumn_NullRowsBlockNotNullTransition()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.null_text (Id int NOT NULL, Caption nvarchar(80) NULL); "
            + "INSERT INTO dbo.null_text (Id, Caption) VALUES (1, NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferentFromModel(
            operation => operation.AlterColumn<string>(
                "Caption",
                "null_text",
                type: "nvarchar(80)",
                maxLength: 80,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(80)",
                oldMaxLength: 80,
                oldNullable: true),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-nullability-data-blocked"));

        var nullableColumnCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.columns "
            + "WHERE object_id = OBJECT_ID(N'dbo.null_text', N'U') "
            + "AND name = N'Caption' AND is_nullable = 1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(1, nullableColumnCount);
    }
}
