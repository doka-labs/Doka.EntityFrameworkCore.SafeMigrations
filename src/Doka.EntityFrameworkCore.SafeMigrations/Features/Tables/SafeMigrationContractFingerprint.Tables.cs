namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureTableIntent intent
    )
    {
        writer.Add((int)intent.Mode);
        WriteTable(writer, intent.Definition);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropTableIntent intent
    ) => WriteTableIdentity(writer, intent.Table, intent.Schema);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        RenameTableIntent intent
    )
    {
        writer.Add(intent.Name);
        writer.Add(intent.Schema);
        writer.Add(intent.NewName);
        writer.Add(intent.NewSchema);
    }

    private static void WriteTable(
        CanonicalHashWriter writer,
        ExpectedTableDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Comment);
        writer.Add(definition.Columns.Count);

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var columnIndex = 0; columnIndex < definition.Columns.Count; columnIndex++)
        {
            var column = definition.Columns[columnIndex];

            WriteColumn(writer, column);
        }

        writer.Add(definition.PrimaryKey is not null);
        if (definition.PrimaryKey is not null)
        {
            WritePrimaryKey(writer, definition.PrimaryKey);
        }

        writer.Add(definition.UniqueConstraints.Count);

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var constraintIndex = 0; constraintIndex < definition.UniqueConstraints.Count; constraintIndex++)
        {
            var constraint = definition.UniqueConstraints[constraintIndex];

            WriteUniqueConstraint(writer, constraint);
        }

        writer.Add(definition.CheckConstraints.Count);

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var constraintIndex1 = 0; constraintIndex1 < definition.CheckConstraints.Count; constraintIndex1++)
        {
            var constraint = definition.CheckConstraints[constraintIndex1];

            WriteCheckConstraint(writer, constraint);
        }

        writer.Add(definition.ForeignKeys.Count);

        // WHY: This immutable definition snapshot is indexed; retain its authored order without interface enumeration.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var constraintIndex2 = 0; constraintIndex2 < definition.ForeignKeys.Count; constraintIndex2++)
        {
            var constraint = definition.ForeignKeys[constraintIndex2];

            WriteForeignKey(writer, constraint);
        }

        if (definition.ProviderAnnotations.Count > 0)
        {
            // WHY: Extend only annotated contracts; existing table hashes must retain their exact byte stream.
            writer.Add("table_provider_annotations:v1");
            writer.Add(definition.ProviderAnnotations.Count);

            // WHY: Preserve indexed immutable-snapshot order in the versioned annotation extension.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < definition.ProviderAnnotations.Count; index++)
            {
                var annotation = definition.ProviderAnnotations[index];
                writer.Add(annotation.Name);
                writer.Add(annotation.Fingerprint);
            }
        }
    }
}
