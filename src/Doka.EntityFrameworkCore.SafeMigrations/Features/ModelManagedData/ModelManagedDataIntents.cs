namespace Doka.EntityFrameworkCore.SafeMigrations;

internal abstract class ModelManagedDataIntent : SafeMigrationIntent
{
    protected ModelManagedDataIntent(
        SafeMigrationOperationKind kind,
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues,
        string[] columns,
        string[] columnTypes,
        string? schema
    ) : base(kind)
    {
        Table = SafeMigrationDefinitionValidator.Required(table, nameof(table));
        Schema = SafeMigrationDefinitionValidator.Optional(schema, nameof(schema));
        KeyColumns = SnapshotIdentifiers(keyColumns, nameof(keyColumns));
        KeyColumnTypes = SnapshotTypes(keyColumnTypes, KeyColumns.Count, nameof(keyColumnTypes));
        Columns = SnapshotIdentifiers(columns, nameof(columns));
        ColumnTypes = SnapshotTypes(columnTypes, Columns.Count, nameof(columnTypes));
        KeyValues = SnapshotMatrix(keyValues, KeyColumns.Count, nameof(keyValues));

        if (KeyValues.RowCount == 0)
        {
            throw new ArgumentException("At least one model-managed row is required.", nameof(keyValues));
        }

        ValidateSharedColumnTypes();
        ValidateNonNullKeys();
        ValidateDistinctKeys();
    }

    public string Table { get; }

    public string? Schema { get; }

    public IReadOnlyList<string> KeyColumns { get; }

    public IReadOnlyList<string> KeyColumnTypes { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<string> ColumnTypes { get; }

    public ModelManagedDataMatrix KeyValues { get; }

    public int RowCount => KeyValues.RowCount;

    public override string ObjectName => Table;

    protected static ModelManagedDataMatrix SnapshotMatrix(
        object?[,] values,
        int expectedColumns,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.GetLength(1) != expectedColumns)
        {
            throw new ArgumentException(
                "The value matrix must contain one column per declared column.",
                parameterName);
        }

        return new ModelManagedDataMatrix(values);
    }

    protected void ValidateBatchSize(
        params ReadOnlySpan<ModelManagedDataMatrix> matrices
    )
    {
        if (RowCount > SafeMigrationModelManagedDataLimits.MaximumRowsPerOperation)
        {
            throw new ArgumentException(
                $"A model-managed-data operation cannot contain more than "
                + $"{SafeMigrationModelManagedDataLimits.MaximumRowsPerOperation} rows.");
        }

        var cells = 0;
        foreach (var matrix in matrices)
        {
            cells = checked(cells + matrix.CellCount);
        }

        if (cells > SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation)
        {
            throw new ArgumentException(
                $"A model-managed-data operation cannot contain more than "
                + $"{SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation} value cells.");
        }
    }

    protected static IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> SnapshotUniqueKeys(
        ExpectedModelManagedDataUniqueKeyDefinition[]? definitions,
        IReadOnlyList<string> columns
    )
    {
        var snapshot = SafeMigrationDefinitionValidator.Definitions(
            definitions ?? [],
            nameof(definitions));

        foreach (var definition in snapshot)
        {
            if (definition.Columns.Any(column => !columns.Contains(column, StringComparer.Ordinal)))
            {
                throw new ArgumentException(
                    "Every candidate-key column must be present in the managed columns.",
                    nameof(definitions));
            }
        }

        return snapshot;
    }

    protected static void ValidateDistinctUniqueTargets(
        IReadOnlyList<string> columns,
        ModelManagedDataMatrix values,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        foreach (var uniqueKey in uniqueKeys)
        {
            var ordinals = uniqueKey.Columns.Select(column => ColumnOrdinal(columns, column)).ToArray();
            for (var leftRow = 0; leftRow < values.RowCount; leftRow++)
            {
                if (ordinals.Any(ordinal => values.GetUnsafeValue(leftRow, ordinal) is null))
                {
                    continue;
                }

                for (var rightRow = leftRow + 1; rightRow < values.RowCount; rightRow++)
                {
                    if (ordinals.All(ordinal => SafeMigrationModelManagedValue.AreEqual(
                            values.GetUnsafeValue(leftRow, ordinal),
                            values.GetUnsafeValue(rightRow, ordinal))))
                    {
                        throw new ArgumentException(
                            "A model-managed-data operation contains duplicate non-null candidate-key values.",
                            nameof(values));
                    }
                }
            }
        }
    }

