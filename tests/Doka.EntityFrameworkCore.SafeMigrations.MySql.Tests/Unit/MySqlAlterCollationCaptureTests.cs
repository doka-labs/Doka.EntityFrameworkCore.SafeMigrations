namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies demand-driven collation capture without trusting name-derived encodings.</summary>
public sealed class MySqlAlterCollationCaptureTests
{
    /// <summary>Ordinary column operations do not introduce charset queries.</summary>
    [Fact]
    public async Task NoBackfillTransitionPerformsNoProbe()
    {
        await using var connection = new CatalogStatementTestConnection(true, Results);
        SafeMigrationOperation[] operations =
        [
            new(
                new EnsureColumnIntent("records", Column("longtext", false, null)),
                SafeMigrationPolicy.ThrowIfDifferent),
            new(new AlterColumnIntent("records", Column("varchar(80)", false, "latin1_bin"),
                Column("varchar(80)", true, "latin1_bin")), SafeMigrationPolicy.RepairIfSafe),
        ];

        var result = await Capture(connection, operations);

        Assert.Empty(result);
        Assert.Empty(connection.Submissions);
    }

    /// <summary>Repeated references share one bound catalog query and retain its exact encoding.</summary>
    [Fact]
    public async Task ExplicitCollationIsResolvedOnce()
    {
        await using var connection = new CatalogStatementTestConnection(true, Results);
        var operations = Enumerable.Repeat(Operation("latin1_bin"), 1000).ToArray();

        var result = await Capture(connection, operations);

        Assert.Equal("latin1", Assert.Single(result).Value);
        var statement = Assert.Single(connection.Submissions);
        Assert.Single(statement.Parameters);
        Assert.Equal(71, statement.Timeout);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>An absent catalog collation remains unresolved instead of using a guessed prefix.</summary>
    [Fact]
    public async Task UnknownCollationDoesNotProduceEvidence()
    {
        await using var connection = new CatalogStatementTestConnection(true, (_, _) => EmptyResults());

        var result = await Capture(connection, [Operation("unknown_bin")]);

        Assert.Empty(result);
        Assert.Single(connection.Submissions);
    }

    /// <summary>Foreign or duplicate result rows cannot enter a shared proof map.</summary>
    /// <param name="duplicate">Whether to inject a duplicate rather than a foreign identity.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedCollationOwnershipIsRejected(bool duplicate)
    {
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var result = Results(sql, parameters);
            result.Rows.Add(duplicate ? "latin1_bin" : "unowned_bin", "latin1");

            return result;
        });

        var exception = await Record.ExceptionAsync(() => Capture(connection, [Operation("latin1_bin")]));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(1, connection.ReadersDisposed);
    }

    private static Task<IReadOnlyDictionary<string, string>> Capture(
        DbConnection connection,
        IReadOnlyList<SafeMigrationOperation> operations
    ) => MySqlSafeMigrationProviderAnalyzer.ReadProjectedAlterCollationCharacterSetsAsync(
        connection, operations, 4 * 1024 * 1024, 71, CancellationToken.None);

    private static SafeMigrationOperation Operation(string collation) => new(
        new AlterColumnIntent("records", Column("varchar(80)", false, collation), Column("longtext", true, collation)),
        SafeMigrationPolicy.RepairIfSafe);

    private static ExpectedColumnDefinition Column(
        string type,
        bool nullable,
        string? collation
    ) => new(
        "value", typeof(string), isNullable: nullable, storeType: type,
        defaultValue: nullable ? SafeMigrationDefaultValue.None : SafeMigrationDefaultValue.Literal("seed"),
        collation: collation is null ? null : new SafeMigrationCollationIdentifier(collation));

    private static DataTable Results(
        string _,
        IReadOnlyList<DbParameter> parameters
    )
    {
        var result = EmptyResults();
        foreach (var parameter in parameters)
        {
            result.Rows.Add(parameter.Value, "latin1");
        }

        return result;
    }

    private static DataTable EmptyResults()
    {
        var result = new DataTable { Locale = CultureInfo.InvariantCulture };
        result.Columns.Add("collation", typeof(string));
        result.Columns.Add("charset", typeof(string));

        return result;
    }
}
