namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies exact catalog command assembly independently of asynchronous transport.</summary>
public sealed class SqlServerCatalogCommandTextTests
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
    public void AssemblyPreservesExactStatementText(
        int count,
        bool delayed
    )
    {
        // Arrange
        var selections = Enumerable
            .Range(0, count)
            .Select(index => $"SELECT {index},N'escaped '' quote \u20ac \ud83d\ude00';")
            .ToArray();

        const string prefix = "";
        var separator = delayed
            ? "\n"
            : "\nUNION ALL\n";

        var trailer = delayed
            ? string.Empty
            : ";";

        var expected = prefix + string.Join(separator, selections) + trailer;

        // Act
        var actual = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(
            selections,
            prefix,
            separator,
            trailer);

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(Encoding.UTF8.GetByteCount(expected), Encoding.UTF8.GetByteCount(actual));
    }

    /// <summary>Repeated assembly preserves the complete large prepared statement and its length checksum.</summary>
    [Fact]
    public void AssemblyPreservesRepeatedPreparedStatements()
    {
        // Arrange
        const int repetitions = 100;
        const int samples = 5;
        var selections = Enumerable
            .Repeat(new string('x', 2_048), 32)
            .ToArray();

        const string prefix = "";
        const string separator = "\n";
        const string trailer = "";
        var expectedLength = prefix.Length
            + selections.Sum(static value => value.Length)
            + (selections.Length - 1) * separator.Length
            + trailer.Length;

        var expected = prefix + string.Join(separator, selections) + trailer;
        var snapshot = selections.ToArray();
        var statements = new string[repetitions * samples];

        // Act
        var checksum = 0L;
        for (var sample = 0; sample < samples; sample++)
        {
            for (var index = 0; index < repetitions; index++)
            {
                var statement = SqlServerSafeMigrationProviderAnalyzer.BuildCatalogCommandText(
                    selections,
                    prefix,
                    separator,
                    trailer);

                statements[sample * repetitions + index] = statement;
                checksum += statement.Length;
            }
        }

        // Assert
        Assert.Equal((long)expectedLength * repetitions * samples, checksum);
        Assert.All(statements, statement => Assert.Equal(expected, statement));
        Assert.Equal(snapshot, selections);
    }
}
