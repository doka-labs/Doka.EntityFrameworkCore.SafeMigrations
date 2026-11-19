namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents a provider-neutral SQL expression with explicit token roles.</summary>
[PublicAPI]
public abstract class SafeMigrationSqlExpression
{
    /// <summary>Initializes a structured expression node without retaining provider state.</summary>
    protected SafeMigrationSqlExpression() { }
}
