namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents one ordered condition and result in a searched CASE expression.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlCaseWhenClause
{
    /// <summary>Initializes an immutable WHEN/THEN clause.</summary>
    /// <param name="condition">The predicate tested before selecting this result.</param>
    /// <param name="result">The expression evaluated when the predicate is true.</param>
    public SafeMigrationSqlCaseWhenClause(
        SafeMigrationSqlExpression condition,
        SafeMigrationSqlExpression result
    )
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(result);

        Condition = condition;
        Result = result;
    }

    /// <summary>Gets the predicate tested for this clause.</summary>
    public SafeMigrationSqlExpression Condition { get; }

    /// <summary>Gets the result selected when the predicate is true.</summary>
    public SafeMigrationSqlExpression Result { get; }
}
