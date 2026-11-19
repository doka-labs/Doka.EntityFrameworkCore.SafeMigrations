namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a function call.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlFunctionExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a function call.</summary>
    /// <param name="name">The unqualified SQL function name.</param>
    /// <param name="arguments">The function arguments.</param>
    public SafeMigrationSqlFunctionExpression(
        string name,
        IEnumerable<SafeMigrationSqlExpression>? arguments = null
    )
    {
        Name = SafeMigrationDefinitionValidator.Required(name, nameof(name));

        if (!(Name[0] == '_' || char.IsAsciiLetter(Name[0]))
            || !Name.All(static character => character == '_' || char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "A SQL function name must start with an ASCII letter or underscore "
                + "and contain only ASCII letters, digits, or underscores.",
                nameof(name));
        }

        // WHY: Snapshot caller arguments once with the existing array and null-validation behavior.
        // ReSharper disable once UseCollectionExpression
        var materialized = (arguments ?? []).ToArray();

        // WHY: Nullable annotations do not prevent callers from supplying null sequence elements at runtime.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        // WHY: The public boundary must reject null entries even from callers that ignore nullable annotations.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (materialized.Any(static value => value is null))
        {
            throw new ArgumentException("Function arguments cannot contain null entries.", nameof(arguments));
        }

        Arguments = Array.AsReadOnly(materialized);
    }

    /// <summary>Gets the function name.</summary>
    public string Name { get; }

    /// <summary>Gets the function arguments.</summary>
    public IReadOnlyList<SafeMigrationSqlExpression> Arguments { get; }
}
