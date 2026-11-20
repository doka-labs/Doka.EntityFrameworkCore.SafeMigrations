namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed class EnsureModelManagedDataScaffoldingOperation : InsertDataOperation
{
    public EnsureModelManagedDataScaffoldingOperation(
        EnsureModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        Columns = intent.Columns.ToArray();

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        ColumnTypes = intent.ColumnTypes.ToArray();
        Values = ModelManagedDataScaffoldingValues.Copy(intent.Values);
    }

    public ModelManagedDataIntent Intent { get; }
}

internal sealed class UpdateModelManagedDataScaffoldingOperation : UpdateDataOperation
{
    public UpdateModelManagedDataScaffoldingOperation(
        UpdateModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        KeyColumns = intent.KeyColumns.ToArray();

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        KeyColumnTypes = intent.KeyColumnTypes.ToArray();
        KeyValues = ModelManagedDataScaffoldingValues.Copy(intent.KeyValues);

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        Columns = intent.Columns.ToArray();

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        ColumnTypes = intent.ColumnTypes.ToArray();
        Values = ModelManagedDataScaffoldingValues.Copy(intent.NewValues);
    }

    public ModelManagedDataIntent Intent { get; }
}

internal sealed class DeleteModelManagedDataScaffoldingOperation : DeleteDataOperation
{
    public DeleteModelManagedDataScaffoldingOperation(
        DeleteModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        KeyColumns = intent.KeyColumns.ToArray();

        // WHY: Retain the existing LINQ snapshot and ownership contract instead of spread-based materialization.
        // ReSharper disable once UseCollectionExpression
        KeyColumnTypes = intent.KeyColumnTypes.ToArray();
        KeyValues = ModelManagedDataScaffoldingValues.Copy(intent.KeyValues);
    }

    public ModelManagedDataIntent Intent { get; }
}

internal static class ModelManagedDataScaffoldingValues
{
    internal static object?[,] Copy(
        ModelManagedDataMatrix values
    )
    {
        var result = new object?[values.RowCount, values.ColumnCount];

        // WHY: Copy matrix rows into the same row coordinates of the owned rectangular array.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < values.RowCount; row++)
        {
            // WHY: Preserve each matrix cell's row and column association in the owned copy.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var column = 0; column < values.ColumnCount; column++)
            {
                result[row, column] = values.GetValue(row, column);
            }
        }

        return result;
    }
}
