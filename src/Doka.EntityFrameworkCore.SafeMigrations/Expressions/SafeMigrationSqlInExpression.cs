namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents an IN predicate.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlInExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes an IN predicate.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="values">The non-empty set of candidate values.</param>
    /// <param name="negated">Whether the predicate is NOT IN.</param>
    public SafeMigrationSqlInExpression(
        SafeMigrationSqlExpression operand,
        IEnumerable<SafeMigrationSqlExpression> values,
        bool negated = false
    )
    {
        ArgumentNullException.ThrowIfNull(operand);
        ArgumentNullException.ThrowIfNull(values);

        // WHY: The public immutable definition must not retain a caller-owned sequence.
        // ReSharper disable once UseCollectionExpression
        var materialized = values.ToArray();

        // WHY: Nullable annotations do not prevent callers from supplying null sequence elements at runtime.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        // WHY: Nullable annotations cannot prove the validity of elements supplied through the public boundary.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (materialized.Length == 0
            || materialized.Any(static value => value is null))
        {
            throw new ArgumentException("An IN predicate requires non-null values.", nameof(values));
        }

        Operand = operand;
        Values = Array.AsReadOnly(materialized);
        Negated = negated;
    }

    /// <summary>Gets the tested operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the candidate values.</summary>
    public IReadOnlyList<SafeMigrationSqlExpression> Values { get; }

    /// <summary>Gets whether the predicate is negated.</summary>
    public bool Negated { get; }
}
