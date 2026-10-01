namespace Doka.EntityFrameworkCore.SafeMigrations;

internal enum SafeMigrationModelManagedRowState : byte
{
    Missing = 0,
    Source = 1,
    Target = 2,
    Different = 3,
}

internal sealed class SafeMigrationModelManagedDataEvidence
{
    /// <summary>Validates and snapshots caller-owned row and dependency evidence.</summary>
    public SafeMigrationModelManagedDataEvidence(
        SafeMigrationModelManagedRowState[] rowStates,
        long[] dependencyCounts
    )
    {
        ArgumentNullException.ThrowIfNull(rowStates);
        ArgumentNullException.ThrowIfNull(dependencyCounts);

        if (rowStates.Any(static state => !Enum.IsDefined(state)))
        {
            throw new ArgumentOutOfRangeException(nameof(rowStates));
        }

        if (dependencyCounts.Any(static count => count < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(dependencyCounts));
        }

        RowStates = rowStates.ToArray();
        DependencyCounts = dependencyCounts.ToArray();
    }

    // WHY: Parse creates and validates both arrays itself. Only that private path may
    // transfer ownership without making a second full snapshot of provider evidence.
    private SafeMigrationModelManagedDataEvidence()
    {
    }

    /// <summary>Gets the owned row classifications in authored order.</summary>
    public SafeMigrationModelManagedRowState[] RowStates { get; private init; } = [];

    /// <summary>Gets the owned incoming dependency counts in authored order.</summary>
    public long[] DependencyCounts { get; private init; } = [];

    /// <summary>Parses compact provider evidence while retaining exact state and cardinality checks.</summary>
    public static SafeMigrationModelManagedDataEvidence Parse(
        string rowStates,
        int expectedRowCount,
        string dependencyCounts,
        int expectedDependencyCount,
        string providerName
    )
    {
        ArgumentNullException.ThrowIfNull(rowStates);
        ArgumentNullException.ThrowIfNull(dependencyCounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (rowStates.Length != expectedRowCount)
        {
            throw new InvalidOperationException(
                $"The {providerName} model-managed row evidence has an inconsistent entry count.");
        }

        var states = new SafeMigrationModelManagedRowState[rowStates.Length];
        for (var index = 0; index < rowStates.Length; index++)
        {
            states[index] = rowStates[index] switch
            {
                '0' => SafeMigrationModelManagedRowState.Missing,
                '1' => SafeMigrationModelManagedRowState.Source,
                '2' => SafeMigrationModelManagedRowState.Target,
                '3' => SafeMigrationModelManagedRowState.Different,
                _ => throw new InvalidOperationException(
                    $"The {providerName} model-managed row evidence contains an unknown state."),
            };
        }

        var payload = dependencyCounts.AsSpan();
        var counts = payload.IsEmpty ? [] : new long[payload.Count(',') + 1];
        var countIndex = 0;
        if (!payload.IsEmpty)
        {
            foreach (var range in payload.Split(','))
            {
                // WHY: Empty fields and signs remain invalid. Span slices remove transient
                // token strings without weakening the invariant nonnegative integer contract.
                if (!long.TryParse(payload[range], NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    || count < 0)
                {
                    throw new InvalidOperationException(
                        $"The {providerName} model-managed dependency evidence is invalid.");
                }

                counts[countIndex++] = count;
            }
        }

        if (counts.Length != expectedDependencyCount)
        {
            throw new InvalidOperationException(
                $"The {providerName} model-managed dependency evidence has an inconsistent entry count.");
        }

        return new SafeMigrationModelManagedDataEvidence
        {
            RowStates = states,
            DependencyCounts = counts,
        };
    }
}
