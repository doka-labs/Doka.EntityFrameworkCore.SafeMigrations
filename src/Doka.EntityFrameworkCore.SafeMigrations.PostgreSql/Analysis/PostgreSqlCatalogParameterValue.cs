namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal readonly record struct PostgreSqlCatalogParameterValue(
    object? Value,
    string StoreType
);

internal sealed class PostgreSqlCatalogParameterValueComparer : IEqualityComparer<PostgreSqlCatalogParameterValue>
{
    public static PostgreSqlCatalogParameterValueComparer Instance { get; } = new();

    public bool Equals(
        PostgreSqlCatalogParameterValue left,
        PostgreSqlCatalogParameterValue right
    ) => StringComparer.OrdinalIgnoreCase.Equals(left.StoreType, right.StoreType)
        && left.Value?.GetType() == right.Value?.GetType()
        && SafeMigrationModelManagedValue.AreEqual(left.Value, right.Value);

    public int GetHashCode(
        PostgreSqlCatalogParameterValue value
    )
    {
        var hash = new HashCode();
        hash.Add(value.StoreType, StringComparer.OrdinalIgnoreCase);
        hash.Add(value.Value?.GetType());

        if (value.Value is byte[] bytes)
        {
            foreach (var item in bytes)
            {
                hash.Add(item);
            }
        }
        else
        {
            hash.Add(value.Value);
        }

        return hash.ToHashCode();
    }
}
