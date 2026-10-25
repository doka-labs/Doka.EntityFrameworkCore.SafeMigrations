namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Pairs provider-enriched forward and inverse EF data rows into immutable,
/// source-frozen SafeMigrations scaffolding operations.
/// </summary>
internal static partial class SafeMigrationModelManagedDataPairer
{
    /// <summary>Pairs forward and inverse model-managed rows before migration source generation.</summary>
    /// <param name="operations">The provider-enriched forward operations in execution order.</param>
    /// <param name="inverseOperations">The provider-enriched inverse operations from the same model difference.</param>
    /// <returns>The source-frozen paired operations in their original forward order.</returns>
    public static IReadOnlyList<MigrationOperation> Pair(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<MigrationOperation> inverseOperations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(inverseOperations);

        var inverseRows = new InverseRowIndex(inverseOperations);
        var columnTransitions = new ColumnTransitionIndex(operations);
        var result = new List<MigrationOperation>(operations.Count);

        inverseRows.ConsumeRowsSubsumedByDroppedTables(operations);

        foreach (var operation in operations)
        {
            columnTransitions.Observe(operation);

            switch (operation)
            {
                case InsertDataOperation insert:
                    AddInsert(result, insert, inverseRows);
                    break;
                case UpdateDataOperation update:
                    AddUpdate(result, update, inverseRows, columnTransitions);
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
                "SafeMigrations could not pair every inverse model-managed-data row exactly once. "
                + "Scaffold both Up and Down from the same provider model difference and retain their "
                + "inverse data operations; row values were not included in this diagnostic.");
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
            // WHY: Store each inverse insert match at the corresponding authored row ordinal.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
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
        InverseRowIndex inverseRows,
        ColumnTransitionIndex columnTransitions
    )
    {
        ValidateOwnedShape(operation, operation.KeyValues.GetLength(0));
        ValidateParallelRows(operation.KeyValues, operation.Values, "update");

        var metadata = SafeMigrationModelManagedDataMetadataStore.Get(operation);
        var keyColumnTypes = RequiredTypes(operation.KeyColumnTypes, operation.KeyColumns.Length, "update key");
        var columnTypes = RequiredTypes(operation.ColumnTypes, operation.Columns.Length, "update");
        var matches = new DataRow[operation.KeyValues.GetLength(0)];

        // WHY: Store each inverse update match at the corresponding authored row ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < matches.Length; row++)
        {
            matches[row] = inverseRows.FindForUpdate(operation, row);
        }

        var firstInverse = (UpdateDataOperation)matches[0].Operation;
        var inverseColumnTypes = RequiredTypes(
            firstInverse.ColumnTypes,
            firstInverse.Columns.Length,
            "inverse update");

        var inverseOrdinals = ColumnOrdinals(firstInverse.Columns);
        var oldValues = new object?[matches.Length, operation.Columns.Length];
        // WHY: Keep authored column types aligned while resolving inverse-operation column ordinals.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var column = 0; column < operation.Columns.Length; column++)
        {
            var columnName = operation.Columns[column];
            if (inverseOrdinals.TryGetValue(columnName, out var inverseOrdinal))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        inverseColumnTypes[inverseOrdinal],
                        columnTypes[column]))
                {
                    throw new InvalidOperationException(
                        $"Inverse update column '{SafeMigrationDiagnosticMessages.BoundForDisplay(columnName)}' "
                        + "uses an inconsistent store type.");
                }

                // WHY: Read matched inverse rows into the same target row and authored column coordinates.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
                for (var row = 0; row < matches.Length; row++)
                {
                    var inverse = (UpdateDataOperation)matches[row].Operation;
                    oldValues[row, column] = inverse.Values[matches[row].Row, inverseOrdinal];
                }

                continue;
            }

            // EF omits a newly mapped column from the inverse data operation.
            // Only the preceding AddColumn operation can prove the value that
            // existing rows hold before the forward data update executes.
            var initialValue = columnTransitions.AddedColumnInitialValue(
                operation,
                columnName,
                columnTypes[column]);

            // WHY: Backfill the selected target column for every authored row at its original ordinal.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var row = 0; row < matches.Length; row++)
            {
                oldValues[row, column] = initialValue;
            }
        }

        foreach (var inverseColumn in firstInverse.Columns)
        {
            if (!operation.Columns.Contains(inverseColumn, StringComparer.Ordinal))
            {
                columnTransitions.RequireDrop(operation, inverseColumn);
            }
        }

        foreach (var match in matches)
        {
            var inverse = (UpdateDataOperation)match.Operation;
            ValidateParallelRows(inverse.KeyValues, inverse.Values, "inverse update");

            if (!inverse.Columns.SequenceEqual(firstInverse.Columns, StringComparer.Ordinal)
                || !RequiredTypes(inverse.ColumnTypes, inverse.Columns.Length, "inverse update")
                    .SequenceEqual(inverseColumnTypes, StringComparer.OrdinalIgnoreCase)
                || !RequiredTypes(inverse.KeyColumnTypes, inverse.KeyColumns.Length, "inverse update key")
                    .SequenceEqual(keyColumnTypes, StringComparer.OrdinalIgnoreCase)
                || !inverse.KeyColumns.SequenceEqual(operation.KeyColumns, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Inverse update rows use inconsistent columns, keys, or store types.");
            }

            match.Consumed = true;
        }

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

        // WHY: Store each inverse delete match at the corresponding authored row ordinal.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Advance by bounded batch windows while retaining the final partial batch.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Retain each source matrix row ordinal in its lightweight inverse-row descriptor.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Pair each delete-key value with the matching insert column instead of assuming equal layouts.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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

        // WHY: Compare corresponding matrix cells without changing either row's column order.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Place each selected source row into its exact destination row coordinate.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < rows.Count; row++)
        {
            var values = getValues(rows[row]);
            // WHY: Copy matching column coordinates while resolving the selected source row separately.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
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
        // WHY: Map each batch-local row back to the original matrix through its source offset.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < count; row++)
        {
            // WHY: Preserve column coordinates while copying the selected row window.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < values.GetLength(1); column++)
            {
                result[row, column] = values[offset + row, column];
            }
        }

        return result;
    }

    private static Dictionary<string, int> ColumnOrdinals(
        string[] columns
    )
    {
        var result = new Dictionary<string, int>(columns.Length, StringComparer.Ordinal);
        // WHY: Record the authored column ordinal needed to interpret inverse update matrices.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 0; ordinal < columns.Length; ordinal++)
        {
            if (!result.TryAdd(columns[ordinal], ordinal))
            {
                throw new InvalidOperationException(
                    $"An inverse update repeats column "
                    + $"'{SafeMigrationDiagnosticMessages.BoundForDisplay(columns[ordinal])}'.");
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
}
