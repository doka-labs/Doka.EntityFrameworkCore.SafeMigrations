namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureCheckConstraintIntent intent
    ) => WriteCheckConstraint(writer, intent.Definition);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropCheckConstraintIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WriteCheckConstraint(
        CanonicalHashWriter writer,
        ExpectedCheckConstraintDefinition definition
    )
    {
        WriteTableIdentity(writer, definition.Table, definition.Schema);
        writer.Add(definition.Name);
        writer.Add(definition.Sql);
        SafeMigrationSqlExpressionContract.Write(writer, definition.Expression);
    }
}
