using System;
using System.Collections.Generic;
using System.Linq;

namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal sealed record SafeMigrationStateDimension(
    string Name,
    IReadOnlyList<string> Values
);

internal sealed record SafeMigrationStateScenario(
    int Index,
    IReadOnlyDictionary<string, string> Values
);

internal static class SafeMigrationStateSpaceGenerator
{
    public static IReadOnlyList<SafeMigrationStateScenario> GeneratePairwise(
        IReadOnlyList<SafeMigrationStateDimension> dimensions,
        int seed
    )
    {
        ArgumentNullException.ThrowIfNull(dimensions);
        if (dimensions.Count < 2)
        {
            throw new ArgumentException("Pairwise generation requires at least two dimensions.", nameof(dimensions));
        }

        ValidateDimensions(dimensions);
        var uncovered = CreateAllPairs(dimensions);
        var random = new Random(seed);
        var rows = new List<int[]>();
        while (uncovered.Count > 0)
        {
            var required = uncovered.Min;
            int[]? best = null;
            var bestScore = -1;
            for (var candidateIndex = 0; candidateIndex < 2048; candidateIndex++)
            {
                var candidate = CreateRandomRow(dimensions, random);
                if (candidateIndex == 0)
                {
                    candidate[required.LeftDimension] = required.LeftValue;
                    candidate[required.RightDimension] = required.RightValue;
                }

                var score = Score(candidate, uncovered);
                if (score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            if (best is null
                || bestScore <= 0)
            {
                throw new InvalidOperationException("The deterministic pairwise generator made no progress.");
            }

            rows.Add(best);
            RemoveCoveredPairs(best, uncovered);
        }

        return rows
            .Select((
                row,
                index
            ) => new SafeMigrationStateScenario(
                index,
                dimensions
                    .Select((
                        dimension,
                        dimensionIndex
                    ) => new KeyValuePair<string, string>(dimension.Name, dimension.Values[row[dimensionIndex]]))
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)))
            .ToArray();
    }

    private static void ValidateDimensions(
        IReadOnlyList<SafeMigrationStateDimension> dimensions
    )
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dimension in dimensions)
        {
            if (string.IsNullOrWhiteSpace(dimension.Name)
                || !names.Add(dimension.Name)
                || dimension.Values.Count == 0
                || dimension.Values.Any(string.IsNullOrWhiteSpace)
                || dimension
                    .Values.Distinct(StringComparer.Ordinal)
                    .Count()
                != dimension.Values.Count)
            {
                throw new ArgumentException(
                    "State dimensions require unique names and non-empty unique values.",
                    nameof(dimensions));
            }
        }
    }

    private static SortedSet<StatePair> CreateAllPairs(
        IReadOnlyList<SafeMigrationStateDimension> dimensions
    )
    {
        var pairs = new SortedSet<StatePair>();
        for (var leftDimension = 0; leftDimension < dimensions.Count; leftDimension++)
        {
            for (var rightDimension = leftDimension + 1; rightDimension < dimensions.Count; rightDimension++)
            {
                for (var leftValue = 0; leftValue < dimensions[leftDimension].Values.Count; leftValue++)
                {
                    for (var rightValue = 0; rightValue < dimensions[rightDimension].Values.Count; rightValue++)
                    {
                        pairs.Add(new StatePair(leftDimension, leftValue, rightDimension, rightValue));
                    }
                }
            }
        }

        return pairs;
    }

    private static int[] CreateRandomRow(
        IReadOnlyList<SafeMigrationStateDimension> dimensions,
        Random random
    )
    {
        var row = new int[dimensions.Count];
        for (var index = 0; index < dimensions.Count; index++)
        {
            row[index] = random.Next(dimensions[index].Values.Count);
        }

        return row;
    }

    private static int Score(
        int[] row,
        IReadOnlySet<StatePair> uncovered
    )
    {
        var score = 0;
        for (var leftDimension = 0; leftDimension < row.Length; leftDimension++)
        {
            for (var rightDimension = leftDimension + 1; rightDimension < row.Length; rightDimension++)
            {
                if (uncovered.Contains(
                        new StatePair(leftDimension, row[leftDimension], rightDimension, row[rightDimension])))
                {
                    score++;
                }
            }
        }

        return score;
    }

    private static void RemoveCoveredPairs(
        int[] row,
        ISet<StatePair> uncovered
    )
    {
        for (var leftDimension = 0; leftDimension < row.Length; leftDimension++)
        {
            for (var rightDimension = leftDimension + 1; rightDimension < row.Length; rightDimension++)
            {
                uncovered.Remove(new StatePair(leftDimension, row[leftDimension], rightDimension, row[rightDimension]));
            }
        }
    }

    private readonly record struct StatePair(
        int LeftDimension,
        int LeftValue,
        int RightDimension,
        int RightValue
    ) : IComparable<StatePair>
    {
        public int CompareTo(
            StatePair other
        )
        {
            var comparison = LeftDimension.CompareTo(other.LeftDimension);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = LeftValue.CompareTo(other.LeftValue);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = RightDimension.CompareTo(other.RightDimension);

            return comparison != 0 ? comparison : RightValue.CompareTo(other.RightValue);
        }
    }
}
