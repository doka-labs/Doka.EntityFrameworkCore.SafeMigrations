namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationDefinitionValidator
{
    public static string Required(
        string value,
        string parameterName
    ) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("The value must not be empty or whitespace.", parameterName)
        : value;

    public static string? Optional(
        string? value,
        string parameterName
    )
    {
        if (value is not null
            && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value must be null or non-whitespace.", parameterName);
        }

        return value;
    }

    public static IReadOnlyList<string> Identifiers(
        IEnumerable<string> values,
        string parameterName,
        bool allowEmpty = false
    )
    {
        ArgumentNullException.ThrowIfNull(values);

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        var snapshot = values.ToArray();
        if (!allowEmpty
            && snapshot.Length == 0)
        {
            throw new ArgumentException("At least one identifier is required.", parameterName);
        }

        foreach (var value in snapshot)
        {
            Required(value, parameterName);
        }

        if (snapshot
                .Distinct(StringComparer.Ordinal)
                .Count()
            != snapshot.Length)
        {
            throw new ArgumentException("Duplicate identifiers are not allowed.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }

    public static IReadOnlyList<T> Definitions<T>(
        IEnumerable<T> values,
        string parameterName,
        bool allowEmpty = true
    )
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values);

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        var snapshot = values.ToArray();
        if (!allowEmpty
            && snapshot.Length == 0)
        {
            throw new ArgumentException("At least one definition is required.", parameterName);
        }

        // WHY: Public definition constructors must reject malformed runtime collections, not trust annotations.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException("Definitions must not contain null values.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
