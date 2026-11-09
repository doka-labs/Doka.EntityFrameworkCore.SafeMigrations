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
        foreach (var column in definition.Columns)
        {
            WriteColumn(writer, column);
        }

        writer.Add(definition.PrimaryKey is not null);
        if (definition.PrimaryKey is not null)
        {
            WritePrimaryKey(writer, definition.PrimaryKey);
        }

        writer.Add(definition.UniqueConstraints.Count);
        foreach (var constraint in definition.UniqueConstraints)
        {
            WriteUniqueConstraint(writer, constraint);
        }

        writer.Add(definition.CheckConstraints.Count);
        foreach (var constraint in definition.CheckConstraints)
        {
            WriteCheckConstraint(writer, constraint);
        }

        writer.Add(definition.ForeignKeys.Count);
        foreach (var constraint in definition.ForeignKeys)
        {
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
