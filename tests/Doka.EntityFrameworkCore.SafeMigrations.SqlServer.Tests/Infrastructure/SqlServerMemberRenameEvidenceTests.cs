namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies independently captured SQL Server column/index destination identities.</summary>
public sealed class SqlServerMemberRenameEvidenceTests
{
    /// <summary>Missing-source state retains owner-local destination facts and the caller transaction.</summary>
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
        await using var transaction = connection.BeginTransaction();

        // Act
        await analyzer.PopulateMemberRenameTargetEvidenceAsync(connection, transaction, operations, analyses,
            71, CancellationToken.None);

        // Assert
        Assert.Equal(expectedTargets, analyses.Select(static result =>
            result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState));
        Assert.Equal(expectedSources, analyses.Select(static result =>
            result.RenameSourceSupported));
        var submission = Assert.Single(connection.Submissions);
        Assert.Contains("sys.columns", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("sys.indexes", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("OBJECT_ID(", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE Latin1", submission.Sql, StringComparison.Ordinal);
        Assert.Equal(71, submission.Timeout);
        Assert.Same(transaction, submission.Transaction);
        Assert.Equal(0, connection.TransactionsDisposed);
    }

    /// <summary>Large captures retain existing 32-row statements and eight-statement batches.</summary>
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
        await analyzer.PopulateMemberRenameTargetEvidenceAsync(connection, null, operations, analyses,
            71, CancellationToken.None);

        // Assert
        Assert.Equal(10, connection.Submissions.Count);
        Assert.Equal(expectedBatches, connection.BatchCounts);
        Assert.All(connection.Submissions, submission => Assert.InRange(Results(submission.Sql).Rows.Count, 1, 32));
        Assert.All(analyses, result => Assert.NotNull(result.RenameTargetExists));
    }

    /// <summary>Malformed result cardinality, ownership or boolean type never publishes partial occupancy.</summary>
    /// <param name="fault">The injected destination protocol fault.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("boolean")]
    [InlineData("null")]
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
                case "duplicate": table.Rows.Add(0, false, DBNull.Value); break;
                case "boolean": table.Rows[1][1] = 1; break;
                case "null": table.Rows[1][1] = DBNull.Value; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
                case "source-kind": table.Rows[1][2] = 1; break;
                case "source-null": table.Rows[1][2] = DBNull.Value; break;
                case "column-source": table.Rows[0][2] = true; break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() =>
            analyzer.PopulateMemberRenameTargetEvidenceAsync(connection, null, operations, analyses,
                71, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameSourceSupported));
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Rejected identifier contracts and ordinary operations do not add catalog roundtrips.</summary>
    [Fact]
    public async Task OrdinaryOrRejectedOperationsDoNotSubmitProbes()
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        SafeMigrationOperation[] operations =
        [
            new(new RenameIndexIntent("source", "items", "target"), SafeMigrationPolicy.ThrowIfDifferent),
            new(new DropColumnIntent("unrelated", "items"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            Live(SafeMigrationObservedState.Unsupported), Live(SafeMigrationObservedState.Matching),
        ];

        await using var connection = new CatalogStatementTestConnection(true, (sql, _) => Results(sql));

        // Act
        await analyzer.PopulateMemberRenameTargetEvidenceAsync(connection, null, operations, analyses,
            71, CancellationToken.None);

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
    }

    private static SafeMigrationDbContext Context() =>
        new("Server=localhost;Database=catalog;Integrated Security=true;");

    private static SqlServerSafeMigrationProviderAnalyzer Analyzer(DbContext context) =>
        Assert.IsType<SqlServerSafeMigrationProviderAnalyzer>(context.GetService<ISafeMigrationProviderAnalyzer>());

    private static SafeMigrationOperation[] Operations(int count) => Enumerable.Range(0, count)
        .Select(static ordinal => new SafeMigrationOperation(ordinal % 2 == 0
            ? new RenameColumnIntent("source_" + ordinal, "items", "target_" + ordinal, "MemberScope")
            : new RenameIndexIntent("source_" + ordinal, "items", "target_" + ordinal, "MemberScope"),
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
        table.Columns.Add("source", typeof(object));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            sql, @"SELECT (\d+),"))
        {
            var ordinal = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            table.Rows.Add(ordinal, ordinal % 2 != 0, ordinal % 2 != 0 ? false : DBNull.Value);
        }

        return table;
    }
}
