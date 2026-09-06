namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal readonly record struct MySqlCatalogParameterValue(
    object? Value,
    string? StoreType
);

internal static class MySqlCatalogTypeMapping
{
    public static RelationalTypeMapping Resolve(
        IRelationalTypeMappingSource typeMappingSource,
        object? value,
        string storeType
    )
    {
        ArgumentNullException.ThrowIfNull(typeMappingSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);

        // WHY: A store-type-only lookup selects the provider CLR type and can
        // reject model values such as char or converter-backed Guid. EF applies
        // the selected mapping's converter while creating literals/parameters,
        // so both halves of the relational type identity must select it.
        var mapping = value is null
            ? typeMappingSource.FindMapping(storeType)
            : typeMappingSource.FindMapping(value.GetType(), storeType);

        return mapping
            ?? throw new NotSupportedException(
                $"MySQL has no type mapping for CLR type '{value?.GetType().FullName ?? "<null>"}' "
                + $"and store type '{storeType}'.");
    }
}

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
