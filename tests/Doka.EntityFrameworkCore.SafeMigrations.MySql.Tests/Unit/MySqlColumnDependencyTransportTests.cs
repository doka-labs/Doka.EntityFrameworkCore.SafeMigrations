namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies bounded complete dependency transport without application-row probes.</summary>
public sealed class MySqlColumnDependencyTransportTests
{
    /// <summary>Four mixed repairs share one statement and preserve complete dependency facts.</summary>
    /// <param name="native">Whether native batching or sequential fallback carries the statement.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedRequestsShareOneCompleteStatement(bool native)
    {
        // Arrange
        var requests = Requests(4);
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, _) => Results(sql, requests));

        Dictionary<int, (bool Primary, IReadOnlySet<string>? Names)> facts = [];

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(
            connection, requests, 4 * 1024 * 1024, 71,
            (ordinal, primary, names) => facts.Add(ordinal, (primary, names)), CancellationToken.None);

        // Assert
        var submission = Assert.Single(connection.Submissions);
        Assert.StartsWith(MySqlSafeMigrationProviderAnalyzer.ColumnDependencyQueryMarker, submission.Sql,
            StringComparison.Ordinal);
        Assert.Contains("rc.DELETE_RULE = 'SET NULL' OR rc.UPDATE_RULE = 'SET NULL'", submission.Sql,
            StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT_TYPE = 'PRIMARY KEY'", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON_ARRAYAGG", submission.Sql, StringComparison.Ordinal);
        Assert.Equal(4, facts.Count);
        Assert.True(facts[1].Primary);
        Assert.False(facts[3].Primary);
        Assert.Null(facts[2].Names);
        var names = Assert.IsAssignableFrom<IReadOnlySet<string>>(facts[0].Names);
        Assert.True(names.SetEquals(["fk_delete_0", "fk_update_0"]));
        Assert.True(names.Contains("FK_DELETE_0"));
        Assert.IsAssignableFrom<System.Collections.Frozen.FrozenSet<string>>(names);
        Assert.Equal(71, submission.Timeout);
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Published FK facts cannot change through a mutable-interface cast by another consumer.</summary>
    [Fact]
    public async Task PublishedForeignKeyFactsRejectMutation()
    {
        // Arrange
        var requests = Requests(1);
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, _) => Results(sql, requests));

        IReadOnlySet<string>? captured = null;
        await MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(connection, requests,
            4 * 1024 * 1024, 71, (_, _, names) => captured = names, CancellationToken.None);
        var names = Assert.IsAssignableFrom<ISet<string>>(captured);
        var original = names.ToArray();

        // Act
        var exception = Record.Exception(() => names.Add("not_catalogued"));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.True(Assert.IsAssignableFrom<ICollection<string>>(names).IsReadOnly);
        Assert.True(names.SetEquals(original));
    }

    /// <summary>Large captures preserve 32-request statement and eight-statement transport limits.</summary>
    /// <param name="native">Whether native batching or sequential fallback carries the capture.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeCapturePreservesBoundedStatements(bool native)
    {
        // Arrange
        var requests = Requests(290);
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, _) => Results(sql, requests));

        HashSet<int> observed = [];
        int[] expectedBatches = native ? [8, 2] : [];

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(
            connection, requests, 4 * 1024 * 1024, 71,
            (ordinal, _, _) => Assert.True(observed.Add(ordinal)), CancellationToken.None);

        // Assert
        Assert.Equal(290, observed.Count);
        Assert.Equal(10, connection.Submissions.Count);
        Assert.Equal(expectedBatches, connection.BatchCounts);
        Assert.All(connection.Submissions, submission =>
        {
            Assert.InRange(Ordinals(submission.Sql).Length, 1, 32);
            Assert.InRange(submission.Parameters.Count, 1, 33);
            Assert.Equal(71, submission.Timeout);
        });
        Assert.Equal(native ? 2 : 10, connection.ReadersDisposed);
        Assert.Equal(native ? 2 : 0, connection.BatchesDisposed);
    }

    /// <summary>Payload-qualified splitting retains every request and rolls back unused parameters.</summary>
    [Fact]
    public async Task SmallPacketBudgetSplitsWithoutLosingRequests()
    {
        // Arrange
        var requests = Requests(40, new string('x', 100));
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, _) => Results(sql, requests));

        HashSet<int> observed = [];

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(
            connection, requests, 6500, 71,
            (ordinal, _, _) => Assert.True(observed.Add(ordinal)), CancellationToken.None);

        // Assert
        Assert.Equal(40, observed.Count);
        Assert.True(connection.Submissions.Count > 2);
        Assert.All(connection.Submissions, submission => Assert.InRange(
            Encoding.UTF8.GetByteCount(submission.Sql)
                + submission.Parameters.Sum(parameter => Encoding.UTF8.GetByteCount((string)parameter.Value!) + 32),
            1, 6500));
    }

    /// <summary>Malformed rows never publish even the earlier valid facts of the same statement.</summary>
    /// <param name="fault">The injected ordinal, sentinel, role, boolean, name or shape failure.</param>
    [Theory]
    [InlineData("missing-sentinel")]
    [InlineData("duplicate-sentinel")]
    [InlineData("missing-primary")]
    [InlineData("duplicate-primary")]
    [InlineData("bad-ordinal")]
    [InlineData("bad-role")]
    [InlineData("bad-target")]
    [InlineData("truthy-primary")]
    [InlineData("string-primary")]
    [InlineData("decimal-primary")]
    [InlineData("empty-name")]
    [InlineData("duplicate-name")]
    [InlineData("wrong-width")]
    public async Task MalformedEvidencePublishesNoStatementFacts(string fault)
    {
        // Arrange
        var requests = Requests(4);
        await using var connection = new CatalogStatementTestConnection(true,
            (sql, _) => Results(sql, requests, fault));

        var observed = 0;

        // Act
        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(connection, requests,
                4 * 1024 * 1024, 71, (_, _, _) => observed++, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(0, observed);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>Cancellation inside the reader releases resources without publishing incomplete facts.</summary>
    /// <param name="native">Whether native batching or sequential fallback carries the capture.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPublishesNoIncompleteFacts(bool native)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var requests = Requests(4);
        var reads = 0;
        await using var connection = new CatalogStatementTestConnection(native,
            (sql, _) => Results(sql, requests))
        {
            BeforeRead = () =>
            {
                if (++reads == 3)
                {
                    cancellation.Cancel();
                }
            },
        };

        var observed = 0;

        // Act
        var exception = await Record.ExceptionAsync(() =>
            MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(connection, requests,
                4 * 1024 * 1024, 71, (_, _, _) => observed++, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(0, observed);
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(native ? 1 : 0, connection.BatchesDisposed);
    }

    /// <summary>Physical dedup uses verified schema aliases and CI members without merging raw table owners.</summary>
    [Fact]
    public void RepeatedTargetsRetainReferenceBoundConsumers()
    {
        // Arrange
        var original = Operation("items", "owner", nullable: false);
        var alias = Operation("items", "OWNER", nullable: false, schema: "selected");
        SafeMigrationOperation[] operations =
        [
            original, alias, original, alias,
            Operation("items", "owner", nullable: true),
            Operation("ITEMS", "owner", nullable: false),
        ];

        var analyses = operations.Select(static _ => Safe()).ToArray();

        // Act
        var capture = MySqlSafeMigrationProviderAnalyzer.CreateColumnDependencyRequests(
            operations, analyses, "selected");

        // Assert
        Assert.Equal(3, capture.Requests.Count);
        Assert.Same(original, capture.Requests[0].Operation);
        var consumers = Assert.Single(Assert.IsAssignableFrom<IReadOnlyDictionary<int,
            HashSet<SafeMigrationOperation>>>(capture.Aliases));
        Assert.Equal(0, consumers.Key);
        Assert.Same(alias, Assert.Single(consumers.Value));
        Assert.True(capture.Requests[1].Nullable);
        Assert.Equal("ITEMS", capture.Requests[2].Table);
    }

    /// <summary>Matching, unsupported, unsafe and foreign requests never enter dependency transport.</summary>
    [Fact]
    public async Task IneligibleSourcesSubmitNoCommands()
    {
        // Arrange
        SafeMigrationOperation[] operations =
        [
            Operation("items", "a", false), Operation("items", "b", false), Operation("items", "c", false),
            Operation("items", "d", false, "foreign"),
        ];

        SafeMigrationProviderAnalysis[] analyses =
        [
            new(SafeMigrationObservedState.Matching, SafeMigrationRepairCapability.None, true, "matching"),
            new(SafeMigrationObservedState.Unsupported, SafeMigrationRepairCapability.None, false, "unsupported"),
            new(SafeMigrationObservedState.Different, SafeMigrationRepairCapability.None, false, "different"),
            Safe(),
        ];

        var capture = MySqlSafeMigrationProviderAnalyzer.CreateColumnDependencyRequests(
            operations, analyses, "selected");
        await using var connection = new CatalogStatementTestConnection(true,
            static (_, _) => throw new InvalidOperationException("No dependency query was expected."));

        // Act
        await MySqlSafeMigrationProviderAnalyzer.ReadColumnDependenciesAsync(connection, capture.Requests,
            4 * 1024 * 1024, 71, static (_, _, _) => throw new InvalidOperationException(), CancellationToken.None);

        // Assert
        Assert.Empty(capture.Requests);
        Assert.Empty(connection.Submissions);
    }

    /// <summary>Creates mixed native-role requests without opening a real server connection.</summary>
    /// <param name="count">The number of reference-distinct physical requests.</param>
    /// <param name="table">The untouched authored owner.</param>
    /// <returns>The mixed PRIMARY/SET NULL source requests.</returns>
    private static MySqlColumnDependencyRequest[] Requests(
        int count,
        string table = "items"
    ) =>
        Enumerable.Range(0, count).Select(index => new MySqlColumnDependencyRequest(
            Operation(table, "column_" + index.ToString(CultureInfo.InvariantCulture), index % 2 == 1), table,
            "column_" + index.ToString(CultureInfo.InvariantCulture), index % 2 == 1, index)).ToArray();

    /// <summary>Creates a reference-distinct repair envelope for a physical target.</summary>
    /// <param name="table">The untouched authored owner.</param>
    /// <param name="column">The requested column.</param>
    /// <param name="nullable">The requested target nullability.</param>
    /// <param name="schema">The optional current-database qualifier.</param>
    /// <returns>The immutable repair envelope.</returns>
    private static SafeMigrationOperation Operation(
        string table,
        string column,
        bool nullable,
        string? schema = null
    ) => new(new EnsureColumnIntent(table,
            new ExpectedColumnDefinition(column, typeof(int), nullable, "int"), schema),
            SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Supplies a source-proven safe repair independently of dependency metadata.</summary>
    /// <returns>The independently qualified source result.</returns>
    private static SafeMigrationProviderAnalysis Safe() => new(SafeMigrationObservedState.Different,
        SafeMigrationRepairCapability.Safe, false, "safe");

    /// <summary>Extracts only the tagged request-source ordinals, independent of row-result contents.</summary>
    /// <param name="sql">The bounded tagged statement.</param>
    /// <returns>The distinct sorted statement-local request identities.</returns>
    private static int[] Ordinals(
        string sql
    ) => System.Text.RegularExpressions.Regex.Matches(sql,
            @"SELECT (\d+) AS request_ordinal,").Cast<System.Text.RegularExpressions.Match>()
        .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();

    /// <summary>Returns explicit statement-local dependency rows and adversarial protocol variants.</summary>
    /// <param name="sql">The tagged request statement.</param>
    /// <param name="requests">The immutable full input stream.</param>
    /// <param name="fault">The optional adversarial evidence variant.</param>
    /// <returns>The explicit statement-local metadata table.</returns>
    private static DataTable Results(
        string sql,
        MySqlColumnDependencyRequest[] requests,
        string? fault = null
    )
    {
        var rows = new DataTable();
        rows.Columns.Add("ordinal", typeof(int));
        rows.Columns.Add("kind", typeof(int));
        rows.Columns.Add("nullable", typeof(object));
        rows.Columns.Add("primary", typeof(object));
        rows.Columns.Add("name", typeof(string));
        foreach (var ordinal in Ordinals(sql))
        {
            var nullable = requests[ordinal].Nullable;
            rows.Rows.Add(ordinal, 0, nullable ? 1L : 0L, DBNull.Value, DBNull.Value);
            if (nullable)
            {
                rows.Rows.Add(ordinal, 1, 1L, ordinal % 4 == 1 ? 1L : 0L, DBNull.Value);
            }
            else if (ordinal % 4 == 0)
            {
                rows.Rows.Add(ordinal, 2, 0L, DBNull.Value,
                    "fk_delete_" + ordinal.ToString(CultureInfo.InvariantCulture));
                rows.Rows.Add(ordinal, 2, 0L, DBNull.Value,
                    "fk_update_" + ordinal.ToString(CultureInfo.InvariantCulture));
            }
        }

        switch (fault)
        {
            case "missing-sentinel": rows.Rows.RemoveAt(0); break;
            case "duplicate-sentinel":
                var sentinel = rows.NewRow();
                sentinel.ItemArray = rows.Rows[0].ItemArray;
                rows.Rows.InsertAt(sentinel, 1);
                break;
            case "missing-primary": rows.Rows.RemoveAt(4); break;
            case "duplicate-primary":
                var primary = rows.NewRow();
                primary.ItemArray = rows.Rows[4].ItemArray;
                rows.Rows.InsertAt(primary, 5);
                break;
            case "bad-ordinal": rows.Rows[0][0] = 99; break;
            case "bad-role": rows.Rows[0][1] = 9; break;
            case "bad-target": rows.Rows[0][2] = true; break;
            case "truthy-primary": rows.Rows[4][3] = 2L; break;
            case "string-primary": rows.Rows[4][3] = "1"; break;
            case "decimal-primary": rows.Rows[4][3] = 1m; break;
            case "empty-name": rows.Rows[1][4] = string.Empty; break;
            case "duplicate-name": rows.Rows[2][4] = "FK_DELETE_0"; break;
            case "wrong-width": rows.Columns.Add("extra", typeof(int)); break;
        }

        return rows;
    }
}