    private static int ColumnOrdinal(
        IReadOnlyList<string> columns,
        string column
    )
    {
        for (var ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            if (StringComparer.Ordinal.Equals(columns[ordinal], column))
            {
                return ordinal;
            }
        }

        throw new UnreachableException();
    }

    private static IReadOnlyList<string> SnapshotIdentifiers(
        string[] values,
        string parameterName
    ) => SafeMigrationDefinitionValidator.Identifiers(values, parameterName);

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> SnapshotTypes(
        string[] values,
        int expectedCount,
        string parameterName
    )
    {
        ArgumentNullException.ThrowIfNull(values);

        var snapshot = values.ToArray();
        if (snapshot.Length != expectedCount)
        {
            throw new ArgumentException(
                "The store-type list must contain one entry per declared column.",
                parameterName);
        }

        foreach (var value in snapshot)
        {
            _ = SafeMigrationDefinitionValidator.Required(value, parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }

    private void ValidateSharedColumnTypes()
    {
        for (var keyOrdinal = 0; keyOrdinal < KeyColumns.Count; keyOrdinal++)
        {
            var columnOrdinal = -1;
            for (var candidateOrdinal = 0; candidateOrdinal < Columns.Count; candidateOrdinal++)
            {
                if (StringComparer.Ordinal.Equals(Columns[candidateOrdinal], KeyColumns[keyOrdinal]))
                {
                    columnOrdinal = candidateOrdinal;
                    break;
                }
            }

            if (columnOrdinal >= 0
                && !StringComparer.OrdinalIgnoreCase.Equals(
                    KeyColumnTypes[keyOrdinal],
                    ColumnTypes[columnOrdinal]))
            {
                throw new ArgumentException(
                    $"Key column '{KeyColumns[keyOrdinal]}' has conflicting store types.",
                    nameof(KeyColumnTypes));
            }
        }
    }

    private void ValidateNonNullKeys()
    {
        for (var row = 0; row < KeyValues.RowCount; row++)
        {
            for (var column = 0; column < KeyValues.ColumnCount; column++)
            {
                if (KeyValues.GetUnsafeValue(row, column) is null)
                {
                    throw new ArgumentException(
                        "A model-managed primary-key value cannot be null.",
                        nameof(KeyValues));
                }
            }
        }
    }

    private void ValidateDistinctKeys()
    {
        for (var leftRow = 0; leftRow < KeyValues.RowCount; leftRow++)
        {
            for (var rightRow = leftRow + 1; rightRow < KeyValues.RowCount; rightRow++)
            {
                var equal = true;
                for (var column = 0; column < KeyValues.ColumnCount; column++)
                {
                    if (SafeMigrationModelManagedValue.AreEqual(
                            KeyValues.GetUnsafeValue(leftRow, column),
                            KeyValues.GetUnsafeValue(rightRow, column)))
                    {
                        continue;
                    }

                    equal = false;
                    break;
                }

                if (equal)
                {
                    throw new ArgumentException(
                        "A model-managed-data operation cannot contain the same typed key more than once.",
                        nameof(KeyValues));
                }
            }
        }
    }
}

internal sealed class EnsureModelManagedDataIntent : ModelManagedDataIntent
{
    public EnsureModelManagedDataIntent(
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        string[] columns,
        string[] columnTypes,
        object?[,] values,
        string? schema,
        ExpectedModelManagedDataUniqueKeyDefinition[]? uniqueKeys
    ) : base(
        SafeMigrationOperationKind.EnsureModelManagedData,
        table,
        keyColumns,
        keyColumnTypes,
        ExtractKeyValues(keyColumns, columns, values),
        columns,
        columnTypes,
        schema)
    {
        Values = SnapshotMatrix(values, Columns.Count, nameof(values));
        UniqueKeys = SnapshotUniqueKeys(uniqueKeys, Columns);

        ValidateDistinctUniqueTargets(Columns, Values, UniqueKeys);
        ValidateBatchSize(KeyValues, Values);
    }

    public ModelManagedDataMatrix Values { get; }

    public IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> UniqueKeys { get; }

    private static object?[,] ExtractKeyValues(
        string[] keyColumns,
        string[] columns,
        object?[,] values
    )
    {
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);

        var ordinals = keyColumns
            .Select(keyColumn => Array.FindIndex(columns, column => StringComparer.Ordinal.Equals(column, keyColumn)))
            .ToArray();

        if (ordinals.Any(static ordinal => ordinal < 0))
        {
            throw new ArgumentException(
                "Every key column must be present in the managed columns.",
                nameof(keyColumns));
        }

        var result = new object?[values.GetLength(0), keyColumns.Length];
        for (var row = 0; row < values.GetLength(0); row++)
        {
            for (var column = 0; column < ordinals.Length; column++)
            {
                result[row, column] = values[row, ordinals[column]];
            }
        }

        return result;
    }
}

internal sealed class UpdateModelManagedDataIntent : ModelManagedDataIntent
{
    public UpdateModelManagedDataIntent(
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues,
        string[] columns,
        string[] columnTypes,
        object?[,] oldValues,
        object?[,] newValues,
        string? schema,
        ExpectedModelManagedDataUniqueKeyDefinition[]? uniqueKeys
    ) : base(
        SafeMigrationOperationKind.UpdateModelManagedData,
        table,
        keyColumns,
        keyColumnTypes,
        keyValues,
        columns,
        columnTypes,
        schema)
    {
        OldValues = SnapshotMatrix(oldValues, Columns.Count, nameof(oldValues));
        NewValues = SnapshotMatrix(newValues, Columns.Count, nameof(newValues));
        UniqueKeys = SnapshotUniqueKeys(uniqueKeys, Columns);

        ValidateEqualRows(OldValues, nameof(oldValues));
        ValidateEqualRows(NewValues, nameof(newValues));
        ValidateDistinctUniqueTargets(Columns, NewValues, UniqueKeys);
        ValidateBatchSize(KeyValues, OldValues, NewValues);
    }

