namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Checks complete PostgreSQL destination capture for source-independent member renames.</summary>
public sealed class PostgreSqlMemberRenameEvidenceTests
{
    /// <summary>Columns use exact owners while indexes preserve PostgreSQL's schema-global namespace.</summary>
    /// <param name="native">Whether the transport supports native batching.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourcesRetainIndependentDestinationOccupancy(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(4);
        var analyses = Analyses(4);
        bool?[] expectedTargets = [false, true, false, true];
        bool?[] expectedSources = [null, false, null, false];

        await using var connection = new CatalogStatementTestConnection(native, (sql, _) => Results(sql));

        // Act
        await analyzer.PopulateRenameTargetEvidenceAsync(connection, operations, analyses, 71, CancellationToken.None);

        // Assert
        Assert.Equal(expectedTargets, analyses.Select(static result =>
            result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState));
        Assert.Equal(expectedSources, analyses.Select(static result =>
            result.RenameSourceSupported));
        Assert.All(analyses, result => Assert.Null(result.RenameIntermediateTargetExists));
        var submission = Assert.Single(connection.Submissions);
        Assert.Contains("pg_catalog.pg_attribute", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.pg_class", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.pg_namespace", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("lower(", submission.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(71, submission.Timeout);
    }

    /// <summary>Native grouping remains limited to 32 destination rows and eight statements per dispatch.</summary>
    /// <param name="native">Whether the transport supports native batching.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeCaptureRetainsBoundedTransport(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(290);
        var analyses = Analyses(290);
        int[] expectedBatches = native ? [8, 2] : [];

        await using var connection = new CatalogStatementTestConnection(native, (sql, _) => Results(sql));

        // Act
        await analyzer.PopulateRenameTargetEvidenceAsync(connection, operations, analyses, 71, CancellationToken.None);

        // Assert
        Assert.Equal(10, connection.Submissions.Count);
        Assert.Equal(expectedBatches, connection.BatchCounts);
        Assert.All(connection.Submissions, submission => Assert.InRange(Results(submission.Sql).Rows.Count, 1, 32));
        Assert.All(analyses, result => Assert.NotNull(result.RenameTargetExists));
    }

    /// <summary>Missing, duplicate or malformed facts cannot certify a partial destination set.</summary>
    /// <param name="fault">The adversarial result contract violation.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("boolean")]
    [InlineData("intermediate")]
    [InlineData("width")]
    [InlineData("source-kind")]
    [InlineData("source-null")]
    [InlineData("column-source")]
    public async Task InvalidEvidencePublishesNothing(string fault)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(4);
        var analyses = Analyses(4);
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) =>
        {
            var table = Results(sql);
            switch (fault)
            {
                case "ordinal": table.Rows[1][0] = 999; break;
                case "missing": table.Rows.RemoveAt(1); break;
                case "duplicate": table.Rows.Add(0, false, DBNull.Value, DBNull.Value); break;
                case "boolean": table.Rows[1][1] = 1; break;
                case "intermediate": table.Rows[1][2] = true; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
                case "source-kind": table.Rows[1][3] = 1; break;
                case "source-null": table.Rows[1][3] = DBNull.Value; break;
                case "column-source": table.Rows[0][3] = true; break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() =>
            analyzer.PopulateRenameTargetEvidenceAsync(connection, operations, analyses, 71, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameSourceSupported));
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Rejected operations and streams without renames add no destination query.</summary>
    [Fact]
    public async Task OrdinaryOrRejectedOperationsDoNotSubmitProbes()
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        SafeMigrationOperation[] operations =
        [
            new(new RenameColumnIntent("source", "items", "target"), SafeMigrationPolicy.ThrowIfDifferent),
            new(new DropColumnIntent("unrelated", "items"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            Live(SafeMigrationObservedState.Unsupported), Live(SafeMigrationObservedState.Matching),
        ];

        await using var connection = new CatalogStatementTestConnection(true, (sql, _) => Results(sql));

        // Act
        await analyzer.PopulateRenameTargetEvidenceAsync(connection, operations, analyses, 71, CancellationToken.None);

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
    }

    private static SafeMigrationDbContext Context() =>
        new("Host=localhost;Database=rename_evidence;Username=unused;Password=unused");

    private static PostgreSqlSafeMigrationProviderAnalyzer Analyzer(DbContext context) =>
        Assert.IsType<PostgreSqlSafeMigrationProviderAnalyzer>(context.GetService<ISafeMigrationProviderAnalyzer>());

    private static SafeMigrationOperation[] Operations(int count) => Enumerable.Range(0, count)
        .Select(static ordinal => new SafeMigrationOperation(ordinal % 2 == 0
            ? new RenameColumnIntent("source_" + ordinal, "MixedOwner", "target_" + ordinal, "MemberScope")
            : new RenameIndexIntent("source_" + ordinal, "MixedOwner", "target_" + ordinal, "MemberScope"),
            SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

    private static SafeMigrationProviderAnalysis[] Analyses(int count) => Enumerable.Range(0, count)
        .Select(static _ => Live(SafeMigrationObservedState.Missing)).ToArray();

    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state) =>
        new(state, SafeMigrationRepairCapability.None, false, "test");

    private static System.Data.DataTable Results(string sql)
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ordinal", typeof(int));
        table.Columns.Add("exists", typeof(object));
        table.Columns.Add("intermediate", typeof(object));
        table.Columns.Add("source", typeof(object));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            sql, @"SELECT (\d+),"))
        {
            var ordinal = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            table.Rows.Add(ordinal, ordinal % 2 != 0, DBNull.Value, ordinal % 2 != 0 ? false : DBNull.Value);
        }

        return table;
    }
}
