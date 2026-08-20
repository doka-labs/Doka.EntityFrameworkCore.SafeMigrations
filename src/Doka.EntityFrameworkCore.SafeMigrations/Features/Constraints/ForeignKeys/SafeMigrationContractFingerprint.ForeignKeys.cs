namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureForeignKeyIntent intent
    ) => WriteForeignKey(writer, intent.Definition);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropForeignKeyIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WriteForeignKey(
        CanonicalHashWriter writer,
        ExpectedForeignKeyDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Name);
        WriteStrings(writer, definition.Columns);

        WriteTableIdentity(writer, definition.PrincipalTable, definition.PrincipalSchema);
        WriteStrings(writer, definition.PrincipalColumns);

        writer.Add((int)definition.OnUpdate);
        writer.Add((int)definition.OnDelete);
    }
}
