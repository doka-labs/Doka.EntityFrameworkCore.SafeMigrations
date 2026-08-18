namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureSchemaIntent intent
    ) => writer.Add(intent.Name);

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropSchemaIntent intent
    ) => writer.Add(intent.Name);
}
