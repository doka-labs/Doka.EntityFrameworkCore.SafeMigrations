namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProofMarkerRenderingTests
{
    /// <summary>Enumerates the closed rendering consumers independently for each unowned proof family.</summary>
    /// <returns>The expression consumer and structural marker lacking an owning descriptor.</returns>
    public static IEnumerable<object[]> UnboundProofRenderCases()
    {
        foreach (var consumer in BinaryRenderCases().Select(static row => (string)row[0]!)
                     .Distinct(StringComparer.Ordinal))
        {
            yield return [consumer, BinaryMarker];
            yield return [consumer, IdentifierMarker];
        }
    }

    /// <summary>No ordinary, prepared or raw-status renderer can render an unowned structural proof.</summary>
    /// <param name="consumer">The public runtime-plan expression entry point.</param>
    /// <param name="marker">The binary-default or identifier-reference proof without a descriptor.</param>
    [Theory]
    [MemberData(nameof(UnboundProofRenderCases))]
    public void EveryConsumerRejectsUnownedProofFamilies(string consumer, string marker)
    {
        // Arrange
        var plan = CreatePlan(marker, null) with { BinaryDefaultProofs = [], IdentifierReferenceProofs = [] };

        // Act
        var exception = Record.Exception(() => Render(plan, consumer));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);

        Assert.Contains("no owning contract", invalid.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The replacement invariant names both proof domains rather than misidentifying an identifier.
    /// </summary>
    /// <param name="marker">The unbound domain reaching the final replacement boundary.</param>
    [Theory]
    [InlineData(BinaryMarker)]
    [InlineData(IdentifierMarker)]
    public void ReplacementBoundaryRejectsEitherUnboundProofDomainPrecisely(string marker)
    {
        // Arrange
        var replace = ReplacementBoundary();

        // Act
        var exception = Record.Exception(() => replace(marker, "FALSE", "FALSE", "FALSE", "FALSE", null));

        // Assert
        var invalid = Assert.IsType<InvalidOperationException>(exception);

        Assert.Equal("Binary-default and identifier-reference proofs must be bound before replacement.",
            invalid.Message);
    }

    /// <summary>Quoted marker payloads remain untouched at the final replacement boundary.</summary>
    /// <param name="marker">The literal payload resembling either proof domain.</param>
    [Theory]
    [InlineData(BinaryMarker)]
    [InlineData(IdentifierMarker)]
    public void ReplacementBoundaryPreservesQuotedProofPayloads(string marker)
    {
        // Arrange
        var expression = "'a''" + marker + "' AND \"b\"\"" + marker + "\" AND `c``" + marker + "`";
        var replace = ReplacementBoundary();

        // Act
        var rendered = replace(expression, "FALSE", "FALSE", "FALSE", "FALSE", null);

        // Assert
        Assert.Same(expression, rendered);
    }

    /// <summary>Existing transition and required-add domains retain their exact independent substitutions.</summary>
    [Fact]
    public void ReplacementBoundaryPreservesExistingProofDomainOrder()
    {
        // Arrange
        var expression = string.Join(" AND ", MySqlSafeMigrationRuntimePlan.DataProbePlaceholder,
            MySqlSafeMigrationRuntimePlan.TransitionInvariantPlaceholder,
            MySqlSafeMigrationRuntimePlan.NullabilityDataProbePlaceholder,
            MySqlSafeMigrationRuntimePlan.ColumnRepairInvariantPlaceholder,
            MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder);

        var replace = ReplacementBoundary();

        // Act
        var rendered = replace(expression, "first", "second", "third", "fourth", "fifth");

        // Assert
        Assert.Equal("first AND second AND third AND fourth AND fifth", rendered);
    }

    /// <summary>Targets the final defensive boundary without inventing an invalid native proof binding.</summary>
    private static Func<string, string, string, string, string, string?, string> ReplacementBoundary()
    {
        // WHY: Normal renderers bind both domains before replacement. Reflection
        // isolates the defensive failure without broadening the product API or
        // pretending its immutable second pass can see a different marker kind.
        var method = typeof(MySqlSafeMigrationRuntimePlan).GetMethod("ReplaceTransitionPlaceholders",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return method.CreateDelegate<Func<string, string, string, string, string, string?, string>>();
    }
}
