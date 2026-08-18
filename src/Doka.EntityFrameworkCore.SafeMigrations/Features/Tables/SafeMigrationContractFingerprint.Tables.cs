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
    }
}
