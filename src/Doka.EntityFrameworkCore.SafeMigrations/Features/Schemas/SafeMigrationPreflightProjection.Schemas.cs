namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private static SafeMigrationProviderAnalysis Project(
        EnsureSchemaIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis;

    private static SafeMigrationProviderAnalysis Project(
        DropSchemaIntent intent,
        SafeMigrationProviderAnalysis liveAnalysis
    ) => liveAnalysis;

    private static void Observe(
        EnsureSchemaIntent intent,
        SafeMigrationDecision decision
    )
    { }

    private static void Observe(
        DropSchemaIntent intent,
        SafeMigrationDecision decision
    )
    { }
}
