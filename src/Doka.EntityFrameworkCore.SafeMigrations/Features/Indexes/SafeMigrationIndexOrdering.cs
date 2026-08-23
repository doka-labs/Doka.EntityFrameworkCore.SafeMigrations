namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Defines the requested sort direction of an index key.</summary>
public enum SafeMigrationIndexSortOrder
{
    /// <summary>Use and verify the provider's effective default direction.</summary>
    ProviderDefault = 0,

    /// <summary>Sort the key in ascending order.</summary>
    Ascending = 1,

    /// <summary>Sort the key in descending order.</summary>
    Descending = 2,
}

/// <summary>Defines the requested null placement of an ordered index key.</summary>
public enum SafeMigrationIndexNullOrder
{
    /// <summary>Use and verify the provider's effective default null placement.</summary>
    ProviderDefault = 0,

    /// <summary>Place null values before non-null values.</summary>
    First = 1,

    /// <summary>Place null values after non-null values.</summary>
    Last = 2,
}
