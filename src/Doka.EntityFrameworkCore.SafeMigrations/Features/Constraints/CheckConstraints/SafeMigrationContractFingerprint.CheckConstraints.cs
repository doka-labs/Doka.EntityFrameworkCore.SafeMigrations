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
        if (definition.CapturedSql is not null)
        {
            // WHY: A bare native numeric literal and its typed rendering can
            // select different division rules; the authored source is part of
            // this captured contract, not merely a display preference.
            writer.Add("captured_check_sql:v1");
            writer.Add(definition.CapturedSql);
        }
    }
}
