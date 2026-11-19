namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Exercises the ColumnsDriftMatrix migration contracts in an independent class collection.</summary>
public sealed class MySqlColumnsDriftMatrixIntegrationTests : MySqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned engine while retaining per-test database ownership.</summary>
    /// <param name="fixture">The initialized shared native engine.</param>
    public MySqlColumnsDriftMatrixIntegrationTests(
        MySqlEngineContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Detects each observable column-facet drift without changing the accepted canonical contract.</summary>
    /// <param name="facet">The independently changed native definition facet.</param>
    [Theory]
    [InlineData("max_length")]
    [InlineData("nullability")]
    [InlineData("collation")]
    [InlineData("comment")]
    [InlineData("default_value")]
    [InlineData("missing_default")]
    [InlineData("fixed_length")]
    [InlineData("precision")]
    [InlineData("scale")]
    [InlineData("computed_expression")]
    [InlineData("stored_generation")]
    public async Task ObservableColumnFacetDrift_IsRejectedOneFieldAtATime(
        string facet
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE `column_facets` (`a` int NOT NULL, `b` int NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);
        var canonical = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "char(4)",
                maxLength: 4,
                isFixedLength: true),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: true),
        };

        var create = new MigrationBuilder(context.Database.ProviderName!);
        foreach (var definition in canonical)
        {
            create.EnsureColumn("column_facets", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        // Act
        await ExecuteOperationsAsync(context, create.Operations, CancellationToken.None);
        await ExecuteOperationsAsync(context, create.Operations, CancellationToken.None);

        var canonicalReport = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                create.Operations,
                new SafeMigrationRunOptions("column-facets-canonical"),
                CancellationToken.None);

        var variant = facet switch
        {
            "max_length" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(81)",
                maxLength: 81,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "nullability" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                false,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "collation" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_general_ci"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "comment" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "different",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "default_value" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("different")),
            "missing_default" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "varchar(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
                comment: "canonical"),
            "fixed_length" => new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "varchar(4)",
                maxLength: 4,
                isFixedLength: false),
            "precision" => new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(11,2)",
                precision: 11,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            "scale" => new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "decimal(10,3)",
                precision: 10,
                scale: 3,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            "computed_expression" => new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Subtract, "b"),
                isStored: true),
            "stored_generation" => new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "int",
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b"),
                isStored: false),
            _ => throw new ArgumentOutOfRangeException(nameof(facet)),
        };

        var drift = new MigrationBuilder(context.Database.ProviderName!);
        drift.EnsureColumn("column_facets", variant, SafeMigrationPolicy.ThrowIfDifferent);

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                drift.Operations,
                new SafeMigrationRunOptions($"column-drift-{variant.Name}"),
                CancellationToken.None);

        // Assert
        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Ready, canonicalReport.Status);
        Assert.All(
            canonicalReport.Assessments,
            rowAssessment => Assert.Equal(SafeMigrationObservedState.Matching, rowAssessment.ObservedState));

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }
}
