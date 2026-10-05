namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies compact provider evidence parsing, ownership and allocation bounds.</summary>
/// <param name="output">The diagnostic output retaining the observed allocation count.</param>
public sealed class SafeMigrationModelManagedDataEvidenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Accepts only complete invariant nonnegative integer evidence.</summary>
    /// <param name="counts">The compact dependency payload.</param>
    /// <param name="expected">The expected dependency counts.</param>
    [Theory]
    [InlineData("", new long[0])]
    [InlineData("0", new long[] { 0 })]
    [InlineData("00,17,9223372036854775807", new long[] { 0, 17, long.MaxValue })]
    public void ParsePreservesCompleteDependencyValues(string counts, long[] expected)
    {
        // Arrange
        const string states = "0123";

        // Act
        var evidence = SafeMigrationModelManagedDataEvidence.Parse(states, 4, counts, expected.Length, "test");

        // Assert
        Assert.Equal(expected, evidence.DependencyCounts);
        Assert.Equal(new[]
        {
            SafeMigrationModelManagedRowState.Missing,
            SafeMigrationModelManagedRowState.Source,
            SafeMigrationModelManagedRowState.Target,
            SafeMigrationModelManagedRowState.Different,
        }, evidence.RowStates);
    }

    /// <summary>Malformed tokens remain invalid even when their cardinality is also wrong.</summary>
    /// <param name="counts">The malformed dependency payload.</param>
    [Theory]
    [InlineData(",")]
    [InlineData("1,")]
    [InlineData(",1")]
    [InlineData("1,,2")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    [InlineData("not-a-count")]
    public void ParseRejectsMalformedDependencyBeforeCountMismatch(string counts)
    {
        // Arrange
        const string states = "0";

        // Act
        var failure = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataEvidence.Parse(states, 1, counts, 0, "test"));

        // Assert
        Assert.Equal("The test model-managed dependency evidence is invalid.", failure.Message);
    }

    /// <summary>Valid dependency values still require the exact authored cardinality.</summary>
    /// <param name="counts">The otherwise valid payload.</param>
    /// <param name="expectedCount">The inconsistent authored cardinality.</param>
    [Theory]
    [InlineData("", 1)]
    [InlineData("1", 0)]
    [InlineData("1,2", 1)]
    [InlineData("1", -1)]
    public void ParseRejectsInconsistentDependencyCount(string counts, int expectedCount)
    {
        // Arrange
        const string states = "0";

        // Act
        var failure = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataEvidence.Parse(states, 1, counts, expectedCount, "test"));

        // Assert
        Assert.Equal("The test model-managed dependency evidence has an inconsistent entry count.", failure.Message);
    }

    /// <summary>Caller-owned arrays retain defensive snapshot isolation.</summary>
    [Fact]
    public void ConstructorSnapshotsCallerOwnedArrays()
    {
        // Arrange
        SafeMigrationModelManagedRowState[] states = [SafeMigrationModelManagedRowState.Source];
        long[] counts = [17];

        // Act
        var evidence = new SafeMigrationModelManagedDataEvidence(states, counts);
        states[0] = SafeMigrationModelManagedRowState.Different;
        counts[0] = 0;

        // Assert
        Assert.Equal(SafeMigrationModelManagedRowState.Source, Assert.Single(evidence.RowStates));
        Assert.Equal(17, Assert.Single(evidence.DependencyCounts));
    }

    /// <summary>Caller-owned evidence still requires defined states and nonnegative counts.</summary>
    /// <param name="state">The authored row classification.</param>
    /// <param name="count">The authored dependency count.</param>
    /// <param name="parameter">The rejected argument.</param>
    [Theory]
    [InlineData(4, 0L, "rowStates")]
    [InlineData(1, -1L, "dependencyCounts")]
    public void ConstructorRejectsInvalidCallerEvidence(
        int state,
        long count,
        string parameter
    )
    {
        // Arrange
        SafeMigrationModelManagedRowState[] states = [(SafeMigrationModelManagedRowState)state];
        long[] counts = [count];

        // Act
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SafeMigrationModelManagedDataEvidence(states, counts));

        // Assert
        Assert.Equal(parameter, failure.ParamName);
    }

    /// <summary>Parsed results own distinct arrays rather than sharing mutable scratch storage.</summary>
    [Fact]
    public void ParseOwnsIndependentResultArrays()
    {
        // Arrange
        var first = SafeMigrationModelManagedDataEvidence.Parse("1", 1, "17", 1, "test");

        // Act
        var second = SafeMigrationModelManagedDataEvidence.Parse("1", 1, "17", 1, "test");
        first.RowStates[0] = SafeMigrationModelManagedRowState.Different;
        first.DependencyCounts[0] = 0;

        // Assert
        Assert.Equal(SafeMigrationModelManagedRowState.Source, Assert.Single(second.RowStates));
        Assert.Equal(17, Assert.Single(second.DependencyCounts));
    }

    /// <summary>Repeated parsing allocates final owned evidence, not token strings and duplicate arrays.</summary>
    [Fact]
    public void ParseKeepsPreparedPayloadAllocationsBounded()
    {
        // Arrange
        const int repetitions = 1_000;
        var states = new string('2', 384);
        var counts = string.Join(',', Enumerable.Repeat("17", 64));
        _ = SafeMigrationModelManagedDataEvidence.Parse(states, 384, counts, 64, "test");

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0L;
        for (var index = 0; index < repetitions; index++)
        {
            var evidence = SafeMigrationModelManagedDataEvidence.Parse(states, 384, counts, 64, "test");
            checksum += evidence.DependencyCounts[63] + evidence.RowStates.Length;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        output.WriteLine($"Prepared evidence parsing: {allocated} bytes for {repetitions} results.");
        Assert.Equal(401_000, checksum);
        Assert.InRange(allocated, 0, 1_100_000);
    }
}
