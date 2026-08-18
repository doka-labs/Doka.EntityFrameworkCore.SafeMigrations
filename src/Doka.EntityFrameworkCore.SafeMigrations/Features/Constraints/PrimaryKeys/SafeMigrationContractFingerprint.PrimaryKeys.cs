namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsurePrimaryKeyIntent intent
    ) => WritePrimaryKey(writer, intent.Definition);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropPrimaryKeyIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WritePrimaryKey(
        CanonicalHashWriter writer,
        ExpectedPrimaryKeyDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Name);
        WriteStrings(writer, definition.Columns);
    }
}
