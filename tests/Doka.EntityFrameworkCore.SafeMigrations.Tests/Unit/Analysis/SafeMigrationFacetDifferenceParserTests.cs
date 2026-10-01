namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks bounded diagnostic parsing without retaining intermediate payload copies.</summary>
/// <param name="output">The allocation evidence output.</param>
public sealed class SafeMigrationFacetDifferenceParserTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Empty records are ignored without changing the order or owned values.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("\u001e")]
    [InlineData("\u001e\u001e")]
    public void EmptyRecordsReturnNoDifferences(string payload)
    {
        // Arrange
        const string provider = "TestProvider";

        // Act
        var differences = SafeMigrationFacetDifferenceParser.Parse(payload, provider);

        // Assert
        Assert.Empty(differences);
    }

    /// <summary>Malformed fields fail without echoing their values.</summary>
    [Theory]
    [InlineData("facet\u001fexpected")]
    [InlineData("facet\u001fexpected\u001factual\u001fextra")]
    [InlineData("\u001fexpected\u001factual")]
    [InlineData("facet\u001f\u001factual")]
    [InlineData("facet\u001fexpected\u001f")]
    [InlineData("facet\u001fexpected\u001fsecret\nvalue")]
    [InlineData("facet\u001fexpected\u001f\u00ff")]
    public void MalformedFieldsAreRejectedWithoutDisclosure(string payload)
    {
        // Arrange
        const string provider = "TestProvider";

        // Act
        var exception = Record.Exception(() => SafeMigrationFacetDifferenceParser.Parse(payload, provider));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.DoesNotContain(payload, exception.Message, StringComparison.Ordinal);
        Assert.Contains("malformed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The record cap applies to nonempty records even with separator padding.</summary>
    [Fact]
    public void ExcessiveRecordsAreRejected()
    {
        // Arrange
        var payload = string.Join("\u001e\u001e", Enumerable.Repeat("facet\u001fexpected\u001factual", 17));

        // Act
        var exception = Record.Exception(() => SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider"));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Overlong individual fields fail even when the total envelope is still bounded.</summary>
    [Theory]
    [InlineData(65, 1, 1)]
    [InlineData(1, 257, 1)]
    [InlineData(1, 1, 257)]
    public void ExcessiveFieldLengthsAreRejected(int facetLength, int expectedLength, int actualLength)
    {
        // Arrange
        var payload = new string('f', facetLength) + "\u001f"
            + new string('e', expectedLength) + "\u001f" + new string('a', actualLength);

        // Act
        var exception = Record.Exception(() => SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider"));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Delimiter padding preserves the maximum accepted record count and field limits.</summary>
    [Fact]
    public void BoundedFieldsPreserveExactValues()
    {
        // Arrange
        var facet = new string('f', 64);
        var expected = new string('e', 256);
        var actual = new string('a', 256);
        var payload = "\u001e" + string.Join("\u001e", Enumerable.Repeat($"{facet}\u001f{expected}\u001f{actual}", 16));

        // Act
        var differences = SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider");

        // Assert
        Assert.Equal(16, differences.Count);
        Assert.All(differences, difference =>
        {
            Assert.Equal(facet, difference.Facet);
            Assert.Equal(expected, difference.Expected);
            Assert.Equal(actual, difference.Actual);
        });
    }

    /// <summary>Prepared payloads retain only the final evidence values.</summary>
    [Fact]
    public void PreparedDiagnosticPayloadAllocationsRemainBounded()
    {
        // Arrange
        const int iterations = 1_000;
        var payload = string.Join(
            "\u001e", Enumerable.Repeat("column_store_type\u001fvarchar(200)\u001fvarchar(10)", 16));

        _ = SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider");

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var index = 0; index < iterations; index++)
        {
            count += SafeMigrationFacetDifferenceParser.Parse(payload, "TestProvider").Count;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        output.WriteLine($"Prepared diagnostic payloads: {allocated} bytes for {iterations} sixteen-record payloads.");
        Assert.Equal(16_000, count);
        // WHY: Keep headroom above final evidence objects but below the old 9 MB intermediate-copy path.
        Assert.InRange(allocated, 0, 5_200_000);
    }
}
