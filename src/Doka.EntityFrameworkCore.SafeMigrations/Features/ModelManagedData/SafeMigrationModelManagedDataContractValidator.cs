namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationModelManagedDataContractValidator
{
    /// <summary>Validates one authored migration batch without any repetition exemption.</summary>
    /// <param name="operations">The complete batch, including ordinary provider operations.</param>
    public static void Validate(
        IReadOnlyList<MigrationOperation> operations
    ) => Validate(operations, migrationIds: null);

    /// <summary>Validates repeated typed keys within each internally certified authored migration boundary.</summary>
    /// <param name="operations">The ordered stream, including ordinary provider operations.</param>
    /// <param name="migrationIds">Internally captured migration provenance, or null for one authored batch.</param>
    /// <remarks>Standalone analysis and SQL generation cannot supply caller-defined boundary exemptions.</remarks>
    public static void Validate(
        IReadOnlyList<MigrationOperation> operations,
        IReadOnlyList<string>? migrationIds
    )
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (migrationIds is not null && migrationIds.Count != operations.Count)
        {
            throw new ArgumentException(
                "Migration identities must match the complete operation stream.", nameof(migrationIds));
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        string? previousMigrationId = null;
        for (var ordinal = 0; ordinal < operations.Count; ordinal++)
        {
            if (migrationIds is not null)
            {
                var migrationId = migrationIds[ordinal];
                if (ordinal > 0 && !StringComparer.Ordinal.Equals(previousMigrationId, migrationId))
                {
                    // WHY: The same seed may change in a later real migration.
                    // Ordinary operations do not reset this ledger within one
                    // authored migration, and caller-created batches lack IDs.
                    identities.Clear();
                }

                previousMigrationId = migrationId;
            }

            if (operations[ordinal] is not SafeMigrationOperation { Intent: ModelManagedDataIntent intent })
            {
                continue;
            }

            var columns = SafeMigrationModelManagedKeyIdentity.CanonicalizeColumns(
                intent.KeyColumns, normalizer: null, out var keyOrdinals);

            for (var row = 0; row < intent.RowCount; row++)
            {
                if (!identities.Add(Identity(intent, row, columns, keyOrdinals)))
                {
                    throw new InvalidOperationException(
                        "A migration cannot transition the same typed model-managed key more than once.");
                }
            }
        }
    }

    /// <summary>Hashes exact authored typed key pairs through the cached per-intent ordinal permutation.</summary>
    /// <param name="intent">The authored row contract and owner.</param>
    /// <param name="row">The immutable key row.</param>
    /// <param name="columns">Exact authored labels, not provider-folded column aliases.</param>
    /// <param name="canonicalOrdinals">Optional canonical-to-authored mapping captured once per intent.</param>
    private static string Identity(
        ModelManagedDataIntent intent,
        int row,
        IReadOnlyList<string> columns,
        int[]? canonicalOrdinals
    )
    {
        using var writer = new CanonicalHashWriter();
        writer.Add(intent.Schema);
        writer.Add(intent.Table);
        writer.Add(columns.Count);
        for (var column = 0; column < columns.Count; column++)
        {
            var ordinal = canonicalOrdinals?[column] ?? column;
            writer.Add(columns[ordinal]);
            SafeMigrationModelManagedValue.Write(writer, intent.KeyValues.GetUnsafeValue(row, ordinal));
        }

        return writer.GetHash();
    }
}
