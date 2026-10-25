namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationModelManagedDataPairer
{
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
                            Fingerprint(update.Table, update.Schema, update.KeyColumns, update.KeyValues, row.Row),
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
                throw new InvalidOperationException("Inverse delete rows for one table use inconsistent key columns.");
            }

            _deleteKeyShapes.TryAdd(table, operation.KeyColumns.ToArray());
            Add(
                _deleteRows,
                Fingerprint(operation.Table, operation.Schema, operation.KeyColumns, operation.KeyValues, row.Row),
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
                    if (candidate.Consumed
                        || !predicate(candidate))
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
            + "model-managed-data convergence. Preserve the provider-generated inverse data operation, "
            + "or author an explicit source-frozen contract; row values were not included in this diagnostic.");

        private static int[] KeyOrdinals(
            string[] columns,
            string[] keyColumns
        )
        {
            var result = new int[keyColumns.Length];
            // WHY: Resolve one source-matrix ordinal for each ordered key column.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var key = 0; key < keyColumns.Length; key++)
            {
                var ordinal = -1;
                // WHY: Retain the matching source column ordinal instead of assuming key and row layouts coincide.
                // ReSharper disable once ForCanBeConvertedToForeach
                // ReSharper disable once LoopCanBeConvertedToQuery
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
                        "An inverse insert row does not contain key column "
                        + $"'{SafeMigrationDiagnosticMessages.BoundForDisplay(keyColumns[key])}'.");
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
        ) => Fingerprint(table, schema, columns, columns.Length, column => values[row, column]);

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
            foreach (var t in columns)
            {
                writer.Add(t);
            }

            writer.Add(valueCount);
            // WHY: Read delegate-supplied values in the exact ordinal order of the canonical fingerprint.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
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
