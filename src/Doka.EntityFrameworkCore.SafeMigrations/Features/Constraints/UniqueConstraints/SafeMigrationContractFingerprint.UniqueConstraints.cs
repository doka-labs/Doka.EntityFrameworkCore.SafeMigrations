namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureUniqueConstraintIntent intent
    ) => WriteUniqueConstraint(writer, intent.Definition);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropUniqueConstraintIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WriteUniqueConstraint(
        CanonicalHashWriter writer,
        ExpectedUniqueConstraintDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Name);
        WriteStrings(writer, definition.Columns);
    }
}
