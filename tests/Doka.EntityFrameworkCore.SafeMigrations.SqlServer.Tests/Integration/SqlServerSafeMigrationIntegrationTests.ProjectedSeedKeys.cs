namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>
    /// Qualifies the complete authored insert lineage with actual SQL Server NULL and collation equality.
    /// </summary>
    [SqlServerLiveTheory]
    [InlineData("Latin1_General_100_CI_AS", "a", "b", SafeMigrationReportStatus.Ready)]
    [InlineData("Latin1_General_100_CI_AS", "a", "A", SafeMigrationReportStatus.Blocked)]
    [InlineData("Latin1_General_100_BIN2", "a", "A", SafeMigrationReportStatus.Ready)]
    [InlineData("Latin1_General_100_CI_AS", null, null, SafeMigrationReportStatus.Blocked)]
    [InlineData("Latin1_General_100_CI_AS", null, "b", SafeMigrationReportStatus.Ready)]
    public async Task ProjectedAuthoredUniqueKey_UsesServerTypedCollatedEquality(
        string collation,
        string? first,
        string? second,
        SafeMigrationReportStatus expected
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation, first, second);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlserver-authored-seed-key"));

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.authored_seed_keys');");

        // Assert
        Assert.Equal(expected, report.Status);
        if (expected == SafeMigrationReportStatus.Ready)
        {
            Assert.Equal(SafeMigrationAction.Apply, report.Assessments[3].Action);
        }
        else
        {
            Assert.Contains(report.Assessments, static assessment =>
                assessment.AnalysisCode == "projected_seed_row_collision");
        }

        Assert.Equal(0, tableCount);
    }

    /// <summary>Successfully applies, replays, and verifies an authored seed followed by its unique index.</summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_AppliesReplaysAndVerifies()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation: null, "alpha", "beta");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-lifecycle"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-postflight"));

        var rowCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.authored_seed_keys;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.Equal(2, rowCount);
        Assert.All(postflight.Assessments, static assessment => Assert.True(assessment.PostconditionSatisfied));
    }

    /// <summary>
    /// Does not deduplicate distinct primary keys merely because destination precision rounds them together.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_TypedPrecisionCollisionIsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("authored_precision", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<decimal>(type: "decimal(8,2)", precision: 8, scale: 2, nullable: false),
        }, constraints: table => table.PrimaryKey("PK_authored_precision", row => row.Id));
        builder.EnsureModelManagedDataFromModel("authored_precision", ["Id"], ["int"],
            ["Id", "Code"], ["int", "decimal(8,2)"], new object?[,] { { 1, 1.231m }, { 2, 1.234m } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        builder.CreateIndexIfNotExists("IX_authored_precision_Code", "authored_precision", ["Code"], unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-precision"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDataBlocked, report.Assessments[1].Action);
        Assert.Equal("projected_seed_row_collision", report.Assessments[1].AnalysisCode);
    }

    /// <summary>
    /// Any earlier opaque DML invalidates authored-row proof rather than silently ignoring additional rows.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_OpaqueMutationInvalidatesTheLineage()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation: null, "alpha", "beta");
        builder.Operations.Insert(3, new SqlOperation
        {
            Sql = "INSERT dbo.authored_seed_keys (Id, Code) VALUES (3, N'alpha');",
        });

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-opaque-boundary"));

        // Assert
        Assert.Equal(SafeMigrationAction.ValidateAtRuntime, report.Assessments[^1].Action);
        Assert.NotEqual("projected_seed_key_data_safe", report.Assessments[^1].AnalysisCode);
    }

    /// <summary>Exact repeated ensures describe one authored physical row instead of a duplicate key.</summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_RepeatedIdenticalEnsureRemainsSafe()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation: null, "alpha", "beta");
        builder.Operations.Insert(2, builder.Operations[1]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-repeat"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[2].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[4].Action);
    }

    /// <summary>A captured insert proof cannot authorize a key after an accepted update or deletion.</summary>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedAuthoredUniqueKey_ManagedMutationInvalidatesTheLineage(bool delete)
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation: null, "alpha", "beta");
        var mutation = new MigrationBuilder(context.Database.ProviderName!);
        if (delete)
        {
            mutation.DeleteModelManagedDataFromModel("authored_seed_keys", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Code"], ["nvarchar(20)"], new object?[,] { { "alpha" } });
        }
        else
        {
            mutation.UpdateModelManagedDataFromModel("authored_seed_keys", ["Id"], ["int"],
                new object?[,] { { 1 } }, ["Code"], ["nvarchar(20)"],
                new object?[,] { { "alpha" } }, new object?[,] { { "gamma" } },
                uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        }

        builder.Operations.Insert(3, mutation.Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-mutation"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[4].Action);
        Assert.Equal("projected_key_data_state_unknown", report.Assessments[4].AnalysisCode);
    }

    /// <summary>
    /// Captured value types do not imply that differently configured destination columns remain safe.
    /// </summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_CapturedStoreTypeMismatchIsUnproven()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = AuthoredSeedKeyBuilder(context, collation: null, "alpha", "beta");
        var seed = new MigrationBuilder(context.Database.ProviderName!);
        seed.EnsureModelManagedDataFromModel("authored_seed_keys", ["Id"], ["int"],
            ["Id", "Code"], ["int", "nvarchar(40)"], new object?[,] { { 3, "gamma" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        builder.Operations.Insert(3, seed.Operations[0]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-captured-type"));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[4].Action);
        Assert.NotEqual("projected_seed_key_data_safe", report.Assessments[4].AnalysisCode);
    }

    /// <summary>A projected ANSI proof cannot authorize a seed that the runtime code-page guard would reject.</summary>
    [SqlServerLiveFact]
    public async Task ProjectedAuthoredUniqueKey_AnsiRuntimeCollationBoundaryRemainsBlocked()
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "ALTER DATABASE CURRENT COLLATE Latin1_General_100_CI_AS;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("authored_ansi", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20,
                nullable: false, collation: "Latin1_General_100_BIN2"),
        }, constraints: table => table.PrimaryKey("PK_authored_ansi", row => row.Id));
        builder.EnsureModelManagedDataFromModel("authored_ansi", ["Id"], ["int"],
            ["Id", "Code"], ["int", "varchar(20)"], new object?[,] { { 1, "\u00e9" } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        builder.CreateIndexIfNotExists("IX_authored_ansi_Code", "authored_ansi", ["Code"], unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-authored-seed-ansi-boundary"));

        var tableCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.authored_ansi');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);
        Assert.Equal("projected_seed_row_data_unproven", report.Assessments[1].AnalysisCode);
        Assert.Equal(0, tableCount);
    }

    private static MigrationBuilder AuthoredSeedKeyBuilder(
        DbContext context,
        string? collation,
        string? first,
        string? second
    )
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("authored_seed_keys", table => new
        {
            Id = table.Column<int>(type: "int", nullable: false),
            Code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true, collation: collation),
        }, constraints: table => table.PrimaryKey("PK_authored_seed_keys", row => row.Id));
        builder.EnsureModelManagedDataFromModel("authored_seed_keys", ["Id"], ["int"],
            ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 1, first } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        builder.EnsureModelManagedDataFromModel("authored_seed_keys", ["Id"], ["int"],
            ["Id", "Code"], ["int", "nvarchar(20)"], new object?[,] { { 2, second } },
            uniqueKeys: [new ExpectedModelManagedDataUniqueKeyDefinition(["Code"])]);
        builder.CreateIndexIfNotExists("IX_authored_seed_keys_Code", "authored_seed_keys", ["Code"], unique: true);

        return builder;
    }
}
