namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Pairs provider-enriched forward and inverse EF data rows into immutable,
/// source-frozen SafeMigrations scaffolding operations.
/// </summary>
internal static class SafeMigrationModelManagedDataPairer
{
    public static IReadOnlyList<MigrationOperation> Pair(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<MigrationOperation> inverseOperations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(inverseOperations);

        var inverseRows = new InverseRowIndex(inverseOperations);
        var result = new List<MigrationOperation>(operations.Count);

        inverseRows.ConsumeRowsSubsumedByDroppedTables(operations);

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertDataOperation insert:
                    AddInsert(result, insert, inverseRows);
                    break;
                case UpdateDataOperation update:
                    AddUpdate(result, update, inverseRows);
                    break;
                case DeleteDataOperation delete:
                    AddDelete(result, delete, inverseRows);
                    break;
                default:
                    result.Add(operation);
                    break;
            }
        }

        if (inverseRows.HasUnconsumedRows)
        {
            throw new InvalidOperationException(
                "SafeMigrations could not pair every inverse model-managed-data row exactly once.");
        }

        SafeMigrationModelManagedDataContractValidator.Validate(result);

        return result.AsReadOnly();
    }

    private static List<DataRow> CreateRows(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var result = new List<DataRow>();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case InsertDataOperation insert:
                    ValidateOwnedShape(insert, insert.Values.GetLength(0));
                    AddRows(result, insert, insert.Values.GetLength(0));
                    break;
                case UpdateDataOperation update:
                    ValidateOwnedShape(update, update.KeyValues.GetLength(0));
                    AddRows(result, update, update.KeyValues.GetLength(0));
                    break;
                case DeleteDataOperation delete:
                    ValidateOwnedShape(delete, delete.KeyValues.GetLength(0));
                    AddRows(result, delete, delete.KeyValues.GetLength(0));
                    break;
            }
        }

        return result;
    }

    private static void AddInsert(
        List<MigrationOperation> result,
        InsertDataOperation operation,
        InverseRowIndex inverseRows
    )
    {
        ValidateOwnedShape(operation, operation.Values.GetLength(0));
        var metadata = SafeMigrationModelManagedDataMetadataStore.Get(operation);
        var columnTypes = RequiredTypes(operation.ColumnTypes, operation.Columns.Length, "insert");
        string[] keyColumns;
        string[] keyColumnTypes;

        if (inverseRows.HasDeleteRows(operation.Table, operation.Schema))
        {
            var matches = new DataRow[operation.Values.GetLength(0)];
            for (var row = 0; row < matches.Length; row++)
            {
                matches[row] = inverseRows.FindForInsert(operation, row);
            }

            var first = (DeleteDataOperation)matches[0].Operation;
            keyColumns = first.KeyColumns;
            keyColumnTypes = RequiredTypes(first.KeyColumnTypes, first.KeyColumns.Length, "inverse delete");
            foreach (var match in matches)
            {
                var delete = (DeleteDataOperation)match.Operation;
                if (!delete.KeyColumns.SequenceEqual(keyColumns, StringComparer.Ordinal)
                    || !RequiredTypes(delete.KeyColumnTypes, delete.KeyColumns.Length, "inverse delete")
                        .SequenceEqual(keyColumnTypes, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Inverse delete rows use inconsistent key columns or store types.");
                }

                match.Consumed = true;
            }
        }
        else
        {
            if (!inverseRows.DropsTable(operation.Table, operation.Schema))
            {
                throw new InvalidOperationException(
                    "SafeMigrations could not find exact inverse evidence for a model-managed insert.");
            }

            // EF omits inverse DeleteData operations when Down drops the whole
            // table. Only in that exact lifecycle may the target model supply
            // the source-frozen primary key needed by the convergent insert.
            keyColumns = RequiredIdentifiers(metadata.PrimaryKeyColumns, "insert primary key");
            keyColumnTypes = RequiredTypes(
                metadata.PrimaryKeyColumnTypes,
                keyColumns.Length,
                "insert primary key");
        }

        AddBatches(
            result,
            operation.Values.GetLength(0),
            keyColumns.Length + operation.Columns.Length,
            (offset, count) => new EnsureModelManagedDataScaffoldingOperation(
                new EnsureModelManagedDataIntent(
                    operation.Table,
                    keyColumns,
                    keyColumnTypes,
                    operation.Columns,
                    columnTypes,
                    Slice(operation.Values, offset, count),
                    operation.Schema,
                    metadata.UniqueKeys)));
    }

    private static void AddUpdate(
        List<MigrationOperation> result,
        UpdateDataOperation operation,
        InverseRowIndex inverseRows
    )
    {
        ValidateOwnedShape(operation, operation.KeyValues.GetLength(0));
        ValidateParallelRows(operation.KeyValues, operation.Values, "update");

        var metadata = SafeMigrationModelManagedDataMetadataStore.Get(operation);
        var keyColumnTypes = RequiredTypes(operation.KeyColumnTypes, operation.KeyColumns.Length, "update key");
        var columnTypes = RequiredTypes(operation.ColumnTypes, operation.Columns.Length, "update");
        var matches = new DataRow[operation.KeyValues.GetLength(0)];

        for (var row = 0; row < matches.Length; row++)
        {
            matches[row] = inverseRows.FindForUpdate(operation, row);
        }

        foreach (var match in matches)
        {
            var inverse = (UpdateDataOperation)match.Operation;
            ValidateParallelRows(inverse.KeyValues, inverse.Values, "inverse update");

            if (!inverse.Columns.SequenceEqual(operation.Columns, StringComparer.Ordinal)
                || !RequiredTypes(inverse.KeyColumnTypes, inverse.KeyColumns.Length, "inverse update key")
                    .SequenceEqual(keyColumnTypes, StringComparer.OrdinalIgnoreCase)
                || !RequiredTypes(inverse.ColumnTypes, inverse.Columns.Length, "inverse update")
                    .SequenceEqual(columnTypes, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Inverse update rows use inconsistent columns or store types.");
            }

            match.Consumed = true;
        }

        var oldValues = CopyRows(
            matches,
            static row => ((UpdateDataOperation)row.Operation).Values,
            operation.Columns.Length);

        AddBatches(
            result,
            operation.KeyValues.GetLength(0),
            operation.KeyColumns.Length + (operation.Columns.Length * 2),
            (offset, count) => new UpdateModelManagedDataScaffoldingOperation(
                new UpdateModelManagedDataIntent(
                    operation.Table,
                    operation.KeyColumns,
                    keyColumnTypes,
                    Slice(operation.KeyValues, offset, count),
                    operation.Columns,
                    columnTypes,
                    Slice(oldValues, offset, count),
                    Slice(operation.Values, offset, count),
                    operation.Schema,
                    metadata.UniqueKeys)));
    }

    private static void AddDelete(
        List<MigrationOperation> result,
        DeleteDataOperation operation,
        InverseRowIndex inverseRows
    )
    {
        ValidateOwnedShape(operation, operation.KeyValues.GetLength(0));
        var metadata = SafeMigrationModelManagedDataMetadataStore.Get(operation);
        var keyColumnTypes = RequiredTypes(operation.KeyColumnTypes, operation.KeyColumns.Length, "delete key");
        var matches = new DataRow[operation.KeyValues.GetLength(0)];

        for (var row = 0; row < matches.Length; row++)
        {
            matches[row] = inverseRows.FindForDelete(operation, row);
        }

        var first = (InsertDataOperation)matches[0].Operation;
        var columnTypes = RequiredTypes(first.ColumnTypes, first.Columns.Length, "inverse insert");
        foreach (var match in matches)
        {
            var insert = (InsertDataOperation)match.Operation;
            if (!insert.Columns.SequenceEqual(first.Columns, StringComparer.Ordinal)
                || !RequiredTypes(insert.ColumnTypes, insert.Columns.Length, "inverse insert")
                    .SequenceEqual(columnTypes, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Inverse insert rows use inconsistent columns or store types.");
            }

            match.Consumed = true;
        }

        var oldValues = CopyRows(
            matches,
            static row => ((InsertDataOperation)row.Operation).Values,
            first.Columns.Length);

        AddBatches(
            result,
            operation.KeyValues.GetLength(0),
            operation.KeyColumns.Length + first.Columns.Length,
            (offset, count) => new DeleteModelManagedDataScaffoldingOperation(
                new DeleteModelManagedDataIntent(
                    operation.Table,
                    operation.KeyColumns,
                    keyColumnTypes,
                    Slice(operation.KeyValues, offset, count),
                    first.Columns,
                    columnTypes,
                    Slice(oldValues, offset, count),
                    operation.Schema,
                    metadata.ForeignKeys)));
    }

    private static void AddBatches(
        List<MigrationOperation> result,
        int rowCount,
        int cellsPerRow,
        Func<int, int, MigrationOperation> create
    )
    {
        var cellBound = SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation / cellsPerRow;
        if (cellBound == 0)
        {
            throw new InvalidOperationException(
                "One model-managed row exceeds the maximum safe value-cell count.");
        }

        var batchSize = Math.Min(SafeMigrationModelManagedDataLimits.MaximumRowsPerOperation, cellBound);
        for (var offset = 0; offset < rowCount; offset += batchSize)
        {
            result.Add(create(offset, Math.Min(batchSize, rowCount - offset)));
        }
    }

    private static void AddRows<T>(
        List<DataRow> result,
        T operation,
        int rowCount
    )
        where T : MigrationOperation
    {
        for (var row = 0; row < rowCount; row++)
        {
            result.Add(new DataRow(operation, row));
        }
    }

    private static bool RowMatchesInsertKey(
        InsertDataOperation insert,
        int insertRow,
        DeleteDataOperation delete,
        int deleteRow
    )
    {
        for (var keyOrdinal = 0; keyOrdinal < delete.KeyColumns.Length; keyOrdinal++)
        {
            var insertOrdinal = Array.FindIndex(
                insert.Columns,
                column => StringComparer.Ordinal.Equals(column, delete.KeyColumns[keyOrdinal]));

            if (insertOrdinal < 0
                || !SafeMigrationModelManagedValue.AreEqual(
                    insert.Values[insertRow, insertOrdinal],
                    delete.KeyValues[deleteRow, keyOrdinal]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatrixRowEquals(
        object?[,] left,
        int leftRow,
        object?[,] right,
        int rightRow
    )
    {
        if (left.GetLength(1) != right.GetLength(1))
        {
            return false;
        }

        for (var column = 0; column < left.GetLength(1); column++)
        {
            if (!SafeMigrationModelManagedValue.AreEqual(left[leftRow, column], right[rightRow, column]))
            {
                return false;
            }
        }

        return true;
    }

    private static object?[,] CopyRows(
        IReadOnlyList<DataRow> rows,
        Func<DataRow, object?[,]> getValues,
        int columnCount
    )
    {
        var result = new object?[rows.Count, columnCount];
        for (var row = 0; row < rows.Count; row++)
        {
            var values = getValues(rows[row]);
            for (var column = 0; column < columnCount; column++)
            {
                result[row, column] = values[rows[row].Row, column];
            }
        }

        return result;
    }

    private static object?[,] Slice(
        object?[,] values,
        int offset,
        int count
    )
    {
        var result = new object?[count, values.GetLength(1)];
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < values.GetLength(1); column++)
            {
                result[row, column] = values[offset + row, column];
            }
        }

        return result;
    }

    private static string[] RequiredTypes(
        string[]? types,
        int expectedCount,
        string context
    )
    {
        if (types is null
            || types.Length != expectedCount
            || types.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"The {context} operation does not contain one exact store type per column.");
        }

        return types.ToArray();
    }

    private static string[] RequiredIdentifiers(
        string[]? values,
        string context
    )
    {
        if (values is null
            || values.Length == 0
            || values.Any(string.IsNullOrWhiteSpace)
            || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new InvalidOperationException(
                $"The {context} metadata does not contain a non-empty, unique identifier vector.");
        }

        return values.ToArray();
    }

    private static void ValidateParallelRows(
        object?[,] keys,
        object?[,] values,
        string context
    )
    {
        if (keys.GetLength(0) != values.GetLength(0))
        {
            throw new InvalidOperationException(
                $"The {context} key and value matrices contain different row counts.");
        }
    }

    private static void ValidateOwnedShape(
        MigrationOperation operation,
        int rowCount
    )
    {
        if (operation.GetAnnotations().Any())
        {
            throw new InvalidOperationException(
                "SafeMigrations cannot source-freeze an annotated model-managed-data operation.");
        }

        if (rowCount == 0)
        {
            throw new InvalidOperationException(
                "SafeMigrations cannot source-freeze an empty model-managed-data operation.");
        }
    }

    private static bool SameTable(
        string leftTable,
        string? leftSchema,
        string rightTable,
        string? rightSchema
    ) => StringComparer.Ordinal.Equals(leftTable, rightTable)
        && StringComparer.Ordinal.Equals(leftSchema, rightSchema);

    private sealed class DataRow(
        MigrationOperation operation,
        int row
    )
    {
        public MigrationOperation Operation { get; } = operation;

        public int Row { get; } = row;

        public bool Consumed { get; set; }
    }

    private sealed class InverseRowIndex
    {
        private readonly HashSet<TableIdentity> _droppedTables = [];
        private readonly Dictionary<TableIdentity, string[]> _deleteKeyShapes = [];
        private readonly Dictionary<string, List<DataRow>> _deleteRows = new(StringComparer.Ordinal);
        private readonly Dictionary<TableIdentity, List<DataRow>> _insertRows = [];
        private readonly Dictionary<LookupShape, Dictionary<string, List<DataRow>>> _insertRowsByKey = [];
        private readonly Dictionary<string, List<DataRow>> _updateRows = new(StringComparer.Ordinal);
        private readonly IReadOnlyList<DataRow> _rows;

        public InverseRowIndex(
            IReadOnlyList<MigrationOperation> operations
        )
        {
            var rows = CreateRows(operations);
            _rows = rows;

            foreach (var dropTable in operations.OfType<DropTableOperation>())
            {
                _droppedTables.Add(new TableIdentity(dropTable.Name, dropTable.Schema));
            }

            foreach (var row in rows)
            {
                switch (row.Operation)
                {
                    case InsertDataOperation insert:
                        Add(_insertRows, new TableIdentity(insert.Table, insert.Schema), row);
                        break;
                    case UpdateDataOperation update:
                        Add(
                            _updateRows,
                            Fingerprint(
                                update.Table,
                                update.Schema,
                                update.KeyColumns,
                                update.KeyValues,
                                row.Row),
                            row);
                        break;
                    case DeleteDataOperation delete:
                        AddDeleteRow(delete, row);
                        break;
                }
            }
        }

        public bool HasUnconsumedRows => _rows.Any(static row => !row.Consumed);

        public bool DropsTable(
            string table,
            string? schema
        ) => _droppedTables.Contains(new TableIdentity(table, schema));

        public bool HasDeleteRows(
            string table,
            string? schema
        ) => _deleteKeyShapes.ContainsKey(new TableIdentity(table, schema));

        public void ConsumeRowsSubsumedByDroppedTables(
            IReadOnlyList<MigrationOperation> operations
        )
        {
            var droppedTables = operations
                .OfType<DropTableOperation>()
                .Select(static operation => new TableIdentity(operation.Name, operation.Schema))
                .ToHashSet();

            if (droppedTables.Count == 0)
            {
                return;
            }

            foreach (var row in _rows)
            {
                var table = row.Operation switch
                {
                    InsertDataOperation insert => new TableIdentity(insert.Table, insert.Schema),
                    UpdateDataOperation update => new TableIdentity(update.Table, update.Schema),
                    DeleteDataOperation delete => new TableIdentity(delete.Table, delete.Schema),
                    _ => throw new UnreachableException(),
                };

                if (droppedTables.Contains(table))
                {
                    row.Consumed = true;
                }
            }
        }

        public DataRow FindForInsert(
            InsertDataOperation operation,
            int row
        )
        {
            var table = new TableIdentity(operation.Table, operation.Schema);
            if (!_deleteKeyShapes.TryGetValue(table, out var keyColumns))
            {
                return MissingInverse();
            }

            var keyOrdinals = KeyOrdinals(operation.Columns, keyColumns);
            var fingerprint = Fingerprint(
                operation.Table,
                operation.Schema,
                keyColumns,
                keyOrdinals.Length,
                column => operation.Values[row, keyOrdinals[column]]);

            return FindSingle(
                _deleteRows.GetValueOrDefault(fingerprint),
                candidate => candidate.Operation is DeleteDataOperation delete
                    && SameTable(operation.Table, operation.Schema, delete.Table, delete.Schema)
                    && RowMatchesInsertKey(operation, row, delete, candidate.Row));
        }

        public DataRow FindForUpdate(
            UpdateDataOperation operation,
            int row
        )
        {
            var fingerprint = Fingerprint(
                operation.Table,
                operation.Schema,
                operation.KeyColumns,
                operation.KeyValues,
                row);

            return FindSingle(
                _updateRows.GetValueOrDefault(fingerprint),
                candidate => candidate.Operation is UpdateDataOperation inverse
                    && SameTable(operation.Table, operation.Schema, inverse.Table, inverse.Schema)
                    && operation.KeyColumns.SequenceEqual(inverse.KeyColumns, StringComparer.Ordinal)
                    && MatrixRowEquals(operation.KeyValues, row, inverse.KeyValues, candidate.Row));
        }

        public DataRow FindForDelete(
            DeleteDataOperation operation,
            int row
        )
        {
            var shape = new LookupShape(
                new TableIdentity(operation.Table, operation.Schema),
                ColumnShape(operation.KeyColumns));

            if (!_insertRowsByKey.TryGetValue(shape, out var index))
            {
                index = BuildInsertIndex(shape.Table, operation.KeyColumns);
                _insertRowsByKey.Add(shape, index);
            }

            var fingerprint = Fingerprint(
                operation.Table,
                operation.Schema,
                operation.KeyColumns,
                operation.KeyValues,
                row);

            return FindSingle(
                index.GetValueOrDefault(fingerprint),
                candidate => candidate.Operation is InsertDataOperation insert
                    && SameTable(operation.Table, operation.Schema, insert.Table, insert.Schema)
                    && RowMatchesInsertKey(insert, candidate.Row, operation, row));
        }

        private void AddDeleteRow(
            DeleteDataOperation operation,
            DataRow row
        )
        {
            var table = new TableIdentity(operation.Table, operation.Schema);
            if (_deleteKeyShapes.TryGetValue(table, out var existingShape)
                && !existingShape.SequenceEqual(operation.KeyColumns, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Inverse delete rows for one table use inconsistent key columns.");
            }

            _deleteKeyShapes.TryAdd(table, operation.KeyColumns.ToArray());
            Add(
                _deleteRows,
                Fingerprint(
                    operation.Table,
                    operation.Schema,
                    operation.KeyColumns,
                    operation.KeyValues,
                    row.Row),
                row);
        }

        private Dictionary<string, List<DataRow>> BuildInsertIndex(
            TableIdentity table,
            string[] keyColumns
        )
        {
            var result = new Dictionary<string, List<DataRow>>(StringComparer.Ordinal);
            if (!_insertRows.TryGetValue(table, out var rows))
            {
                return result;
            }

            foreach (var row in rows)
            {
                var insert = (InsertDataOperation)row.Operation;
                var keyOrdinals = KeyOrdinals(insert.Columns, keyColumns);
                var fingerprint = Fingerprint(
                    insert.Table,
                    insert.Schema,
                    keyColumns,
                    keyOrdinals.Length,
                    column => insert.Values[row.Row, keyOrdinals[column]]);

                Add(result, fingerprint, row);
            }

            return result;
        }

        private static DataRow FindSingle(
            IReadOnlyList<DataRow>? candidates,
            Func<DataRow, bool> predicate
        )
        {
            // Canonical hashes only narrow the candidate set. The exact
            // predicate remains authoritative so a hash collision cannot pair
            // two different model-managed rows.
            DataRow? match = null;
            if (candidates is not null)
            {
                foreach (var candidate in candidates)
                {
                    if (candidate.Consumed || !predicate(candidate))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        throw new InvalidOperationException(
                            "SafeMigrations found more than one inverse row for one "
                            + "model-managed-data transition.");
                    }

                    match = candidate;
                }
            }

            return match ?? MissingInverse();
        }

        private static DataRow MissingInverse() => throw new InvalidOperationException(
            "SafeMigrations could not find the exact inverse row required for "
            + "model-managed-data convergence.");

        private static int[] KeyOrdinals(
            string[] columns,
            string[] keyColumns
        )
        {
            var result = new int[keyColumns.Length];
            for (var key = 0; key < keyColumns.Length; key++)
            {
                var ordinal = -1;
                for (var column = 0; column < columns.Length; column++)
                {
                    if (StringComparer.Ordinal.Equals(columns[column], keyColumns[key]))
                    {
                        ordinal = column;
                        break;
                    }
                }

                if (ordinal < 0)
                {
                    throw new InvalidOperationException(
                        $"An inverse insert row does not contain key column '{keyColumns[key]}'.");
                }

                result[key] = ordinal;
            }

            return result;
        }

        private static string Fingerprint(
            string table,
            string? schema,
            string[] columns,
            object?[,] values,
            int row
        ) => Fingerprint(
            table,
            schema,
            columns,
            columns.Length,
            column => values[row, column]);

        private static string Fingerprint(
            string table,
            string? schema,
            string[] columns,
            int valueCount,
            Func<int, object?> value
        )
        {
            using var writer = new CanonicalHashWriter();
            writer.Add(schema);
            writer.Add(table);
            writer.Add(columns.Length);
            for (var column = 0; column < columns.Length; column++)
            {
                writer.Add(columns[column]);
            }

            writer.Add(valueCount);
            for (var column = 0; column < valueCount; column++)
            {
                SafeMigrationModelManagedValue.Write(writer, value(column));
            }

            return writer.GetHash();
        }

        private static string ColumnShape(
            string[] columns
        )
        {
            using var writer = new CanonicalHashWriter();
            writer.Add(columns.Length);
            foreach (var column in columns)
            {
                writer.Add(column);
            }

            return writer.GetHash();
        }

        private static void Add<TKey>(
            Dictionary<TKey, List<DataRow>> index,
            TKey key,
            DataRow row
        )
            where TKey : notnull
        {
            if (!index.TryGetValue(key, out var rows))
            {
                rows = [];
                index.Add(key, rows);
            }

            rows.Add(row);
        }

        private readonly record struct TableIdentity(
            string Table,
            string? Schema
        );

        private readonly record struct LookupShape(
            TableIdentity Table,
            string Columns
        );
    }
}
