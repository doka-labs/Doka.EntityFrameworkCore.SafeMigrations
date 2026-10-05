namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Verifies that only accepted provider-proven predicate replacements supersede stale live rejection.
/// </summary>
public sealed class SqlServerProjectedFilterReplacementTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=filter_replacements;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Exact accepted changes repair the old filter failure without weakening unrelated invariants.</summary>
    [Theory]
    [InlineData("accepted", SafeMigrationAction.Apply)]
    [InlineData("rejected", SafeMigrationAction.RejectUnsupported)]
    [InlineData("missing", SafeMigrationAction.RejectUnsupported)]
    [InlineData("stale", SafeMigrationAction.RejectUnsupported)]
    [InlineData("unproven", SafeMigrationAction.RejectUnsupported)]
    [InlineData("untouched", SafeMigrationAction.RejectUnsupported)]
    [InlineData("other-invariant", SafeMigrationAction.RejectUnsupported)]
    public async Task InvariantFilterFailure_RequiresEveryPredicateColumnToBeQualifiedAndAccepted(
        string scenario,
        SafeMigrationAction expected
    )
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var analyzer = new SqlServerSafeMigrationProviderAnalyzer(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var code = new ExpectedColumnDefinition("Code", typeof(int), true, "int");
        var initial = new ExpectedColumnDefinition("Flag", typeof(int), true, "int");
        var replacement = new ExpectedColumnDefinition("Flag", typeof(string), true, "nvarchar(20)");
        var untouched = new ExpectedColumnDefinition("Other", typeof(string), true, "nvarchar(20)");
        var filter = new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Flag"),
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("abc"));

        SafeMigrationSqlExpression predicate = scenario == "untouched"
            ? new SafeMigrationSqlBinaryExpression(filter, SafeMigrationSqlBinaryOperator.And,
                new SafeMigrationSqlBinaryExpression(SafeMigrationSql.Identifier("Other"),
                    SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("unchanged"))) : filter;

        var index = new EnsureIndexIntent(new ExpectedIndexDefinition("IX_filters_Code", "filters",
            [new ExpectedIndexKeyDefinition("Code")], structuredFilter: predicate));

        var operations = new SafeMigrationOperation[]
        {
            new(new EnsureTableIntent(new ExpectedTableDefinition("filters", [code, initial, untouched]),
                SafeMigrationTableMode.StrictDefinition), SafeMigrationPolicy.ThrowIfDifferent),
            new(new AlterColumnIntent("filters", replacement, initial), SafeMigrationPolicy.RepairIfSafe),
            new(index, SafeMigrationPolicy.ThrowIfDifferent),
        };

        var acceptedFlag = scenario switch
        {
            "rejected" => initial,
            "stale" => new ExpectedColumnDefinition("Flag", typeof(string), true, "nvarchar(20)"),
            "missing" => null,
            _ => replacement,
        };

        var source = new Source([code, untouched], acceptedFlag);
        var codeName = scenario == "other-invariant" ? "physical_table_unproven" : "index_filter_unproven";
        var live = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Unsupported,
            SafeMigrationRepairCapability.None, false, codeName) { IsInvariantUnsupported = true };

        var projected = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "projected_column_available");

        var proofResult = scenario == "unproven" ? -1 : 1;

        // Act
        await analyzer.CaptureProjectedSeedProofsAsync(operations,
            (_, _) => Task.FromResult(proofResult), CancellationToken.None);
        var analysis = ((ISafeMigrationProjectedKeyAnalyzer)analyzer).ValidateProjectedIndex(
            index, source, live, projected);

        var decision = SafeMigrationDecisionPlanner.Plan(index.Kind, analysis.ObservedState,
            operations[^1].Policy, analysis.RepairCapability);

        // Assert
        Assert.Equal(expected, decision.Action);
        Assert.Equal(expected == SafeMigrationAction.Apply
            ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported, analysis.ObservedState);
    }

    private sealed class Source(
        ExpectedColumnDefinition[] unchanged,
        ExpectedColumnDefinition? changed
    )
        : ISafeMigrationProjectedColumnSource, ISafeMigrationProjectedTableSource
    {
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string name,
            [NotNullWhen(true)] out ExpectedColumnDefinition? column
        )
        {
            column = changed?.Name == name ? changed : unchanged.FirstOrDefault(value => value.Name == name);

            return column is not null;
        }

        public bool TryGetProjectedTableState(
            string table,
            string? schema,
            out SafeMigrationProjectedTableState state
        )
        {
            state = new SafeMigrationProjectedTableState(false, false, false, false);

            return true;
        }
    }
}
