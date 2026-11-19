namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents provider-owned SQL whose structure cannot be compared by Core.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlProviderFragmentExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a provider fragment.</summary>
    /// <param name="providerId">The provider contract identifier.</param>
    /// <param name="sql">The provider-owned SQL.</param>
    public SafeMigrationSqlProviderFragmentExpression(
        string providerId,
        string sql
    )
    {
        ProviderId = SafeMigrationDefinitionValidator.Required(providerId, nameof(providerId));
        Sql = SafeMigrationDefinitionValidator.Required(sql, nameof(sql));
    }

    /// <summary>Gets the provider contract identifier.</summary>
    public string ProviderId { get; }

    /// <summary>Gets the provider-owned SQL.</summary>
    public string Sql { get; }
}
