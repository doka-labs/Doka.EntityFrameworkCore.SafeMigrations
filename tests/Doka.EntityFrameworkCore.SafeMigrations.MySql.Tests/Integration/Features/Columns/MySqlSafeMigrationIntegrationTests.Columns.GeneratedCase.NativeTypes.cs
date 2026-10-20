namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>An invalid native CAST target cannot borrow the portable AST renderer's valid integer alias.</summary>
    /// <param name="check">Whether native source belongs to a CHECK rather than a generated column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedNativeCastTargetCannotBorrowPortableAlias(bool check)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL, "
            + "native_result bigint GENERATED ALWAYS AS (CAST(id AS SIGNED)) VIRTUAL, "
            + "CONSTRAINT ck_source_cast CHECK(CAST(id AS SIGNED) > 0));");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (check)
        {
            builder.EnsureCheckConstraint(SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
            {
                Name = "ck_source_cast", Table = Table, Sql = "CAST(id AS bigint) > 0",
            }), SafeMigrationPolicy.ThrowIfDifferent);
        }
        else
        {
            builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
            {
                Name = "native_result", Table = Table, ClrType = typeof(long), ColumnType = "bigint",
                ComputedColumnSql = "CAST(id AS bigint)", IsStored = false, IsNullable = true,
            }), SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            $"PREPARE source_cast_probe FROM 'SELECT CAST(id AS bigint) FROM {Table}';"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("captured-native-cast-target"), CancellationToken.None);

        // Assert
        Assert.Equal(1064, Assert.IsType<MySqlException>(nativeFailure).Number);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("captured_expression_cast_target_unverified", Assert.Single(report.Assessments).AnalysisCode);
    }

    /// <summary>Native CAST spellings and explicitly typed portable aliases retain successful replay.</summary>
    /// <param name="source">The retained native spelling, or null for the explicit portable API.</param>
    [Theory]
    [InlineData("CAST(id AS SIGNED)")]
    [InlineData("CAST(id AS SIGNED INTEGER)")]
    [InlineData(null)]
    public async Task NativeCastSourceAndExplicitPortableAliasRemainSupported(string? source)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL); "
            + $"INSERT INTO `{Table}` VALUES (-1);");
        var definition = source is null
            ? new ExpectedColumnDefinition("native_result", typeof(long), true, "bigint",
                computedExpression: SafeMigrationSql.Cast(SqlColumn("id"), "bigint"), isStored: false)
            : SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
            {
                Name = "native_result", Table = Table, ClrType = typeof(long), ColumnType = "bigint",
                ComputedColumnSql = source, IsStored = false, IsNullable = true,
            });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-cast-source"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var result = failure is null
            ? await ScalarIntAsync(connectionString, $"SELECT native_result FROM `{Table}`;") : int.MinValue;

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.True(failure is null, failure?.ToString());
        Assert.Equal(-1, result);
    }
}
