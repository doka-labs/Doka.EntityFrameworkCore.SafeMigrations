namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

/// <summary>Bounds native-catalog normalization to explicit structured expected expressions.</summary>
public sealed class SqliteStructuredCatalogExpressionTests : SqliteIntegrationTestBase
{
    /// <summary>Matches identifier delimiters without changing literal bytes, operators, or unknown syntax.</summary>
    /// <param name="actualSql">The actual valid SQLite catalog expression.</param>
    /// <param name="expectedSql">The structured helper's SQL input.</param>
    /// <param name="matching">Whether the expressions have the same structural contract.</param>
    [Theory]
    [InlineData("Code = 'Active'", "Code = 'Active'", true)]
    [InlineData("[Code] = 'Active'", "Code = 'Active'", true)]
    [InlineData("`Code` = 'Active'", "Code = 'Active'", true)]
    [InlineData("\"Code\" = 'Active'", "Code = 'Active'", true)]
    [InlineData("\"Code\" = 'ACTIVE'", "Code = 'Active'", false)]
    [InlineData("Code = 'Active  Value'", "Code = 'Active Value'", false)]
    [InlineData("Id >= 0", "Id > 0", false)]
    [InlineData("Code = 'it''s active'", "Code = 'it''s active'", true)]
    [InlineData("Code = 'a'' b'", "Code = 'a b'", false)]
    [InlineData("Id > 0 /* catalog trivia */", "Id > 0", true)]
    [InlineData("Id == 1", "Id = 1", false)]
    [InlineData("((\"Code\"\n=\n'Active'))", "Code = 'Active'", true)]
    public async Task StructuredCheckMatchesOnlyEquivalentNativeCatalogSql(
        string actualSql,
        string expectedSql,
        bool matching
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE expression_nodes (Id INTEGER NOT NULL, Code TEXT NOT NULL, "
            + "CONSTRAINT ck_expression_nodes CHECK (" + actualSql + "));");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddCheckConstraintIfNotExists("ck_expression_nodes", "expression_nodes", expectedSql);
        var versionBefore = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-structured-native-check"));

        var exception = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var versionAfter = await ScalarIntAsync(connection, "PRAGMA schema_version;");

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            assessment.ObservedState);
        Assert.Equal(versionBefore, versionAfter);
        if (matching)
        {
            Assert.Null(exception);
            Assert.Equal(SafeMigrationAction.NoOp, assessment.Action);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
        }
    }

    /// <summary>Explicit opaque expected contracts never gain structural normalization as a side effect.</summary>
    [Fact]
    public async Task ExplicitOpaqueCheckRetainsItsTextualContract()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection,
            "CREATE TABLE expression_nodes (Code TEXT NOT NULL, "
            + "CONSTRAINT ck_expression_nodes CHECK (\"Code\" = 'Active'));");

        await using var context = CreateContext(connection);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(
            new ExpectedCheckConstraintDefinition("ck_expression_nodes", "expression_nodes", "Code = 'Active'"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
            context, builder.Operations, new SafeMigrationRunOptions("sqlite-opaque-check-boundary"));

        // Assert
        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SafeMigrationObservedState.Different, assessment.ObservedState);
        Assert.Equal(SafeMigrationAction.RejectDifferent, assessment.Action);
    }
}
