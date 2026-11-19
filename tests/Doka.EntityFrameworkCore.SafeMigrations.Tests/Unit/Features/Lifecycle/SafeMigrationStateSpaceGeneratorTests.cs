namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins deterministic pairwise scenario coverage and rejection of invalid dimension identities before generation.
/// </summary>
public sealed class SafeMigrationStateSpaceGeneratorTests
{
    private const int FixedSeed = 0x5AFE2026;

    /// <summary>
    /// Produces reproducible unique scenarios and covers every independent pair of declared dimension values.
    /// </summary>
    [Fact]
    public void FixedSeed_IsReproducibleAndCoversEveryDimensionPair()
    {
        // Arrange
        var dimensions = CreateDimensions();

        // Act
        var first = SafeMigrationStateSpaceGenerator.GeneratePairwise(dimensions, FixedSeed);
        var second = SafeMigrationStateSpaceGenerator.GeneratePairwise(dimensions, FixedSeed);

        // Assert
        Assert.NotEmpty(first);
        Assert.Equal(Serialize(first), Serialize(second));
        Assert.Equal(
            first.Count,
            first
                .Select(Serialize)
                .Distinct(StringComparer.Ordinal)
                .Count());

        for (var left = 0; left < dimensions.Count; left++)
        {
            for (var right = left + 1; right < dimensions.Count; right++)
            {
                foreach (var leftValue in dimensions[left].Values)
                {
                    foreach (var rightValue in dimensions[right].Values)
                    {
                        Assert.Contains(
                            first,
                            scenario => scenario.Values[dimensions[left].Name] == leftValue
                                && scenario.Values[dimensions[right].Name] == rightValue);
                    }
                }
            }
        }
    }

    /// <summary>Rejects each invalid dimension contract before generating a scenario.</summary>
    /// <param name="duplicate">Whether two dimensions reuse a name instead of supplying too few dimensions.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidDimensionContracts_FailBeforeGeneration(
        bool duplicate
    )
    {
        // Arrange
        SafeMigrationStateDimension[] dimensions = duplicate
            ?
            [
                new SafeMigrationStateDimension("duplicate", ["one"]),
                new SafeMigrationStateDimension("duplicate", ["two"]),
            ]
            : [new SafeMigrationStateDimension("only", ["one"])];

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationStateSpaceGenerator.GeneratePairwise(dimensions, FixedSeed));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    private static IReadOnlyList<SafeMigrationStateDimension> CreateDimensions() =>
    [
        new(
            "table",
            [
                "missing",
                "complete",
                "module_missing",
                "container",
                "view",
            ]),
        new(
            "column",
            [
                "missing_subset", "matching", "single_drift", "multiple_drift",
            ]),
        new("relations", ["missing", "matching", "different"]),
        new("extras", ["none", "unknown_objects"]),
        new(
            "data",
            [
                "empty",
                "valid",
                "duplicates",
                "orphans",
                "null_or_range_blocker",
            ]),
        new(
            "history",
            [
                "none", "legacy", "partial_failure", "complete_core",
            ]),
        new("parallelism", ["separate_databases", "same_database"]),
        new("context", ["canonical", "derived_matching", "derived_different"]),
    ];

    private static string Serialize(
        IReadOnlyList<SafeMigrationStateScenario> scenarios
    ) => string.Join(Environment.NewLine, scenarios.Select(Serialize));

    private static string Serialize(
        SafeMigrationStateScenario scenario
    ) => string.Join(
        "|",
        scenario
            .Values
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}={pair.Value}"));
}
