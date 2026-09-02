namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureModelManagedDataIntent intent
    )
    {
        WriteBase(writer, intent);
        WriteMatrix(writer, intent.Values);
        WriteUniqueKeys(writer, intent.UniqueKeys);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        UpdateModelManagedDataIntent intent
    )
    {
        WriteBase(writer, intent);
        WriteMatrix(writer, intent.OldValues);
        WriteMatrix(writer, intent.NewValues);
        WriteUniqueKeys(writer, intent.UniqueKeys);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DeleteModelManagedDataIntent intent
    )
    {
        WriteBase(writer, intent);
        WriteMatrix(writer, intent.OldValues);
        writer.Add(intent.ForeignKeys.Count);

        foreach (var foreignKey in intent.ForeignKeys)
        {
            WriteTableIdentity(writer, foreignKey.Table, foreignKey.Schema);
            WriteStrings(writer, foreignKey.Columns);
            WriteStrings(writer, foreignKey.PrincipalColumns);
        }
    }

    private static void WriteBase(
        CanonicalHashWriter writer,
        ModelManagedDataIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        WriteStrings(writer, intent.KeyColumns);
        WriteStrings(writer, intent.KeyColumnTypes);
        WriteMatrix(writer, intent.KeyValues);
        WriteStrings(writer, intent.Columns);
        WriteStrings(writer, intent.ColumnTypes);
    }

    private static void WriteUniqueKeys(
        CanonicalHashWriter writer,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        writer.Add(uniqueKeys.Count);
        foreach (var uniqueKey in uniqueKeys)
        {
            WriteStrings(writer, uniqueKey.Columns);
        }
    }

    private static void WriteMatrix(
        CanonicalHashWriter writer,
        ModelManagedDataMatrix values
    )
    {
        writer.Add(values.RowCount);
        writer.Add(values.ColumnCount);

        for (var row = 0; row < values.RowCount; row++)
        {
            for (var column = 0; column < values.ColumnCount; column++)
            {
                SafeMigrationModelManagedValue.Write(writer, values.GetUnsafeValue(row, column));
            }
        }
    }
}
