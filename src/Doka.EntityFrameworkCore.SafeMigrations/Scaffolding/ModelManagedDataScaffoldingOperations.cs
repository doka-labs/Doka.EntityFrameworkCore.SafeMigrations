namespace Doka.EntityFrameworkCore.SafeMigrations;

internal interface IModelManagedDataScaffoldingOperation
{
    ModelManagedDataIntent Intent { get; }
}

internal sealed class EnsureModelManagedDataScaffoldingOperation : InsertDataOperation,
    IModelManagedDataScaffoldingOperation
{
    public EnsureModelManagedDataScaffoldingOperation(
        EnsureModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;
        Columns = intent.Columns.ToArray();
        ColumnTypes = intent.ColumnTypes.ToArray();
        Values = ModelManagedDataScaffoldingValues.Copy(intent.Values);
    }

    public ModelManagedDataIntent Intent { get; }
}

internal sealed class UpdateModelManagedDataScaffoldingOperation : UpdateDataOperation,
    IModelManagedDataScaffoldingOperation
{
    public UpdateModelManagedDataScaffoldingOperation(
        UpdateModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;
        KeyColumns = intent.KeyColumns.ToArray();
        KeyColumnTypes = intent.KeyColumnTypes.ToArray();
        KeyValues = ModelManagedDataScaffoldingValues.Copy(intent.KeyValues);
        Columns = intent.Columns.ToArray();
        ColumnTypes = intent.ColumnTypes.ToArray();
        Values = ModelManagedDataScaffoldingValues.Copy(intent.NewValues);
    }

    public ModelManagedDataIntent Intent { get; }
}

internal sealed class DeleteModelManagedDataScaffoldingOperation : DeleteDataOperation,
    IModelManagedDataScaffoldingOperation
{
    public DeleteModelManagedDataScaffoldingOperation(
        DeleteModelManagedDataIntent intent
    )
    {
        Intent = intent;
        Table = intent.Table;
        Schema = intent.Schema;
        KeyColumns = intent.KeyColumns.ToArray();
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
        for (var row = 0; row < values.RowCount; row++)
        {
            for (var column = 0; column < values.ColumnCount; column++)
            {
                result[row, column] = values.GetValue(row, column);
            }
        }

        return result;
    }
}
