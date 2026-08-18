namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationDefinitionEquivalence
{
    private static bool Identity(
        string leftTable,
        string? leftSchema,
        string rightTable,
        string? rightSchema
    ) => StringComparer.Ordinal.Equals(leftTable, rightTable) && StringComparer.Ordinal.Equals(leftSchema, rightSchema);

    private static bool Strings(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right
    ) => Sequence(left, right, StringComparer.Ordinal.Equals);

    private static bool Optional<T>(
        T? left,
        T? right,
        Func<T, T, bool> equals
    )
        where T : class
    {
        if (left is null
            || right is null)
        {
            return left is null && right is null;
        }

        return equals(left, right);
    }

    private static bool Sequence<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, T, bool> equals
    )
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }
}
