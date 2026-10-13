namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Pins source-independent member destinations to complete bounded native catalog evidence.</summary>
public sealed class MySqlMemberRenameEvidenceTests
{
    /// <summary>Mixed destinations retain state and distinguish occupancy independently of the source.</summary>
    /// <param name="native">Whether the transport supports native batching.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSourcesRetainIndependentDestinationOccupancy(bool native)
    {
        // Arrange
        var operations = Operations(4);
        var analyses = Analyses(4);
        bool?[] expectedTargets = [false, true, false, true];
        bool?[] expectedSources = [null, false, null, false];
        bool?[] expectedTargetSupport = [null, true, null, true];

        await using var connection = new CatalogStatementTestConnection(native, (sql, _) => Results(sql));

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
            analyses, "catalog-test", 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Equal(expectedTargets, analyses.Select(static result =>
            result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Equal(SafeMigrationObservedState.Missing, result.ObservedState));
        Assert.Equal(expectedSources, analyses.Select(static result =>
            result.RenameSourceSupported));
        Assert.Equal(expectedTargetSupport, analyses.Select(static result =>
            result.RenameTargetSupported));
        var submission = Assert.Single(connection.Submissions);
        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("INFORMATION_SCHEMA.STATISTICS", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("@@lower_case_table_names", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("CAST(c.TABLE_NAME AS BINARY)", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("CAST(LOWER(s.TABLE_NAME) AS BINARY)", submission.Sql, StringComparison.Ordinal);
        Assert.Equal(71, submission.Timeout);
        Assert.DoesNotContain("FROM `", submission.Sql, StringComparison.Ordinal);
    }

    /// <summary>Large captures preserve statement and batch bounds without publishing unowned ordinals.</summary>
    /// <param name="native">Whether the transport supports native batching.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeCaptureRetainsBoundedTransport(bool native)
    {
        // Arrange
        var operations = Operations(290);
        var analyses = Analyses(290);
        int[] expectedBatches = native ? [8, 2] : [];

        await using var connection = new CatalogStatementTestConnection(native, (sql, _) => Results(sql));

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
            analyses, "catalog-test", 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Equal(10, connection.Submissions.Count);
        Assert.Equal(expectedBatches, connection.BatchCounts);
        Assert.All(connection.Submissions, submission => Assert.InRange(Results(submission.Sql).Rows.Count, 1, 32));
        Assert.All(analyses, result => Assert.NotNull(result.RenameTargetExists));
    }

    /// <summary>Malformed cardinality, ordinal or boolean facts cannot publish earlier valid destinations.</summary>
    /// <param name="fault">The adversarial result contract violation.</param>
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
        var operations = Operations(4);
        var analyses = Analyses(4);
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) =>
        {
            var table = Results(sql);
            switch (fault)
            {
                case "ordinal": table.Rows[1][0] = 999; break;
                case "missing": table.Rows.RemoveAt(1); break;
                case "duplicate": table.Rows.Add(0, 0L, DBNull.Value); break;
                case "boolean": table.Rows[1][1] = 2L; break;
                case "null": table.Rows[1][1] = DBNull.Value; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
                case "source-kind": table.Rows[1][2] = 2L; break;
                case "source-null": table.Rows[1][2] = DBNull.Value; break;
                case "column-source": table.Rows[0][2] = 1L; break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
                analyses, "catalog-test", 4 * 1024 * 1024, 71, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameSourceSupported));
        Assert.All(analyses, result => Assert.Null(result.RenameTargetSupported));
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>Rejected identities and ordinary streams add no destination catalog roundtrip.</summary>
    [Fact]
    public async Task OrdinaryOrRejectedOperationsDoNotSubmitProbes()
    {
        // Arrange
        SafeMigrationOperation[] operations =
        [
            new(new RenameColumnIntent("source", "items", "target", "foreign"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(new RenameIndexIntent("source_index", "items", "target_index"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(new DropColumnIntent("unrelated", "items"), SafeMigrationPolicy.ThrowIfDifferent),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            Live(SafeMigrationObservedState.Unsupported), Live(SafeMigrationObservedState.PrerequisiteMissing),
            Live(SafeMigrationObservedState.Matching),
        ];

        await using var connection = new CatalogStatementTestConnection(true, (sql, _) => Results(sql));

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
            analyses, "catalog-test", 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameTargetSupported));
    }

    /// <summary>Cancellation releases transport without publishing partially read facts.</summary>
    [Fact]
    public async Task CancelledCapturePublishesNothing()
    {
        // Arrange
        var operations = Operations(4);
        var analyses = Analyses(4);
        using var cancellation = new CancellationTokenSource();
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) => Results(sql))
        {
            BeforeRead = () =>
            {
                if (++reads == 2)
                {
                    cancellation.Cancel();
                }
            },
        };

        // Act
        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
                analyses, "catalog-test", 4 * 1024 * 1024, 71, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameTargetSupported));
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Reserved destination roles are immutable and require no additional catalog submission.</summary>
    /// <param name="target">The authored index destination.</param>
    /// <param name="supported">Whether the authored destination is independently supported.</param>
    [Theory]
    [InlineData("PRIMARY", false)]
    [InlineData("primary", false)]
    [InlineData("PrImArY", false)]
    [InlineData("destination_index", true)]
    public async Task AuthoredDestinationRolePublishesWithCompleteCapture(string target, bool supported)
    {
        // Arrange
        SafeMigrationOperation[] operations =
        [
            new(new RenameColumnIntent("source_value", "items", "target_value"),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(new RenameIndexIntent("source_index", "items", target),
                SafeMigrationPolicy.ThrowIfDifferent),
        ];

        var analyses = Analyses(2);
        await using var connection = new CatalogStatementTestConnection(true, (sql, _) => Results(sql));

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadMemberRenameTargetEvidenceAsync(connection, operations,
            analyses, "catalog-test", 4 * 1024 * 1024, 71, CancellationToken.None);

        // Assert
        Assert.Null(analyses[0].RenameTargetSupported);
        Assert.Equal(supported, analyses[1].RenameTargetSupported);
        Assert.Equal(SafeMigrationObservedState.Missing, analyses[1].ObservedState);
        Assert.Single(connection.Submissions);
    }

    private static SafeMigrationOperation[] Operations(int count) => Enumerable.Range(0, count)
        .Select(static ordinal => new SafeMigrationOperation(ordinal % 2 == 0
            ? new RenameColumnIntent("source_" + ordinal, "items", "target_" + ordinal)
            : new RenameIndexIntent("source_" + ordinal, "items", "target_" + ordinal),
            SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

    private static SafeMigrationProviderAnalysis[] Analyses(int count) => Enumerable.Range(0, count)
        .Select(static _ => Live(SafeMigrationObservedState.Missing)).ToArray();

    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state) =>
        new(state, SafeMigrationRepairCapability.None, false, "test");

    private static System.Data.DataTable Results(string sql)
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ordinal", typeof(int));
        table.Columns.Add("exists", typeof(long));
        table.Columns.Add("source", typeof(object));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            sql, @"SELECT (\d+),"))
        {
            var ordinal = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            table.Rows.Add(ordinal, (long)(ordinal % 2), ordinal % 2 != 0 ? 0L : DBNull.Value);
        }

        return table;
    }
}
