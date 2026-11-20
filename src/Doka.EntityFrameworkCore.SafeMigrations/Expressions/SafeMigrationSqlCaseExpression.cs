namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents an ordered searched CASE expression with an optional ELSE result.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlCaseExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a searched CASE and snapshots its ordered clauses.</summary>
    /// <param name="whenClauses">The non-empty sequence of non-null WHEN/THEN clauses.</param>
    /// <param name="elseResult">The fallback expression, or null for the SQL implicit NULL result.</param>
    public SafeMigrationSqlCaseExpression(
        IEnumerable<SafeMigrationSqlCaseWhenClause> whenClauses,
        SafeMigrationSqlExpression? elseResult = null
    )
    {
        ArgumentNullException.ThrowIfNull(whenClauses);

        // WHY: Retain the explicit defensive snapshot and its exact array representation.
        // ReSharper disable once UseCollectionExpression
        var clauses = whenClauses.ToArray();

        // WHY: Nullable annotations do not prevent callers from supplying null sequence elements at runtime.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        // WHY: Nullable annotations cannot prevent runtime callers from supplying null clause entries.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (clauses.Length == 0
            || clauses.Any(static clause => clause is null))
        {
            throw new ArgumentException("A CASE expression requires non-null WHEN/THEN clauses.", nameof(whenClauses));
        }

        // WHY: Branch order is semantic, and caller-owned arrays must not
        // mutate the expression after analysis or contract fingerprinting.
        WhenClauses = Array.AsReadOnly(clauses);
        ElseResult = elseResult;
    }

    /// <summary>Gets the clauses in their SQL evaluation order.</summary>
    public IReadOnlyList<SafeMigrationSqlCaseWhenClause> WhenClauses { get; }

    /// <summary>Gets the fallback expression, or null for an omitted ELSE.</summary>
    public SafeMigrationSqlExpression? ElseResult { get; }
}
