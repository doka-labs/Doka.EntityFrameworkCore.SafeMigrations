namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlGeneratedCaseIntegrationTests
{
    /// <summary>Fresh declarations enforce native identity-source capability before any table DDL.</summary>
    [Fact]
    public async Task DeclaredGeneratedIdentitySourceRetainsEngineSpecificCapability()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        var identityOperation = new AddColumnOperation
        {
            Name = "id", Table = Table, ClrType = typeof(int), ColumnType = "int", IsNullable = false,
        };

        identityOperation.AddAnnotation("Doka:MySql:ValueGenerationStrategy",
            MySqlValueGenerationStrategy.AutoIncrement);
        var identity = SafeMigrationExpectedDefinitionFactory.From(identityOperation);
        var generated = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "identity_result", Table = Table, ClrType = typeof(int), ColumnType = "int",
            ComputedColumnSql = "CASE WHEN `id` > 0 THEN 1 ELSE 0 END", IsStored = false, IsNullable = true,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition(Table, [identity, generated],
                primaryKey: new ExpectedPrimaryKeyDefinition("PRIMARY", Table, ["id"])),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("declared-generated-identity-capability"), CancellationToken.None);

        // Assert
        Assert.Equal(IsMySqlEngine() ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        if (IsMySqlEngine())
        {
            Assert.Equal("generated_column_identity_dependency", Assert.Single(report.Assessments).AnalysisCode);
        }
    }

    /// <summary>Unknown functions and even deterministic stored routines are not native generated built-ins.</summary>
    /// <param name="routineExists">Whether a deterministic stored routine exists with the captured name.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedFunctionCapabilityRejectsUnknownAndStoredRoutines(bool routineExists)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL); "
            + "CREATE TABLE native_function_probe (`id` int NOT NULL);");
        if (routineExists)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE FUNCTION source_only_function(value_input int) RETURNS int DETERMINISTIC NO SQL "
                + "RETURN value_input;");
        }

        const string expression = "source_only_function(`id`)";
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "function_result", Table = Table, ClrType = typeof(int), ColumnType = "int",
            ComputedColumnSql = expression, IsStored = false, IsNullable = true,
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE native_function_probe ADD COLUMN function_result int GENERATED ALWAYS AS ("
            + expression + ") VIRTUAL;"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-unverified-function"), CancellationToken.None);

        // Assert
        Assert.IsType<MySqlException>(nativeFailure);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("generated_column_function_unverified", Assert.Single(report.Assessments).AnalysisCode);
    }

    /// <summary>The measured deterministic built-ins create, preserve source values and replay exactly.</summary>
    /// <param name="expression">The admitted built-in and its native argument grammar.</param>
    /// <param name="type">The target physical type.</param>
    /// <param name="expected">The exact generated value for the seeded fixture.</param>
    [Theory]
    [InlineData("ABS(`id`)", "int", "1")]
    [InlineData("COALESCE(`id`, 0)", "int", "-1")]
    [InlineData("LOWER(`name`)", "varchar(20)", "alpha")]
    public async Task DeterministicGeneratedBuiltinsRetainNativeReplay(string expression, string type, string expected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL, name varchar(20) NULL); "
            + $"INSERT INTO `{Table}` VALUES (-1, 'Alpha');");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "function_result", Table = Table, ClrType = type == "int" ? typeof(int) : typeof(string),
            ColumnType = type, MaxLength = type == "int" ? null : 20,
            ComputedColumnSql = expression, IsStored = false, IsNullable = true,
        }), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var preflight = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-deterministic-builtin"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(async () =>
        {
            await ExecuteOperationsAsync(context, builder.Operations);
            await ExecuteOperationsAsync(context, builder.Operations);
        });

        var postflight = await context.GetService<ISafeMigrationRunner>().VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-deterministic-builtin-verify"), CancellationToken.None);
        var value = failure is null
            ? await ScalarStringAsync(connectionString, $"SELECT CAST(function_result AS CHAR) FROM `{Table}`;") : null;

        // Assert
        Assert.True(preflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(preflight));
        Assert.True(failure is null, failure?.ToString());
        Assert.True(postflight.Status == SafeMigrationReportStatus.Ready, DescribeGeneratedReport(postflight));
        Assert.Equal(expected, value);
    }

    /// <summary>Native rejection of non-deterministic generated expressions must be predicted before DDL.</summary>
    /// <param name="sql">The captured native expression rather than an opaque fallback.</param>
    /// <param name="stored">Whether the computed value is physically persisted.</param>
    [Theory]
    [InlineData("CURRENT_TIMESTAMP", false)]
    [InlineData("CURRENT_TIMESTAMP", true)]
    [InlineData("NOW()", false)]
    [InlineData("NOW()", true)]
    public async Task GeneratedClockCapabilityMatchesNativeEngine(string sql, bool stored)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` (`id` int NOT NULL); "
            + "CREATE TABLE native_clock_probe (`id` int NOT NULL);");
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "clock_result", Table = Table, ClrType = typeof(DateTime), ColumnType = "datetime(6)",
            ComputedColumnSql = sql, IsStored = stored, IsNullable = true,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE native_clock_probe ADD COLUMN clock_result datetime(6) GENERATED ALWAYS AS ("
            + sql + ") " + (stored ? "STORED" : "VIRTUAL") + ";"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-clock-capability"), CancellationToken.None);

        // Assert
        Assert.True(nativeFailure is null || nativeFailure is MySqlException,
            nativeFailure?.ToString());
        Assert.Equal(IsMySqlEngine() || stored, nativeFailure is not null);
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("generated_column_nondeterministic_expression", Assert.Single(report.Assessments).AnalysisCode);
    }

    /// <summary>Generated operands retain native AUTO_INCREMENT restrictions, not only type checks.</summary>
    [Fact]
    public async Task GeneratedIdentitySourceCapabilityMatchesNativeEngine()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await using var context = CreateContext(connectionString);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `{Table}` "
            + "(`id` int NOT NULL AUTO_INCREMENT PRIMARY KEY); "
            + "CREATE TABLE native_identity_probe (`id` int NOT NULL AUTO_INCREMENT PRIMARY KEY);");
        const string expression = "CASE WHEN `id` > 0 THEN 1 ELSE 0 END";
        var definition = SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "identity_result", Table = Table, ClrType = typeof(int), ColumnType = "int",
            ComputedColumnSql = expression, IsStored = false, IsNullable = true,
        });

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureColumn(Table, definition, SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var nativeFailure = await Record.ExceptionAsync(() => ExecuteSqlAsync(connectionString,
            "ALTER TABLE native_identity_probe ADD COLUMN identity_result int GENERATED ALWAYS AS ("
            + expression + ") VIRTUAL;"));
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-identity-capability"), CancellationToken.None);

        // Assert
        Assert.True(nativeFailure is null || nativeFailure is MySqlException, nativeFailure?.ToString());
        Assert.Equal(nativeFailure is null ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);
        Assert.Equal(nativeFailure is null
                ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported,
            Assert.Single(report.Assessments).ObservedState);
    }
}
