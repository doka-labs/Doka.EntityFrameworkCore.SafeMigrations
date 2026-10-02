namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies exact catalog command assembly independently of asynchronous transport allocations.</summary>
/// <param name="output">The diagnostic output retaining the observed allocation count.</param>
public sealed class SqlServerCatalogCommandTextTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Retains empty, direct and delayed statement text including quoted and Unicode payloads.</summary>
    /// <param name="count">The selection count.</param>
    /// <param name="delayed">Whether the statement includes the delayed classifier envelope.</param>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void AssemblyPreservesExactStatementText(int count, bool delayed)
    {
        // Arrange
        var selections = Enumerable.Range(0, count)
            .Select(index => $"SELECT {index},N'escaped '' quote \u20ac \ud83d\ude00';")
            .ToArray();
        const string prefix = "";
        var separator = delayed ? "\n" : "\nUNION ALL\n";
        var trailer = delayed ? string.Empty : ";";
        var expected = prefix + string.Join(separator, selections) + trailer;

        // Act
        var actual = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(
            selections, prefix, separator, trailer);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), Encoding.UTF8.GetByteCount(actual));
    }

    /// <summary>Large catalog statements require only their final string, not a full joined scratch copy.</summary>
    [Fact]
    public void AssemblyKeepsPreparedStatementAllocationsBounded()
    {
        // Arrange
        const int repetitions = 100;
        const int samples = 5;
        var selections = Enumerable.Repeat(new string('x', 2_048), 32).ToArray();
        const string prefix = "";
        const string separator = "\n";
        const string trailer = "";
        var expectedLength = prefix.Length + selections.Sum(static value => value.Length)
            + ((selections.Length - 1) * separator.Length) + trailer.Length;

        var allocations = new long[samples];
        _ = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(selections, prefix, separator, trailer);

        // Act
        var checksum = 0L;
        for (var sample = 0; sample < samples; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < repetitions; index++)
            {
                checksum += SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(
                    selections, prefix, separator, trailer).Length;
            }

            allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        // Assert
        // WHY: Match the benchmark's five-sample median for the steady-state contract.
        // An intermediate joined buffer recurs in every sample and cannot pass this unchanged cap.
        Array.Sort(allocations);
        var median = allocations[samples / 2];
        output.WriteLine(
            $"Prepared catalog assembly: {median} median bytes for {repetitions} statements; "
            + $"samples {string.Join(",", allocations)}.");

        Assert.Equal((long)expectedLength * repetitions * samples, checksum);
        Assert.InRange(median, 0, repetitions * ((2L * expectedLength) + 64));
    }
}