    public ModelManagedDataMatrix OldValues { get; }

    public ModelManagedDataMatrix NewValues { get; }

    public IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> UniqueKeys { get; }

    private void ValidateEqualRows(
        ModelManagedDataMatrix values,
        string parameterName
    )
    {
        if (values.RowCount != RowCount)
        {
            throw new ArgumentException(
                "Every model-managed matrix must contain the same number of rows.",
                parameterName);
        }
    }
}

internal sealed class DeleteModelManagedDataIntent : ModelManagedDataIntent
{
    public DeleteModelManagedDataIntent(
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues,
        string[] columns,
        string[] columnTypes,
        object?[,] oldValues,
        string? schema,
        ExpectedModelManagedDataForeignKeyDefinition[]? foreignKeys
    ) : base(
        SafeMigrationOperationKind.DeleteModelManagedData,
        table,
        keyColumns,
        keyColumnTypes,
        keyValues,
        columns,
        columnTypes,
        schema)
    {
        OldValues = SnapshotMatrix(oldValues, Columns.Count, nameof(oldValues));
        ForeignKeys = SafeMigrationDefinitionValidator.Definitions(foreignKeys ?? [], nameof(foreignKeys));

        if (OldValues.RowCount != RowCount)
        {
            throw new ArgumentException(
                "Every model-managed matrix must contain the same number of rows.",
                nameof(oldValues));
        }

        foreach (var foreignKey in ForeignKeys)
        {
            if (foreignKey.PrincipalColumns.Any(column => !Columns.Contains(column, StringComparer.Ordinal)))
            {
                throw new ArgumentException(
                    "Every principal foreign-key column must be present in the captured source columns.",
                    nameof(foreignKeys));
            }
        }

        ValidateBatchSize(KeyValues, OldValues);
    }

    public ModelManagedDataMatrix OldValues { get; }

    public IReadOnlyList<ExpectedModelManagedDataForeignKeyDefinition> ForeignKeys { get; }
}

internal sealed class ModelManagedDataMatrix
{
    private readonly object?[] _values;

    public ModelManagedDataMatrix(
        object?[,] values
    )
    {
        RowCount = values.GetLength(0);
        ColumnCount = values.GetLength(1);
        _values = new object?[checked(RowCount * ColumnCount)];

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                _values[(row * ColumnCount) + column] = SafeMigrationModelManagedValue.Clone(values[row, column]);
            }
        }
    }

    public int RowCount { get; }

    public int ColumnCount { get; }

    public int CellCount => _values.Length;

    public object? GetValue(
        int row,
        int column
    ) => SafeMigrationModelManagedValue.Clone(_values[checked((row * ColumnCount) + column)]);

    public object? GetUnsafeValue(
        int row,
        int column
    ) => _values[checked((row * ColumnCount) + column)];
}
