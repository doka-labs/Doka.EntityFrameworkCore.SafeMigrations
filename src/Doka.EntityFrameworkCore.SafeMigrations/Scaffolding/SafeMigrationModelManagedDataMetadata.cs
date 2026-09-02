namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed record SafeMigrationModelManagedDataMetadata(
    string[] PrimaryKeyColumns,
    string[] PrimaryKeyColumnTypes,
    ExpectedModelManagedDataUniqueKeyDefinition[] UniqueKeys,
    ExpectedModelManagedDataForeignKeyDefinition[] ForeignKeys
);

internal static class SafeMigrationModelManagedDataMetadataStore
{
    private static readonly ConditionalWeakTable<MigrationOperation, SafeMigrationModelManagedDataMetadata> s_metadata =
        new();

    public static void Set(
        MigrationOperation operation,
        SafeMigrationModelManagedDataMetadata metadata
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(metadata);

        s_metadata.Remove(operation);
        s_metadata.Add(operation, metadata);
    }

    public static SafeMigrationModelManagedDataMetadata Get(
        MigrationOperation operation
    ) => s_metadata.TryGetValue(operation, out var metadata)
        ? metadata
        : new SafeMigrationModelManagedDataMetadata([], [], [], []);
}
