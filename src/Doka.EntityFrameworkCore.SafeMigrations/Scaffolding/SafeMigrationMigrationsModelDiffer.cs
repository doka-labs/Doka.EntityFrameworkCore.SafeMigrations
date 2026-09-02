namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Decorates the active provider differ and captures only the relational model
/// facts required to source-freeze model-managed data safely.
/// </summary>
internal sealed class SafeMigrationMigrationsModelDiffer : IMigrationsModelDiffer
{
    private readonly IMigrationsModelDiffer _providerDiffer;

    public SafeMigrationMigrationsModelDiffer(
        IMigrationsModelDiffer providerDiffer
    )
    {
        ArgumentNullException.ThrowIfNull(providerDiffer);

        _providerDiffer = providerDiffer;
    }

    public bool HasDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    ) => _providerDiffer.HasDifferences(source, target);

    public IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = _providerDiffer.GetDifferences(source, target);
        foreach (var operation in operations)
        {
            Enrich(operation, source, target);
        }

        return operations;
    }

    private static void Enrich(
        MigrationOperation operation,
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        switch (operation)
        {
            case InsertDataOperation insert:
            {
                var table = RequiredTable(target, insert.Table, insert.Schema, "insert target");
                insert.ColumnTypes = CompleteTypes(insert.Columns, insert.ColumnTypes, table, "insert");
                var primaryKey = RequiredPrimaryKey(table, insert.Columns);

                SafeMigrationModelManagedDataMetadataStore.Set(
                    insert,
                    new SafeMigrationModelManagedDataMetadata(
                        primaryKey.Columns,
                        primaryKey.ColumnTypes,
                        UniqueKeys(table, insert.Columns),
                        []));
                break;
            }

            case UpdateDataOperation update:
            {
                var sourceTable = RequiredTable(source, update.Table, update.Schema, "update source");
                var targetTable = RequiredTable(target, update.Table, update.Schema, "update target");
                update.KeyColumnTypes = CompleteTypes(
                    update.KeyColumns,
                    update.KeyColumnTypes,
                    sourceTable,
                    "update key");
                update.ColumnTypes = CompleteTypes(
                    update.Columns,
                    update.ColumnTypes,
                    targetTable,
                    "update target");

                ValidateTypes(update.KeyColumns, update.KeyColumnTypes, targetTable, "update target key");
                ValidateTypes(update.Columns, update.ColumnTypes, sourceTable, "update source");
                SafeMigrationModelManagedDataMetadataStore.Set(
                    update,
                    new SafeMigrationModelManagedDataMetadata(
                        [],
                        [],
                        UniqueKeys(targetTable, update.Columns),
                        []));
                break;
            }

            case DeleteDataOperation delete:
            {
                var table = RequiredTable(source, delete.Table, delete.Schema, "delete source");
                delete.KeyColumnTypes = CompleteTypes(delete.KeyColumns, delete.KeyColumnTypes, table, "delete key");
                SafeMigrationModelManagedDataMetadataStore.Set(
                    delete,
                    new SafeMigrationModelManagedDataMetadata(
                        [],
                        [],
                        [],
                        IncomingForeignKeys(source!, table)));
                break;
            }
        }
    }

    private static ITable RequiredTable(
        IRelationalModel? model,
        string table,
        string? schema,
        string context
    )
    {
        var match = model?.Tables.SingleOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Name, table)
            && StringComparer.Ordinal.Equals(candidate.Schema, schema));

        return match
            ?? throw new InvalidOperationException(
                $"SafeMigrations could not resolve the {context} table in the relational model.");
    }

    private static string[] CompleteTypes(
        string[] columns,
        string[]? existingTypes,
        ITable table,
        string context
    )
    {
        if (existingTypes is { Length: > 0 })
        {
            if (existingTypes.Length != columns.Length
                || existingTypes.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"The provider emitted an incomplete {context} store-type vector.");
            }

            ValidateTypes(columns, existingTypes, table, context);

            return existingTypes.ToArray();
        }

        return columns
            .Select(column => table.Columns.SingleOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Name, column))
                ?.StoreType
                ?? throw new InvalidOperationException(
                    $"SafeMigrations could not resolve {context} column '{column}' in the relational model."))
            .ToArray();
    }

    private static void ValidateTypes(
        string[] columns,
        string[] types,
        ITable table,
        string context
    )
    {
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            var modelType = table.Columns.SingleOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Name, columns[ordinal]))
                ?.StoreType
                ?? throw new InvalidOperationException(
                    $"SafeMigrations could not resolve {context} column '{columns[ordinal]}' "
                    + "in the relational model.");

            if (!StringComparer.OrdinalIgnoreCase.Equals(types[ordinal], modelType))
            {
                throw new InvalidOperationException(
                    $"The provider-emitted {context} store type for column '{columns[ordinal]}' "
                    + "does not match the relational model.");
            }
        }
    }

    private static ExpectedModelManagedDataUniqueKeyDefinition[] UniqueKeys(
        ITable table,
        string[] managedColumns
    )
    {
        var keys = new List<ExpectedModelManagedDataUniqueKeyDefinition>();
        foreach (var constraint in table.UniqueConstraints)
        {
            if (ReferenceEquals(constraint, table.PrimaryKey))
            {
                continue;
            }

            AddUniqueKey(keys, constraint.Columns.Select(static column => column.Name).ToArray(), managedColumns);
        }

        foreach (var index in table.Indexes.Where(static index => index.IsUnique))
        {
            AddUniqueKey(keys, index.Columns.Select(static column => column.Name).ToArray(), managedColumns);
        }

        return keys
            .DistinctBy(static key => string.Join("\u001f", key.Columns), StringComparer.Ordinal)
            .ToArray();
    }

    private static (string[] Columns, string[] ColumnTypes) RequiredPrimaryKey(
        ITable table,
        string[] managedColumns
    )
    {
        var primaryKey = table.PrimaryKey
            ?? throw new InvalidOperationException(
                "SafeMigrations cannot source-freeze model-managed inserts for a table without a primary key.");

        var columns = primaryKey.Columns.Select(static column => column.Name).ToArray();

        if (columns.Any(column => !managedColumns.Contains(column, StringComparer.Ordinal)))
        {
            throw new InvalidOperationException(
                "A model-managed insert does not contain every primary-key column from the relational model.");
        }

        return (
            columns,
            primaryKey.Columns.Select(static column => column.StoreType).ToArray());
    }

    private static void AddUniqueKey(
        List<ExpectedModelManagedDataUniqueKeyDefinition> keys,
        string[] columns,
        string[] managedColumns
    )
    {
        if (columns.All(column => managedColumns.Contains(column, StringComparer.Ordinal)))
        {
            keys.Add(new ExpectedModelManagedDataUniqueKeyDefinition(columns));
        }
    }

    private static ExpectedModelManagedDataForeignKeyDefinition[] IncomingForeignKeys(
        IRelationalModel source,
        ITable principalTable
    ) => source
        .Tables
        .SelectMany(static table => table.ForeignKeyConstraints)
        .Where(foreignKey => ReferenceEquals(foreignKey.PrincipalTable, principalTable)
            || (StringComparer.Ordinal.Equals(foreignKey.PrincipalTable.Name, principalTable.Name)
                && StringComparer.Ordinal.Equals(foreignKey.PrincipalTable.Schema, principalTable.Schema)))
        .Select(foreignKey => new ExpectedModelManagedDataForeignKeyDefinition(
            foreignKey.Table.Name,
            foreignKey.Columns.Select(static column => column.Name),
            foreignKey.PrincipalColumns.Select(static column => column.Name),
            foreignKey.Table.Schema))
        .ToArray();
}
