namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies independently captured SQL Server column/index destination identities.</summary>
public sealed class SqlServerMemberRenameEvidenceTests
{
    /// <summary>Destination predicates expose native bit metadata rather than integer CASE values.</summary>
    /// <param name="index">Whether the destination belongs to an index rather than a column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationExpressionUsesNativeBit(bool index)
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        SafeMigrationIntent intent = index
            ? new RenameIndexIntent("source", "items", "target", "MemberScope")
            : new RenameColumnIntent("source", "items", "target", "MemberScope");

        // Act
        var expression = catalog.BuildMemberRenameDestinationExpression(intent);

        // Assert
        Assert.StartsWith("CAST(CASE WHEN ", expression, StringComparison.Ordinal);
        Assert.EndsWith(" THEN 1 ELSE 0 END AS bit)", expression, StringComparison.Ordinal);
        Assert.Contains(index ? "sys.indexes" : "sys.columns", expression, StringComparison.Ordinal);
    }

    /// <summary>Index support uses the same bit type as nullable column-source UNION branches.</summary>
    [Fact]
    public void IndexSourceExpressionUsesNativeBit()
    {
        // Arrange
        using var context = Context();
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var intent = new RenameIndexIntent("source", "items", "target", "MemberScope");

        // Act
        var expression = catalog.BuildIndexRenameSourceSupportedExpression(intent);

        // Assert
        Assert.StartsWith("CAST(CASE WHEN ", expression, StringComparison.Ordinal);
        Assert.EndsWith(" THEN 1 ELSE 0 END AS bit)", expression, StringComparison.Ordinal);
        Assert.Contains("is_primary_key = 0", expression, StringComparison.Ordinal);
        Assert.Contains("is_unique_constraint = 0", expression, StringComparison.Ordinal);
    }

    /// <summary>The transport double cannot certify uncast integer predicates as Boolean evidence.</summary>
    /// <param name="native">Whether the transport supports native batching.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntypedSqlCannotProduceBooleanMockEvidence(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(4);
        var analyses = Analyses(4);
        await using var connection = new CatalogStatementTestConnection(native, (sql, _) => Results(
            sql.Replace("CAST(CASE WHEN ", "CASE WHEN ", StringComparison.Ordinal)
                .Replace(" END AS bit)", " END", StringComparison.Ordinal)));

        // Act
        var exception = await Record.ExceptionAsync(() =>
            analyzer.PopulateMemberRenameTargetEvidenceAsync(connection, null, operations, analyses,
                71, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.All(analyses, result => Assert.Null(result.RenameTargetExists));
        Assert.All(analyses, result => Assert.Null(result.RenameSourceSupported));
    }

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

    /// <summary>Models the explicit top-level SQL scalar types instead of inventing Boolean results.</summary>
    /// <param name="sql">The bounded member-probe statement, including its ordering trailer.</param>
    /// <returns>Controlled values with the declared bit or integer CLR types.</returns>
    private static System.Data.DataTable Results(string sql)
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ordinal", typeof(int));
        table.Columns.Add("exists", typeof(object));
        table.Columns.Add("source", typeof(object));
        var selections = sql[..^SafeMigrationCatalogQueryLimits.Trailer.Length]
            .Split(SafeMigrationCatalogQueryLimits.Separator, StringSplitOptions.None);

        foreach (var selection in selections)
        {
            var match = System.Text.RegularExpressions.Regex.Match(selection, @"^SELECT (\d+),");
            var ordinal = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var target = ordinal % 2 != 0;
            object targetValue = selection.StartsWith($"SELECT {ordinal}, CAST(CASE WHEN ", StringComparison.Ordinal)
                ? target : target ? 1 : 0;

            // WHY: The original double returned bool even for uncast CASE 0/1,
            // hiding the native SqlClient int result that failed every live rename.
            object sourceValue = ordinal % 2 == 0 ? DBNull.Value
                : selection.EndsWith(" END AS bit)", StringComparison.Ordinal) ? false : 0;

            table.Rows.Add(ordinal, targetValue, sourceValue);
        }

        return table;
    }
}
