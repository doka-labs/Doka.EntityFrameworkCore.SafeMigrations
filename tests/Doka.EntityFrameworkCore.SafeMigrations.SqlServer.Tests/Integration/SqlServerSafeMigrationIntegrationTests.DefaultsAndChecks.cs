namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Distinguishes absent defaults from both inline and legacy stand-alone bound defaults.</summary>
    /// <param name="defaultKind">The original physical default representation.</param>
    /// <param name="expectedState">The expected exact absence classification.</param>
    [SqlServerLiveTheory]
    [InlineData("none", SafeMigrationObservedState.Matching)]
    [InlineData("inline", SafeMigrationObservedState.Different)]
    [InlineData("bound", SafeMigrationObservedState.Different)]
    public async Task DefaultFreeColumn_MatchesOnlyAnAbsentPhysicalDefault(
        string defaultKind,
        SafeMigrationObservedState expectedState
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var defaultSql = defaultKind switch
        {
            "none" => string.Empty,
            "inline" => "ALTER TABLE dbo.default_absence ADD CONSTRAINT DF_default_absence "
                + "DEFAULT ('legacy') FOR Caption;",
            "bound" => "EXEC(N'CREATE DEFAULT dbo.bound_caption_default AS ''legacy'';'); "
                + "EXEC sys.sp_bindefault N'dbo.bound_caption_default', N'dbo.default_absence.Caption';",
            _ => throw new ArgumentOutOfRangeException(nameof(defaultKind)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_absence (Id int NOT NULL, Caption varchar(80) NULL); " + defaultSql);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>("Caption", "default_absence", type: "varchar(80)",
            nullable: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context,
            builder.Operations, new SafeMigrationRunOptions("sqlserver-default-absence"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(expectedState, assessment.ObservedState);
        Assert.Equal(expectedState == SafeMigrationObservedState.Matching
            ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, assessment.Action);
    }

    /// <summary>
    /// Replays a generated check constraint after SQL Server persists its normalized expression.
    /// </summary>
    [SqlServerLiveFact]
    public async Task GeneratedCheckConstraint_AppliesAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.checked_orders (Id int NOT NULL, Amount int NOT NULL);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "CK_checked_orders_Amount",
            "checked_orders",
            "[Amount] >= 0");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var constraintCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id = OBJECT_ID(N'dbo.checked_orders', N'U') "
            + "AND name = N'CK_checked_orders_Amount' AND is_disabled = 0 AND is_not_trusted = 0;");

        // Assert
        Assert.Equal(1, constraintCount);
    }

    /// <summary>
    /// Replays a generated literal default after SQL Server normalizes its stored definition.
    /// </summary>
    [SqlServerLiveFact]
    public async Task GeneratedLiteralDefault_AppliesAndReplays()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.default_orders (Id int NOT NULL); "
            + "INSERT INTO dbo.default_orders (Id) VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "Caption",
            "default_orders",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "ready");

        // Act
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);

        var matchingRows = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM dbo.default_orders WHERE Id = 1 AND Caption = N'ready';");

        var defaultCount = await ScalarIntAsync(
            connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints WHERE parent_object_id = "
            + "OBJECT_ID(N'dbo.default_orders', N'U');");

        // Assert
        Assert.Equal(1, matchingRows);
        Assert.Equal(1, defaultCount);
    }

    /// <summary>
    /// Refuses to treat an unstamped check constraint as source-controlled evidence.
    /// </summary>
    [SqlServerLiveFact]
    public async Task UnstampedCheckConstraint_IsDifferentEvenWhenNameAndTextMatch()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.unstamped_checks (Amount int NOT NULL); "
            + "ALTER TABLE dbo.unstamped_checks ADD CONSTRAINT CK_unstamped_checks_Amount "
            + "CHECK ([Amount] >= 0);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists(
            "CK_unstamped_checks_Amount",
            "unstamped_checks",
            "[Amount] >= 0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-unstamped-check"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
    }

    /// <summary>
    /// Refuses to treat an unstamped default constraint as source-controlled evidence.
    /// </summary>
    [SqlServerLiveFact]
    public async Task UnstampedDefaultConstraint_IsDifferentEvenWhenLiteralMatches()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE dbo.unstamped_defaults "
            + "(Id int NOT NULL, Caption nvarchar(80) NOT NULL "
            + "CONSTRAINT DF_unstamped_defaults_Caption DEFAULT (N'ready'));");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<string>(
            "Caption",
            "unstamped_defaults",
            type: "nvarchar(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "ready");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("sqlserver-unstamped-default"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, Assert.Single(report.Assessments).ObservedState);
    }
}
