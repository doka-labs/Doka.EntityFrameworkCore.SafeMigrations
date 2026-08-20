namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureIndexIntent intent
    ) => WriteIndex(writer, intent.Definition);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropIndexIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        RenameIndexIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
        writer.Add(intent.NewName);
    }

    private static void WriteIndex(
        CanonicalHashWriter writer,
        ExpectedIndexDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Name);
        writer.Add(definition.Unique);
        writer.Add(definition.Filter);
        writer.Add(definition.Method);
        writer.Add(definition.NullsDistinct);

        writer.Add(definition.Keys.Count);

        foreach (var key in definition.Keys)
        {
            writer.Add(key.Column);
            writer.Add(key.Expression);

            writer.Add(key.Descending);
            writer.Add(key.PrefixLength);
            writer.Add(key.Collation);
            writer.Add(key.OperatorClass);
        }

        WriteStrings(writer, definition.IncludedColumns);
    }
}
