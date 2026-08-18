using Doka.EntityFrameworkCore.SafeMigrations.Testing;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationStateSpaceGeneratorTests
{
    private const int FixedSeed = 0x5AFE2026;

    [Fact]
    public void FixedSeed_IsReproducibleAndCoversEveryDimensionPair()
    {
        var dimensions = CreateDimensions();

        var first = SafeMigrationStateSpaceGenerator.GeneratePairwise(dimensions, FixedSeed);
        var second = SafeMigrationStateSpaceGenerator.GeneratePairwise(dimensions, FixedSeed);

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

    [Fact]
    public void InvalidDimensionContracts_FailBeforeGeneration()
    {
        Assert.Throws<ArgumentException>(() =>
            SafeMigrationStateSpaceGenerator.GeneratePairwise(
                [new SafeMigrationStateDimension("only", ["one"])],
                FixedSeed));
        Assert.Throws<ArgumentException>(() => SafeMigrationStateSpaceGenerator.GeneratePairwise(
            [
                new SafeMigrationStateDimension("duplicate", ["one"]),
                new SafeMigrationStateDimension("duplicate", ["two"]),
            ],
            FixedSeed));
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
                "missing_subset",
                "matching",
                "single_drift",
                "multiple_drift",
            ]),
        new(
            "relations",
            [
                "missing",
                "matching",
                "different",
            ]),
        new(
            "extras",
            [
                "none",
                "unknown_objects",
            ]),
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
                "none",
                "legacy",
                "partial_failure",
                "complete_core",
            ]),
        new(
            "parallelism",
            [
                "separate_databases",
                "same_database",
            ]),
        new(
            "context",
            [
                "canonical",
                "derived_matching",
                "derived_different",
            ]),
    ];

    private static string Serialize(
        IReadOnlyList<SafeMigrationStateScenario> scenarios
    ) => string.Join(Environment.NewLine, scenarios.Select(Serialize));

    private static string Serialize(
        SafeMigrationStateScenario scenario
    ) => string.Join(
        "|",
        scenario
            .Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}={pair.Value}"));
}
