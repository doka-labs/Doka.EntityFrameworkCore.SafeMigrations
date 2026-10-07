namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies streamed SQL literal escaping without full-size escaped temporary strings.</summary>
public sealed class SqlServerSqlLiteralAppendTests
{
    private const string ConnectionString = "Server=127.0.0.1,1433;Database=literal_append;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>Appends the exact reference escaping while preserving existing prefix and suffix text.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SELECT 1;\n")]
    [InlineData("'")]
    [InlineData("''''''''")]
    [InlineData("EXEC sys.sp_executesql N'SELECT N''O''''Brien'';';\n")]
    [InlineData("SELECT N'\uD83D\uDE80 O''Brien \uD834\uDD1E';\n")]
    public void EscapedAppend_ExactContentMatchesOrdinalReplace(
        string? sql
    )
    {
        // Arrange
        var builder = new StringBuilder("prefix|");
        var expected = "prefix|" + (sql?.Replace("'", "''", StringComparison.Ordinal) ?? string.Empty) + "|suffix";

        // Act
        SqlServerSafeMigrationsSqlGenerator.AppendEscapedSqlLiteral(builder, sql.AsSpan());
        builder.Append("|suffix");

        // Assert
        Assert.Equal(expected, builder.ToString());
    }

    /// <summary>Preserves sliced Unicode input and dense escaping when the destination grows across chunks.</summary>
    [Theory]
    [InlineData(4, 257)]
    [InlineData(16, 1024)]
    [InlineData(1024, 4096)]
    public void EscapedAppend_SlicedSourceAndChunkedDestinationRemainExact(
        int capacity,
        int repetitions
    )
    {
        // Arrange
        var payload = string.Concat(Enumerable.Repeat("'\uD83D\uDE80x\u0301''", repetitions));
        var source = "excluded-prefix|" + payload + "|excluded-suffix";
        var builder = new StringBuilder(capacity).Append("kept-prefix|");
        var expected = "kept-prefix|" + payload.Replace("'", "''", StringComparison.Ordinal) + "|kept-suffix";

        // Act
        SqlServerSafeMigrationsSqlGenerator.AppendEscapedSqlLiteral(builder,
            source.AsSpan("excluded-prefix|".Length, payload.Length));

        builder.Append("|kept-suffix");
        var chunks = 0;
        foreach (var chunk in builder.GetChunks())
        {
            chunks += chunk.IsEmpty ? 0 : 1;
        }

        // Assert
        Assert.True(chunks > 1);
        Assert.Equal(expected, builder.ToString());
        Assert.Equal("excluded-prefix|" + payload + "|excluded-suffix", source);
    }

    /// <summary>State and postcondition scopes retain scalar escaping without an unused repair scope.</summary>
    [Fact]
    public void GeneratedManagedGuard_NestedScalarScopesMatchIndependentReference()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var intent = new EnsureModelManagedDataIntent("O'Brien", ["Id"], ["int"], ["Id", "Caption"],
            ["int", "nvarchar(80)"], new object?[,] { { 7, "O'Brien \uD83D\uDE80" } }, null, null);

        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent);
        var catalog = new SqlServerSafeMigrationCatalogSqlBuilder(
            context.GetService<IRelationalTypeMappingSource>(), context.GetService<ISqlGenerationHelper>());

        var plan = catalog.Build(operation);
        var expectedState = ScalarReference(
            plan.StateExpression, "nvarchar(32)", "@doka_state", plan.CatalogPreambleSql);

        var postconditionExpression = $"COALESCE(({plan.ExecutionPostcondition ?? plan.Postcondition}), 0)";
        var expectedPostcondition = ScalarReference(postconditionExpression, "int", "@doka_postcondition");

        // Act
        var guard = SqlServerGuardedSqlTestContract.GenerateBody(context, operation);

        // Assert
        Assert.True(plan.RequiresDelayedBinding || plan.CatalogPreambleSql is not null);
        Assert.Contains(expectedState, guard, StringComparison.Ordinal);
        Assert.DoesNotContain("@doka_value = @doka_repair_ok OUTPUT", guard, StringComparison.Ordinal);
        Assert.Contains(expectedPostcondition, guard, StringComparison.Ordinal);
        Assert.Contains("THROW 51005, N'doka_sm_postcondition'", guard, StringComparison.Ordinal);
    }

    /// <summary>Rejects an absent output builder before attempting literal rendering.</summary>
    [Fact]
    public void EscapedAppend_NullBuilderFailsBeforeRendering()
    {
        // Arrange
        StringBuilder? builder = null;

        // Act
        var failure = Record.Exception(() =>
            SqlServerSafeMigrationsSqlGenerator.AppendEscapedSqlLiteral(builder!, ReadOnlySpan<char>.Empty));

        // Assert
        Assert.Equal("builder", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }

    /// <summary>Retains the previous scalar template as an independent byte-level reference.</summary>
    private static string ScalarReference(
        string expression,
        string type,
        string targetVariable,
        string? preamble = null
    ) => "EXEC sys.sp_executesql N'" + (preamble?.Replace("'", "''", StringComparison.Ordinal) ?? string.Empty)
        + "\nSET @doka_value = (" + expression.Replace("'", "''", StringComparison.Ordinal)
        + ");', N'@doka_value " + type + " OUTPUT', @doka_value = " + targetVariable + " OUTPUT;\n";
}
