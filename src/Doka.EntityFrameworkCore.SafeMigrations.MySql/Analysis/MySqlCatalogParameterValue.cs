namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal readonly record struct MySqlCatalogParameterValue(
    object? Value,
    string? StoreType
);

internal sealed class MySqlCatalogParameterValueComparer : IEqualityComparer<MySqlCatalogParameterValue>
{
    public static MySqlCatalogParameterValueComparer Instance { get; } = new();

    public bool Equals(
        MySqlCatalogParameterValue left,
        MySqlCatalogParameterValue right
    ) => StringComparer.OrdinalIgnoreCase.Equals(left.StoreType, right.StoreType)
        && (left.Value?.GetType() == right.Value?.GetType())
        && SafeMigrationModelManagedValue.AreEqual(left.Value, right.Value);

    public int GetHashCode(
        MySqlCatalogParameterValue value
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
