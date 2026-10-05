namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlSafeMigrationIntegrationTests
{
    /// <summary>Checks exact table matching cannot substitute column count for names or ordinal positions.</summary>
    /// <param name="liveColumns">The independently created live column declarations.</param>
    /// <param name="matching">Whether the declarations match the exact expected shape.</param>
    [Theory]
    [InlineData("`id` int NOT NULL, `code` int NOT NULL", true)]
    [InlineData("`code` int NOT NULL, `id` int NOT NULL", false)]
    [InlineData("`id` int NOT NULL, `replacement` int NOT NULL", false)]
    [InlineData("`id` int NOT NULL", false)]
    [InlineData("`id` int NOT NULL, `code` int NOT NULL, `legacy` int NULL", false)]
    public async Task StrictTableDefinition_VerifiesExactColumnNamesAndOrdinals(
        string liveColumns,
        bool matching
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE `strict_column_shape` ({liveColumns});");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "strict_column_shape",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "int"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // WHY: Identical types and nullability isolate name/order drift from ordinary facet checks;
        // the independent live DDL also prevents the generator from preparing its own expected shape.
        var definitionBefore = await ReadStrictColumnTableDefinitionAsync(connectionString, "strict_column_shape");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-column-shape-preflight"));

        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-column-shape-postflight"));

        var definitionAfter = await ReadStrictColumnTableDefinitionAsync(connectionString, "strict_column_shape");

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(matching ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, preflight.Status);
        Assert.Equal(
            matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(matching ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, assessment.Action);
        Assert.Equal(matching, Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(definitionBefore, definitionAfter);
        if (matching)
        {
            Assert.Null(runtimeException);
        }
        else
        {
            var rejection = Assert.IsType<MySqlException>(runtimeException);
            Assert.Contains("doka_sm_different", rejection.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Checks a SQL NULL default comparison is a mismatch, while the expected default matches.</summary>
    /// <param name="hasExpectedDefault">Whether the live column declares the expected default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrictTableDefinition_RejectsNullDefaultComparisonWithoutLosingMatchingDefaults(
        bool hasExpectedDefault
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        var defaultClause = hasExpectedDefault ? " DEFAULT 7" : string.Empty;
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `strict_column_default` (`id` int NOT NULL, `code` int NOT NULL"
            + defaultClause + ");");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "strict_column_default",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "int"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "int",
                        defaultValue: SafeMigrationDefaultValue.Literal(7)),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        // WHY: SQL NULL is not FALSE. Confirm the actual catalog comparison is NULL so this case
        // guards the counting predicate's three-valued semantics rather than an ordinary inequality.
        var nullComparison = await ScalarIntAsync(
            connectionString,
            "SELECT (c.COLUMN_DEFAULT IN ('7')) IS NULL FROM INFORMATION_SCHEMA.COLUMNS c "
            + "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = 'strict_column_default' "
            + "AND c.COLUMN_NAME = 'code';");

        var definitionBefore = await ReadStrictColumnTableDefinitionAsync(connectionString, "strict_column_default");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-column-default-preflight"));

        var runtimeException = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var postflight = await runner.VerifyAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("strict-column-default-postflight"));

        var definitionAfter = await ReadStrictColumnTableDefinitionAsync(connectionString, "strict_column_default");

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(hasExpectedDefault ? 0 : 1, nullComparison);
        Assert.Equal(
            hasExpectedDefault ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            preflight.Status);
        Assert.Equal(
            hasExpectedDefault ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(
            hasExpectedDefault ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent,
            assessment.Action);
        Assert.Equal(hasExpectedDefault, Assert.Single(postflight.Assessments).PostconditionSatisfied);
        Assert.Equal(definitionBefore, definitionAfter);
        if (hasExpectedDefault)
        {
            Assert.Null(runtimeException);
        }
        else
        {
            var rejection = Assert.IsType<MySqlException>(runtimeException);
            Assert.Contains("doka_sm_different", rejection.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Checks a column collation of its own is still verified beside inherited ones.</summary>
    /// <param name="liveCollation">The collation the independently created pinned column carries.</param>
    /// <param name="matching">Whether that collation is the expected one.</param>
    /// <remarks>
    /// WHY: The inherited-table-default comparison is carried once per table rather than once per
    /// column, which is only sound while every column inherits. A table that mixes an explicit
    /// expectation with inherited ones must keep the per-column comparison, so this case fails if
    /// the shared clause is ever applied to a column that expects its own collation.
    /// </remarks>
    [Theory]
    [InlineData("utf8mb4_bin", true)]
    [InlineData("utf8mb4_general_ci", false)]
    public async Task StrictTableDefinition_VerifiesPinnedCollationBesideInheritedColumns(
        string liveCollation,
        bool matching
    )
    {
        // Arrange
        var connectionString = await Fixture.CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `mixed_collation` ("
            + $"`pinned` varchar(32) CHARACTER SET utf8mb4 COLLATE {liveCollation} NOT NULL, "
            + "`inherited` varchar(32) NOT NULL) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_general_ci;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "mixed_collation",
                [
                    new ExpectedColumnDefinition("pinned", typeof(string), isNullable: false,
                        storeType: "varchar(32)", collation: new SafeMigrationCollationIdentifier("utf8mb4_bin")),
                    new ExpectedColumnDefinition("inherited", typeof(string), isNullable: false,
                        storeType: "varchar(32)"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        var definitionBefore = await ReadStrictColumnTableDefinitionAsync(connectionString, "mixed_collation");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(
            context,
            builder.Operations,
            new SafeMigrationRunOptions("mixed-collation-preflight"));

        var definitionAfter = await ReadStrictColumnTableDefinitionAsync(connectionString, "mixed_collation");

        // Assert
        var assessment = Assert.Single(preflight.Assessments);
        Assert.Equal(
            matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(definitionBefore, definitionAfter);
    }

    /// <summary>Reads the complete server-side DDL for the independently created column fixture.</summary>
    private static async Task<string> ReadStrictColumnTableDefinitionAsync(
        string connectionString,
        string table
    )
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW CREATE TABLE `{table.Replace("`", "``", StringComparison.Ordinal)}`;";
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        if (!await reader.ReadAsync(CancellationToken.None))
        {
            throw new InvalidOperationException("The strict column fixture has no table definition.");
        }

        // WHY: SHOW CREATE TABLE returns the table name first; the second column contains the DDL.
        return reader.GetString(1);
    }
}
