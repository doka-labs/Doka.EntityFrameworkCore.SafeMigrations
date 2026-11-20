namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static partial class SafeMigrationModelManagedDataPairer
{
    private sealed class ColumnTransitionIndex
    {
        private readonly Dictionary<ColumnIdentity, (AddColumnOperation Operation, bool Ambiguous)> _added = [];
        private readonly HashSet<ColumnIdentity> _dropped = [];

        public ColumnTransitionIndex(
            IReadOnlyList<MigrationOperation> operations
        )
        {
            foreach (var operation in operations)
            {
                if (operation is DropColumnOperation drop)
                {
                    _dropped.Add(Identity(drop.Table, drop.Schema, drop.Name));
                }
            }
        }

        public void Observe(
            MigrationOperation operation
        )
        {
            switch (operation)
            {
                case AddColumnOperation add:
                    {
                        var identity = Identity(add.Table, add.Schema, add.Name);
                        var ambiguous = _added.ContainsKey(identity);
                        _added[identity] = (add, ambiguous);
                        break;
                    }

                case DropColumnOperation drop:
                    _added.Remove(Identity(drop.Table, drop.Schema, drop.Name));
                    break;
            }
        }

        public object? AddedColumnInitialValue(
            UpdateDataOperation update,
            string column,
            string expectedStoreType
        )
        {
            var identity = Identity(update.Table, update.Schema, column);
            if (!_added.TryGetValue(identity, out var addition)
                || addition.Ambiguous)
            {
                throw new InvalidOperationException(
                    $"Model-managed update column '{SafeMigrationDiagnosticMessages.BoundForDisplay(column)}' "
                    + "has no inverse value or preceding column addition. Keep the provider-generated inverse "
                    + "operation, or supply an explicit source-frozen data contract.");
            }

            var operation = addition.Operation;
            if (operation.ColumnType is not null
                && !StringComparer.OrdinalIgnoreCase.Equals(operation.ColumnType, expectedStoreType))
            {
                throw new InvalidOperationException(
                    $"Added model-managed column '{SafeMigrationDiagnosticMessages.BoundForDisplay(column)}' "
                    + "uses an inconsistent store type.");
            }

            if (operation.ComputedColumnSql is not null
                || operation.DefaultValueSql is not null)
            {
                throw new InvalidOperationException(
                    $"Added model-managed column '{SafeMigrationDiagnosticMessages.BoundForDisplay(column)}' "
                    + "has no deterministic source-frozen initial value. Supply an explicit data transition "
                    + "rather than deriving old row values from a SQL default or computed expression.");
            }

            if (operation.DefaultValue is not null)
            {
                return operation.DefaultValue;
            }

            return operation.IsNullable
                ? null
                : throw new InvalidOperationException(
                    $"Required model-managed column '{SafeMigrationDiagnosticMessages.BoundForDisplay(column)}' "
                    + "has no explicit initial value. Supply a deterministic initial value or an explicit "
                    + "source-frozen data transition.");
        }

        public void RequireDrop(
            UpdateDataOperation update,
            string column
        )
        {
            var identity = Identity(update.Table, update.Schema, column);
            if (!_dropped.Contains(identity))
            {
                throw new InvalidOperationException(
                    $"Inverse update column '{SafeMigrationDiagnosticMessages.BoundForDisplay(column)}' "
                    + "is absent without a column drop.");
            }
        }

        private static ColumnIdentity Identity(
            string table,
            string? schema,
            string column
        ) => new(schema, table, column);

        private readonly record struct ColumnIdentity(

            // WHY: The generated record equality keeps same-name members in different schemas independent.
            // ReSharper disable once NotAccessedPositionalProperty.Local
            string? Schema,

            // WHY: The table is part of generated dictionary-key equality, not an independently read payload.
            // ReSharper disable once NotAccessedPositionalProperty.Local
            string Table,

            // WHY: Generated record equality must distinguish each physical member within its exact owner.
            // ReSharper disable once NotAccessedPositionalProperty.Local
            string Column
        );
    }
}
