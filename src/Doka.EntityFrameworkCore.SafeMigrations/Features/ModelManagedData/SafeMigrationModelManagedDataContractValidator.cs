namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationModelManagedDataContractValidator
{
    public static void Validate(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            if (operation.Intent is not ModelManagedDataIntent intent)
            {
                continue;
            }

            for (var row = 0; row < intent.RowCount; row++)
            {
                if (!identities.Add(Identity(intent, row)))
                {
                    throw new InvalidOperationException(
                        "A migration cannot transition the same typed model-managed key more than once.");
                }
            }
        }
    }

    private static string Identity(
        ModelManagedDataIntent intent,
        int row
    )
    {
        var ordinals = Enumerable.Range(0, intent.KeyColumns.Count)
            .OrderBy(ordinal => intent.KeyColumns[ordinal], StringComparer.Ordinal)
            .ToArray();

        using var writer = new CanonicalHashWriter();
        writer.Add(intent.Schema);
        writer.Add(intent.Table);
        writer.Add(ordinals.Length);
        foreach (var ordinal in ordinals)
        {
            writer.Add(intent.KeyColumns[ordinal]);
            SafeMigrationModelManagedValue.Write(writer, intent.KeyValues.GetUnsafeValue(row, ordinal));
        }

        return writer.GetHash();
    }
}
