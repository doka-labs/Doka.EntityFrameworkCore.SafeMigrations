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

    public SafeMigrationModelManagedRowState[] RowStates { get; }

    public long[] DependencyCounts { get; }

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

        var counts = dependencyCounts.Length == 0
            ? []
            : dependencyCounts
                .Split(',', StringSplitOptions.None)
                .Select(value => long.TryParse(
                        value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var count)
                    && count >= 0
                        ? count
                        : throw new InvalidOperationException(
                            $"The {providerName} model-managed dependency evidence is invalid."))
                .ToArray();

        if (counts.Length != expectedDependencyCount)
        {
            throw new InvalidOperationException(
                $"The {providerName} model-managed dependency evidence has an inconsistent entry count.");
        }

        return new SafeMigrationModelManagedDataEvidence(states, counts);
    }
}
