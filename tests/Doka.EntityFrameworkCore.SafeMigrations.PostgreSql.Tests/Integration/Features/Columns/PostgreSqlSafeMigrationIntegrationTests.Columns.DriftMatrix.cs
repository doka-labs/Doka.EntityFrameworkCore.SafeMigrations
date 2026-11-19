namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ColumnsDriftMatrix PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlColumnsDriftMatrixIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlColumnsDriftMatrixIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Observable Column Facet Drift Is Rejected One Field At A Time.</summary>
    /// <param name="caseId">The independently discovered single-facet drift fixture.</param>
    [Theory]
    [InlineData("max-length")]
    [InlineData("nullability")]
    [InlineData("collation")]
    [InlineData("comment")]
    [InlineData("default-value")]
    [InlineData("default-presence")]
    [InlineData("fixed-length")]
    [InlineData("precision")]
    [InlineData("scale")]
    [InlineData("computed-expression")]
    public async Task ObservableColumnFacetDrift_IsRejectedOneFieldAtATime(
        string caseId
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);

        await ExecuteSqlAsync(
            connectionString,
            "CREATE TABLE column_facets (a integer NOT NULL, b integer NOT NULL);",
            cancellationToken: CancellationToken.None);

        await using var context = CreateContext(connectionString);

        var canonical = new[]
        {
            new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "character(4)",
                maxLength: 4,
                isFixedLength: true),
            new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(10,2)",
                precision: 10,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "integer",
                isStored: true,
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Add, "b")),
        };

        var create = new MigrationBuilder(context.Database.ProviderName!);

        // WHY: Register the canonical baseline in declaration order before selecting one independent drift.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var definition in canonical)
        {
            create.EnsureColumn("column_facets", definition, SafeMigrationPolicy.ThrowIfDifferent);
        }

        var variant = caseId switch
        {
            "max-length" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(81)",
                maxLength: 81,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "nullability" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                false,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "collation" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("POSIX"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "comment" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "different",
                defaultValue: SafeMigrationDefaultValue.Literal("canonical")),
            "default-value" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "canonical",
                defaultValue: SafeMigrationDefaultValue.Literal("different")),
            "default-presence" => new ExpectedColumnDefinition(
                "text_value",
                typeof(string),
                true,
                "character varying(80)",
                maxLength: 80,
                collation: new SafeMigrationCollationIdentifier("C"),
                comment: "canonical"),
            "fixed-length" => new ExpectedColumnDefinition(
                "fixed_value",
                typeof(string),
                true,
                "character varying(4)",
                maxLength: 4,
                isFixedLength: false),
            "precision" => new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(11,2)",
                precision: 11,
                scale: 2,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            "scale" => new ExpectedColumnDefinition(
                "numeric_value",
                typeof(decimal),
                false,
                "numeric(10,3)",
                precision: 10,
                scale: 3,
                defaultValue: SafeMigrationDefaultValue.Literal(12.34m)),
            "computed-expression" => new ExpectedColumnDefinition(
                "generated_value",
                typeof(int),
                true,
                "integer",
                isStored: true,
                computedExpression: SqlColumnAndColumn("a", SafeMigrationSqlBinaryOperator.Subtract, "b")),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, "Unknown facet fixture."),
        };

        var drift = new MigrationBuilder(context.Database.ProviderName!);

        drift.EnsureColumn("column_facets", variant, SafeMigrationPolicy.ThrowIfDifferent);

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

        var report = await context
            .GetService<ISafeMigrationRunner>()
            .AnalyzeAsync(
                context,
                drift.Operations,
                new SafeMigrationRunOptions($"column-drift-{variant.Name}"),
                CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, canonicalReport.Status);
        Assert.Equal(4, canonicalReport.Assessments.Count);

        Assert.All(
            canonicalReport.Assessments,
            assessment => Assert.Equal(SafeMigrationObservedState.Matching, assessment.ObservedState));

        var assessment = Assert.Single(report.Assessments);

        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);

        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);

        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }
}
