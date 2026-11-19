namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Represents an explicit provider store-type cast.</summary>
[PublicAPI]
public sealed class SafeMigrationSqlCastExpression : SafeMigrationSqlExpression
{
    /// <summary>Initializes a cast.</summary>
    /// <param name="operand">The cast operand.</param>
    /// <param name="storeType">The provider store type.</param>
    public SafeMigrationSqlCastExpression(
        SafeMigrationSqlExpression operand,
        string storeType
    ) : this(operand, storeType, usesSqlServerConvert: false) { }

    /// <summary>Retains dialect provenance when metadata used SQL Server's reversed CONVERT argument roles.</summary>
    internal SafeMigrationSqlCastExpression(
        SafeMigrationSqlExpression operand,
        string storeType,
        bool usesSqlServerConvert
    )
    {
        ArgumentNullException.ThrowIfNull(operand);

        Operand = operand;
        StoreType = SafeMigrationDefinitionValidator.Required(storeType, nameof(storeType));
        UsesSqlServerConvert = usesSqlServerConvert;
    }

    /// <summary>Gets the cast operand.</summary>
    public SafeMigrationSqlExpression Operand { get; }

    /// <summary>Gets the provider store type.</summary>
    public string StoreType { get; }

    /// <summary>Gets whether parsing requires SQL Server's type-first CONVERT interpretation.</summary>
    internal bool UsesSqlServerConvert { get; }
}
