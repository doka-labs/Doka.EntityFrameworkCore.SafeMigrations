namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies exact private-scope rendering without expanding or flattening its source buffer.</summary>
public sealed class SqlServerGuardScopeAllocationTests
{
    /// <summary>Quotes empty, ordinary, dense and nested Unicode batches without changing the input.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("SELECT 1;\n")]
    [InlineData("'")]
    [InlineData("''''''''")]
    [InlineData("EXEC sys.sp_executesql N'SELECT N''O''''Brien'';';\n")]
    [InlineData("SELECT N'\uD83D\uDE80 O''Brien \uD834\uDD1E';\n")]
    public void ScopeBuffer_ExactOutputPreservesOriginalCharactersAndCapacity(
        string body
    )
    {
        // Arrange
        var buffer = new StringBuilder(body);
        var originalCapacity = buffer.Capacity;
        var expected = ReferenceScope(body);

        // Act
        var scoped = SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(buffer);

        // Assert
        Assert.Equal(expected, scoped);
        Assert.Equal(body, buffer.ToString());
        Assert.Equal(originalCapacity, buffer.Capacity);
        Assert.Equal(body, SqlServerGuardedSqlTestContract.DecodeScope(scoped));
    }

    /// <summary>Handles adjacent quotes across chunk edges and surrogate pairs across many source chunks.</summary>
    [Theory]
    [InlineData(4, 2)]
    [InlineData(16, 4)]
    [InlineData(1024, 32)]
    public void ScopeBuffer_ChunkBoundariesRetainExactEscapingAndInputLayout(
        int initialCapacity,
        int repetitions
    )
    {
        // Arrange
        var buffer = new StringBuilder(initialCapacity);
        buffer.Append('x', initialCapacity - 1).Append('\'').Append('\'');
        for (var index = 0; index < repetitions; index++)
        {
            buffer.Append('x', 1023).Append('\'').Append("\uD83D\uDE80").Append('\'');
        }

        var original = buffer.ToString();
        var originalCapacity = buffer.Capacity;
        var originalChunks = SnapshotChunks(buffer);
        var expected = ReferenceScope(original);

        // Act
        var scoped = SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(buffer);
        var remainingChunks = SnapshotChunks(buffer);

        // Assert
        Assert.True(originalChunks.Length > 1);
        Assert.Equal(expected, scoped);
        Assert.Equal(original, buffer.ToString());
        Assert.Equal(originalCapacity, buffer.Capacity);
        Assert.Equal(originalChunks, remainingChunks);
        Assert.Equal(original, SqlServerGuardedSqlTestContract.DecodeScope(scoped));
    }

    /// <summary>Allocates less than mutable buffer expansion while preserving the identical final SQL.</summary>
    [Fact]
    public void ScopeBuffer_DenseGuardAllocatesLessThanExpandedMutableWrapper()
    {
        // Arrange
        const int operationCount = 64;
        var body = string.Concat(Enumerable.Repeat("SELECT N'O''Brien';\n", 1024));
        var immutableBuffers = Enumerable.Range(0, operationCount).Select(_ => new StringBuilder(body)).ToArray();
        var mutableBuffers = Enumerable.Range(0, operationCount).Select(_ => new StringBuilder(body)).ToArray();
        Func<int, string> renderImmutable = index =>
            SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(immutableBuffers[index]);

        Func<int, string> renderMutable = index => RenderExpandedMutableScope(mutableBuffers[index]);

        // WHY: Warm the same paths before counting managed allocations. This
        // compares deterministic buffer work, not machine-specific timings.
        SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(new StringBuilder(body));
        RenderExpandedMutableScope(new StringBuilder(body));

        // Act
        var immutable = MeasureAllocations(renderImmutable, operationCount);
        var mutable = MeasureAllocations(renderMutable, operationCount);

        // Assert
        Assert.Equal(mutable.Sql, immutable.Sql);
        Assert.Equal(ReferenceScope(body), immutable.Sql);
        Assert.InRange(immutable.Bytes, 1, mutable.Bytes - 1);
        Assert.All(immutableBuffers, buffer => Assert.Equal(body, buffer.ToString()));
    }

    /// <summary>Rejects an absent operation buffer instead of creating an empty SQL contract.</summary>
    [Fact]
    public void ScopeBuffer_NullInputFailsBeforeRendering()
    {
        // Arrange
        StringBuilder? buffer = null;

        // Act
        var failure = Record.Exception(() => SqlServerSafeMigrationsSqlGenerator.BuildIsolatedGuardSql(buffer!));

        // Assert
        Assert.Equal("body", Assert.IsType<ArgumentNullException>(failure).ParamName);
    }

    /// <summary>Builds the independent literal reference without relying on the optimized implementation.</summary>
    private static string ReferenceScope(
        string body
    ) => "EXEC sys.sp_executesql N'" + body.Replace("'", "''", StringComparison.Ordinal) + "';\n";

    /// <summary>Snapshots source chunks so tests detect mutation as well as equivalent flattened content.</summary>
    private static string[] SnapshotChunks(
        StringBuilder body
    )
    {
        var chunks = new List<string>();
        foreach (var chunk in body.GetChunks())
        {
            chunks.Add(chunk.ToString());
        }

        return chunks.ToArray();
    }

    /// <summary>Retains the former wrapper as the allocation comparison without modifying product budgets.</summary>
    private static string RenderExpandedMutableScope(
        StringBuilder body
    ) => body.Replace("'", "''").Insert(0, "EXEC sys.sp_executesql N'").Append("';\n").ToString();

    /// <summary>Counts only rendering allocations on the executing test thread.</summary>
    private static (long Bytes, string Sql) MeasureAllocations(
        Func<int, string> render,
        int count
    )
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sql = string.Empty;
        for (var index = 0; index < count; index++)
        {
            sql = render(index);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        return (allocated, sql);
    }
}
